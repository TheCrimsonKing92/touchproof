using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>The complete changed-file scope captured for one pair of Reviewer revisions.</summary>
public sealed record ReviewerChangedFileScope(
    string MergeBase,
    string IntegrationCommit,
    string CandidateCommit,
    IReadOnlyList<ReviewerScopedChange> Changes)
{
    public IReadOnlyList<ReviewerScopedChange> Changes { get; } = Changes;

    [JsonIgnore]
    public IReadOnlyList<string> ChangedFiles { get; } = Changes.Select(change => change.Path).ToArray();

    [JsonIgnore]
    public int TotalChangedFileCount => Changes.Count;

    internal string ThreeDotRange => $"{IntegrationCommit}...{CandidateCommit}";
}

public sealed record ReviewerScopedChange(string Path, string? PreviousPath, char Status, bool CanHaveTextHunks)
{
    // Git forbids NUL in a pathname. Reserve it to tag Git's lossless quoted spelling
    // when a pathname cannot be represented as UTF-8, including in dispatch snapshots.
    internal static string EncodeNonUtf8Path(string gitQuotedPath) => "\0" + gitQuotedPath;

    internal static string? GetNonUtf8PathQuote(string path) => path.StartsWith('\0') ? path[1..] : null;

    public bool CanHaveTextHunks
    {
        // Git can emit deletion and addition hunks for a type change with identical blob IDs.
        get => Status == 'T' || field;
        init;
    } = CanHaveTextHunks;
}

/// <summary>Reviewer preflight inputs retained with the dispatch for prompt restoration.</summary>
public sealed record ReviewerDispatchScope(
    ReviewerChangedFileScope Scope,
    bool MergeTreeClean,
    IReadOnlyList<string> MergeTreeConflictPaths,
    int MergeTreeTotalConflictPathCount);
