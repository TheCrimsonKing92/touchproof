using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class GitHubPullRequestStatusReader(IForgeProcessRunner runner) : IPullRequestStatusReader
{
    public PullRequestStatus Read(string repositoryRoot, int number)
    {
        PullRequestStatus Unavailable(string reason) => new(PullRequestStatusKind.Unavailable,
            Reason: ForgeDiagnosticRedactor.Redact(reason));
        try
        {
            if (number <= 0) return Unavailable("invalid pull request number");
            var root = Path.GetFullPath(repositoryRoot);
            var environment = new Dictionary<string, string>
            {
                ["GIT_CEILING_DIRECTORIES"] = Directory.GetParent(root)?.FullName ?? root,
                ["GIT_TERMINAL_PROMPT"] = "0"
            };
            var origin = runner.Run("git", ["remote", "get-url", "origin"], root, environment, TimeSpan.FromSeconds(60));
            if (!Ok(origin)) return Unavailable("origin lookup: " + Diagnostic(origin));
            if (!GitHubRemote.TryParse(origin.StandardOutput.Trim(), out var repository))
                return Unavailable("origin is not a supported GitHub repository URL");
            var response = runner.Run("gh", ["pr", "view", number.ToString(CultureInfo.InvariantCulture),
                "--repo", repository!, "--json", "state,mergedAt,mergeCommit,closedAt,url"],
                root, environment, TimeSpan.FromSeconds(30));
            if (!Ok(response)) return Unavailable("gh pr view: " + Diagnostic(response));
            using var document = JsonDocument.Parse(response.StandardOutput);
            var row = document.RootElement;
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("state", out var state) ||
                state.ValueKind != JsonValueKind.String || !row.TryGetProperty("mergedAt", out var mergedAt) ||
                !row.TryGetProperty("closedAt", out var closedAt) || !row.TryGetProperty("mergeCommit", out var merge) ||
                !row.TryGetProperty("url", out var url)) return Unavailable("gh response missing required fields");
            if (!Timestamp(mergedAt) || !Timestamp(closedAt)) return Unavailable("gh response has invalid terminal timestamps");
            string? commit = null;
            if (merge.ValueKind != JsonValueKind.Null)
            {
                if (merge.ValueKind != JsonValueKind.Object || !merge.TryGetProperty("oid", out var oid) ||
                    oid.ValueKind != JsonValueKind.String || !IsRevision(oid.GetString()))
                    return Unavailable("gh response has invalid merge commit");
                commit = merge.GetProperty("oid").GetString()!.ToLowerInvariant();
            }
            if (url.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                return Unavailable("gh response has invalid url");
            var address = url.ValueKind == JsonValueKind.String ? url.GetString() : null;
            if (address is not null && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !uri.AbsolutePath.Equals($"/{repository}/pull/{number}", StringComparison.OrdinalIgnoreCase)))
                return Unavailable("gh response url does not match origin and pull request number");
            return state.GetString() switch
            {
                "OPEN" when commit is null && mergedAt.ValueKind == JsonValueKind.Null && closedAt.ValueKind == JsonValueKind.Null =>
                    new(PullRequestStatusKind.Open, Url: address, Repository: repository),
                "MERGED" when commit is not null && mergedAt.ValueKind == JsonValueKind.String =>
                    new(PullRequestStatusKind.Merged, commit, address, Repository: repository),
                "CLOSED" when commit is null && mergedAt.ValueKind == JsonValueKind.Null && !string.IsNullOrWhiteSpace(address) =>
                    new(PullRequestStatusKind.ClosedUnmerged, Url: address, Repository: repository),
                _ => Unavailable("gh response has unknown state or contradictory terminal fields")
            };
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            return Unavailable(exception is JsonException ? "gh response is empty or invalid JSON" : exception.Message);
        }
    }

    private static bool Timestamp(JsonElement value) => value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);
    private static bool IsRevision(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static bool Ok(ForgeProcessResult result) => result.Started && !result.TimedOut && result.ExitCode == 0;
    private static string Diagnostic(ForgeProcessResult result) => !result.Started ? "command did not start: " + result.StandardError :
        result.TimedOut ? "command timed out" : $"exit {result.ExitCode}: " +
            (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
}
