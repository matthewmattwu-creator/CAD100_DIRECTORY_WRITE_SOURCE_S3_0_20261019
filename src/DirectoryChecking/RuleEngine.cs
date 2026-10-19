using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Cad100.DirectoryChecking
{
    public sealed class TextValue
    {
        public bool Readable; public bool Supported; public string Display; public string Normalized;
    }
    public static class RuleEngine
    {
        public const string Number = "图号", Name = "图名";
        static FieldData Field(Dictionary<string, FieldData> fields, string key)
        {
            FieldData f;
            return fields.TryGetValue(key, out f) && f != null ? f : new FieldData { State = ReadState.MissingAttribute, Writable = false };
        }
        // Deliberately narrow format support: width/height overrides, paragraph and literal escapes.
        // Unknown controls and fields are retained and rejected; never silently flattened.
        public static TextValue Read(FieldData field)
        {
            var r = new TextValue { Display = "", Normalized = "", Supported = true };
            if (field.State != ReadState.Value && field.State != ReadState.Empty) return r;
            string raw = field.EmbeddedPresent ? field.EmbeddedRaw : field.OrdinaryRaw;
            if (raw == null) return r;
            r.Readable = true;
            var s = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\') { s.Append(c); continue; }
                if (++i >= raw.Length) { r.Supported = false; s.Append('\\'); break; }
                char control = raw[i];
                if (control == 'P') s.Append('\n');
                else if (control == '\\' || control == '{' || control == '}') s.Append(control);
                else if (control == 'W' || control == 'H')
                {
                    int end = raw.IndexOf(';', i + 1);
                    if (end < 0 || !Regex.IsMatch(raw.Substring(i + 1, end - i - 1), @"^[0-9]+(?:\.[0-9]+)?x?$"))
                    { r.Supported = false; s.Append('\\').Append(control); }
                    else i = end;
                }
                else { r.Supported = false; s.Append('\\').Append(control); }
            }
            r.Display = s.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
            if (raw.Contains("%<") || r.Display.Contains("{") || r.Display.Contains("}")) r.Supported = false;
            r.Normalized = r.Display.Trim(' '); // No case-folding, internal whitespace or point removal.
            return r;
        }
        static Issue Problem(string code, string rule, string field, string message, bool block, bool difference)
        { return new Issue { Code = code, Rule = rule, Field = field, Message = message, BlocksConfirmation = block, ContentDifference = difference }; }
        static void Add(TitleCheck r, string code, string rule, string field, string message, bool block, bool difference)
        { r.Issues.Add(Problem(code, rule, field, message, block, difference)); }
        static List<string> Candidates(List<DirectoryRow> rows, string field, TextValue v)
        {
            if (!v.Readable || !v.Supported || v.Normalized.Length == 0) return new List<string>();
            return rows.Where(d => { var x = Read(Field(d.Fields, field)); return x.Readable && x.Supported && x.Normalized == v.Normalized; }).Select(d => d.Id).ToList();
        }
        static IEnumerable<IGrouping<string, DirectoryRow>> DuplicateRows(List<DirectoryRow> rows, string field)
        {
            return rows.Where(d => { var t = Read(Field(d.Fields, field)); return t.Readable && t.Supported && t.Normalized.Length > 0; })
                .GroupBy(d => Read(Field(d.Fields, field)).Normalized, StringComparer.Ordinal).Where(g => g.Count() > 1);
        }
        static bool ValidText(TitleCheck r, FieldData f, string key)
        {
            var text = Read(f);
            if (!text.Readable)
            {
                Add(r, f.State == ReadState.MissingAttribute ? "MISSING_ATTRIBUTE" : "READ_FAILED", "R17/R18", key, key + "属性不存在或无法读取", true, false);
                return false;
            }
            if (!text.Supported) { Add(r, "UNSUPPORTED_FORMAT", "R21", key, key + "含未支持的格式或字段表达式", true, false); return false; }
            if (f.EmbeddedPresent)
            {
                var outer = Read(new FieldData { State = ReadState.Value, OrdinaryRaw = f.OrdinaryRaw });
                if (outer.Readable && outer.Supported && outer.Normalized != text.Normalized)
                    Add(r, "REPRESENTATION_MISMATCH", "R20", key, "普通属性与多行显示正文不同", false, true);
            }
            return true;
        }
        static void CompareField(TitleCheck r, TitleRecord title, DirectoryRow row, string key, bool optional)
        {
            var target = Field(row.Fields, key); var baseline = Read(target);
            if (optional && target.State == ReadState.NoColumn)
            { Add(r, "NO_BASELINE_COLUMN", "R15", key, "目录没有该列，保留原值", false, false); return; }
            if (!baseline.Readable || !baseline.Supported)
            { Add(r, "BASELINE_UNREADABLE", "R18/R21", key, "目录字段未映射或无法读取", true, false); return; }
            if (baseline.Normalized.Length == 0)
            { Add(r, "BASELINE_EMPTY", "R16", key, "目录基准为空，保留原值", !optional, false); return; }
            var current = Field(title.Fields, key);
            if (!optional || ValidText(r, current, key))
            {
                var value = Read(current);
                if (!value.Readable || !value.Supported) return;
                bool repair = r.Issues.Any(i => i.Field == key && i.Code == "REPRESENTATION_MISMATCH");
                if (value.Normalized != baseline.Normalized || repair)
                {
                    if (value.Normalized != baseline.Normalized)
                    {
                        string code = value.Normalized.Length == 0 ? "EMPTY_VALUE" : "CONTENT_DIFFERENT";
                        if (value.Normalized.Replace("\n", "") == baseline.Normalized.Replace("\n", "")) code = "LINE_BREAK_DIFFERENT";
                        Add(r, code, code == "LINE_BREAK_DIFFERENT" ? "R23" : "R12/R24", key, key + "与目录不同", false, true);
                    }
                    if (current.Writable)
                        r.ProposedChanges.Add(new FieldChange { Field = key, Original = value.Display, Proposed = baseline.Display, AttributeHandle = current.AttributeHandle, SynchronizeRepresentations = repair });
                    else Add(r, "NOT_WRITABLE", "R17", key, "现有属性不可写入", true, false);
                }
            }
        }
        public static string Fingerprint(BatchInput input)
        {
            // Conservative invalidation: any batch snapshot change invalidates all old decisions.
            byte[] data = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(input));
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }
        public static BatchResult Evaluate(BatchInput input)
        {
            if (!input.BaselineConfirmed) throw new InvalidOperationException("请先人工确认目录基准。");
            if (input.Directory.Count == 0) throw new ArgumentException("目录没有可核对的数据行。");
            if (input.Directory.Any(d => string.IsNullOrEmpty(d.Id)) || input.Directory.Select(d => d.Id).Distinct().Count() != input.Directory.Count)
                throw new ArgumentException("目录行内部标识必须独立且唯一。");
            if (input.Titles.Any(t => string.IsNullOrEmpty(t.Id)) || input.Titles.Select(t => t.Id).Distinct().Count() != input.Titles.Count)
                throw new ArgumentException("图签内部标识必须独立且唯一。");
            if (input.OptionalFields.Any(k => !new[] { "日期", "版本号", "图别" }.Contains(k)))
                throw new ArgumentException("可选校对字段超出当前范围。");
            var result = new BatchResult { SnapshotToken = Fingerprint(input) };
            var badNumbers = new HashSet<string>(DuplicateRows(input.Directory, Number).SelectMany(g => g.Select(d => d.Id)));
            foreach (var group in DuplicateRows(input.Directory, Number))
                result.DirectoryIssues.Add(Problem("DUPLICATE_DIRECTORY_NUMBER", "R31", Number, "目录重复图号：" + group.Key, true, false));
            foreach (var g in input.Directory.Where(d => !string.IsNullOrEmpty(d.Sequence)).GroupBy(d => d.Sequence).Where(g => g.Count() > 1))
                result.DirectoryIssues.Add(Problem("DUPLICATE_SEQUENCE", "R33", "序号", "目录序号重复：" + g.Key, false, false));
            foreach (var title in input.Titles)
            {
                var r = new TitleCheck { TitleId = title.Id, Association = "Unmatched" };
                result.Titles.Add(r);
                if (title.ScanFailed) { Add(r, "SCAN_FAILED", "R36", "", "文件或图签扫描失败", true, false); continue; }
                var number = Field(title.Fields, Number); var name = Field(title.Fields, Name);
                bool numberOK = ValidText(r, number, Number), nameOK = ValidText(r, name, Name);
                var nv = Read(number); var tv = Read(name);
                r.NumberCandidates = Candidates(input.Directory, Number, nv); r.NameCandidates = Candidates(input.Directory, Name, tv);
                string bound;
                if (input.ManualBindings.TryGetValue(title.Id, out bound))
                {
                    if (!input.Directory.Any(d => d.Id == bound)) throw new ArgumentException("人工绑定的目录行不存在。");
                    r.DirectoryRowId = bound; r.Association = "ManualBound";
                    Add(r, "MANUAL_BINDING", "R28/R29", "", "使用人工指定的目录行；保留原候选", false, false);
                }
                // Equal names alone are not ambiguous when the unique number identifies
                // one of those same-name rows. Disjoint number/name evidence stays blocked.
                else if (r.NumberCandidates.Count == 1 && r.NameCandidates.Count > 1 && r.NameCandidates.Contains(r.NumberCandidates[0]))
                { r.DirectoryRowId = r.NumberCandidates[0]; r.Association = "ByNumber"; }
                else if (r.NumberCandidates.Count > 1 || r.NameCandidates.Count > 1)
                { r.Association = "Ambiguous"; Add(r, "MULTIPLE_CANDIDATES", "R29", "", "找到多个目录候选，请选择", true, false); }
                else if (r.NumberCandidates.Count == 1 && r.NameCandidates.Count == 1 && r.NumberCandidates[0] != r.NameCandidates[0])
                { r.Association = "Conflict"; Add(r, "NUMBER_NAME_CONFLICT", "R28", "", "图号与图名指向不同图纸", true, true); }
                else if (r.NumberCandidates.Count == 1)
                { r.DirectoryRowId = r.NumberCandidates[0]; r.Association = r.NameCandidates.Count == 1 ? "Both" : "ByNumber"; }
                else if (r.NameCandidates.Count == 1)
                { r.DirectoryRowId = r.NameCandidates[0]; r.Association = "ByNamePendingConfirmation"; }
                else Add(r, "NOT_IN_DIRECTORY", "R30/R35", "", "本次目录未找到对应项", true, false);
                if (numberOK && nv.Normalized.Length == 0 && r.DirectoryRowId == null) Add(r, "EMPTY_VALUE", "R27", Number, "图号为空", false, true);
                if (nameOK && tv.Normalized.Length == 0 && r.DirectoryRowId == null) Add(r, "EMPTY_VALUE", "R26", Name, "图名为空", false, true);
                if (r.DirectoryRowId != null)
                {
                    if (badNumbers.Contains(r.DirectoryRowId)) Add(r, "DUPLICATE_DIRECTORY_NUMBER", "R31", Number, "目标目录行的图号重复", true, false);
                    var row = input.Directory.Single(d => d.Id == r.DirectoryRowId);
                    CompareField(r, title, row, Number, false); CompareField(r, title, row, Name, false);
                    foreach (string key in input.OptionalFields.Distinct()) CompareField(r, title, row, key, true);
                }
            }
            foreach (var g in input.Titles.Where(t => !t.ScanFailed).GroupBy(t => Read(Field(t.Fields, Number)).Normalized)
                .Where(g => g.Key.Length > 0 && g.Count() > 1))
                foreach (var t in g) Add(result.Titles.Single(r => r.TitleId == t.Id), "DUPLICATE_TITLE_NUMBER", "R32", Number, "本次图签图号重复", true, false);
            foreach (var g in result.Titles.Where(r => r.DirectoryRowId != null).GroupBy(r => r.DirectoryRowId).Where(g => g.Count() > 1))
                foreach (var r in g) Add(r, "DIRECTORY_ROW_CLAIMED_TWICE", "R49", "", "多个图签对应同一目录行", true, false);
            foreach (var r in result.Titles)
            {
                r.ContentMatches = r.DirectoryRowId != null && !r.Issues.Any(i => i.BlocksConfirmation || i.ContentDifference);
                r.EligibleForBatchConfirmation = r.DirectoryRowId != null && r.ProposedChanges.Count > 0 && !r.Issues.Any(i => i.BlocksConfirmation);
            }
            var resolved = new HashSet<string>(result.Titles.Where(r => r.DirectoryRowId != null).Select(r => r.DirectoryRowId));
            var pending = new HashSet<string>(result.Titles.Where(r => r.DirectoryRowId == null).SelectMany(r => r.NumberCandidates.Concat(r.NameCandidates)));
            foreach (var row in input.Directory)
                if (!resolved.Contains(row.Id))
                    if (pending.Contains(row.Id)) result.PendingAssociation.Add(row.Id);
                    else result.NotFoundInScope.Add(row.Id);
            return result;
        }
        public static List<ConfirmedDecision> ConfirmSelected(BatchResult scan, IEnumerable<string> ids)
        { return ConfirmFields(scan, ids, null); }
        public static List<ConfirmedDecision> ConfirmNumbers(BatchResult scan, IEnumerable<string> ids)
        { return ConfirmFields(scan, ids, new[] { Number }); }
        static List<ConfirmedDecision> ConfirmFields(BatchResult scan, IEnumerable<string> ids, string[] fields)
        {
            var selected = ids.ToList();
            if (selected.Distinct().Count() != selected.Count) throw new ArgumentException("重复选择图签。");
            foreach (var id in selected)
                if (!scan.Titles.Any(r => r.TitleId == id && r.EligibleForBatchConfirmation)) throw new InvalidOperationException("所选项含不确定或不可修改项目。");
            foreach (var id in selected)
                if (fields != null && fields.Any(f => !scan.Titles.Single(r => r.TitleId == id).ProposedChanges.Any(c => c.Field == f)))
                    throw new InvalidOperationException("所选字段不存在明确修改建议。");
            return selected.Select(id => new ConfirmedDecision { TitleId = id, SnapshotToken = scan.SnapshotToken, Choice = UserChoice.UseDirectory,
                Fields = fields == null ? scan.Titles.Single(r => r.TitleId == id).ProposedChanges.Select(c => c.Field).ToList() : fields.ToList() }).ToList();
        }
        public static List<PlanPreview> PreviewConfirmed(BatchInput input, IEnumerable<ConfirmedDecision> decisions)
        {
            var scan = Evaluate(input); var list = decisions.ToList(); var previews = new List<PlanPreview>();
            if (list.Select(d => d.TitleId).Distinct().Count() != list.Count) throw new ArgumentException("同一图签存在多个决定。");
            foreach (var d in list)
            {
                if (d.SnapshotToken != scan.SnapshotToken) throw new InvalidOperationException("数据已变化，请重新核对并确认。");
                if (!Enum.IsDefined(typeof(UserChoice), d.Choice)) throw new ArgumentException("未知的人工决定。");
                var r = scan.Titles.SingleOrDefault(t => t.TitleId == d.TitleId);
                if (r == null) throw new ArgumentException("图签不存在。");
                if (d.Choice != UserChoice.UseDirectory) continue; // Keep/defer never erase the original difference.
                if (!r.EligibleForBatchConfirmation) throw new InvalidOperationException("该项尚不能生成明确修改计划。");
                if (d.Fields == null || d.Fields.Count == 0 || d.Fields.Distinct().Count() != d.Fields.Count || d.Fields.Any(f => !r.ProposedChanges.Any(c => c.Field == f)))
                    throw new ArgumentException("必须明确确认拟修改的字段。");
                previews.Add(new PlanPreview { TitleId = d.TitleId, DirectoryRowId = r.DirectoryRowId,
                    OriginalGeometrySnapshot = input.Titles.Single(t => t.Id == d.TitleId).GeometrySnapshot,
                    Changes = r.ProposedChanges.Where(c => d.Fields.Contains(c.Field)).ToList() });
            }
            return previews; // Data only; no DWG writer exists in this module.
        }
    }
}
