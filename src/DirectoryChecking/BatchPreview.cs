using System;
using System.Collections.Generic;
using System.Linq;

namespace Cad100.DirectoryChecking
{
    // Stage 2: recompute every proposal from the current snapshot; persisted UI values
    // can select/exclude a proposal but cannot inject arbitrary output strings.
    public static class BatchPreview
    {
        public static BatchDraft Create(BatchInput input)
        {
            var scan = RuleEngine.Evaluate(input);
            return new BatchDraft { SchemaVersion = 2, SnapshotToken = scan.SnapshotToken,
                Selections = scan.Titles.Select(t => new DraftSelection { TitleId = t.TitleId, Choice = PreviewChoice.Unchecked }).ToList() };
        }
        static BatchResult Validate(BatchInput input, BatchDraft draft)
        {
            var scan = RuleEngine.Evaluate(input);
            if (draft != null && draft.SchemaVersion != 2)
                throw new InvalidOperationException("旧版预览不含本轮人工确认记录，请重新核对后确认。");
            if (draft == null || draft.SnapshotToken != scan.SnapshotToken)
                throw new InvalidOperationException("目录或源数据已变化，请重新扫描；旧预览和确认已失效。");
            if (draft.Selections == null || draft.Selections.Any(s => s == null || !Enum.IsDefined(typeof(PreviewChoice), s.Choice)) ||
                draft.Selections.Count != scan.Titles.Count || draft.Selections.Select(s => s.TitleId).Distinct().Count() != scan.Titles.Count ||
                draft.Selections.Any(s => !scan.Titles.Any(t => t.TitleId == s.TitleId)))
                throw new ArgumentException("预览选择记录不完整或包含未知项目，请重新扫描。");
            return scan;
        }
        public static void SetChoice(BatchInput input, BatchDraft draft, string id, PreviewChoice choice)
        {
            var scan = Validate(input, draft);
            if (!Enum.IsDefined(typeof(PreviewChoice), choice)) throw new ArgumentException("未知处理决定。");
            var row = scan.Titles.SingleOrDefault(t => t.TitleId == id);
            if (row == null) throw new ArgumentException("图签不存在。");
            if (choice == PreviewChoice.Auto && !row.EligibleForBatchConfirmation)
                throw new InvalidOperationException("此项一致、关联未确定或存在受阻字段，不能勾选修改。");
            draft.Selections.Single(s => s.TitleId == id).Choice = choice;
        }
        public static int ConfirmAllClear(BatchInput input, BatchDraft draft)
        {
            var scan = Validate(input, draft);
            int count = 0;
            foreach (var check in scan.Titles.Where(t => t.EligibleForBatchConfirmation))
            {
                var selection = draft.Selections.Single(s => s.TitleId == check.TitleId);
                // Explicit keep/defer decisions survive bulk confirmation.
                if (selection.Choice != PreviewChoice.Unchecked) continue;
                selection.Choice = PreviewChoice.Auto; count++;
            }
            return count;
        }
        static string Value(TitleRecord title, string field)
        { FieldData data; return title.Fields.TryGetValue(field, out data) ? RuleEngine.Read(data).Display : ""; }
        static string Proposed(TitleCheck check, string field, string original, bool use)
        {
            var change = check.ProposedChanges.SingleOrDefault(c => c.Field == field);
            return use && change != null ? change.Proposed : original;
        }
        public static BatchOutputPreview Preview(BatchInput input, BatchDraft draft)
        {
            var scan = Validate(input, draft);
            var rows = new List<OutputPreviewRow>();
            foreach (var check in scan.Titles)
            {
                var title = input.Titles.Single(t => t.Id == check.TitleId);
                var choice = draft.Selections.Single(s => s.TitleId == check.TitleId).Choice;
                bool selected = check.EligibleForBatchConfirmation && choice == PreviewChoice.Auto;
                string state = check.ContentMatches ? "一致" : !check.EligibleForBatchConfirmation ? "需确认或受阻" : "待修改";
                if (check.Issues.Any(i => i.Code == "SCAN_FAILED" || i.Code == "READ_FAILED")) state = "读取失败";
                if (choice == PreviewChoice.KeepOriginal) state += " · 已确认保留";
                if (choice == PreviewChoice.Defer) state += " · 暂不处理";
                bool failed = check.Issues.Any(i => i.Code == "SCAN_FAILED" || i.Code == "READ_FAILED");
                bool reviewed = check.ContentMatches || choice == PreviewChoice.KeepOriginal || choice == PreviewChoice.Defer || selected;
                bool requiresReview = !reviewed && !failed;
                if (!check.ContentMatches && choice == PreviewChoice.Unchecked) state += " · 未确认";
                if (selected) state += " · 已确认";
                string number = Value(title, RuleEngine.Number), name = Value(title, RuleEngine.Name);
                // Even an unchecked item retains the directory proposal for inspection.
                // KeepOriginal/Defer retain source values, while proposals stay in scan evidence.
                bool displayProposal = check.EligibleForBatchConfirmation && choice != PreviewChoice.KeepOriginal && choice != PreviewChoice.Defer;
                rows.Add(new OutputPreviewRow { TitleId = title.Id, OriginalNumber = number, OriginalName = name,
                    OutputNumber = Proposed(check, RuleEngine.Number, number, displayProposal),
                    OutputName = Proposed(check, RuleEngine.Name, name, displayProposal),
                    State = state, Eligible = check.EligibleForBatchConfirmation, Selected = selected, Reviewed = reviewed, RequiresReview = requiresReview });
            }
            // Reuse the existing guarded field-plan builder. These transient decisions
            // authorize this calculation only; Preview is not a persisted batch confirmation.
            var decisions = RuleEngine.ConfirmSelected(scan, rows.Where(r => r.Selected).Select(r => r.TitleId));
            var plans = RuleEngine.PreviewConfirmed(input, decisions);
            return new BatchOutputPreview { Rows = rows, Plans = plans,
                ChangedNumbers = plans.Sum(p => p.Changes.Count(c => c.Field == RuleEngine.Number)),
                ChangedNames = plans.Sum(p => p.Changes.Count(c => c.Field == RuleEngine.Name)),
                Pending = rows.Count(t => t.RequiresReview) };
        }
        public static List<ConfirmedDecision> ConfirmBatch(BatchInput input, BatchDraft draft)
        {
            Validate(input, draft);
            var preview = Preview(input, draft);
            var scan = RuleEngine.Evaluate(input);
            var decisions = RuleEngine.ConfirmSelected(scan, preview.Rows.Where(r => r.Selected).Select(r => r.TitleId));
            decisions.AddRange(draft.Selections.Where(s => s.Choice == PreviewChoice.KeepOriginal || s.Choice == PreviewChoice.Defer)
                .Select(s => new ConfirmedDecision { TitleId = s.TitleId, SnapshotToken = scan.SnapshotToken,
                    Choice = s.Choice == PreviewChoice.KeepOriginal ? UserChoice.KeepOriginal : UserChoice.Defer }));
            return decisions; // No writer, no filesystem or CAD calls.
        }
    }
}
