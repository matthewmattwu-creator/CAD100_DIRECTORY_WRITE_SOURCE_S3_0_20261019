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

namespace Cad100.DirectoryChecking
{
    public sealed class AttributePatch
    {
        public string Handle, Parent, Tag, Original, OriginalEmbedded, Value, Embedded;
        public bool HasEmbedded;
    }
    public sealed class DirectoryWriteItem
    {
        public string File, State, Reason, Output;
        public List<AttributePatch> Patches = new List<AttributePatch>();
    }
    public sealed class DirectoryWriteResult
    {
        public string OutputDirectory, SnapshotToken;
        public List<DirectoryWriteItem> Files = new List<DirectoryWriteItem>();
    }
    // Separate from the batch replacement engine: no insert/explode/template/personnel operations.
    public static class DwgDirectoryWriter
    {
        sealed class SkipFile : Exception { public SkipFile(string message) : base(message) { } }
        public static string Hash(string file)
        { using (var s = SHA256.Create()) using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return BitConverter.ToString(s.ComputeHash(f)).Replace("-", ""); }
        public static string PreserveFormat(string original, string proposed)
        {
            if (proposed == null || proposed.IndexOf('\0') >= 0 || proposed.Contains("%<")) throw new InvalidOperationException("目录文字含不支持的控制内容。");
            var match = Regex.Match(original ?? "", @"^(?:\\[WH][-+]?(?:\d+(?:\.\d*)?|\.\d+)[xX]?;)*", RegexOptions.IgnoreCase);
            string body = (original ?? "").Substring(match.Length);
            // Do not flatten paragraph layouts, nested formatting, fields, or interior overrides.
            if (body.IndexOfAny(new[] { '\\', '{', '}' }) >= 0 || body.Contains("%<"))
                throw new InvalidOperationException("原文字含分段或复杂富文本；本版保留原样并跳过，请先人工检查。");
            string escaped = proposed.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r\n", "\\P").Replace("\n", "\\P");
            string value = match.Value + escaped;
            if (value.Length > 240) throw new InvalidOperationException("文字过长，超过本次属性写入试验范围（240字符）。");
            return value;
        }
        public static DirectoryWriteResult Preflight(DirectoryScanSession session, BatchDraft draft)
        {
            var preview = BatchPreview.Preview(session.Input, draft);
            var result = new DirectoryWriteResult { SnapshotToken = draft.SnapshotToken };
            foreach (var source in session.Files)
            {
                var item = new DirectoryWriteItem { File = source.File, State = "跳过", Reason = "没有已确认的修改" }; result.Files.Add(item);
                try
                {
                    if (!string.IsNullOrEmpty(source.Error)) throw new InvalidOperationException("原文件读取失败：" + source.Error);
                    var titles = session.Input.Titles.Where(t => string.Equals(t.File, source.File, StringComparison.OrdinalIgnoreCase)).ToList();
                    var ids = new HashSet<string>(titles.Select(t => t.Id));
                    var plans = preview.Plans.Where(p => ids.Contains(p.TitleId)).ToList();
                    if (plans.Count == 0)
                    {
                        if (preview.Rows.Any(r => ids.Contains(r.TitleId) && r.RequiresReview)) item.Reason = "仍有待人工核对项，尚未形成已确认的修改计划";
                        continue;
                    }
                    if (preview.Rows.Any(r => ids.Contains(r.TitleId) && (r.RequiresReview || r.State.StartsWith("读取失败") || r.State.Contains("暂不处理"))))
                        throw new InvalidOperationException("同一DWG仍有未确认、受阻或暂不处理的图签，本文件整体跳过。");
                    foreach (var plan in plans)
                    {
                        var title = titles.Single(t => t.Id == plan.TitleId);
                        if (title.SourceRevision != source.Hash || plan.OriginalGeometrySnapshot != title.GeometrySnapshot)
                            throw new InvalidOperationException("图签快照与源文件不一致，请重新扫描。");
                        var insert = source.Entities.SingleOrDefault(e => e.Type == "INSERT" && e.Handle == title.InsertHandle);
                        if (insert == null) throw new InvalidOperationException("没有找到原图签块，不能创建替代图签。");
                        foreach (var change in plan.Changes)
                        {
                            var field = title.Fields[change.Field];
                            var attribute = source.Entities.SingleOrDefault(e => e.Type == "ATTRIB" && e.Handle == change.AttributeHandle && e.Parent == title.InsertHandle);
                            if (!field.Writable || attribute == null || field.AttributeHandle != change.AttributeHandle)
                                throw new InvalidOperationException("原图签缺少可写属性：" + change.Field + "；本版不新建文字。");
                            if (attribute.Ordinary != (field.OrdinaryRaw ?? "") || attribute.HasEmbedded != field.EmbeddedPresent || (attribute.HasEmbedded && attribute.Embedded != field.EmbeddedRaw))
                                throw new InvalidOperationException("原属性内容与扫描快照不同。");
                            if (change.Proposed == null || change.Proposed.Length > 240 || change.Proposed.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || change.Proposed.Contains("%<") || change.Proposed.Any(char.IsSurrogate))
                                throw new InvalidOperationException("目录内容过长或包含换行，本文件需人工排版。");
                            // Ordinary ATTRIB is literal TEXT; rich formatting belongs only to its embedded MTEXT.
                            if (!attribute.HasEmbedded && (attribute.Ordinary.Contains("\\") || attribute.Ordinary.Contains("%<")))
                                throw new InvalidOperationException("单行属性包含格式或字段表达式，暂不自动改写。");
                            item.Patches.Add(new AttributePatch { Handle = attribute.Handle, Parent = title.InsertHandle, Tag = attribute.Tag,
                                Original = attribute.Ordinary, OriginalEmbedded = attribute.Embedded, HasEmbedded = attribute.HasEmbedded,
                                Value = change.Proposed, Embedded = attribute.HasEmbedded ? PreserveFormat(attribute.Embedded, change.Proposed) : "" });
                        }
                    }
                    if (item.Patches.Select(p => p.Handle).Distinct().Count() != item.Patches.Count) throw new InvalidOperationException("多个修改指向同一属性，已阻止写入。");
                    item.State = "待写入"; item.Reason = "预检通过";
                }
                catch (Exception ex) { item.State = "跳过"; item.Reason = ex.Message; item.Patches.Clear(); }
            }
            return result;
        }
        // ASCII source avoids SCR/LSP path encoding ambiguity. Unicode requires LISPSYS=1/2.
        public static string LispString(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (value.Any(char.IsSurrogate)) throw new InvalidOperationException("暂不支持补充平面字符，请人工检查。");
            return "(strcat " + string.Join(" ", value.Select(c => "(chr " + ((int)c).ToString(CultureInfo.InvariantCulture) + ")")) + ")";
        }
        public static string Payload(IEnumerable<AttributePatch> patches)
        {
            return "(setq dcw:patches (list " + string.Join("\n", patches.Select(p => "(list " + string.Join(" ", new[] { LispString(p.Handle), LispString(p.Parent), LispString(p.Tag), LispString(p.Original), LispString(p.OriginalEmbedded), p.HasEmbedded ? "T" : "nil", LispString(p.Value), LispString(p.Embedded) }) + ")")) + "))\n";
        }
        static void Native(string core, string job, string[] commands, string logName)
        {
            string scr = Path.Combine(job, logName + ".scr"); File.WriteAllLines(scr, commands, Encoding.ASCII);
            var info = new ProcessStartInfo(core, "/i \"" + Path.Combine(job, "input.dwg") + "\" /s \"" + scr + "\" /l zh-CN")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.Unicode, StandardErrorEncoding = Encoding.Unicode };
            var log = new StringBuilder(); object gate = new object();
            using (var p = new Process { StartInfo = info })
            {
                DataReceivedEventHandler receive = delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) if (log.Length < 2000000) log.AppendLine(e.Data); };
                p.OutputDataReceived += receive; p.ErrorDataReceived += receive;
                p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                bool done = p.WaitForExit(120000); if (!done) { try { p.Kill(); p.WaitForExit(); } catch { } }
                if (done) p.WaitForExit();
                File.WriteAllText(Path.Combine(job, logName + ".txt"), log.ToString(), Encoding.UTF8);
                if (!done) throw new TimeoutException("CAD处理超过120秒，已停止；没有发布该文件。");
                if (p.ExitCode != 0) throw new InvalidOperationException(log.ToString().Contains("Problem with setting up current profile") ?
                    "AutoCAD用户配置初始化失败，尚未进入DWG处理；请查看" + logName + ".txt。" : "CAD未正常退出；请查看" + logName + ".txt。");
            }
        }
        public static void VerifyInventory(string before, string after)
        {
            var a = File.ReadAllLines(before, Encoding.UTF8); var b = File.ReadAllLines(after, Encoding.UTF8);
            if (a.Length < 2 || a[0] != "CAD100_PROTECTED_V1" || a[a.Length - 1] != "DONE" || b.Length < 2 || b[0] != a[0] || b[b.Length - 1] != "DONE")
                throw new InvalidDataException("保护属性清单不完整，不能验收。");
            if (!a.Skip(1).Take(a.Length - 2).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.Skip(1).Take(b.Length - 2).OrderBy(x => x, StringComparer.Ordinal)))
                throw new InvalidDataException("保存后保护属性发生变化（含字号、坐标、图签变换或其他实体），未发布该DWG。");
        }
        public static void VerifyText(List<CadEntity> entities, IEnumerable<AttributePatch> patches)
        {
            foreach (var patch in patches)
            {
                var e = entities.SingleOrDefault(x => x.Type == "ATTRIB" && x.Handle == patch.Handle && x.Parent == patch.Parent && x.Tag == patch.Tag);
                if (e == null || e.Ordinary != patch.Value || e.HasEmbedded != patch.HasEmbedded || (patch.HasEmbedded && e.Embedded != patch.Embedded))
                    throw new InvalidDataException("保存后文字与确认内容不一致：属性" + patch.Tag + "；未发布该DWG。");
            }
        }
        public static DirectoryWriteResult Execute(DirectoryScanSession session, BatchDraft draft, string core, string scripts, Action<string> progress)
        {
            // Preserve the creator's license boundary at the actual write entry, not just the UI.
            CadFrameReplacer.CoreLicenseGuard.EnsureAuthorized("directory-apply");
            DwgDirectoryScanner.VerifySources(session);
            if (!File.Exists(core) || FileVersionInfo.GetVersionInfo(core).FileMajorPart < 24) throw new InvalidOperationException("需要AutoCAD 2021及以上运行环境。");
            foreach (string f in new[] { "directory_write.lsp", "directory_scan.lsp" }) if (!File.Exists(Path.Combine(scripts, f))) throw new FileNotFoundException("缺少处理脚本", f);
            var result = Preflight(session, draft);
            if (!result.Files.Any(f => f.State == "待写入")) throw new InvalidOperationException("没有通过预检的已确认修改。" + string.Join("\n", result.Files.Select(f => Path.GetFileName(f.File) + "：" + f.Reason)));
            string root = Path.Combine(session.RunDirectory, "DWG输出_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root); result.OutputDirectory = root;
            string staging = Path.Combine(session.RunDirectory, "write_jobs_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            int i = 0;
            foreach (var item in result.Files.Where(f => f.State == "待写入"))
            {
                progress("处理 " + (++i) + "：" + Path.GetFileName(item.File));
                string job = Path.Combine(staging, i.ToString("D3")); Directory.CreateDirectory(job);
                try
                {
                    DwgDirectoryScanner.VerifySources(session);
                    string originalHash = session.Files.Single(f => f.File == item.File).Hash;
                    string copy = Path.Combine(job, "input.dwg");
                    using (var src = new FileStream(item.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) src.CopyTo(dst);
                    if (Hash(copy) != originalHash) throw new IOException("工作副本与扫描源文件不一致。");
                    File.Copy(Path.Combine(scripts, "directory_scan.lsp"), Path.Combine(job, "reader.lsp"));
                    File.Copy(Path.Combine(scripts, "directory_write.lsp"), Path.Combine(job, "writer.lsp"));
                    File.WriteAllText(Path.Combine(job, "patches.lsp"), Payload(item.Patches), Encoding.ASCII);
                    string load = "(load (strcat (getvar \"DWGPREFIX\") ";
                    Native(core, job, new[] { "(setvar \"SECURELOAD\" 0)", load + "\"reader.lsp\"))", load + "\"writer.lsp\"))", "(dcw:run)", "_.QUIT", "_Y" }, "write_console");
                    if (!File.Exists(Path.Combine(job, "written.ok")))
                    {
                        string errorPath = Path.Combine(job, "native_error.txt");
                        string error = File.Exists(errorPath) ? File.ReadAllText(errorPath, Encoding.UTF8) : "";
                        string reason = NativeError(job, "CAD未完成属性写入和保存；退出码0不代表成功。");
                        if (error.Contains("External dependencies") || error.Contains("layer is locked") || error.Contains("Invisible attribute")) throw new SkipFile(reason);
                        throw new IOException(reason);
                    }
                    Native(core, job, new[] { "(setvar \"SECURELOAD\" 0)", load + "\"reader.lsp\"))", load + "\"writer.lsp\"))", "(dcw:verify)", "_.QUIT", "_Y" }, "verify_console");
                    if (!File.Exists(Path.Combine(job, "reopened.txt")) || !File.Exists(Path.Combine(job, "entities.tsv")))
                        throw new IOException("输出DWG复扫未完整结束，未发布；请查看verify_console.txt。" + (File.Exists(Path.Combine(job, "native_error.txt")) ? File.ReadAllText(Path.Combine(job, "native_error.txt"), Encoding.UTF8) : ""));
                    VerifyInventory(Path.Combine(job, "before.txt"), Path.Combine(job, "after.txt"));
                    VerifyInventory(Path.Combine(job, "before.txt"), Path.Combine(job, "reopened.txt"));
                    bool changed; VerifyText(DwgDirectoryScanner.Parse(Path.Combine(job, "entities.tsv"), out changed), item.Patches);
                    if (changed) throw new InvalidDataException("输出复扫改变了文档状态，不能验收。");
                    DwgDirectoryScanner.VerifySources(session);
                    string destination = Path.Combine(root, Path.GetFileName(item.File));
                    File.Copy(copy, destination, false);
                    if (Hash(destination) != Hash(copy)) { File.Delete(destination); throw new IOException("成果复制校验失败。"); }
                    item.Output = destination; item.State = "通过"; item.Reason = "新DWG已保存，重新打开后内容及保护属性验收通过";
                }
                catch (SkipFile ex) { item.State = "跳过"; item.Reason = ex.Message + "；日志：" + job; }
                catch (Exception ex) { item.State = "失败"; item.Reason = ex.Message + "；日志：" + job; }
                Save(result);
            }
            Save(result); return result;
        }
        static string NativeError(string job, string fallback)
        {
            string path = Path.Combine(job, "native_error.txt");
            if (!File.Exists(path)) return fallback + " 请查看write_console.txt。";
            string error = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (error.Contains("Unicode AutoLISP")) return "当前CAD脚本环境未启用Unicode。请在CAD中将LISPSYS设为1，重启CAD后重试；本次没有发布DWG。";
            if (error.Contains("layer is locked")) return "原图签属性所在图层已锁定，请检查DWG；本文件没有发布。";
            if (error.Contains("Invisible attribute")) return "目标为隐藏属性，本版不将隐藏值当作可见文字改写；本文件没有发布。";
            if (error.Contains("External dependencies")) return "此DWG含外参或外链图像/底图。为避免副本移动后丢失参照，本阶段整体跳过；源文件未修改。";
            if (error.Contains("content changed")) return "原属性实际文字与扫描快照不一致，请重新扫描；本文件没有发布。";
            return fallback + " CAD原因：" + error;
        }
        static void Save(DirectoryWriteResult result)
        {
            File.WriteAllText(Path.Combine(result.OutputDirectory, "处理验收.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(result), Encoding.UTF8);
            var text = new StringBuilder("目录校对第三阶段：只在副本中更新原图签\r\n通过的DWG才放入此目录；未修改或跳过文件不另复制。\r\n原图形分组不作主动编辑；含外参或外链底图文件暂不发布；本版不做视觉溢出/字体替代验收。\r\n\r\n");
            foreach (var f in result.Files) text.AppendLine(Path.GetFileName(f.File) + "｜" + f.State + "｜" + f.Reason);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "处理验收报告.txt"), text.ToString(), Encoding.UTF8);
        }
    }
}
