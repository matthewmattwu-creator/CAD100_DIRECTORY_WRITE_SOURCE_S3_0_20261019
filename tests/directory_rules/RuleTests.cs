using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Cad100.DirectoryChecking;

public static class RuleTests
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly List<object> Results = new List<object>();
    static int failed, assertions;
    static BatchInput Real;
    static void Require(bool yes, string message) { assertions++; if (!yes) throw new Exception(message); }
    static void Test(string id, string name, string rules, string origin, Action run)
    {
        int start = assertions;
        try { run(); Results.Add(new { id, name, rules, origin, status = "PASS", assertions = assertions - start }); }
        catch (Exception ex) { failed++; Results.Add(new { id, name, rules, origin, status = "FAIL", error = ex.Message, assertions = assertions - start }); }
    }
    static void Reject(Action run)
    {
        bool caught = false;
        try { run(); } catch (InvalidOperationException) { caught = true; } catch (ArgumentException) { caught = true; }
        Require(caught, "必须阻止不确定或失效操作");
    }
    static FieldData F(string v) { return new FieldData { State = v == "" ? ReadState.Empty : ReadState.Value, OrdinaryRaw = v, AttributeHandle = "TEST-ATTR" }; }
    static DirectoryRow D(string id, string no, string name)
    { return new DirectoryRow { Id = id, File = "目录.dwg", Layout = "Model", Sequence = id, Fields = new Dictionary<string, FieldData> { { RuleEngine.Number, F(no) }, { RuleEngine.Name, F(name) } } }; }
    static TitleRecord T(string id, string no, string name)
    { return new TitleRecord { Id = id, File = id + ".dwg", Layout = "布局1", InsertHandle = id, SourceRevision = "revision-1", GeometrySnapshot = "{precise:150.00001811,position:[10,20],height:3.5}", Fields = new Dictionary<string, FieldData> { { RuleEngine.Number, F(no) }, { RuleEngine.Name, F(name) } } }; }
    static BatchInput B(string no, string name)
    { return new BatchInput { BaselineConfirmed = true, BaselineRevision = "base-1", Directory = new List<DirectoryRow> { D("r1", "A-001", "生态旱溪详图") }, Titles = new List<TitleRecord> { T("t1", no, name) } }; }
    static TitleCheck One(BatchInput b) { return RuleEngine.Evaluate(b).Titles.Single(); }
    static bool Has(TitleCheck r, string code) { return r.Issues.Any(i => i.Code == code); }
    static BatchInput Clone(BatchInput b) { return Json.Deserialize<BatchInput>(Json.Serialize(b)); }
    static void Difference(BatchInput b) { var r = One(b); Require(!r.ContentMatches, "差异不能判通过"); }
    static BatchInput Conflict()
    {
        var b = B("A-001", "标准园路大样详图"); b.Directory.Add(D("r2", "A-002", "标准园路大样详图")); return b;
    }
    static BatchInput Twenty()
    {
        var b = new BatchInput { BaselineConfirmed = true, BaselineRevision = "twenty" };
        for (int i = 1; i <= 18; i++) { b.Directory.Add(D("r" + i, "P-" + i, "唯一图名" + i)); b.Titles.Add(T("t" + i, "", "唯一图名" + i)); }
        b.Directory.Add(D("r19", "P-19", "共享图名")); b.Directory.Add(D("r20", "P-20", "其他图名"));
        b.Titles.Add(T("t19", "", "共享图名")); b.Titles.Add(T("t20", "", "共享图名")); return b;
    }
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 2) { Console.WriteLine("RuleTests fixture.json results-directory"); return 2; }
        Real = Json.Deserialize<BatchInput>(File.ReadAllText(args[0], Encoding.UTF8));
        Directory.CreateDirectory(args[1]);
        var realResult = RuleEngine.Evaluate(Real);
        File.WriteAllText(Path.Combine(args[1], "native_sample_results.json"), Json.Serialize(realResult), Encoding.UTF8);
        Test("N01", "真实目录57行、图签10个，目录文件和一文件两图签均保留", "R08/R09", "native-backed", delegate {
            Require(Real.Directory.Count == 57 && realResult.Titles.Count == 10, "数量错误");
            Require(Real.Titles.Any(t => t.File.StartsWith("L-P-CL-001")), "目录图签未进入清单");
            Require(Real.Titles.Count(t => t.File.Contains("半月池")) == 2, "一文件两图签丢失"); });
        Test("N02", "真实标准园路号名冲突，保留两个候选且无自动修改", "R28", "native-backed", delegate {
            var r = realResult.Titles.Single(t => t.TitleId == "title-02"); Require(r.Association == "Conflict", "未识别冲突");
            Require(r.NumberCandidates.Count == 1 && r.NameCandidates.Count == 1 && r.NumberCandidates[0] != r.NameCandidates[0], "未保留两种依据");
            Require(!r.EligibleForBatchConfirmation && r.ProposedChanges.Count == 0, "冲突被自动采纳"); });
        Test("N03", "真实材料表按空显示正文核对，普通值不掩盖缺字", "R20/R26", "native-backed", delegate {
            Require(Real.Titles.Single(t => t.Id == "title-03").Fields[RuleEngine.Name].State == ReadState.Empty, "空正文状态没有明确保留");
            var r = realResult.Titles.Single(t => t.TitleId == "title-03"); Require(Has(r, "EMPTY_VALUE") && Has(r, "REPRESENTATION_MISMATCH"), "未查出双重问题");
            Require(r.ProposedChanges.Single(c => c.Field == RuleEngine.Name).Proposed == "材料表", "补名建议错误"); });
        Test("N04", "真实铺装剖面/平面错误与两种属性表示差异同时保留", "R20/R24/R39", "native-backed", delegate {
            var r = realResult.Titles.Single(t => t.TitleId == "title-04"); Require(Has(r, "CONTENT_DIFFERENT") && Has(r, "REPRESENTATION_MISMATCH"), "问题被覆盖");
            Require(r.ProposedChanges.Single(c => c.Field == RuleEngine.Name).Proposed == "铺装物料平面图", "目标图名错误"); });
        Test("N05", "真实生态旱溪只产生补号候选，不执行写入", "R27", "native-backed", delegate {
            var r = realResult.Titles.Single(t => t.TitleId == "title-05"); Require(r.Association == "ByNamePendingConfirmation", "错误关联");
            Require(r.ProposedChanges.Single().Field == RuleEngine.Number && r.ProposedChanges.Single().Proposed == "L-P-FP-S2-001", "错误补号"); });
        Test("N06", "真实参照4项一致，目录自身和总平面2项仍显示差异", "R24", "native-backed", delegate {
            Require(realResult.Titles.Count(r => r.ContentMatches) == 4, "一致数错误");
            Require(Has(realResult.Titles.Single(r => r.TitleId == "title-01"), "CONTENT_DIFFERENT"), "目录自身差异漏掉");
            Require(Has(realResult.Titles.Single(r => r.TitleId == "title-10"), "CONTENT_DIFFERENT"), "总平面少字差异漏掉"); });
        Test("N07", "真实空日期保留，缺少图别列不生成覆盖建议", "R13/R15/R16", "native-backed", delegate {
            Require(realResult.Titles.Where(r => r.DirectoryRowId != null).All(r => Has(r, "BASELINE_EMPTY") && Has(r, "NO_BASELINE_COLUMN")), "基准提示缺失");
            Require(realResult.Titles.SelectMany(r => r.ProposedChanges).All(c => c.Field != "日期" && c.Field != "图别"), "空基准生成了覆盖"); });
        Test("N08", "真实重复序号09只提示序号重复，不报重复图号", "R31/R33", "native-backed", delegate {
            Require(realResult.DirectoryIssues.Count(i => i.Code == "DUPLICATE_SEQUENCE") == 1, "未检测重复序号");
            Require(!realResult.DirectoryIssues.Any(i => i.Code == "DUPLICATE_DIRECTORY_NUMBER"), "误报重复图号"); });
        Test("N09", "真实冲突未确认时区分46项未找到与2项目录行待关联", "R28/R34", "native-backed", delegate {
            Require(realResult.NotFoundInScope.Count == 46 && realResult.PendingAssociation.Count == 2, "冲突候选被直接当成漏图"); });
        Test("N10", "人工绑定真实标准园路后，10行关联、47行本范围未找到", "R28/R34", "native-backed", delegate {
            var b = Clone(Real); b.ManualBindings["title-02"] = b.Directory.Single(d => d.Fields[RuleEngine.Number].OrdinaryRaw == "L-P-DT-002").Id;
            var r = RuleEngine.Evaluate(b); Require(r.NotFoundInScope.Count == 47 && r.PendingAssociation.Count == 0, "绑定后范围数错误");
            Require(r.Titles.Single(t => t.TitleId == "title-02").ProposedChanges.Single().Proposed == "L-P-DT-002", "人工选择未生效"); });
        Test("S01", "完全一致不生成修改", "R25", "synthetic", delegate { var r = One(B("A-001", "生态旱溪详图")); Require(r.ContentMatches && r.ProposedChanges.Count == 0, "无差异重写"); });
        Test("S02", "首尾普通空格忽略，输入原文不变", "R22", "synthetic", delegate {
            var b = B(" A-001 ", " 生态旱溪详图 "); var before = Json.Serialize(b); Require(One(b).ContentMatches, "误报首尾空格"); Require(before == Json.Serialize(b), "修改了输入"); });
        Test("S03", "内部空格不忽略", "R22", "synthetic", delegate { Difference(B("A-001", "生态 旱溪详图")); });
        Test("S04", "大小写不忽略", "R22", "synthetic", delegate { var r = One(B("a-001", "生态旱溪详图")); Require(r.Association == "ByNamePendingConfirmation" && !r.ContentMatches, "编号大小写被合并"); });
        Test("S05", "前导零不删除", "R22", "synthetic", delegate { Require(One(B("A-1", "生态旱溪详图")).ProposedChanges.Single().Proposed == "A-001", "前导零丢失"); });
        Test("S06", "点号后缀两个编号保持独立", "R22", "synthetic", delegate {
            var b = B("A-003.2", "绿化井盖"); b.Directory = new List<DirectoryRow> { D("r1", "A-003.1", "铺装井盖"), D("r2", "A-003.2", "绿化井盖") };
            Require(One(b).DirectoryRowId == "r2" && One(b).ContentMatches, "点号编号混淆"); });
        Test("S07", "少字不按模糊相似判相同", "R24", "synthetic", delegate { Difference(B("A-001", "生态旱溪")); });
        Test("S08", "换行保留且单独提示", "R23", "synthetic", delegate {
            var b = B("A-001", @"生态\P旱溪详图"); var r = One(b); Require(Has(r, "LINE_BREAK_DIFFERENT"), "换行被抹掉"); Require(RuleEngine.Read(b.Titles[0].Fields[RuleEngine.Name]).Display.Contains("\n"), "正文换行丢失"); });
        Test("S09", "已支持宽度控制只影响比较表示，不改变原文", "R19/R21", "synthetic", delegate {
            var b = B("A-001", @"\W0.8000;生态旱溪详图"); Require(One(b).ContentMatches, "格式指令被当正文"); Require(b.Titles[0].Fields[RuleEngine.Name].OrdinaryRaw.StartsWith(@"\W"), "原文被清洗覆盖"); });
        Test("S10", "未知格式阻止生成可确认计划", "R21", "synthetic", delegate { var r = One(B("A-001", @"\Qunknown;生态旱溪详图")); Require(Has(r, "UNSUPPORTED_FORMAT") && !r.EligibleForBatchConfirmation, "未知格式被静默删掉"); });
        Test("S11", "嵌入正文确实为空时不回退旧普通值", "R20", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); var f = b.Titles[0].Fields[RuleEngine.Name]; f.EmbeddedPresent = true; f.EmbeddedRaw = "";
            Require(Has(One(b), "EMPTY_VALUE"), "空多行被普通值掩盖"); });
        Test("S12", "嵌入正文无法读取与空白分开", "R18/R20", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); var f = b.Titles[0].Fields[RuleEngine.Name]; f.EmbeddedPresent = true; f.EmbeddedRaw = null;
            var r = One(b); Require(Has(r, "READ_FAILED") && !r.EligibleForBatchConfirmation, "读取失败被当空白可写"); });
        Test("S13", "显示正确但普通缓存错误仍保留问题及同步表示建议", "R20", "synthetic", delegate {
            var b = B("A-001", "旧错误名"); var f = b.Titles[0].Fields[RuleEngine.Name]; f.EmbeddedPresent = true; f.EmbeddedRaw = "生态旱溪详图";
            var r = One(b); Require(Has(r, "REPRESENTATION_MISMATCH") && !r.ContentMatches, "缓存差异被隐藏"); Require(r.ProposedChanges.Single().SynchronizeRepresentations, "没有两种表示同步建议"); });
        Test("S14", "没有图号属性时禁止自动新增", "R17", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); b.Titles[0].Fields[RuleEngine.Number].State = ReadState.MissingAttribute;
            var r = One(b); Require(Has(r, "MISSING_ATTRIBUTE") && !r.EligibleForBatchConfirmation && r.ProposedChanges.Count == 0, "创建了不存在的属性"); });
        Test("S15", "字段读取失败不判一致", "R18", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.Titles[0].Fields[RuleEngine.Name].State = ReadState.ReadError;
            var r = One(b); Require(!r.ContentMatches && !r.EligibleForBatchConfirmation, "读取错误被通过"); });
        Test("S16", "不可写字段不能进入批量确认", "R17", "synthetic", delegate {
            var b = B("A-001", "旧名"); b.Titles[0].Fields[RuleEngine.Name].Writable = false; Require(!One(b).EligibleForBatchConfirmation, "不可写项可确认"); });
        Test("S17", "空日期基准不清空原日期，并提示", "R16", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.OptionalFields.Add("日期"); b.Directory[0].Fields["日期"] = F(""); b.Titles[0].Fields["日期"] = F("2025年11月");
            var r = One(b); Require(r.ContentMatches && Has(r, "BASELINE_EMPTY") && r.ProposedChanges.Count == 0, "空基准覆盖了原值"); });
        Test("S18", "缺少目录列不判图签错误", "R15", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.OptionalFields.Add("图别"); b.Directory[0].Fields["图别"] = new FieldData { State = ReadState.NoColumn }; Require(One(b).ContentMatches && Has(One(b), "NO_BASELINE_COLUMN"), "无列被判错"); });
        Test("S19", "列未映射不是空基准", "R44", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.OptionalFields.Add("日期"); b.Directory[0].Fields["日期"] = new FieldData { State = ReadState.UnmappedColumn };
            Require(Has(One(b), "BASELINE_UNREADABLE") && !One(b).ContentMatches, "未映射被忽略"); });
        Test("S20", "未启用的可选日期不同不生成修改", "R13", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.Directory[0].Fields["日期"] = F("2026"); b.Titles[0].Fields["日期"] = F("2025"); Require(One(b).ContentMatches, "未选字段被覆盖"); });
        Test("S21", "已启用且有明确基准的版本不同需提示", "R13", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.OptionalFields.Add("版本号"); b.Directory[0].Fields["版本号"] = F("1"); b.Titles[0].Fields["版本号"] = F("0");
            Require(One(b).ProposedChanges.Single().Field == "版本号", "版本差异漏掉"); });
        Test("S22", "唯一图名产生错误图号修正候选", "R27", "synthetic", delegate { Require(One(B("不存在的编号", "生态旱溪详图")).Association == "ByNamePendingConfirmation", "没有按名称候选"); });
        Test("S23", "号名冲突禁止批量确认", "R28", "synthetic", delegate { var b = Conflict(); var r = RuleEngine.Evaluate(b); Require(Has(r.Titles[0], "NUMBER_NAME_CONFLICT"), "未查冲突"); Reject(delegate { RuleEngine.ConfirmSelected(r, new[] { "t1" }); }); });
        Test("S24", "目录同名产生多个候选，不取第一条", "R29", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); b.Directory.Add(D("r2", "A-002", "生态旱溪详图")); Require(One(b).Association == "Ambiguous" && !One(b).EligibleForBatchConfirmation, "同名误配"); });
        Test("S25", "目录重复图号阻止相关确认", "R31", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.Directory.Add(D("r2", "A-001", "另一图")); Require(!One(b).EligibleForBatchConfirmation && Has(One(b), "MULTIPLE_CANDIDATES"), "重复号自动关联"); });
        Test("S26", "图签重复图号单独报告", "R32", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.Titles.Add(T("t2", "A-001", "生态旱溪详图")); var r = RuleEngine.Evaluate(b); Require(r.Titles.All(x => Has(x, "DUPLICATE_TITLE_NUMBER") && !x.ContentMatches), "图纸重复号漏掉"); });
        Test("S27", "两个空号图签争用同一目录行不批量补同号", "R49", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); b.Titles.Add(T("t2", "", "生态旱溪详图")); Require(RuleEngine.Evaluate(b).Titles.All(r => Has(r, "DIRECTORY_ROW_CLAIMED_TWICE") && !r.EligibleForBatchConfirmation), "生成重复补号"); });
        Test("S28", "同文件两个图签独立核对", "R09", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.Directory.Add(D("r2", "A-002", "另一图")); b.Titles.Add(T("t2", "A-002", "另一图")); b.Titles[1].File = b.Titles[0].File;
            Require(RuleEngine.Evaluate(b).Titles.Count(r => r.ContentMatches) == 2, "同文件被合并"); });
        Test("S29", "号名都空，不用文件名或顺序补号", "R30/R51", "synthetic", delegate {
            var b = B("", ""); b.Titles[0].File = "A-001 生态旱溪详图.dwg"; var r = One(b); Require(r.Association == "Unmatched" && r.ProposedChanges.Count == 0, "按文件名或顺序猜测"); });
        Test("S30", "目录基准未确认时拒绝执行规则", "R07", "synthetic", delegate { var b = B("A-001", "生态旱溪详图"); b.BaselineConfirmed = false; Reject(delegate { RuleEngine.Evaluate(b); }); });
        Test("S31", "扫描失败不拖累其他文件，也不伪装通过", "R36", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); var t = T("t2", "", ""); t.ScanFailed = true; b.Titles.Add(t); var r = RuleEngine.Evaluate(b); Require(r.Titles.Count(x => x.ContentMatches) == 1 && Has(r.Titles[1], "SCAN_FAILED"), "失败吞掉或中断全批"); });
        Test("S32", "不允许将人员等范围外字段混入校对", "R14", "synthetic", delegate { var b = B("A-001", "生态旱溪详图"); b.OptionalFields.Add("设计"); Reject(delegate { RuleEngine.Evaluate(b); }); });
        Test("S33", "20张空号中18唯一候选，2争用项保持待处理", "R49/R50", "synthetic", delegate {
            var r = RuleEngine.Evaluate(Twenty()); Require(r.Titles.Count(x => x.EligibleForBatchConfirmation) == 18, "明确候选数错误"); Require(r.Titles.Skip(18).All(x => !x.EligibleForBatchConfirmation), "冲突混入批量"); });
        Test("S34", "批量确认18项只产生补号预览，原几何原样保留", "R50/R51/R52", "synthetic", delegate {
            var b = Twenty(); string before = Json.Serialize(b); var r = RuleEngine.Evaluate(b); var decisions = RuleEngine.ConfirmSelected(r, r.Titles.Where(x => x.EligibleForBatchConfirmation).Select(x => x.TitleId));
            var plans = RuleEngine.PreviewConfirmed(b, decisions); Require(plans.Count == 18 && plans.All(p => p.Changes.Count == 1 && p.Changes[0].Field == RuleEngine.Number), "修改了图号以外字段");
            Require(plans.All(p => p.OriginalGeometrySnapshot == b.Titles.Single(t => t.Id == p.TitleId).GeometrySnapshot), "几何快照被归一化"); Require(before == Json.Serialize(b), "确认或预览改动输入"); });
        Test("S35", "混合选择含冲突项时整次确认拒绝，不部分确认", "R50", "synthetic", delegate { var r = RuleEngine.Evaluate(Twenty()); Reject(delegate { RuleEngine.ConfirmSelected(r, new[] { "t1", "t19" }); }); });
        Test("S36", "只选择两项时计划仅含这两项", "R50/R52", "synthetic", delegate {
            var b = Twenty(); var r = RuleEngine.Evaluate(b); var p = RuleEngine.PreviewConfirmed(b, RuleEngine.ConfirmSelected(r, new[] { "t3", "t8" })); Require(p.Count == 2 && p.Any(x => x.TitleId == "t3") && p.Any(x => x.TitleId == "t8"), "写入未选择项目"); });
        Test("S37", "保留原值和暂不处理均不产生计划，差异不消失", "R41", "synthetic", delegate {
            var b = B("A-001", "旧名"); var r = RuleEngine.Evaluate(b);
            foreach (var choice in new[] { UserChoice.KeepOriginal, UserChoice.Defer }) Require(RuleEngine.PreviewConfirmed(b, new[] { new ConfirmedDecision { TitleId = "t1", SnapshotToken = r.SnapshotToken, Choice = choice } }).Count == 0, "保留/跳过生成修改");
            Require(!r.Titles[0].ContentMatches && Has(r.Titles[0], "CONTENT_DIFFERENT"), "人工决定掩盖差异"); });
        string[] changes = { "baseline", "directory", "title", "geometry", "membership" };
        for (int i = 0; i < changes.Length; i++)
        {
            string change = changes[i];
            Test("S" + (38 + i), "确认后" + change + "变化使旧确认失效", "R52", "synthetic", delegate {
                var b = B("", "生态旱溪详图"); var r = RuleEngine.Evaluate(b); var ds = RuleEngine.ConfirmSelected(r, new[] { "t1" });
                if (change == "baseline") b.BaselineRevision = "base-2";
                if (change == "directory") b.Directory[0].Fields[RuleEngine.Number] = F("A-002");
                if (change == "title") b.Titles[0].Fields[RuleEngine.Name] = F("新名");
                if (change == "geometry") b.Titles[0].GeometrySnapshot += "changed";
                if (change == "membership") b.Titles.Add(T("t2", "", "生态旱溪详图"));
                Reject(delegate { RuleEngine.PreviewConfirmed(b, ds); }); });
        }
        Test("S43", "人工绑定冲突后只建议修改被选目录所需字段", "R28/R41", "synthetic", delegate {
            var b = Conflict(); b.ManualBindings["t1"] = "r2"; var r = One(b); Require(r.Association == "ManualBound" && r.ProposedChanges.Single().Field == RuleEngine.Number && r.ProposedChanges.Single().Proposed == "A-002", "人工关联误改图名"); });
        Test("S44", "目标目录号为空时不生成空号覆盖", "R16/R49", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); b.Directory[0].Fields[RuleEngine.Number] = F(""); Require(!One(b).EligibleForBatchConfirmation && One(b).ProposedChanges.Count == 0, "空目标被采纳"); });
        Test("S45", "重复内部标识拒绝，防止同文件对象覆盖", "R43", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.Titles.Add(T("t1", "A-002", "别图")); Reject(delegate { RuleEngine.Evaluate(b); }); });
        Test("S46", "伪造或复用不确定项确认不能通过复核", "R28/R52", "synthetic", delegate {
            var b = Conflict(); var r = RuleEngine.Evaluate(b); Reject(delegate { RuleEngine.PreviewConfirmed(b, new[] { new ConfirmedDecision { TitleId = "t1", SnapshotToken = r.SnapshotToken, Choice = UserChoice.UseDirectory } }); }); });
        Test("S47", "空目录不能作为已准备好的核对基准", "R06/R07", "synthetic", delegate {
            var b = B("A-001", "生态旱溪详图"); b.Directory.Clear(); Reject(delegate { RuleEngine.Evaluate(b); }); });
        Test("S48", "批量补号不顺带修改已启用但未确认的版本差异", "R50/R51/R52", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); b.OptionalFields.Add("版本号"); b.Directory[0].Fields["版本号"] = F("2"); b.Titles[0].Fields["版本号"] = F("1");
            var r = RuleEngine.Evaluate(b); Require(r.Titles[0].ProposedChanges.Count == 2, "测试未覆盖两字段差异");
            var plans = RuleEngine.PreviewConfirmed(b, RuleEngine.ConfirmNumbers(r, new[] { "t1" })); Require(plans.Single().Changes.Count == 1 && plans.Single().Changes.Single().Field == RuleEngine.Number, "补号顺带覆盖了版本"); });
        Test("S49", "未知人工决定不能被默认为跳过", "R41", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); var r = RuleEngine.Evaluate(b); Reject(delegate { RuleEngine.PreviewConfirmed(b, new[] { new ConfirmedDecision { TitleId = "t1", SnapshotToken = r.SnapshotToken, Choice = (UserChoice)99 } }); }); });
        Test("S50", "缺失字段级确认不能生成计划", "R52", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); var r = RuleEngine.Evaluate(b); Reject(delegate { RuleEngine.PreviewConfirmed(b, new[] { new ConfirmedDecision { TitleId = "t1", SnapshotToken = r.SnapshotToken, Choice = UserChoice.UseDirectory } }); }); });
        Test("S51", "人工绑定不存在目录行时明确拒绝", "R29/R43", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); b.ManualBindings["t1"] = "not-a-row"; Reject(delegate { RuleEngine.Evaluate(b); }); });
        Test("S52", "重复选择不能生成多份相同确认", "R50", "synthetic", delegate {
            var b = B("", "生态旱溪详图"); var r = RuleEngine.Evaluate(b); Reject(delegate { RuleEngine.ConfirmSelected(r, new[] { "t1", "t1" }); }); });
        string resultPath = Path.Combine(args[1], "test_results.json");
        File.WriteAllText(resultPath, Json.Serialize(new { passed = Results.Count - failed, failed, assertions, tests = Results, nativeTests = 10, syntheticTests = Results.Count - 10 }), Encoding.UTF8);
        Console.WriteLine("Tests: {0}, passed: {1}, failed: {2}, assertions: {3}", Results.Count, Results.Count - failed, failed, assertions);
        foreach (object r in Results) Console.WriteLine(Json.Serialize(r));
        return failed == 0 ? 0 : 1;
    }
}
