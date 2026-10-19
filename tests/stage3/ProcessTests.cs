using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Cad100.DirectoryChecking;
class ProcessTests {
 static int count;
 static void Need(bool value,string label){if(!value)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
 static int Main(string[] args) {try{
  string core=Path.GetFullPath(args[0]), scripts=Path.GetFullPath(args[1]), qa=Path.GetFullPath(args[2]);Directory.CreateDirectory(qa);
  CadFrameReplacer.CoreLicenseGuard.Deny=true;bool denied=false;
  try{DwgDirectoryWriter.Execute(null,null,null,null,null);}catch(UnauthorizedAccessException){denied=true;}
  Need(denied,"license guard runs before any source/file/CAD operation (isolated test guard)");CadFrameReplacer.CoreLicenseGuard.Deny=false;
  foreach(string mode in new[]{"good","exit0","exit1","verifyExit1","geometry","partial","oldVisible","external","locked","invisible"}){
   string run=Path.Combine(qa,mode),folder=Path.Combine(run,"inputs");Directory.CreateDirectory(folder);
   string file=Path.Combine(folder,"test.dwg");File.WriteAllText(file,mode);string hash=DwgDirectoryWriter.Hash(file);
   var dir=new DirectoryRow{Id="row",File=file,Fields=new Dictionary<string,FieldData>{{"图号",new FieldData{State=ReadState.Value,OrdinaryRaw="X-001"}},{"图名",new FieldData{State=ReadState.Value,OrdinaryRaw="New"}}}};
   var title=new TitleRecord{Id="title",File=file,Layout="Model",InsertHandle="B",SourceRevision=hash,GeometrySnapshot="precise original",Fields=new Dictionary<string,FieldData>{{"图号",new FieldData{State=ReadState.Value,OrdinaryRaw="X-001",AttributeHandle="C"}},{"图名",new FieldData{State=ReadState.Value,OrdinaryRaw="Old",EmbeddedPresent=true,EmbeddedRaw="\\W0.8;Old",AttributeHandle="A"}}}};
   var input=new BatchInput{BaselineConfirmed=true,BaselineRevision=hash,Directory=new List<DirectoryRow>{dir},Titles=new List<TitleRecord>{title}};
   var data=new ScanFileData{File=file,Hash=hash,Entities=new List<CadEntity>{new CadEntity{Type="INSERT",Handle="B"},new CadEntity{Type="ATTRIB",Handle="A",Parent="B",Tag="图名",Ordinary="Old",HasEmbedded=true,Embedded="\\W0.8;Old"}}};
   var session=new DirectoryScanSession{Folder=folder,RunDirectory=run,Input=input,Result=RuleEngine.Evaluate(input),Files=new List<ScanFileData>{data}};
   var draft=BatchPreview.Create(input);BatchPreview.ConfirmAllClear(input,draft);
   Need(BatchPreview.Preview(input,draft).Plans.Count==1,mode+": explicit approval before any write");
   var result=DwgDirectoryWriter.Execute(session,draft,core,scripts,Console.WriteLine);
   Need(DwgDirectoryWriter.Hash(file)==hash,mode+": source bytes remain unchanged");
   if(mode=="good"){
    Need(result.Files.Single().State=="通过",mode+": reopen and protected inventory gates pass");
    Need(Directory.GetFiles(result.OutputDirectory,"*.dwg").Length==1 && DwgDirectoryWriter.Hash(result.Files[0].Output)!=hash,"only verified updated copy is published");
   }else if(mode=="external" || mode=="locked" || mode=="invisible"){
    Need(result.Files.Single().State=="跳过",mode+": known unsupported native preflight is skipped with readable reason");
    Need(Directory.GetFiles(result.OutputDirectory,"*.dwg").Length==0,mode+": unsupported file never enters results");
   }else{
    Need(result.Files.Single().State=="失败",mode+": failed backend/audit never becomes success");
    Need(Directory.GetFiles(result.OutputDirectory,"*.dwg").Length==0,mode+": failed work copy never enters results");
   }
   Need(File.Exists(Path.Combine(result.OutputDirectory,"处理验收报告.txt")),mode+": readable result report exists");
  }
  Need(CadFrameReplacer.CoreLicenseGuard.Calls==11,"each backend execution checks the unchanged license entry contract");
  File.WriteAllText(Path.Combine(qa,"process_tests.json"),new JavaScriptSerializer().Serialize(new{passed=count,failed=0,scope="Simulated executable subprocess orchestration; .dwg files contain plain test bytes. No AutoCAD or valid DWG execution."}));
  return 0;
 }catch(Exception ex){Console.WriteLine(ex);return 1;}}
}
