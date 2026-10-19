using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Cad100.DirectoryChecking;
class WriterUiTests {
 static int count;static BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
 static object Field(object x,string n){return x.GetType().GetField(n,F).GetValue(x);}
 static object Call(object x,string n,params object[] a){return x.GetType().GetMethod(n,F).Invoke(x,a);}
 static object Prop(object x,string n){return x.GetType().GetProperty(n).GetValue(x,null);}
 static void Need(bool x,string label){if(!x)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
 [STAThread]static int Main(string[] args){try{
  var app=new Application();var assembly=Assembly.LoadFrom(args[0]);var page=(FrameworkElement)Activator.CreateInstance(assembly.GetType("Drawing100WpfPreview.DirectoryCheckingPage"),true);
  var input=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<BatchInput>(File.ReadAllText(args[1]));
  string qa=Path.GetFullPath(args[2]);Directory.CreateDirectory(qa);
  var session=new DirectoryScanSession{Input=input,Result=RuleEngine.Evaluate(input),RunDirectory=qa,Files=new List<ScanFileData>()};
  Call(page,"AcceptScan",session);var grid=(DataGrid)Field(page,"grid");
  var result=new DirectoryWriteResult{OutputDirectory=qa,Files=input.Titles.Select(t=>t.File).Distinct().Select(f=>new DirectoryWriteItem{File=f,State="跳过",Reason="没有已确认修改"}).ToList()};
  var material=result.Files.Single(f=>f.File.Contains("1材料表"));material.State="失败";material.Reason="输出复扫文字不一致";
  var pass=result.Files.Single(f=>f.File.Contains("L-P-DT-005"));pass.State="通过";pass.Reason="重新打开验收通过";
  page.GetType().GetField("writeResult",F).SetValue(page,result);Call(page,"RefreshRows");
  Need(grid.Items.Cast<object>().All(r=>!(bool)Prop(r,"CanConfirm")),"处理结果冻结本轮确认按钮，避免重复提交");
  Need(((TextBlock)Field(page,"summaryHint")).Text.Contains("通过 1，跳过 7，失败 1"),"处理完成按文件明确展示通过跳过失败数量");
  Need(((TextBlock)Field(page,"totalMetric")).Text=="9" && ((TextBlock)Field(page,"readyMetric")).Text=="1" && ((TextBlock)Field(page,"reviewMetric")).Text=="7" && ((TextBlock)Field(page,"failedMetric")).Text=="1","完成状态的四个统计按文件相加等于总数，不混用图签数量");
  var failed=grid.Items.Cast<object>().Single(r=>((string)Prop(r,"Id")).Contains("1材料表"));
  Need(((string)Prop(failed,"Status")).StartsWith("失败"),"失败项目在列表中保持明确失败标记");
  var old=input.Titles.Single(t=>t.File==material.File);Need((string)Prop(failed,"Name")==RuleEngine.Read(old.Fields[RuleEngine.Name]).Display,"失败行不把计划文字冒充已保存文字");
  grid.SelectedItem=failed;Call(page,"ShowDetail");Need(((TextBlock)Field(page,"resultText")).Text.Contains("输出复扫文字不一致"),"点击整行可直接查看失败原因");
  bool blocked=false;try{Call(page,"ConfirmClearRows");}catch(TargetInvocationException e){blocked=e.InnerException is InvalidOperationException;}Need(blocked,"批量确认不能修改已结束轮次");
  blocked=false;try{Call(page,"ChooseCurrent",PreviewChoice.Auto);}catch(TargetInvocationException e){blocked=e.InnerException is InvalidOperationException;}Need(blocked,"单行修改不能污染已发布结果");
  Call(page,"AcceptScan",session);Need(Field(page,"writeResult")==null,"重新扫描清除旧成果状态");
  Need(((TextBlock)Field(page,"summaryHint")).Text.Contains("尚未写入"),"新扫描恢复为未写入预览，不沿用上轮成功提示");
  File.WriteAllText(Path.Combine(qa,"writer_ui_tests.json"),new JavaScriptSerializer().Serialize(new{passed=count,failed=0,scope="Actual WPF result handlers with simulated outcome records; no DWG processing"}));app.Shutdown();return 0;
 }catch(Exception e){Console.WriteLine(e);return 1;}}
}
