using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Cad100.DirectoryChecking;
class WriteTests
{
 static int count; static List<string> labels = new List<string>();
 static JavaScriptSerializer json=new JavaScriptSerializer { MaxJsonLength=int.MaxValue };
 static void Need(bool ok,string text){if(!ok)throw new Exception(text);count++;labels.Add(text);Console.WriteLine("PASS "+text);}
 static void Reject(Action action,string text){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}catch(ArgumentException){rejected=true;}catch(InvalidDataException){rejected=true;}Need(rejected,text);}
 static DirectoryScanSession Session(string path)
 {
  var input=json.Deserialize<BatchInput>(File.ReadAllText(path));input.OptionalFields.Clear();
  var files=new List<ScanFileData>();
  foreach(var group in input.Titles.GroupBy(t=>t.File)){
   var file=new ScanFileData{File=group.Key,Hash="source-hash"};files.Add(file);
   foreach(var t in group){t.SourceRevision=file.Hash;
    file.Entities.Add(new CadEntity{Type="INSERT",Handle=t.InsertHandle,Geometry=t.GeometrySnapshot});
    foreach(var f in t.Fields.Values.Where(f=>!string.IsNullOrEmpty(f.AttributeHandle)))
     file.Entities.Add(new CadEntity{Type="ATTRIB",Handle=f.AttributeHandle,Parent=t.InsertHandle,Tag="field",Ordinary=f.OrdinaryRaw??"",HasEmbedded=f.EmbeddedPresent,Embedded=f.EmbeddedRaw??""});
   }
  }
  return new DirectoryScanSession{Input=input,Files=files,Result=RuleEngine.Evaluate(input)};
 }
 static void Main(string[] args)
 {
  string input=args[0], qa=args[1];Directory.CreateDirectory(qa);
  var s=Session(input);var draft=BatchPreview.Create(s.Input);
  Need(DwgDirectoryWriter.Preflight(s,draft).Files.All(f=>f.State=="跳过"),"未确认时没有可写文件");
  Need(BatchPreview.ConfirmAllClear(s.Input,draft)==5,"明确项目只在人工批量确认后纳入");
  var plan=DwgDirectoryWriter.Preflight(s,draft);
  Need(plan.Files.Count(f=>f.State=="待写入")==5,"五个明确文件通过预检，冲突文件保留");
  Need(plan.Files.Where(f=>f.State=="待写入").All(f=>f.Patches.Count>0),"每个可写文件都有确定属性句柄");
  var title=s.Input.Titles.Single(t=>t.Id.Contains("1标准园路"));
  var row=s.Input.Directory.Single(d=>RuleEngine.Read(d.Fields[RuleEngine.Number]).Display=="L-P-DT-002");
  s.Input.ManualBindings[title.Id]=row.Id;draft=BatchPreview.Create(s.Input);BatchPreview.ConfirmAllClear(s.Input,draft);
  Need(DwgDirectoryWriter.Preflight(s,draft).Files.Count(f=>f.State=="待写入")==6,"人工绑定并确认后六个文件可写");
  Need(DwgDirectoryWriter.PreserveFormat("\\W0.8000;\\H1.2x;旧字","新字")=="\\W0.8000;\\H1.2x;新字","保留原内嵌字宽和字高格式，不叠加格式");
  Need(DwgDirectoryWriter.PreserveFormat("\\W0.8;旧字","A{B}\\C")=="\\W0.8;A\\{B\\}\\\\C","新文字按字面转义富文本控制字符");
  foreach(string old in new[]{"一\\P二","{\\W0.8;旧字}","旧\\H2;字","%<field>%","\\FArial;旧字"})
   Reject(()=>DwgDirectoryWriter.PreserveFormat(old,"新字"),"不扁平化复杂原格式："+old);
  Reject(()=>DwgDirectoryWriter.PreserveFormat("旧字",new string('a',241)),"长文字超过试验边界时拒绝");
  Reject(()=>DwgDirectoryWriter.LispString("\ud83d\ude00"),"补充平面字符不静默损坏");
  var payload=DwgDirectoryWriter.Payload(plan.Files.First(f=>f.State=="待写入").Patches);
  Need(payload.All(c=>c<128) && payload.Contains("(chr "),"Unicode载荷是纯ASCII表达式");
  Action<string,Action<DirectoryScanSession,TitleRecord,ScanFileData>> bad=(label,mutate)=>{
   var x=Session(input);var t=x.Input.Titles.Single(z=>z.Id.Contains("1材料表"));var f=x.Files.Single(z=>z.File==t.File);
   mutate(x,t,f);var d=BatchPreview.Create(x.Input);BatchPreview.ConfirmAllClear(x.Input,d);
   var item=DwgDirectoryWriter.Preflight(x,d).Files.Single(z=>z.File==t.File);
   Need(item.State=="跳过" && item.Patches.Count==0,label);
  };
  bad("属性丢失时整个文件跳过",(x,t,f)=>f.Entities.RemoveAll(e=>e.Type=="ATTRIB"));
  bad("原图签块丢失时禁止重建",(x,t,f)=>f.Entities.RemoveAll(e=>e.Type=="INSERT"));
  bad("属性归属变化被拦截",(x,t,f)=>{foreach(var e in f.Entities)e.Parent="wrong";});
  bad("原始文字变化被拦截",(x,t,f)=>{foreach(var e in f.Entities)e.Ordinary="changed";});
  bad("源版本快照不一致被拦截",(x,t,f)=>t.SourceRevision="changed");
  bad("属性不可写时不新增替代文字",(x,t,f)=>{foreach(var v in t.Fields.Values)v.Writable=false;});
  bad("同DWG有另一未确认冲突时整体跳过",(x,t,f)=>{
   var other=x.Input.Titles.Single(z=>z.Id.Contains("1标准园路"));other.File=t.File;
  });
  // Output audits have independent positive and negative controls.
  string before=Path.Combine(qa,"before.txt"),after=Path.Combine(qa,"after.txt");
  File.WriteAllLines(before,new[]{"CAD100_PROTECTED_V1","A\tINSERT scale=150.00001811","B\tATTRIB height=3.5","DONE"});
  File.WriteAllLines(after,new[]{"CAD100_PROTECTED_V1","B\tATTRIB height=3.5","A\tINSERT scale=150.00001811","DONE"});
  DwgDirectoryWriter.VerifyInventory(before,after);Need(true,"清单顺序变化不误报");
  foreach(string badLine in new[]{"A\tINSERT scale=150","B\tATTRIB height=3.6","C\tLINE removed"}){
   File.WriteAllLines(after,new[]{"CAD100_PROTECTED_V1",badLine,"B\tATTRIB height=3.5","DONE"});
   Reject(()=>DwgDirectoryWriter.VerifyInventory(before,after),"精确变换或几何变化拦截："+badLine);
  }
  File.WriteAllLines(after,new[]{"CAD100_PROTECTED_V1","A"});Reject(()=>DwgDirectoryWriter.VerifyInventory(before,after),"部分保护清单不能验收");
  var patch=new AttributePatch{Handle="A",Parent="B",Tag="图名",Value="新图名",HasEmbedded=true,Embedded="\\W0.8;新图名"};
  var entity=new CadEntity{Type="ATTRIB",Handle="A",Parent="B",Tag="图名",Ordinary="新图名",HasEmbedded=true,Embedded="\\W0.8;新图名"};
  DwgDirectoryWriter.VerifyText(new List<CadEntity>{entity},new[]{patch});Need(true,"重新打开后普通和内嵌内容均匹配才通过");
  entity.Embedded="旧图名";Reject(()=>DwgDirectoryWriter.VerifyText(new List<CadEntity>{entity},new[]{patch}),"属性值已改但可见内嵌文字未改时拦截");
  entity.Embedded=patch.Embedded;entity.Parent="wrong";Reject(()=>DwgDirectoryWriter.VerifyText(new List<CadEntity>{entity},new[]{patch}),"复扫属性归属不一致时拦截");
  var stale=BatchPreview.Create(s.Input);s.Input.BaselineRevision="changed";Reject(()=>DwgDirectoryWriter.Preflight(s,stale),"旧人工确认不能用于新数据快照");
  File.WriteAllText(Path.Combine(qa,"writer_tests.json"),json.Serialize(new{passed=count,failed=0,scope="White-box managed preflight/format/audit tests; synthetic entities based on native recorded input; no live DWG write",labels}));
  Console.WriteLine("ALL "+count+" PASSED");
 }
}
