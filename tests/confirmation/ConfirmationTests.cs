using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cad100.DirectoryChecking;
public static class ConfirmationTests
{
    static int checks; static List<string> labels=new List<string>();
    static JavaScriptSerializer J=new JavaScriptSerializer { MaxJsonLength=int.MaxValue };
    static BindingFlags F=BindingFlags.NonPublic|BindingFlags.Instance;
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static object Prop(object o,string n){return o.GetType().GetProperty(n).GetValue(o,null);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,F).Invoke(o,args);}
    static void Need(bool b,string label){if(!b)throw new Exception(label);checks++;labels.Add(label);Console.WriteLine("PASS "+label);}
    static void Reject(Action a,string label){bool failed=false;try{a();}catch(InvalidOperationException){failed=true;}catch(ArgumentException){failed=true;}Need(failed,label);}
    static void Capture(FrameworkElement page,string file){page.Measure(new Size(1480,980));page.Arrange(new Rect(0,0,1480,980));page.UpdateLayout();var bmp=new RenderTargetBitmap(1480,980,96,96,PixelFormats.Pbgra32);bmp.Render(page);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(bmp));using(var f=File.Create(file))e.Save(f);}
    [STAThread] public static int Main(string[] args)
    {try{
        Directory.CreateDirectory(args[2]);var app=new Application();var assembly=Assembly.LoadFrom(args[0]);
        var page=(FrameworkElement)Activator.CreateInstance(assembly.GetType("Drawing100WpfPreview.DirectoryCheckingPage",true),true);
        var input=J.Deserialize<BatchInput>(File.ReadAllText(args[1]));
        var session=new DirectoryScanSession {Input=input,Result=RuleEngine.Evaluate(input),Files=new List<ScanFileData>(),RunDirectory=Path.GetFullPath(args[2])};
        Call(page,"AcceptScan",session);var grid=(DataGrid)Field(page,"grid");
        ((TextBox)Field(page,"folderBox")).Text=Path.GetDirectoryName(input.Directory.First().File);
        var initialOption=Activator.CreateInstance(assembly.GetType("Drawing100WpfPreview.BaselineOption",true),true);
        initialOption.GetType().GetProperty("Path").SetValue(initialOption,input.Directory.First().File,null);
        initialOption.GetType().GetProperty("Label").SetValue(initialOption,Path.GetFileName(input.Directory.First().File),null);
        var initialBaseline=(ComboBox)Field(page,"baseline");initialBaseline.ItemsSource=new[]{initialOption};initialBaseline.SelectedIndex=0;
        Func<BatchDraft> draft=()=> (BatchDraft)Field(page,"draft");Func<BatchOutputPreview> preview=()=>BatchPreview.Preview(input,draft());
        Func<string,string> metric=n=>((TextBlock)Field(page,n)).Text;
        Func<string,object> row=hint=>grid.Items.Cast<object>().Single(x=>((string)Prop(x,"Id")).Contains(hint));
        Need(draft().SchemaVersion==2,"new draft explicitly uses manual-review schema");
        Need(preview().Plans.Count==0 && preview().Rows.Count(r=>r.Selected)==0,"scan never silently approves a modification");
        Need(preview().Pending==6 && metric("reviewMetric")=="6" && metric("readyMetric")=="4","initial six differences all await review, four matching rows ready");
        Need(grid.Items.Cast<object>().Count(x=>(string)Prop(x,"Tone")=="yellow")==6,"yellow status count equals pending metric");
        Need(grid.Columns.Count==5 && grid.Columns.Last() is DataGridTemplateColumn && Convert.ToString(grid.Columns.Last().Header)=="人工核对","confirmation buttons occupy far-right former details position");
        Need(!grid.Columns.OfType<DataGridCheckBoxColumn>().Any(),"left checkbox column removed");
        Need(grid.RowStyle.Triggers.Count==1 && ((SolidColorBrush)((Setter)grid.RowStyle.Setters.Cast<Setter>().Single(s=>s.Property==Control.ForegroundProperty)).Value).Color==Colors.White,"row text stays white; only the blocked-state trigger may tint background");
        Need(grid.Columns.OfType<DataGridTextColumn>().Where(c=>Convert.ToString(c.Header)!="核对结果").All(c=>!c.ElementStyle.Setters.OfType<Setter>().Any(s=>s.Property==TextBlock.ForegroundProperty)),"only result text column has per-row color binding");
        Need(preview().Rows.Single(r=>r.TitleId.Contains("1生态旱溪")).OutputNumber=="L-P-FP-S2-001","unconfirmed row still shows useful output proposal");
        var matching=grid.Items.Cast<object>().First(x=>(string)Prop(x,"Status")=="一致");Need((string)Prop(matching,"ConfirmText")=="已确认" && !(bool)Prop(matching,"CanConfirm"),"matching row needs no manual click");
        Need(grid.Items.Cast<object>().Count(x=>(bool)Prop(x,"NeedsAttention"))==1,"only one blocked title receives whole-row yellow background");
        Need(grid.Items.Cast<object>().Where(x=>((string)Prop(x,"Id")).Contains("1材料表") || ((string)Prop(x,"Id")).Contains("1生态旱溪")).All(x=>!(bool)Prop(x,"NeedsAttention")),"ordinary unconfirmed proposals keep result-only coloring");
        Need(((DataTrigger)grid.RowStyle.Triggers[0]).Setters.OfType<Setter>().All(x=>x.Property==Control.BackgroundProperty),"attention trigger changes background only, never row text");
        Need(grid.CellStyle.Triggers.OfType<MultiDataTrigger>().Any(),"selected blocked row retains yellow highlight instead of blue masking it");
        Need(grid.SelectedItem==null,"automatic yellow check starts with no row selected");
        Capture(page,Path.Combine(args[2],"U1_3_no_click.png"));
        var roadContainer=grid.ItemContainerGenerator.ContainerFromItem(row("1标准园路")) as DataGridRow;
        var materialContainer=grid.ItemContainerGenerator.ContainerFromItem(row("1材料表")) as DataGridRow;
        Need(roadContainer!=null && roadContainer.Background.ToString()=="#E048341B","unselected blocked row actually renders batch amber immediately after scan");
        Need(materialContainer!=null && materialContainer.Background.ToString()=="#FF171D22","unselected ordinary difference actually keeps dark background");
        Need(grid.ReadLocalValue(DataGrid.RowBackgroundProperty)==DependencyProperty.UnsetValue && grid.ReadLocalValue(DataGrid.AlternatingRowBackgroundProperty)==DependencyProperty.UnsetValue,"no local grid background overrides row triggers");
        Need(roadContainer.Foreground.ToString()=="#FFFFFFFF","automatic amber row keeps readable white non-status text");
        var road=row("1标准园路");Need((string)Prop(road,"ConfirmText")=="未确认" && !(bool)Prop(road,"CanConfirm"),"conflict cannot be approved with one click");
        var material=row("1材料表");Call(page,"ConfirmRowClicked",new Button{DataContext=material},new RoutedEventArgs(Button.ClickEvent));
        Need(preview().Plans.Count==1 && preview().Pending==5 && metric("readyMetric")=="5","single-row confirmation admits exactly one plan and updates counts");
        Need((string)Prop(row("1材料表"),"ConfirmText")=="已确认" && (string)Prop(row("1材料表"),"Tone")=="normal","confirmed row no longer uses pending yellow");
        Need(((string)Prop(row("1材料表"),"Status")).Contains("待补图名") && ((string)Prop(row("1材料表"),"Status")).Contains("已确认"),"confirmation preserves difference instead of claiming content already matches");
        Call(page,"ConfirmRowClicked",new Button{DataContext=row("1材料表")},new RoutedEventArgs(Button.ClickEvent));
        Need(preview().Plans.Count==0 && preview().Pending==6,"click confirmed again revokes approval and removes plan");
        Call(page,"ConfirmClearRows");Need(preview().Plans.Count==5 && preview().Pending==1 && metric("readyMetric")=="9","one bulk action confirms only five clear rows");
        Need(!preview().Rows.Single(r=>r.TitleId.Contains("1标准园路")).Selected,"bulk confirmation never guesses unresolved conflict");
        Need(grid.Items.Cast<object>().Count(x=>(string)Prop(x,"Tone")=="yellow")==1,"after bulk, exactly one yellow pending status remains");
        grid.SelectedItem=row("1标准园路");Call(page,"ShowDetail");
        var radios=((StackPanel)Field(page,"candidateChoices")).Children.OfType<RadioButton>().ToList();
        string target=input.Directory.Single(d=>RuleEngine.Read(d.Fields["图号"]).Display=="L-P-DT-002").Id;
        radios.Single(b=>(string)b.Tag==target).IsChecked=true;
        Need(preview().Plans.Count==5,"candidate preview alone does not approve output");Call(page,"ApplyCandidate");
        Need(preview().Plans.Count==5 && preview().Pending==1 && (bool)Prop(row("1标准园路"),"CanConfirm"),"binding resolves association but still requires human approval");
        Need(!(bool)Prop(row("1标准园路"),"NeedsAttention"),"whole-row yellow clears after association blocker resolved, even before confirmation");
        Call(page,"ConfirmRowClicked",new Button{DataContext=row("1标准园路")},new RoutedEventArgs(Button.ClickEvent));
        Need(preview().Plans.Count==6 && preview().ChangedNames==4 && preview().ChangedNumbers==2 && preview().Pending==0,"after all approvals exact six plans comprise four names and two numbers");
        Need(metric("totalMetric")=="10" && metric("readyMetric")=="10" && metric("reviewMetric")=="0","top metrics reflect completed human review, not fake DWG output");
        Need(BatchPreview.ConfirmBatch(input,draft()).Count==6,"batch submit uses only reviewed eligible rows");
        Need(J.Deserialize<List<PlanPreview>>(File.ReadAllText(Path.Combine(args[2],"confirmed_plan.json"))).Count==0,"human review is distinct from final batch-submit record");
        var restored=J.Deserialize<BatchDraft>(J.Serialize(draft()));Need(BatchPreview.Preview(input,restored).Plans.Count==6,"manual approvals persist through JSON roundtrip");
        var legacy=J.Deserialize<BatchDraft>(J.Serialize(draft()));legacy.SchemaVersion=0;Reject(()=>BatchPreview.Preview(input,legacy),"legacy auto-selected drafts cannot restore as manual approvals");
        var changed=J.Deserialize<BatchInput>(J.Serialize(input));changed.BaselineRevision="changed";Reject(()=>BatchPreview.Preview(changed,draft()),"source snapshot change invalidates prior manual review");
        grid.SelectedItem=row("1材料表");Call(page,"ChooseCurrent",PreviewChoice.KeepOriginal);Need(preview().Plans.Count==5 && preview().Pending==0 && preview().Rows.Single(r=>r.TitleId.Contains("1材料表")).Reviewed,"explicit keep counts as reviewed without modification");
        Call(page,"ConfirmClearRows");Need(preview().Plans.Count==5,"bulk confirm preserves explicit keep decision");
        Call(page,"ChooseCurrent",PreviewChoice.Auto);Call(page,"ChooseCurrent",PreviewChoice.Defer);Need(preview().Plans.Count==5 && preview().Pending==0,"explicit skip is reviewed but never a write plan");Call(page,"ConfirmClearRows");Need(preview().Plans.Count==5,"bulk confirm preserves skip decision");Call(page,"ChooseCurrent",PreviewChoice.Auto);
        object host=Activator.CreateInstance(assembly.GetType("Drawing100WpfPreview.MainWindow",true),true);
        try{var main=(Window)host;page.Width=double.NaN;page.Height=double.NaN;host.GetType().GetField("directoryPage",F).SetValue(host,page);Call(host,"ShowPage","directory");Capture((FrameworkElement)main.Content,Path.Combine(args[2],"U1_3_reviewed.png"));Call(host,"ShowPage","batch");Call(host,"ShowPage","directory");Need(preview().Plans.Count==6,"switching pages preserves approval and plans");((Panel)Field(host,"pageHost")).Children.Clear();}finally{((Window)host).Close();}
        var fresh=BatchPreview.Create(input);page.GetType().GetField("draft",F).SetValue(page,fresh);Call(page,"RefreshRows");
        // Synthetic failure isolates error classification from review classification.
        var failed=input.Titles.Single(t=>t.Id.Contains("1材料表"));failed.ScanFailed=true;session.Result=RuleEngine.Evaluate(input);page.GetType().GetField("draft",F).SetValue(page,BatchPreview.Create(input));Call(page,"RefreshRows");
        Need(metric("failedMetric")=="1" && metric("reviewMetric")=="5" && metric("readyMetric")=="4","read failure counted separately from five yellow pending items");
        Need(!(bool)Prop(row("1材料表"),"CanConfirm") && (string)Prop(row("1材料表"),"ConfirmText")=="未确认","failed row cannot be approved as writeable");Call(page,"ConfirmClearRows");Need(preview().Plans.Count==5 && metric("failedMetric")=="1" && metric("reviewMetric")=="0","bulk confirms safe rows while failure remains red");
        Need(grid.RowStyle.Triggers.Count==1,"read failure does not inherit a whole-row text color");
        // Capture the initial user-visible pending state using original recorded input.
        var original=J.Deserialize<BatchInput>(File.ReadAllText(args[1]));session.Input=original;session.Result=RuleEngine.Evaluate(original);Call(page,"AcceptScan",session);grid.SelectedItem=grid.Items.Cast<object>().First(x=>((string)Prop(x,"Id")).Contains("1标准园路"));Call(page,"ShowDetail");
        ((TextBox)Field(page,"folderBox")).Text=Path.GetDirectoryName(original.Directory.First().File);
        Need(metric("totalMetric")=="10" && metric("reviewMetric")=="6", "pending screenshot retains real populated scan results");
        host=Activator.CreateInstance(assembly.GetType("Drawing100WpfPreview.MainWindow",true),true);
        try{page.Width=double.NaN;page.Height=double.NaN;host.GetType().GetField("directoryPage",F).SetValue(host,page);Call(host,"ShowPage","directory");Capture((FrameworkElement)((Window)host).Content,Path.Combine(args[2],"U1_3_pending.png"));((Panel)Field(host,"pageHost")).Children.Clear();}finally{((Window)host).Close();}
        File.WriteAllText(Path.Combine(args[2],"confirmation_test_results.json"),J.Serialize(new{passed=checks,failed=0,scope="Native diagnostic replay, model and real WPF handlers; no native CAD scan or DWG writing",labels}));Console.WriteLine("Confirmation checks passed: "+checks);app.Shutdown();return 0;
    }catch(Exception e){Console.WriteLine(e.ToString());return 1;}}
}
