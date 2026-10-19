using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Cad100.DirectoryChecking;

namespace Drawing100WpfPreview
{
    internal sealed class DirectoryGridRow
    {


        public bool Eligible { get; set; }
        public string Tone { get; set; }
        public string ConfirmText { get; set; }
        public bool CanConfirm { get; set; }
        public bool Reviewed { get; set; }
        public bool NeedsAttention { get; set; }
        public bool Selected { get; set; }
        public Brush StatusBrush { get; set; }
        public string Id { get; set; }
        public string File { get; set; }
        public string Layout { get; set; }
        public string Number { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
    }
    internal sealed class BaselineOption
    {
        public string Path { get; set; }
        public string Label { get; set; }
        public override string ToString() { return Label; }
    }
    internal sealed class DirectoryCheckingPage : UserControl
    {
        readonly TextBox folderBox = Box();
        readonly ComboBox baseline = new ComboBox { MinWidth = 250, FontSize = 13, Margin = new Thickness(4), Height = 30 };
        readonly TextBlock status = Text("请选择文件夹；本版仅核对，不写入DWG。", 13);
        readonly TextBlock detailTitle = Text("查看差异｜请选择黄标或红标项目", 17);
        readonly TextBlock originalText = Text("—", 14), outputText = Text("—", 14), resultText = Text("", 13);
        readonly TextBlock choiceHint = Text("确认本行的处理方式", 12);
        readonly StackPanel candidateChoices = new StackPanel();
        readonly StackPanel rightChoices = new StackPanel();
        readonly ComboBox otherRows = new ComboBox { FontSize = 12, MaxWidth = 430, Margin = new Thickness(0, 4, 0, 4) };
        readonly Expander otherRowsExpander = new Expander { Header = "其他目录行", Foreground = Brushes.White, FontSize = 12 };
        string candidateRowId;
        readonly DataGrid grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, FontSize = 13, RowHeight = 34, HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 8, 0, 8) };
        readonly List<Button> controls = new List<Button>();
        readonly List<Border> stepCards = new List<Border>();
        readonly TextBlock summaryHint = Text("选择目录基准，扫描并核对整套图纸", 12);
        TextBlock totalMetric, readyMetric, reviewMetric, failedMetric;
        readonly ProgressBar scanProgress = MainWindow.CreateUiProgressBar();
        readonly TextBlock progressLabel = Text("0%", 18);
        readonly Dictionary<string, ConfirmedDecision> decisions = new Dictionary<string, ConfirmedDecision>();
        readonly List<CheckBox> optionalChecks = new List<CheckBox>();
        DirectoryScanSession session;
        DirectoryWriteResult writeResult;
        BatchDraft draft;
        List<DirectoryGridRow> rows = new List<DirectoryGridRow>();
        Button bindButton, participateButton;
        bool busy;
        internal bool IsBusy { get { return busy; } }
        static Brush B(string s) { return (Brush)new BrushConverter().ConvertFromString(s); }
        static TextBox Box() { return new TextBox { FontSize = 13, Padding = new Thickness(5), Margin = new Thickness(4), Height = 30 }; }
        static TextBlock Text(string s, double size) { return new TextBlock { Text = s, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; }
        Button Action(string text, Action action)
        {
            var b = MainWindow.OutlineButton(text, double.NaN, 30); b.Margin = new Thickness(0, 0, 8, 0); b.Padding = new Thickness(10, 3, 10, 3);
            b.Style = DarkButtonStyle();
            b.Click += delegate { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "目录校对", MessageBoxButton.OK, MessageBoxImage.Warning); } };
            controls.Add(b); return b;
        }
        public DirectoryCheckingPage()
        {
            Background = B("#101418");

            // Like batch processing, let the row style control Background.
            // A local DataGrid.RowBackground overrides DataGridRow style triggers via WPF transfer coercion.
            grid.Background = B("#171D22"); grid.Foreground = Brushes.White;
            var selectedCells = new Style(typeof(DataGridCell));
            selectedCells.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            selectedCells.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var selectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, B("#24425C")));
            selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            selectedCells.Triggers.Add(selectedTrigger);
            var selectedAttention = new MultiDataTrigger();
            selectedAttention.Conditions.Add(new Condition(new Binding("IsSelected") { RelativeSource = new RelativeSource(RelativeSourceMode.Self) }, true));
            selectedAttention.Conditions.Add(new Condition(new Binding("NeedsAttention"), true));
            selectedAttention.Setters.Add(new Setter(Control.BackgroundProperty, B("#604A27"))); selectedCells.Triggers.Add(selectedAttention);
            grid.CellStyle = selectedCells;
            grid.ColumnHeaderStyle = MainWindow.HeaderStyle(); var neutralRows = new Style(typeof(DataGridRow)); neutralRows.Setters.Add(new Setter(Control.BackgroundProperty, B("#171D22"))); neutralRows.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); neutralRows.Setters.Add(new Setter(Control.BorderBrushProperty, B("#293138")));
            var attention = new DataTrigger { Binding = new Binding("NeedsAttention"), Value = true };
            attention.Setters.Add(new Setter(Control.BackgroundProperty, B("#E048341B"))); neutralRows.Triggers.Add(attention); grid.RowStyle = neutralRows;
            grid.RowHeight = 27; grid.ColumnHeaderHeight = 30; grid.FontSize = 11;
            grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; grid.BorderThickness = new Thickness(0);
            var root = new Grid { Margin = new Thickness(18) };
            foreach (var h in new[] { new GridLength(94), new GridLength(82), new GridLength(38), new GridLength(34), new GridLength(1, GridUnitType.Star), new GridLength(225), new GridLength(42) }) root.RowDefinitions.Add(new RowDefinition { Height = h });
            root.Children.Add(BuildSummary());
            var cards = new Grid { Margin = new Thickness(0, 0, 0, 8) }; for (int i = 0; i < 4; i++) { cards.ColumnDefinitions.Add(new ColumnDefinition()); if (i < 3) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); }
            string[] titles = { "选择文件夹", "扫描图纸", "开始处理", "打开输出文件夹" };
            string[] hints = { "只选择待核对目录", "读取目录与图签", "更新副本并复扫验收", "查看成果与验收报告" };
            Action[] actions = { ChooseFolder, StartScan, ConfirmBatchPlan, OpenReport };
            for (int i = 0; i < 4; i++)
            {
                var card = MainWindow.ActionCard((i + 1).ToString(), titles[i], hints[i], false); int index = i;
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.MouseLeftButtonUp += delegate { if (busy) return; try { actions[index](); } catch (Exception ex) { MessageBox.Show(ex.Message, "目录校对"); } };
                Grid.SetColumn(card, i * 2); cards.Children.Add(card); stepCards.Add(card);
            }
            Grid.SetRow(cards, 1); root.Children.Add(cards);
            var inputs = new Grid(); inputs.ColumnDefinitions.Add(new ColumnDefinition()); inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            folderBox.Background = B("#202A32"); folderBox.Foreground = Brushes.White; folderBox.BorderBrush = B("#354654");
            folderBox.IsReadOnly = true; folderBox.ToolTip = "只检查当前层DWG，不扫描子文件夹"; inputs.Children.Add(folderBox);
            var baselines = new StackPanel { Orientation = Orientation.Horizontal }; baselines.Children.Add(Text("目录基准：", 13)); baselines.Children.Add(baseline); Grid.SetColumn(baselines, 1); inputs.Children.Add(baselines);
            Grid.SetRow(inputs, 2); root.Children.Add(inputs); Grid.SetRow(status, 3); root.Children.Add(status);
            AddColumn("文件", "File", 2); AddColumn("准备输出图号", "Number", 1); AddColumn("准备输出图名", "Name", 1.4); AddColumn("核对结果", "Status", 1.2); AddConfirmationColumn();
            grid.SelectionChanged += delegate { ShowDetail(); }; Grid.SetRow(grid, 4); root.Children.Add(grid);
            var differencePanel = BuildDifferencePanel(); Grid.SetRow(differencePanel, 5); root.Children.Add(differencePanel);
            var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            footer.Children.Add(Text("运行进度", 12)); Grid.SetColumn(scanProgress, 1); footer.Children.Add(scanProgress);
            progressLabel.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(progressLabel, 2); footer.Children.Add(progressLabel);
            var confirmAll = Action("确认全部明确项", ConfirmClearRows); Grid.SetColumn(confirmAll, 3); footer.Children.Add(confirmAll);
            var restore = Action("恢复保存的预览", RestoreDraft); Grid.SetColumn(restore, 4); footer.Children.Add(restore);
            Grid.SetRow(footer, 6); root.Children.Add(footer);
            Content = root;
            baseline.SelectionChanged += delegate
            {
                if (session == null || busy) return;
                var option = baseline.SelectedItem as BaselineOption;
                if (option == null || !string.Equals(option.Path, session.Input.Directory.First().File, StringComparison.OrdinalIgnoreCase))
                { writeResult = null; session = null; draft = null; decisions.Clear(); grid.ItemsSource = null; ClearDetail(); ResetMetrics(); scanProgress.Value = 0; progressLabel.Text = "0%"; status.Text = "目录基准已改变，请重新扫描；旧决定不会沿用。"; }
            };
        }
        UIElement BuildSummary()
        {
            var card = MainWindow.Card(new Thickness(0, 0, 0, 10), new CornerRadius(6));
            var g = new Grid { Margin = new Thickness(18, 8, 18, 8) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.72, GridUnitType.Star) });
            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; title.Children.Add(MainWindow.T("目录校对", 25, Colors.White, FontWeights.Bold)); title.Children.Add(summaryHint); g.Children.Add(title);
            totalMetric = MainWindow.AddMetric(g, 1, "图签总数", "0", Colors.White);
            readyMetric = MainWindow.AddMetric(g, 2, "可处理", "0", Colors.White); readyMetric.ToolTip = "包括无需修改的一致项与已人工确认的项目；仅确认按目录修改的项目进入修改计划。";
            reviewMetric = MainWindow.AddMetric(g, 3, "待人工核对", "0", (Color)ColorConverter.ConvertFromString("#F0AD42"));
            failedMetric = MainWindow.AddMetric(g, 4, "处理失败", "0", (Color)ColorConverter.ConvertFromString("#E75555")); failedMetric.ToolTip = "本阶段为读取失败项数量；尚未执行 DWG 写入。";
            card.Child = g; return card;
        }
        void ResetMetrics()
        { MetricLabel(totalMetric, "本次图签"); MetricLabel(readyMetric, "可处理"); MetricLabel(reviewMetric, "待人工核对"); MetricLabel(failedMetric, "处理失败"); totalMetric.Text = readyMetric.Text = reviewMetric.Text = failedMetric.Text = "0"; summaryHint.Text = "选择目录基准，扫描并核对整套图纸"; }
        void ShowScanProgress(string text)
        {
            status.Text = text;
            var match = System.Text.RegularExpressions.Regex.Match(text, @"读取\s+(\d+)/(\d+)");
            if (match.Success) { int index = int.Parse(match.Groups[1].Value), count = int.Parse(match.Groups[2].Value); scanProgress.Value = count == 0 ? 0 : (index - 1) * 100.0 / count; progressLabel.Text = ((int)scanProgress.Value) + "%"; }
            summaryHint.Text = "扫描中，请等待本轮读取完成";
        }
        void AddColumn(string header, string path, double star)
        {
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path)));
            if (path == "Status") style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding("StatusBrush")));
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), ElementStyle = style, IsReadOnly = true, MinWidth = path == "Status" ? 170 : path == "File" ? 280 : path == "Name" ? 200 : 155, Width = new DataGridLength(star, DataGridLengthUnitType.Star) });
        }
        void ChooseFolder()
        {
            if (busy) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择整套DWG所在文件夹（不扫描子文件夹）" })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) LoadFolder(dialog.SelectedPath);
            }
        }
        void LoadFolder(string path)
        {
            folderBox.Text = path;
            var files = Directory.GetFiles(path, "*.dwg", SearchOption.TopDirectoryOnly).OrderBy(p => Path.GetFileName(p).Contains("目录") ? 0 : 1).ThenBy(p => p).ToArray();
            writeResult = null; session = null; draft = null; decisions.Clear(); ClearDetail();
            baseline.ItemsSource = files.Select(p => new BaselineOption { Path = p, Label = Path.GetFileName(p) }).ToList(); baseline.SelectedIndex = -1;
            rows = files.Select(p => new DirectoryGridRow { Id = p, File = Path.GetFileName(p), Number = "—", Name = "—", Status = "待扫描", Eligible = false, Tone = "normal", ConfirmText = "待扫描", CanConfirm = false, StatusBrush = B("#A8B4BE") }).ToList();
            grid.ItemsSource = rows;
            ResetMetrics(); scanProgress.Value = 0; progressLabel.Text = "0%";
            status.Text = "已选择 " + files.Length + " 个 DWG（仅当前层），请在右侧选择目录基准，再扫描图纸。";
        }
        void AcceptScan(DirectoryScanSession scanned)
        {
            writeResult = null; session = scanned; draft = BatchPreview.Create(session.Input); ClearDetail(); RefreshRows(); SaveDraft(); scanProgress.Value = 100; progressLabel.Text = "100%";
        }
        async void StartScan()
        {
            if (busy) return;
            try
            {
                CadFrameReplacer.CoreLicenseGuard.EnsureAuthorized("directory-scan");
                var selected = baseline.SelectedItem as BaselineOption;
                if (selected == null) throw new InvalidOperationException("请先人工选择目录基准。");
                string core = DwgDirectoryScanner.FindCore();
                if (!File.Exists(core)) throw new InvalidOperationException("未检测到已安装的AutoCAD 2021及以上版本，请检查安装环境。此限制针对CAD运行环境，不是DWG保存格式。");
                string dir = folderBox.Text, file = selected.Path;
                writeResult = null; session = null; draft = null; decisions.Clear(); rows.Clear(); grid.ItemsSource = null; ClearDetail(); ResetMetrics(); scanProgress.Value = 0; progressLabel.Text = "0%"; SetBusy(true);
                string app = AppDomain.CurrentDomain.BaseDirectory;
                string[] optional = OptionalFields();
                var scanned = await Task.Run(delegate { return DwgDirectoryScanner.Scan(dir, file, core, Path.Combine(app, "engine", "scripts", "directory_scan.lsp"), Path.Combine(app, "DirectoryTrialRuns"), delegate(string text) { Dispatcher.Invoke(new Action(delegate { ShowScanProgress(text); })); }, optional); });
                AcceptScan(scanned);
            }
            catch (Exception ex) { status.Text = "本次扫描未完成：" + ex.Message; MessageBox.Show(ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
        string[] OptionalFields() { return optionalChecks.Where(b => b.IsChecked == true).Select(b => Convert.ToString(b.Content)).ToArray(); }
        void SetBusy(bool value) { busy = value; foreach (var b in controls) b.IsEnabled = !value; foreach (var card in stepCards) { card.IsEnabled = !value; card.Opacity = value ? 0.55 : 1; } foreach (var c in optionalChecks) c.IsEnabled = !value; baseline.IsEnabled = !value; grid.IsEnabled = !value; }
        string Value(TitleRecord t, string field) { FieldData f; return t.Fields.TryGetValue(field, out f) ? RuleEngine.Read(f).Display : ""; }
        void RefreshRows()
        {
            if (session == null) return;
            if (draft == null) draft = BatchPreview.Create(session.Input);
            var preview = BatchPreview.Preview(session.Input, draft);
            string selected = (grid.SelectedItem as DirectoryGridRow) == null ? null : ((DirectoryGridRow)grid.SelectedItem).Id;
            rows = preview.Rows.Select(r => {
                var t = session.Input.Titles.Single(x => x.Id == r.TitleId);
                var check = session.Result.Titles.Single(x => x.TitleId == r.TitleId);
                string label;
                if (check.ContentMatches) label = "一致";
                else if (r.Eligible && r.State.StartsWith("待修改")) label = string.Join("、", check.ProposedChanges.Select(c => (string.IsNullOrEmpty(c.Original) ? "待补" : "待修改") + c.Field)) + (r.Reviewed ? " · 已确认" : " · 未确认");
                else label = r.State;
                var choice = draft.Selections.Single(d => d.TitleId == r.TitleId).Choice;
                var row = new DirectoryGridRow { Id = t.Id, File = Path.GetFileName(t.File), Layout = t.Layout,
                    Number = r.OutputNumber, Name = r.OutputName, Status = label, Eligible = r.Eligible, Selected = r.Selected,
                    NeedsAttention = r.RequiresReview && !r.Eligible && !r.State.StartsWith("读取失败"),
                    Reviewed = r.Reviewed, ConfirmText = r.Reviewed ? "已确认" : "未确认", CanConfirm = r.Eligible || choice == PreviewChoice.KeepOriginal || choice == PreviewChoice.Defer,
                    Tone = r.State.StartsWith("读取失败") ? "red" : r.RequiresReview ? "yellow" : "normal",
                    StatusBrush = r.State.StartsWith("读取失败") ? B("#E75555") : r.RequiresReview ? B("#F0AD42") : check.ContentMatches ? B("#74CEAF") : B("#8DAAC5") };
                if (writeResult != null)
                {
                    var done = writeResult.Files.Single(f => string.Equals(f.File, t.File, StringComparison.OrdinalIgnoreCase));
                    row.CanConfirm = false;
                    row.Status = done.State == "通过" ? "处理通过 · 已复扫验收" : done.State + " · " + done.Reason;
                    row.StatusBrush = done.State == "通过" ? B("#74CEAF") : done.State == "失败" ? B("#E75555") : B("#F0AD42");
                    row.NeedsAttention = done.State == "跳过" && r.RequiresReview;
                    if (done.State != "通过") { row.Number = r.OriginalNumber; row.Name = r.OriginalName; }
                }
                return row;
            }).ToList();
            grid.ItemsSource = rows; if (selected != null) grid.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
            MetricLabel(totalMetric, "本次图签"); MetricLabel(readyMetric, "可处理"); MetricLabel(reviewMetric, "待人工核对"); MetricLabel(failedMetric, "处理失败");
            totalMetric.Text = rows.Count.ToString();
            failedMetric.Text = preview.Rows.Count(r => r.State.StartsWith("读取失败")).ToString();
            reviewMetric.Text = preview.Pending.ToString();
            readyMetric.Text = preview.Rows.Count(r => !r.RequiresReview && !r.State.StartsWith("读取失败")).ToString();
            summaryHint.Text = "扫描完成｜一致 " + session.Result.Titles.Count(r => r.ContentMatches) + "，已确认修改 " + preview.Plans.Count + "；尚未写入 DWG";
            status.Text = string.Format("目录 {0} 行｜图签 {1}｜一致 {2}｜已确认修改 {3}（图号 {4}、图名 {5}）｜待人工核对 {6}。准备输出预览，尚未写入DWG。", session.Input.Directory.Count, rows.Count, session.Result.Titles.Count(r => r.ContentMatches), preview.Plans.Count, preview.ChangedNumbers, preview.ChangedNames, preview.Pending);
            if (writeResult != null)
            {
                summaryHint.Text = string.Format("处理结束（按文件）｜通过 {0}，跳过 {1}，失败 {2}", writeResult.Files.Count(f => f.State == "通过"), writeResult.Files.Count(f => f.State == "跳过"), writeResult.Files.Count(f => f.State == "失败"));
                MetricLabel(totalMetric, "本次文件"); MetricLabel(readyMetric, "处理通过"); MetricLabel(reviewMetric, "已跳过");
                totalMetric.Text = writeResult.Files.Count.ToString();
                readyMetric.Text = writeResult.Files.Count(f => f.State == "通过").ToString();
                reviewMetric.Text = writeResult.Files.Count(f => f.State == "跳过").ToString();
                failedMetric.Text = writeResult.Files.Count(f => f.State == "失败").ToString();
                status.Text = summaryHint.Text + "。仅验收通过的修改副本进入成果目录；源DWG未覆盖。请点击打开输出文件夹。";
            }
        }
        static void MetricLabel(TextBlock value, string label)
        { var panel = value.Parent as StackPanel; if (panel != null) ((TextBlock)panel.Children[0]).Text = label; }
        void EnsureEditable()
        {
            if (writeResult != null) throw new InvalidOperationException("本轮处理已结束。请先查看成果；如需调整决定，请重新扫描源文件。");
        }
        void AddConfirmationColumn()
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(FrameworkElement.StyleProperty, DarkButtonStyle());
            button.SetBinding(ContentControl.ContentProperty, new Binding("ConfirmText"));
            button.SetBinding(UIElement.IsEnabledProperty, new Binding("CanConfirm"));
            button.SetValue(Control.ForegroundProperty, Brushes.White); button.SetValue(Control.BackgroundProperty, B("#2C3944"));
            button.SetValue(Control.BorderBrushProperty, B("#8DAAC5")); button.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 2, 5, 2));
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler(ConfirmRowClicked));
            grid.Columns.Add(new DataGridTemplateColumn { Header = "人工核对", Width = 90, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = button } });
        }
        Style DarkButtonStyle()
        {
            var style = new Style(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) }); border.AppendChild(content);
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, B("#A7B2BC"))); disabled.Setters.Add(new Setter(Control.BackgroundProperty, B("#1B2228"))); disabled.Setters.Add(new Setter(Control.BorderBrushProperty, B("#35404A"))); style.Triggers.Add(disabled);
            return style;
        }
        void ConfirmRowClicked(object sender, RoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as DirectoryGridRow;
            if (row == null) return; grid.SelectedItem = row;
            if (!row.CanConfirm) { ShowDetail(); return; }
            ChooseCurrent(row.Reviewed ? PreviewChoice.Unchecked : PreviewChoice.Auto); e.Handled = true;
        }
        void ConfirmClearRows()
        {
            if (session == null) { status.Text = "请先扫描图纸。"; return; }
            EnsureEditable();
            int count = BatchPreview.ConfirmAllClear(session.Input, draft);
            decisions.Clear(); SaveDraft(); RefreshRows(); ShowDetail();
            status.Text += " 本次已确认 " + count + " 个明确项目，冲突和读取失败项未放行。";
        }
        void ChooseCurrent(PreviewChoice choice)
        {
            EnsureEditable();
            var r = Current(); BatchPreview.SetChoice(session.Input, draft, r.TitleId, choice);
            decisions.Clear(); SaveDraft(); RefreshRows(); ShowDetail();
        }
        TitleCheck Current()
        {
            var row = grid.SelectedItem as DirectoryGridRow;
            if (session == null || row == null) throw new InvalidOperationException("请扫描后选择一条图签。");
            return session.Result.Titles.Single(r => r.TitleId == row.Id);
        }
        Border Panel(UIElement child, Thickness padding)
        { return new Border { Background = B("#171D22"), BorderBrush = B("#35404A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = padding, Child = child }; }
        UIElement BuildDifferencePanel()
        {
            var content = new DockPanel(); DockPanel.SetDock(detailTitle, Dock.Top); detailTitle.Margin = new Thickness(0, 0, 0, 10); content.Children.Add(detailTitle);
            var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
            var comparisons = new Grid(); comparisons.ColumnDefinitions.Add(new ColumnDefinition()); comparisons.ColumnDefinitions.Add(new ColumnDefinition());
            var original = new StackPanel(); original.Children.Add(Text("原始内容", 12)); originalText.Margin = new Thickness(0, 8, 10, 0); original.Children.Add(originalText); comparisons.Children.Add(original);
            var output = new StackPanel(); output.Children.Add(Text("准备输出", 12)); outputText.Margin = new Thickness(0, 8, 0, 0); output.Children.Add(outputText); Grid.SetColumn(output, 1); comparisons.Children.Add(output);
            left.Children.Add(comparisons); resultText.Margin = new Thickness(0, 16, 0, 0); resultText.Foreground = B("#F0AD42"); left.Children.Add(resultText); columns.Children.Add(left);
            rightChoices.Children.Add(Text("选择更改内容", 14)); choiceHint.Margin = new Thickness(0, 5, 0, 5); rightChoices.Children.Add(choiceHint);
            rightChoices.Children.Add(new ScrollViewer { Content = candidateChoices, MaxHeight = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            otherRowsExpander.Content = otherRows; otherRowsExpander.Visibility = Visibility.Collapsed; rightChoices.Children.Add(otherRowsExpander);
            otherRows.SelectionChanged += delegate { var option = otherRows.SelectedItem as BaselineOption; if (option != null) { candidateRowId = option.Path; foreach (RadioButton radio in candidateChoices.Children.OfType<RadioButton>()) radio.IsChecked = false; PreviewCandidate(); } };
            var operations = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            bindButton = Action("采用所选目录", ApplyCandidate); operations.Children.Add(bindButton);
            participateButton = Action("确认按目录修改", delegate { ChooseCurrent(PreviewChoice.Auto); }); operations.Children.Add(participateButton);
            operations.Children.Add(Action("保留原值", delegate { ChooseCurrent(PreviewChoice.KeepOriginal); }));
            operations.Children.Add(Action("暂不处理", delegate { ChooseCurrent(PreviewChoice.Defer); }));
            rightChoices.Children.Add(operations); Grid.SetColumn(rightChoices, 1); columns.Children.Add(rightChoices); content.Children.Add(columns);
            ClearDetail(); return Panel(content, new Thickness(14));
        }
        void ClearDetail()
        {
            detailTitle.Text = "查看差异｜请选择黄标或红标项目"; originalText.Text = "—"; outputText.Text = "—"; resultText.Text = "";
            candidateRowId = null; candidateChoices.Children.Clear(); otherRows.ItemsSource = null; otherRowsExpander.Visibility = Visibility.Collapsed;
            rightChoices.Visibility = Visibility.Collapsed; if (bindButton != null) bindButton.Visibility = Visibility.Collapsed;
        }
        static string Display(string value) { return string.IsNullOrEmpty(value) ? "（空白）" : value; }
        string Summary(TitleCheck check)
        {
            if (check.ContentMatches) return "内容一致，无需修改。";
            if (check.Issues.Any(i => i.Code == "SCAN_FAILED")) return "图纸读取失败或图签未识别，本项暂不能处理。";
            if (check.Issues.Any(i => i.Code == "NUMBER_NAME_CONFLICT")) return "图号和图名对应不同图纸，请在右侧选择正确的目录记录。";
            if (check.Issues.Any(i => i.Code == "MULTIPLE_CANDIDATES")) return "找到多个目录候选，请在右侧选择正确记录。";
            var descriptions = new List<string>();
            foreach (var change in check.ProposedChanges)
                descriptions.Add(string.IsNullOrEmpty(change.Original) ? change.Field + "缺失，目录提供了补全值。" : change.Original == change.Proposed ? change.Field + "显示记录不一致，目录提供了修复值。" : change.Field + "与目录不同，请查看准备输出值。");
            if (check.Issues.Any(i => i.Code == "REPRESENTATION_MISMATCH")) descriptions.Add("属性记录与实际显示不同，以实际显示内容核对。");
            if (check.Issues.Any(i => i.Code == "MISSING_ATTRIBUTE")) descriptions.Add("缺少可修改的图签属性，不能直接补值。");
            if (check.Issues.Any(i => i.Code == "READ_FAILED")) descriptions.Add("部分文字无法读取，本项暂不能处理。");
            if (check.Issues.Any(i => i.Code.Contains("DUPLICATE") || i.Code == "DIRECTORY_ROW_CLAIMED_TWICE")) descriptions.Add("图号重复或目录记录被多张图纸占用，需要人工检查。");
            if (check.Issues.Any(i => i.Code == "UNSUPPORTED_FORMAT" || i.Code == "NOT_WRITABLE")) descriptions.Add("文字格式暂不支持或属性不可修改，需要人工处理。");
            if (check.DirectoryRowId == null && descriptions.Count == 0) descriptions.Add("目录未找到唯一对应项，请选择目录记录或暂不处理。");
            if (check.Issues.Any(i => i.BlocksConfirmation) && descriptions.Count == 0) descriptions.Add("目录基准不完整或无法可靠核对，本项暂不能处理。");
            return string.Join("\n", descriptions.Distinct());
        }
        void ShowDetail()
        {
            if (session == null || grid.SelectedItem == null) { ClearDetail(); return; }
            var check = Current(); var title = session.Input.Titles.Single(t => t.Id == check.TitleId);
            ClearDetail(); detailTitle.Text = "查看差异｜" + Path.GetFileName(title.File);
            var output = BatchPreview.Preview(session.Input, draft).Rows.Single(r => r.TitleId == check.TitleId);
            originalText.Text = "图号：" + Display(output.OriginalNumber) + "\n图名：" + Display(output.OriginalName);
            outputText.Text = (check.DirectoryRowId == null ? "待选择目录记录" : "图号：" + Display(output.OutputNumber) + "\n图名：" + Display(output.OutputName));
            resultText.Text = "核对结果：" + Summary(check);
            if (writeResult != null)
            {
                var done = writeResult.Files.Single(f => string.Equals(f.File, title.File, StringComparison.OrdinalIgnoreCase));
                resultText.Text = "处理结果：" + done.State + "\n" + done.Reason;
                return;
            }
            if (check.ContentMatches) return;
            rightChoices.Visibility = Visibility.Visible;
            participateButton.IsEnabled = check.EligibleForBatchConfirmation;
            choiceHint.Text = output.Selected ? "已人工确认；点击开始处理后，才会更新新DWG副本。" : "本项未确认，不会进入修改计划。";
            if (check.DirectoryRowId != null) return;
            bindButton.Visibility = Visibility.Visible;
            foreach (var id in check.NumberCandidates.Concat(check.NameCandidates).Distinct())
            {
                var row = session.Input.Directory.Single(d => d.Id == id);
                var radio = new RadioButton { Content = Text(DirectoryLabel(row), 13), GroupName = "directory-candidate", Margin = new Thickness(0, 3, 0, 3), Foreground = Brushes.White, Tag = id };
                radio.Checked += delegate { otherRows.SelectedItem = null; candidateRowId = Convert.ToString(radio.Tag); PreviewCandidate(); };
                candidateChoices.Children.Add(radio);
            }
            otherRowsExpander.Visibility = Visibility.Visible; otherRowsExpander.IsExpanded = candidateChoices.Children.Count == 0;
            otherRows.ItemsSource = session.Input.Directory.Select(d => new BaselineOption { Path = d.Id, Label = DirectoryLabel(d) }).ToList();
        }
        string DirectoryLabel(DirectoryRow row) { return RuleEngine.Read(row.Fields["图号"]).Display + "  " + RuleEngine.Read(row.Fields["图名"]).Display; }
        void PreviewCandidate()
        {
            if (session == null || candidateRowId == null) return;
            var row = session.Input.Directory.Single(d => d.Id == candidateRowId);
            outputText.Text = "图号：" + Display(RuleEngine.Read(row.Fields["图号"]).Display) + "\n图名：" + Display(RuleEngine.Read(row.Fields["图名"]).Display);
            choiceHint.Text = "候选预览；点击“采用所选目录”确定关联后，再人工确认修改。";
        }
        async void ConfirmBatchPlan()
        {
            if (busy) return;
            try
            {
                EnsureEditable();
                if (session == null) throw new InvalidOperationException("请先扫描。");
                CadFrameReplacer.CoreLicenseGuard.EnsureAuthorized("directory-apply");
                grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
                DwgDirectoryScanner.VerifySources(session);
                var preflight = DwgDirectoryWriter.Preflight(session, draft);
                if (!preflight.Files.Any(f => f.State == "待写入"))
                    throw new InvalidOperationException("没有通过预检的已确认修改。\n" + string.Join("\n", preflight.Files.Select(f => Path.GetFileName(f.File) + "：" + f.Reason)));
                string core = DwgDirectoryScanner.FindCore();
                if (!File.Exists(core)) throw new InvalidOperationException("未检测到AutoCAD 2021及以上运行环境。");
                var confirmed = BatchPreview.ConfirmBatch(session.Input, draft); decisions.Clear();
                foreach (var d in confirmed) decisions[d.TitleId] = d;
                SaveDraft(); SetBusy(true); scanProgress.IsIndeterminate = true; progressLabel.Text = "处理中";
                string scripts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine", "scripts");
                writeResult = await Task.Run(delegate { return DwgDirectoryWriter.Execute(session, draft, core, scripts,
                    delegate(string text) { Dispatcher.Invoke(new Action(delegate { status.Text = text + "；正在更新副本并重新打开验收。"; })); }); });
                RefreshRows(); ShowDetail();
            }
            catch (Exception ex) { status.Text = "处理未完成：" + ex.Message; MessageBox.Show(ex.Message, "目录处理", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { scanProgress.IsIndeterminate = false; scanProgress.Value = writeResult == null ? 0 : 100; progressLabel.Text = writeResult == null ? "未完成" : "100%"; SetBusy(false); }
        }
        void RestoreDraft()
        {
            EnsureEditable();
            if (session == null) throw new InvalidOperationException("请先扫描本次图纸。");
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "保存的预览|draft.json", Title = "恢复相同数据快照的预览决定" };
            if (dialog.ShowDialog() != true) return;
            DwgDirectoryScanner.VerifySources(session);
            var saved = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<BatchDraft>(File.ReadAllText(dialog.FileName, Encoding.UTF8));
            BatchPreview.Preview(session.Input, saved); // Validate before replacing the active draft.
            draft = saved; decisions.Clear(); SaveDraft(); RefreshRows(); ShowDetail();
        }
        void ApplyCandidate()
        {
            var check = Current();
            if (check.DirectoryRowId != null) throw new InvalidOperationException("此项已关联目录，无需重新选择。");
            if (string.IsNullOrEmpty(candidateRowId)) { choiceHint.Text = "请先选择一条目录记录。"; return; }
            EnsureEditable();
            var previous = draft.Selections.ToDictionary(s => s.TitleId, s => s.Choice);
            session.Input.ManualBindings[check.TitleId] = candidateRowId;
            session.Result = RuleEngine.Evaluate(session.Input); decisions.Clear(); draft = BatchPreview.Create(session.Input);
            foreach (var selection in draft.Selections)
                if (selection.TitleId != check.TitleId && previous.ContainsKey(selection.TitleId)) selection.Choice = previous[selection.TitleId];
            DwgDirectoryScanner.SaveEvidence(session); SaveDraft(); RefreshRows(); ShowDetail();
        }
        void SaveDraft()
        {
            if (session == null || draft == null) return;
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var preview = BatchPreview.Preview(session.Input, draft);
            var plans = RuleEngine.PreviewConfirmed(session.Input, decisions.Values);
            File.WriteAllText(Path.Combine(session.RunDirectory, "draft.json"), json.Serialize(draft), Encoding.UTF8);
            File.WriteAllText(Path.Combine(session.RunDirectory, "output_preview.json"), json.Serialize(preview), Encoding.UTF8);
            File.WriteAllText(Path.Combine(session.RunDirectory, "decisions.json"), json.Serialize(decisions.Values.ToArray()), Encoding.UTF8);
            File.WriteAllText(Path.Combine(session.RunDirectory, "confirmed_plan.json"), json.Serialize(plans), Encoding.UTF8);
        }
        void OpenReport()
        {
            if (session == null) throw new InvalidOperationException("扫描成功后可查看报告；失败时请查看提示中的日志目录。");
            SaveDraft();
            if (writeResult != null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + writeResult.OutputDirectory + "\"") { UseShellExecute = true });
                return;
            }
            var text = new StringBuilder("出图100% 目录校对扫描与计划报告\r\n未修改DWG\r\n\r\n");
            foreach (var row in rows) text.AppendLine(row.File + "｜" + row.Layout + "｜" + row.Number + "｜" + row.Name + "｜" + row.Status);
            text.AppendLine("\r\n目录问题："); foreach (var i in session.Result.DirectoryIssues) text.AppendLine(i.Message);
            text.AppendLine("本次范围未找到：" + session.Result.NotFoundInScope.Count + "；待关联目录行：" + session.Result.PendingAssociation.Count);
            foreach (var f in session.Files.Where(x => !string.IsNullOrEmpty(x.Error))) text.AppendLine("读取失败：" + f.File + "；" + f.Error);
            File.WriteAllText(Path.Combine(session.RunDirectory, "核对报告.txt"), text.ToString(), Encoding.UTF8);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + session.RunDirectory + "\"") { UseShellExecute = true });
        }
    }
}


