using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Cad100.DirectoryChecking
{
    public sealed class CadEntity
    {
        public string Type, Handle, Parent, Layout, Layer, Tag, Ordinary, Embedded, Geometry;
        public bool HasEmbedded;
        public double[] Position, Alignment;
        public double Height;
        public string Text { get { return RuleEngine.Read(new FieldData { State = ReadState.Value, OrdinaryRaw = Ordinary, EmbeddedPresent = HasEmbedded, EmbeddedRaw = Embedded }).Display; } }
    }
    public sealed class ScanFileData
    {
        public string File, Hash, Error, LogPath;
        public bool DocumentStateChanged;
        public List<CadEntity> Entities = new List<CadEntity>();
    }
    public sealed class DirectoryScanSession
    {
        public string Folder, RunDirectory;
        public BatchInput Input;
        public BatchResult Result;
        public List<ScanFileData> Files;
    }
    public static class DwgDirectoryScanner
    {
        static double N(string s) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }
        static double[] P(string s) { return Regex.Matches(s ?? "", @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Cast<Match>().Select(m => N(m.Value)).ToArray(); }
        static string Unescape(string s)
        {
            var b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\\' && i + 1 < s.Length)
                { char c = s[++i]; b.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c); }
                else b.Append(s[i]);
            return b.ToString();
        }
        public static List<CadEntity> Parse(string path, out bool changed)
        {
            changed = false; bool done = false, header = false; var result = new List<CadEntity>();
            byte[] bytes = File.ReadAllBytes(path);
            string firstLine = Encoding.ASCII.GetString(bytes.TakeWhile(b => b != 10).ToArray());
            Encoding encoding = firstLine.TrimEnd('\r').EndsWith("\tCP936", StringComparison.Ordinal) ? Encoding.GetEncoding(936) :
                firstLine.TrimEnd('\r').EndsWith("\tSYSTEM", StringComparison.Ordinal) ? Encoding.Default : new UTF8Encoding(false, true);
            foreach (string line in File.ReadAllLines(path, encoding))
            {
                string[] a = line.Split('\t').Select(Unescape).ToArray();
                if (a[0] == "ERROR") throw new InvalidDataException("CAD提取失败：" + string.Join(" ", a.Skip(1)));
                if (a[0] == "META") { if (a.Length < 4 || a[1] != "CAD100_DIRECTORY_SCAN_V1") throw new InvalidDataException("提取文件版本不支持。"); header = true; }
                else if (a[0] == "DONE") { if (a.Length < 3) throw new InvalidDataException("提取结束标记不完整。"); done = true; changed = a[1] != a[2]; }
                else if (a[0] == "ENTITY")
                {
                    if (a.Length < 12) throw new InvalidDataException("实体数据不完整。");
                    result.Add(new CadEntity { Type = a[1], Handle = a[2], Parent = a[3], Layout = a[4], Layer = a[5], Tag = a[6], Ordinary = a[7], HasEmbedded = a[8] == "1", Embedded = a[9], Position = P(a[10]), Alignment = P(a[11]), Height = a.Length > 12 ? N(a[12]) : 0, Geometry = string.Join("\t", a.Skip(10)) });
                }
            }
            if (!header || !done) throw new InvalidDataException("CAD未完整导出，不能将部分数据当作扫描成功。");
            return result;
        }
        public static string FindCore()
        {
            var paths = new List<string>();
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD"))
                if (key != null) foreach (var version in key.GetSubKeyNames().OrderByDescending(x => x))
                    using (var v = key.OpenSubKey(version))
                        if (v != null) foreach (var product in v.GetSubKeyNames())
                            using (var p = v.OpenSubKey(product))
                                if (p != null) paths.Add(Path.Combine(Convert.ToString(p.GetValue("AcadLocation", "")), "accoreconsole.exe"));
            foreach (string path in paths)
                if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileMajorPart >= 24) return path;
            return "";
        }
        static string Hash(string file) { using (var sha = SHA256.Create()) using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", ""); }
        static void CopyShared(string file, string copy)
        { using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var dst = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) src.CopyTo(dst); }
        static string Quote(string s) { return "\"" + s + "\""; }
        public static string[] ReaderScriptLines()
        {
            // SCR input is decoded differently across CAD profiles. Keep every byte ASCII;
            // obtain the Unicode working-directory path from the already opened DWG itself.
            return new[] { "(setvar \"SECURELOAD\" 0)", "(load (strcat (getvar \"DWGPREFIX\") \"reader.lsp\"))", "(setq dcs:output (strcat (getvar \"DWGPREFIX\") \"entities.tsv\"))", "(c:CAD100DIRECTORYSCAN)", "_.QUIT", "_Y" };
        }
        public static DirectoryScanSession Scan(string folder, string baseline, string core, string script, string runs, Action<string> progress)
        { return Scan(folder, baseline, core, script, runs, progress, new string[0]); }
        public static DirectoryScanSession Scan(string folder, string baseline, string core, string script, string runs, Action<string> progress, string[] optionalFields)
        {
            if (!Directory.Exists(folder) || !File.Exists(core)) throw new ArgumentException("请选择DWG文件夹和AutoCAD核心控制台。");
            if (FileVersionInfo.GetVersionInfo(core).FileMajorPart < 24) throw new ArgumentException("此临时读取器仅支持AutoCAD 2021及以上，请选择对应 accoreconsole.exe。");
            string[] files = Directory.GetFiles(folder, "*.dwg", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!files.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(baseline), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("目录基准必须是所选文件夹这一层中的DWG。");
            files = files.OrderBy(f => string.Equals(f, baseline, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!File.Exists(script)) throw new FileNotFoundException("缺少目录只读扫描脚本。", script);
            string run = Path.Combine(runs, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(run);
            var data = new List<ScanFileData>();
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i]; progress(string.Format("读取 {0}/{1}：{2}", i + 1, files.Length, Path.GetFileName(file)));
                var item = new ScanFileData { File = file }; data.Add(item);
                string job = Path.Combine(run, (i + 1).ToString("D3")); Directory.CreateDirectory(job);
                item.LogPath = Path.Combine(job, "console.txt");
                try
                {
                    item.Hash = Hash(file); string copy = Path.Combine(job, "input.dwg"); CopyShared(file, copy);
                    if (Hash(copy) != item.Hash) throw new IOException("工作副本与源文件不一致。");
                    string lisp = Path.Combine(job, "reader.lsp"); File.Copy(script, lisp, false);
                    string output = Path.Combine(job, "entities.tsv"), scr = Path.Combine(job, "read.scr");
                    File.WriteAllLines(scr, ReaderScriptLines(), Encoding.ASCII);
                    var info = new ProcessStartInfo(core, "/i " + Quote(copy) + " /s " + Quote(scr) + " /l zh-CN /readonly")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.Unicode, StandardErrorEncoding = Encoding.Unicode };
                    var log = new StringBuilder(); object gate = new object();
                    using (var p = new Process { StartInfo = info })
                    {
                        p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) { if (log.Length < 2000000) log.AppendLine(e.Data); } };
                        p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) { if (log.Length < 2000000) log.AppendLine(e.Data); } };
                        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                        if (!p.WaitForExit(120000)) { try { p.Kill(); p.WaitForExit(); } catch { } throw new TimeoutException("单文件读取超过120秒，已停止本次读取进程。"); }
                        p.WaitForExit(); File.WriteAllText(item.LogPath, log.ToString(), Encoding.UTF8);
                        if (!File.Exists(output))
                        {
                            string reason = log.ToString().Contains("Problem with setting up current profile") ? "AutoCAD用户配置初始化失败" :
                                (log.ToString().Contains("LOAD 失败") || log.ToString().Contains("no function definition: C:CAD100DIRECTORYSCAN")) ? "AutoCAD未成功加载只读扫描脚本" : "AutoCAD未生成提取数据";
                            throw new InvalidOperationException(reason + "；退出码=" + p.ExitCode + "不能单独代表扫描成功。日志：" + item.LogPath);
                        }
                    }
                    bool changed; item.Entities = Parse(output, out changed); item.DocumentStateChanged = changed;
                    if (Hash(file) != item.Hash) throw new IOException("扫描期间源文件发生变化，本文件结果无效。");
                }
                catch (Exception ex) { item.Error = ex.Message; File.WriteAllText(Path.Combine(job, "error.txt"), ex.ToString(), Encoding.UTF8); }
                if (string.Equals(file, baseline, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(item.Error))
                    throw new InvalidOperationException("目录基准读取失败，已停止本轮扫描，未生成成功结果：" + item.Error + "\n日志目录：" + run);
            }
            var baseData = data.Single(f => string.Equals(f.File, baseline, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(baseData.Error)) throw new InvalidOperationException("目录基准读取失败：" + baseData.Error + "\n本次日志：" + run);
            BatchInput input = BuildInput(data, baseline);
            input.OptionalFields = optionalFields.ToList();
            var session = new DirectoryScanSession { Folder = folder, RunDirectory = run, Files = data, Input = input, Result = RuleEngine.Evaluate(input) };
            SaveEvidence(session); return session;
        }
        static string Header(string raw)
        {
            string v = Regex.Replace(raw ?? "", @"\s", "").Replace("：", "").Replace(":", "");
            if (v == "图纸名称" || v == "图名") return "图名";
            if (v == "出图日期" || v == "日期") return "日期";
            return new[] { "序号", "图号", "版本号", "图别", "图幅", "备注" }.Contains(v) ? v : "";
        }
        static FieldData F(string raw, string handle)
        {
            return new FieldData { State = string.IsNullOrEmpty(raw) ? ReadState.Empty : ReadState.Value, OrdinaryRaw = raw ?? "", AttributeHandle = handle };
        }
        static bool SameY(CadEntity a, CadEntity b, double tolerance) { return Math.Abs(a.Position[1] - b.Position[1]) <= tolerance; }
        // Grid based: header labels identify columns; intersecting grid lines define boundaries.
        // No sample filenames, absolute table coordinates or fixed row spacing.
        public static List<DirectoryRow> ExtractDirectory(ScanFileData data)
        {
            var result = new List<DirectoryRow>();
            var tableHeaders = data.Entities.Where(e => (e.Type == "TEXT" || e.Type == "MTEXT") && e.Position.Length >= 2 && Header(e.Text) == "序号").ToList();
            if (tableHeaders.Count > 0 && tableHeaders.Any(e => e.Layout != tableHeaders[0].Layout || Math.Abs(e.Position[1] - tableHeaders[0].Position[1]) > Math.Max(e.Height, 0.01)))
                throw new InvalidDataException("基准中发现多组不同位置/布局的目录表头。此版不自动合并，请用只有一组目录表的DWG测试；同一表的左右分栏可读取。");
            foreach (string layout in data.Entities.Select(e => e.Layout).Distinct())
            {
                var texts = data.Entities.Where(e => e.Layout == layout && (e.Type == "TEXT" || e.Type == "MTEXT") && e.Position.Length >= 2).ToList();
                var lines = data.Entities.Where(e => e.Layout == layout && e.Type == "LINE" && e.Position.Length >= 2 && e.Alignment.Length >= 2).ToList();
                foreach (var seq in texts.Where(e => Header(e.Text) == "序号"))
                {
                    double h = Math.Max(seq.Height, 0.01), tolerance = h * 0.8;
                    double nextPanel = texts.Where(e => Header(e.Text) == "序号" && SameY(seq, e, tolerance) && e.Position[0] > seq.Position[0] + h).Select(e => e.Position[0]).DefaultIfEmpty(double.MaxValue).Min();
                    var headers = texts.Where(e => SameY(seq, e, tolerance) && e.Position[0] >= seq.Position[0] - h && e.Position[0] < nextPanel && Header(e.Text) != "").OrderBy(e => e.Position[0]).ToList();
                    var numberHeader = headers.FirstOrDefault(e => Header(e.Text) == "图号"); var nameHeader = headers.FirstOrDefault(e => Header(e.Text) == "图名");
                    if (numberHeader == null || nameHeader == null) continue;
                    var horizontal = lines.Where(l => Math.Abs(l.Position[1] - l.Alignment[1]) < h * 0.01).ToList();
                    var anchors = horizontal.Where(l => Math.Min(l.Position[0], l.Alignment[0]) <= seq.Position[0] && Math.Max(l.Position[0], l.Alignment[0]) > numberHeader.Position[0] && Math.Abs(l.Position[1] - seq.Position[1]) < h * 3).ToList();
                    if (anchors.Count == 0) continue;
                    var anchor = anchors.OrderBy(l => Math.Abs(l.Position[1] - seq.Position[1])).ThenBy(l => Math.Abs(l.Position[0] - l.Alignment[0])).First();
                    double left = Math.Min(anchor.Position[0], anchor.Alignment[0]), right = Math.Max(anchor.Position[0], anchor.Alignment[0]);
                    headers = headers.Where(e => e.Position[0] < right).ToList();
                    var verticals = lines.Where(l => Math.Abs(l.Position[0] - l.Alignment[0]) < h * 0.01 && Math.Min(l.Position[1], l.Alignment[1]) < seq.Position[1] && Math.Max(l.Position[1], l.Alignment[1]) > seq.Position[1]).Select(l => l.Position[0]).Concat(new[] { left, right }).OrderBy(x => x).ToList();
                    var columns = new Dictionary<string, double[]>();
                    foreach (var head in headers)
                    {
                        double x = head.Position[0]; var lo = verticals.Where(v => v <= x).ToList(); var hi = verticals.Where(v => v > x).ToList();
                        if (lo.Count != 0 && hi.Count != 0 && !columns.ContainsKey(Header(head.Text))) columns.Add(Header(head.Text), new[] { lo.Max(), hi.Min() });
                    }
                    if (!columns.ContainsKey("序号") || !columns.ContainsKey("图号") || !columns.ContainsKey("图名")) continue;
                    var rowEdges = horizontal.Where(l => Math.Min(l.Position[0], l.Alignment[0]) <= left + h * 0.05 && Math.Max(l.Position[0], l.Alignment[0]) >= right - h * 0.05).Select(l => l.Position[1]).Distinct().ToList();
                    var serials = texts.Where(e => e.Position[0] >= columns["序号"][0] && e.Position[0] < columns["序号"][1] && e.Position[1] < seq.Position[1] - h && Regex.IsMatch(e.Text.Trim(), @"^\d+$")).OrderByDescending(e => e.Position[1]).ToList();
                    var pitches = serials.Zip(serials.Skip(1), (a, b) => a.Position[1] - b.Position[1]).Where(x => x > h).OrderBy(x => x).ToList();
                    double pitch = pitches.Count == 0 ? h * 3 : pitches[pitches.Count / 2];
                    foreach (var serial in serials)
                    {
                        var above = rowEdges.Where(y => y > serial.Position[1]).ToList(); var below = rowEdges.Where(y => y < serial.Position[1]).ToList();
                        // Broken/merged section separators must not merge adjacent serial rows.
                        // Pitch comes from the current table, never a sample-specific coordinate.
                        double top = Math.Min(above.Count == 0 ? double.MaxValue : above.Min(), serial.Position[1] + pitch * 0.45);
                        double bottom = Math.Max(below.Count == 0 ? double.MinValue : below.Max(), serial.Position[1] - pitch * 0.45);
                        string id = Path.GetFileName(data.File) + "|" + layout + "|row:" + serial.Handle;
                        if (result.Any(r => r.Id == id)) continue;
                        var row = new DirectoryRow { Id = id, File = data.File, Layout = layout, Sequence = serial.Text.Trim(), SourceHandles = serial.Handle };
                        foreach (string field in new[] { "图号", "图名", "日期", "版本号", "图别" })
                        {
                            if (!columns.ContainsKey(field)) { row.Fields[field] = new FieldData { State = ReadState.NoColumn, Writable = false }; continue; }
                            var bounds = columns[field];
                            var cells = texts.Where(e => e.Position[0] >= bounds[0] && e.Position[0] < bounds[1] && e.Position[1] < top && e.Position[1] > bottom).OrderByDescending(e => e.Position[1]).ThenBy(e => e.Position[0]).ToList();
                            row.Fields[field] = F(string.Join("\n", cells.Select(e => e.Text)), string.Join(",", cells.Select(e => e.Handle)));
                        }
                        result.Add(row);
                    }
                }
            }
            if (result.Count == 0) throw new InvalidDataException("未可靠识别目录表。此版支持有序号、图名、图号表头及直线网格的未旋转目录（可多栏）。不猜测TABLE、嵌套或无网格目录。请选择另一份基准或提供报告。");
            if (result.Any(r => (r.Fields["图号"].OrdinaryRaw ?? "").Contains("\n"))) throw new InvalidDataException("某目录行读出多个编号，行归属不明确；已停止，避免错误补号。");
            return result;
        }
        public static BatchInput BuildInput(List<ScanFileData> files, string baseline)
        {
            var b = files.Single(f => f.File == baseline);
            var input = new BatchInput { BaselineConfirmed = true, BaselineRevision = b.Hash, Directory = ExtractDirectory(b), OptionalFields = new List<string> { "日期", "版本号", "图别" } };
            foreach (var file in files)
            {
                int count = 0;
                foreach (var insert in file.Entities.Where(e => e.Type == "INSERT"))
                {
                    var attrs = file.Entities.Where(e => e.Type == "ATTRIB" && e.Parent == insert.Handle).ToList();
                    var tags = attrs.Select(e => Regex.Replace(e.Tag ?? "", @"_\d+$", "")).ToList();
                    bool title = (insert.Tag ?? "").Contains("图签") || (tags.Contains("图名") && tags.Contains("图号") && new[] { "设计项目号", "日期", "版本号", "图别" }.Count(tags.Contains) >= 2);
                    if (!title) continue;
                    var t = new TitleRecord { Id = Path.GetFileName(file.File) + "|" + insert.Layout + "|" + insert.Handle, File = file.File, Layout = insert.Layout, InsertHandle = insert.Handle, SourceRevision = file.Hash,
                        GeometrySnapshot = insert.Geometry + "\n" + string.Join("\n", attrs.Select(a => a.Handle + ":" + a.Geometry)), ScanFailed = !string.IsNullOrEmpty(file.Error) };
                    foreach (string field in new[] { "图号", "图名", "日期", "版本号", "图别" })
                    {
                        var list = attrs.Where(a => Regex.Replace(a.Tag ?? "", @"_\d+$", "") == field).ToList();
                        if (list.Count != 1) { t.Fields[field] = new FieldData { State = list.Count == 0 ? ReadState.MissingAttribute : ReadState.ReadError, Writable = false }; continue; }
                        var attr = list[0]; var f = new FieldData { State = ReadState.Value, OrdinaryRaw = attr.Ordinary, EmbeddedPresent = attr.HasEmbedded, EmbeddedRaw = attr.Embedded, AttributeHandle = attr.Handle };
                        if (RuleEngine.Read(f).Normalized.Length == 0) f.State = ReadState.Empty;
                        t.Fields[field] = f;
                    }
                    input.Titles.Add(t); count++;
                }
                if (count == 0) input.Titles.Add(new TitleRecord { Id = Path.GetFileName(file.File) + "|NO_RELIABLE_TITLE", File = file.File, Layout = "未确定", ScanFailed = true, SourceRevision = file.Hash, GeometrySnapshot = "未提取" });
            }
            return input;
        }
        public static void SaveEvidence(DirectoryScanSession session)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            File.WriteAllText(Path.Combine(session.RunDirectory, "input.json"), json.Serialize(session.Input), Encoding.UTF8);
            File.WriteAllText(Path.Combine(session.RunDirectory, "results.json"), json.Serialize(session.Result), Encoding.UTF8);
            File.WriteAllText(Path.Combine(session.RunDirectory, "files.json"), json.Serialize(session.Files.Select(f => new { f.File, f.Hash, f.Error, f.LogPath, f.DocumentStateChanged }).ToArray()), Encoding.UTF8);
        }
        public static void VerifySources(DirectoryScanSession session)
        {
            if (session == null || !Directory.Exists(session.Folder) || session.Files == null || session.Files.Count == 0)
                throw new InvalidOperationException("缺少实际扫描来源，请重新扫描。");
            var current = Directory.GetFiles(session.Folder, "*.dwg", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            var scanned = session.Files.Select(f => Path.GetFullPath(f.File)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            if (!current.SequenceEqual(scanned, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("文件范围已变化，请重新扫描；旧计划不能继续确认。");
            foreach (var file in session.Files)
                if (!string.IsNullOrEmpty(file.Hash) && !string.Equals(Hash(file.File), file.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("源DWG或目录已变化，请重新扫描：" + Path.GetFileName(file.File));
        }
    }
}
