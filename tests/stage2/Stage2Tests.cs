using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Cad100.DirectoryChecking;
public static class Stage2Tests
{
    static JavaScriptSerializer J = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static List<object> results = new List<object>(); static int failed;
    static void Test(string name, Action run) { try { run(); results.Add(new { name, status = "PASS" }); } catch (Exception e) { failed++; results.Add(new { name, status = "FAIL", error = e.Message }); } }
    static void Need(bool b) { if (!b) throw new Exception("assertion failed"); }
    static void Reject(Action a) { bool rejected=false; try { a(); } catch (InvalidOperationException) { rejected=true; } catch (ArgumentException) { rejected=true; } Need(rejected); }
    static FieldData F(string s) { return new FieldData { State = s == "" ? ReadState.Empty : ReadState.Value, OrdinaryRaw = s, AttributeHandle = Guid.NewGuid().ToString() }; }
    static BatchInput B(string number, string name) { return new BatchInput { BaselineConfirmed = true, BaselineRevision = "revision",
        Directory = new List<DirectoryRow> { new DirectoryRow { Id = "d1", Fields = new Dictionary<string, FieldData> { { "图号", F("A1") }, { "图名", F("标准园路") } } } },
        Titles = new List<TitleRecord> { new TitleRecord { Id = "t1", File = "one.dwg", Layout = "Model", SourceRevision = "hash", InsertHandle = "1", GeometrySnapshot = "scale:150.00001811;height:3.5;point:1.23456789",
        Fields = new Dictionary<string, FieldData> { { "图号", F(number) }, { "图名", F(name) } } } } }; }
    // Historical plan tests now explicitly perform the human bulk-confirm action first.
    // New ConfirmationTests separately assert that Create itself yields zero executable plans.
    static BatchDraft ReviewedDraft(BatchInput input) { var draft = BatchPreview.Create(input); BatchPreview.ConfirmAllClear(input, draft); return draft; }
    static BatchInput Clone(BatchInput b) { return J.Deserialize<BatchInput>(J.Serialize(b)); }
    static DirectoryRow D(string id, string number, string name) { var d=B(number,name).Directory[0]; d.Id=id; d.Fields["图号"]=F(number); d.Fields["图名"]=F(name); return d; }
    static string Hash(string path) { using (var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    public static int Main(string[] args)
    {
        var native = J.Deserialize<BatchInput>(File.ReadAllText(args[0])); Directory.CreateDirectory(args[1]);
        Test("After explicit bulk review: 5 plans, 4 matches, 1 pending; no guessed standard-road binding", delegate {
            var p=BatchPreview.Preview(native,ReviewedDraft(native)); Need(p.Plans.Count==5 && p.Pending==1 && p.Rows.Count(r=>r.State=="一致")==4);
            Need(!p.Rows.Single(r=>r.TitleId.Contains("1标准园路")).Selected);
        });
        var accepted=Clone(native);
        foreach(var pair in new[]{new[]{"L-P-CL-001目录", "L-P-CL-001"},new[]{"1标准园路", "L-P-DT-002"},new[]{"1材料表", "L-P-CL-002"},new[]{"1铺装物料", "L-P-SP-003"},new[]{"1生态旱溪", "L-P-FP-S2-001"},new[]{"L-P-SP-001", "L-P-SP-001"}})
        {
            var title=accepted.Titles.Single(t=>Path.GetFileName(t.File).StartsWith(pair[0]));
            accepted.ManualBindings[title.Id]=accepted.Directory.Single(d=>RuleEngine.Read(d.Fields["图号"]).Normalized==pair[1]).Id;
        }
        Test("Accepted six bindings yield exactly 6 plans: 4 names, 2 numbers, 4 unchanged", delegate {
            var p=BatchPreview.Preview(accepted,ReviewedDraft(accepted)); Need(p.Plans.Count==6 && p.ChangedNames==4 && p.ChangedNumbers==2 && p.Pending==0 && p.Rows.Count==10);
            Need(p.Plans.Sum(x=>x.Changes.Count)==6); File.WriteAllText(Path.Combine(args[1],"accepted_sample_output_preview.json"),J.Serialize(p));
        });
        Test("Material empty display and paving wrong display repaired with synchronized representation metadata", delegate {
            var p=BatchPreview.Preview(accepted,ReviewedDraft(accepted));
            var material=p.Plans.Single(x=>x.TitleId.Contains("1材料表")).Changes.Single(); Need(material.Original=="" && material.Proposed=="材料表" && material.SynchronizeRepresentations);
            var paving=p.Plans.Single(x=>x.TitleId.Contains("1铺装物料")).Changes.Single(); Need(paving.Original=="铺装物料剖面图" && paving.Proposed=="铺装物料平面图" && paving.SynchronizeRepresentations);
        });
        Test("Unique name missing number prefilled and explicitly confirmed without filename inference", delegate { var b=B("", "标准园路"); var p=BatchPreview.Preview(b,ReviewedDraft(b)); Need(p.Plans.Count==1 && p.Rows[0].OutputNumber=="A1" && p.ChangedNumbers==1); });
        Test("Unique number missing name prefilled", delegate { var b=B("A1", ""); var p=BatchPreview.Preview(b,ReviewedDraft(b)); Need(p.Rows[0].OutputName=="标准园路" && p.ChangedNames==1); });
        Test("Unchecked removes plan but preserves proposal and source", delegate { var b=B("A1", "旧图名"); var d=ReviewedDraft(b); BatchPreview.SetChoice(b,d,"t1",PreviewChoice.Unchecked); var p=BatchPreview.Preview(b,d); Need(p.Plans.Count==0 && p.Rows[0].OutputName=="标准园路" && p.Rows[0].OriginalName=="旧图名"); });
        Test("Keep original persists difference, no plan", delegate { var b=B("A1", "旧图名"); var d=ReviewedDraft(b); BatchPreview.SetChoice(b,d,"t1",PreviewChoice.KeepOriginal); var p=BatchPreview.Preview(b,d); Need(p.Plans.Count==0 && p.Rows[0].OutputName=="旧图名" && p.Rows[0].State.Contains("已确认保留") && !RuleEngine.Evaluate(b).Titles[0].ContentMatches); });
        Test("Defer is skipped, not passed", delegate { var b=B("A1", "旧图名"); var d=ReviewedDraft(b); BatchPreview.SetChoice(b,d,"t1",PreviewChoice.Defer); Need(BatchPreview.Preview(b,d).Rows[0].State.Contains("暂不处理") && BatchPreview.ConfirmBatch(b,d)[0].Choice==UserChoice.Defer); });
        Test("Re-enable restores guarded proposal", delegate { var b=B("A1", "旧图名"); var d=ReviewedDraft(b); BatchPreview.SetChoice(b,d,"t1",PreviewChoice.KeepOriginal); BatchPreview.SetChoice(b,d,"t1",PreviewChoice.Auto); Need(BatchPreview.Preview(b,d).Plans.Count==1); });
        Test("Empty both cannot auto select or create arbitrary plan", delegate { var b=B("", ""); var d=ReviewedDraft(b); Need(BatchPreview.Preview(b,d).Plans.Count==0); Reject(delegate { BatchPreview.SetChoice(b,d,"t1",PreviewChoice.Auto); }); });
        Test("Number/name conflict blocked; explicit binding changes result", delegate { var b=B("A1","另一张图"); b.Directory.Add(D("d2","A2","另一张图")); var d=ReviewedDraft(b); Need(BatchPreview.Preview(b,d).Pending==1); b.ManualBindings["t1"]="d2"; Reject(delegate { BatchPreview.Preview(b,d); }); d=ReviewedDraft(b); Need(BatchPreview.Preview(b,d).Rows[0].OutputNumber=="A2"); });
        Test("Same names with distinct unique numbers do not cause ambiguity", delegate { var b=B("A1","标准园路"); b.Directory.Add(D("d2","A2","标准园路")); Need(BatchPreview.Preview(b,ReviewedDraft(b)).Rows[0].State=="一致"); });
        Test("Same names without number remain ambiguous", delegate { var b=B("","标准园路"); b.Directory.Add(D("d2","A2","标准园路")); Need(BatchPreview.Preview(b,ReviewedDraft(b)).Plans.Count==0); });
        Test("Duplicate directory numbers block even manual binding", delegate { var b=B("A1","旧图名"); b.Directory.Add(D("d2","A1","另一张")); b.ManualBindings["t1"]="d1"; Need(BatchPreview.Preview(b,ReviewedDraft(b)).Plans.Count==0); });
        Test("Duplicate title numbers and row claims blocked", delegate { var b=B("A1","旧图名"); var t=Clone(b).Titles[0]; t.Id="t2"; b.Titles.Add(t); Need(BatchPreview.Preview(b,ReviewedDraft(b)).Plans.Count==0); });
        Test("Missing attribute not treated as blank", delegate { var b=B("A1", ""); b.Titles[0].Fields.Remove("图名"); Need(BatchPreview.Preview(b,ReviewedDraft(b)).Plans.Count==0); });
        Test("Read error is failure, not empty", delegate { var b=B("A1", ""); b.Titles[0].Fields["图名"].State=ReadState.ReadError; var p=BatchPreview.Preview(b,ReviewedDraft(b)); Need(p.Plans.Count==0 && p.Rows[0].State.StartsWith("读取失败")); });
        Test("Nonwritable and unsupported expressions cannot auto plan", delegate { var b=B("A1","旧图名"); b.Titles[0].Fields["图名"].Writable=false; Need(BatchPreview.Preview(b,ReviewedDraft(b)).Plans.Count==0); b.Titles[0].Fields["图名"]=F("%<expression>%"); Need(BatchPreview.Preview(b,ReviewedDraft(b)).Plans.Count==0); });
        Test("Enabled empty optional baseline does not clear original date", delegate { var b=B("A1", "旧图名"); b.OptionalFields.Add("日期"); b.Directory[0].Fields["日期"]=F(""); b.Titles[0].Fields["日期"]=F("2025年11月"); var p=BatchPreview.Preview(b,ReviewedDraft(b)); Need(p.Plans.Single().Changes.All(c=>c.Field!="日期") && b.Titles[0].Fields["日期"].OrdinaryRaw=="2025年11月"); });
        Test("Plan retains exact geometry; preview does not mutate input", delegate { var b=B("A1","旧图名"); var before=J.Serialize(b); var p=BatchPreview.Preview(b,ReviewedDraft(b)); Need(p.Plans[0].OriginalGeometrySnapshot==b.Titles[0].GeometrySnapshot && J.Serialize(b)==before); });
        Test("Draft JSON roundtrip retains exclusions and keep decisions", delegate { var d=ReviewedDraft(accepted); BatchPreview.SetChoice(accepted,d,d.Selections[0].TitleId,PreviewChoice.KeepOriginal); var restored=J.Deserialize<BatchDraft>(J.Serialize(d)); Need(J.Serialize(BatchPreview.Preview(accepted,d))==J.Serialize(BatchPreview.Preview(accepted,restored))); });
        Test("Repeated confirmation is idempotent; unchecked cannot leak", delegate { var b=B("A1","旧图名"); var d=ReviewedDraft(b); Need(J.Serialize(BatchPreview.ConfirmBatch(b,d))==J.Serialize(BatchPreview.ConfirmBatch(b,d))); BatchPreview.SetChoice(b,d,"t1",PreviewChoice.Unchecked); Need(BatchPreview.ConfirmBatch(b,d).Count==0); });
        Test("Changed source, geometry, directory, baseline, binding, scope invalidate old drafts", delegate {
            var b=B("A1","旧图名"); var d=ReviewedDraft(b);
            var a=Clone(b); a.Titles[0].SourceRevision="changed"; Reject(delegate{BatchPreview.ConfirmBatch(a,d);});
            a=Clone(b);a.Titles[0].GeometrySnapshot="changed";Reject(delegate{BatchPreview.Preview(a,d);});
            a=Clone(b);a.Directory[0].Fields["图名"]=F("changed");Reject(delegate{BatchPreview.Preview(a,d);});
            a=Clone(b);a.BaselineRevision="changed";Reject(delegate{BatchPreview.Preview(a,d);});
            a=Clone(b);a.ManualBindings["t1"]="d1";Reject(delegate{BatchPreview.Preview(a,d);});
            a=Clone(b);a.Titles.Clear();Reject(delegate{BatchPreview.Preview(a,d);});
        });
        Test("Tampered selections, unknown choice, duplicates and unknown IDs rejected", delegate { var b=B("A1","旧图名"); var d=ReviewedDraft(b); d.Selections[0].Choice=(PreviewChoice)999; Reject(delegate{BatchPreview.Preview(b,d);}); d=ReviewedDraft(b);d.Selections.Add(d.Selections[0]);Reject(delegate{BatchPreview.Preview(b,d);}); d=ReviewedDraft(b);d.Selections[0].TitleId="other";Reject(delegate{BatchPreview.Preview(b,d);}); });
        Test("Source file verification checks actual bytes and scope without modifying originals", delegate {
            var folder=Path.Combine(args[1],"synthetic_sources_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var file=Path.Combine(folder,"one.dwg");File.WriteAllText(file,"synthetic data, not a real DWG");
            var session=new DirectoryScanSession{Folder=folder,Files=new List<ScanFileData>{new ScanFileData{File=file,Hash=Hash(file)}}};
            var original=Hash(file);DwgDirectoryScanner.VerifySources(session);Need(Hash(file)==original);
            File.AppendAllText(file,"changed");Reject(delegate{DwgDirectoryScanner.VerifySources(session);});
            session.Files[0].Hash=Hash(file);File.WriteAllText(Path.Combine(folder,"two.dwg"),"extra");Reject(delegate{DwgDirectoryScanner.VerifySources(session);});
        });
        File.WriteAllText(Path.Combine(args[1],"stage2_test_results.json"),J.Serialize(new { passed=results.Count-failed, failed, scope="Rule/model tests and recorded native diagnostic replay; no real CAD write or live CAD scan", results }));
        File.WriteAllText(Path.Combine(args[1],"accepted_input.json"),J.Serialize(accepted));
        Console.WriteLine(J.Serialize(new { passed=results.Count-failed, failed })); return failed==0?0:1;
    }
}

