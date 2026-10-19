using System;
using System.Collections.Generic;

namespace Cad100.DirectoryChecking
{
    // Pure data objects. No CAD, UI, licensing, filesystem or network access.
    public enum ReadState { Value, Empty, MissingAttribute, ReadError, NoColumn, UnmappedColumn }
    public enum UserChoice { UseDirectory, KeepOriginal, Defer }
    public enum PreviewChoice { Auto, Unchecked, KeepOriginal, Defer }

    // A draft is an editable proposal, never an authorization to write a DWG.
    public sealed class DraftSelection
    {
        public string TitleId { get; set; }
        public PreviewChoice Choice { get; set; }
    }
    public sealed class BatchDraft
    {
        // Missing in old auto-selected drafts: reject rather than silently carrying approval forward.
        public int SchemaVersion { get; set; }
        public string SnapshotToken { get; set; }
        public List<DraftSelection> Selections { get; set; }
        public BatchDraft() { Selections = new List<DraftSelection>(); }
    }
    public sealed class OutputPreviewRow
    {
        public string TitleId { get; set; }
        public string OriginalNumber { get; set; }
        public string OriginalName { get; set; }
        public string OutputNumber { get; set; }
        public string OutputName { get; set; }
        public string State { get; set; }
        public bool Eligible { get; set; }
        public bool Selected { get; set; }
        public bool Reviewed { get; set; }
        public bool RequiresReview { get; set; }
    }
    public sealed class BatchOutputPreview
    {
        public List<OutputPreviewRow> Rows { get; set; }
        public List<PlanPreview> Plans { get; set; }
        public int ChangedNumbers { get; set; }
        public int ChangedNames { get; set; }
        public int Pending { get; set; }
    }

    public sealed class FieldData
    {
        public ReadState State { get; set; }
        public string OrdinaryRaw { get; set; }
        public bool EmbeddedPresent { get; set; }
        public string EmbeddedRaw { get; set; }
        public string AttributeHandle { get; set; }
        public bool Writable { get; set; }
        public FieldData() { Writable = true; }
    }
    public sealed class DirectoryRow
    {
        public string Id { get; set; }
        public string File { get; set; }
        public string Layout { get; set; }
        public string Sequence { get; set; }
        public string SourceHandles { get; set; }
        public Dictionary<string, FieldData> Fields { get; set; }
        public DirectoryRow() { Fields = new Dictionary<string, FieldData>(); }
    }
    public sealed class TitleRecord
    {
        public string Id { get; set; }
        public string File { get; set; }
        public string Layout { get; set; }
        public string InsertHandle { get; set; }
        public string SourceRevision { get; set; }
        // Opaque, unmodified original geometry. Not interpreted as verified CAD geometry.
        public string GeometrySnapshot { get; set; }
        public bool ScanFailed { get; set; }
        public Dictionary<string, FieldData> Fields { get; set; }
        public TitleRecord() { Fields = new Dictionary<string, FieldData>(); }
    }
    public sealed class BatchInput
    {
        public bool BaselineConfirmed { get; set; }
        public string BaselineRevision { get; set; }
        public List<DirectoryRow> Directory { get; set; }
        public List<TitleRecord> Titles { get; set; }
        public List<string> OptionalFields { get; set; }
        public Dictionary<string, string> ManualBindings { get; set; }
        public BatchInput()
        {
            Directory = new List<DirectoryRow>(); Titles = new List<TitleRecord>();
            OptionalFields = new List<string>(); ManualBindings = new Dictionary<string, string>();
        }
    }
    public sealed class Issue
    {
        public string Code { get; set; }
        public string Rule { get; set; }
        public string Field { get; set; }
        public string Message { get; set; }
        public bool BlocksConfirmation { get; set; }
        public bool ContentDifference { get; set; }
    }
    public sealed class FieldChange
    {
        public string Field { get; set; }
        public string Original { get; set; }
        public string Proposed { get; set; }
        public string AttributeHandle { get; set; }
        public bool SynchronizeRepresentations { get; set; }
    }
    public sealed class TitleCheck
    {
        public string TitleId { get; set; }
        public string DirectoryRowId { get; set; }
        public string Association { get; set; }
        public List<string> NumberCandidates { get; set; }
        public List<string> NameCandidates { get; set; }
        public List<Issue> Issues { get; set; }
        public List<FieldChange> ProposedChanges { get; set; }
        public bool ContentMatches { get; set; }
        public bool EligibleForBatchConfirmation { get; set; }
        public TitleCheck()
        {
            NumberCandidates = new List<string>(); NameCandidates = new List<string>();
            Issues = new List<Issue>(); ProposedChanges = new List<FieldChange>();
        }
    }
    public sealed class BatchResult
    {
        public string SnapshotToken { get; set; }
        public List<TitleCheck> Titles { get; set; }
        public List<Issue> DirectoryIssues { get; set; }
        public List<string> NotFoundInScope { get; set; }
        public List<string> PendingAssociation { get; set; }
        public BatchResult()
        {
            Titles = new List<TitleCheck>(); DirectoryIssues = new List<Issue>();
            NotFoundInScope = new List<string>(); PendingAssociation = new List<string>();
        }
    }
    public sealed class ConfirmedDecision
    {
        public string TitleId { get; set; }
        public string SnapshotToken { get; set; }
        public UserChoice Choice { get; set; }
        public string Reason { get; set; }
        public List<string> Fields { get; set; }
    }
    public sealed class PlanPreview
    {
        public string TitleId { get; set; }
        public string DirectoryRowId { get; set; }
        public string OriginalGeometrySnapshot { get; set; }
        public List<FieldChange> Changes { get; set; }
    }
}
