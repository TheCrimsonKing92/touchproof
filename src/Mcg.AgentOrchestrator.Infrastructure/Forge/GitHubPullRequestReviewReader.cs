using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GitHubPullRequestReviewReader
{
    internal static bool TryRead(JsonElement row, out string? decision,
        out IReadOnlyList<PullRequestChangesRequest> requests)
    {
        decision = null;
        requests = [];
        if (!row.TryGetProperty("reviewDecision", out var reviewDecision) ||
            reviewDecision.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            !row.TryGetProperty("latestReviews", out var reviews) || reviews.ValueKind != JsonValueKind.Array)
            return false;
        decision = reviewDecision.GetString();
        var changes = new List<PullRequestChangesRequest>();
        foreach (var review in reviews.EnumerateArray())
        {
            // Validate every review, including states that do not qualify for recording.
            if (review.ValueKind != JsonValueKind.Object ||
                !review.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
                !review.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object ||
                !author.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.String ||
                !review.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String ||
                !review.TryGetProperty("submittedAt", out var submitted) || submitted.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(submitted.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var submittedAt))
                return false;
            if (state.GetString() != "CHANGES_REQUESTED") continue;
            var excerpt = new StringBuilder();
            foreach (var rune in body.GetString()!.EnumerateRunes().Take(400)) excerpt.Append(rune.ToString());
            changes.Add(new(login.GetString()!, submittedAt, excerpt.ToString()));
        }
        requests = changes;
        return true;
    }
}
