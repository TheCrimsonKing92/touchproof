using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

internal static partial class CliRefusalSignals
{
    internal static IReadOnlyList<string> GetCliRefusalSignalLines(TaskVerificationRecord verification) =>
        ProviderLimitEvidenceParser.GetCliRefusalLines(
            verification.ExitCode,
            DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false));

    internal static bool TryGetAbsoluteResetRetryAfter(
        string line,
        DateTimeOffset completedAt,
        out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        var match = AbsoluteCliReset().Match(line);
        var dateless = !match.Success;
        if (dateless)
            match = DatelessCliReset().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var zoneId = match.Groups["zone"].Value;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) &&
            !(TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneId, out var windowsId) &&
              TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone)))
        {
            return false;
        }

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success
            ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture)
            : 0;
        if (hour is < 1 or > 12 || minute > 59)
        {
            return false;
        }

        hour = hour % 12 + (match.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        if (dateless)
            return TryGetNextLocalReset(zone, completedAt, hour, minute, out retryAfter);

        if (!DateTime.TryParseExact(match.Groups["month"].Value, "MMM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var month))
            return false;
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var year = completedAt.UtcDateTime.Year;
        try
        {
            // Rebuild the local date on rollover so the zone's offset is resolved for that year.
            for (var candidateYear = year; candidateYear <= year + 1; candidateYear++)
            {
                var local = new DateTime(candidateYear, month.Month, day, hour, minute, 0, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local))
                {
                    return false;
                }

                var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                if (utc >= completedAt)
                {
                    retryAfter = utc;
                    return true;
                }
            }
        }
        catch (ArgumentException)
        {
            // Impossible dates and unsupported years leave the existing limit hold without a reset.
        }

        return false;
    }

    private static bool TryGetNextLocalReset(TimeZoneInfo zone, DateTimeOffset completedAt,
        int hour, int minute, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        try
        {
            var date = TimeZoneInfo.ConvertTime(completedAt, zone).Date;
            for (var day = 0; day <= 2; day++)
            {
                var local = date.AddDays(day).AddHours(hour).AddMinutes(minute);
                if (zone.IsInvalidTime(local))
                    continue;

                // A repeated clock time has two instants; the larger offset occurs first.
                var offsets = zone.IsAmbiguousTime(local)
                    ? zone.GetAmbiguousTimeOffsets(local).OrderByDescending(offset => offset).ToArray()
                    : new[] { zone.GetUtcOffset(local) };
                foreach (var offset in offsets)
                {
                    var candidate = new DateTimeOffset(local, offset).ToUniversalTime();
                    if (candidate > completedAt)
                    {
                        retryAfter = candidate;
                        return true;
                    }
                }
            }
        }
        catch (ArgumentException)
        {
            // An unsupported date leaves the reset unparseable.
        }
        return false;
    }

    internal static bool IsApiErrorOverloadLine(string line)
    {
        var match = CliApiErrorStatus().Match(line);
        return match.Success && match.Groups["status"].Value != "429" &&
            (match.Groups["status"].Value == "529" || CliOverloadText().IsMatch(line));
    }

    internal static string BuildProviderAuthenticationEvidenceSummary(TaskSpec task, TaskVerificationRecord verification)
    {
        var hasLine = ProviderFailureSignals.TryGetProviderAuthenticationLine(verification, out var line);
        var dispatch = SubscriptionLimitEvidence.FindDispatchForVerification(task, verification) ?? task.LastDispatch;
        var remediation = "codex login / provider re-auth";
        if (dispatch is not null &&
            dispatch.WorkerProviderKind is not (ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna or ProviderKind.OpenAICodexOssCli) &&
            !dispatch.WorkerName.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
        {
            var command = ProviderLoginCommand().Match(line);
            remediation = command.Success
                ? $"run '{command.Groups["command"].Value}'"
                : $"provider re-login for {dispatch.WorkerName}";
        }

        return hasLine
            ? $"provider-authentication: {DispatchEvidenceSummaries.TruncateEvidence(line)}; remediation={remediation}"
            : $"provider-authentication; remediation={remediation}";
    }

    [GeneratedRegex(@"\bresets\s+(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+(?<day>\d{1,2}),?\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>am|pm)\s*\((?<zone>[A-Za-z_]+(?:/[A-Za-z0-9_+\-]+)+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteCliReset();

    [GeneratedRegex(@"\bresets\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>am|pm)\s*\((?<zone>[A-Za-z_]+(?:/[A-Za-z0-9_+\-]+)+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatelessCliReset();

    [GeneratedRegex(@"^API Error:?\s*(?<status>[1-5]\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex CliApiErrorStatus();

    [GeneratedRegex(@"\b(?:Overloaded|overloaded_error)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliOverloadText();

    [GeneratedRegex(@"\brun:?\s+(?<command>[a-z][\w.-]*\s+login(?:\s+--?[\w-]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderLoginCommand();
}
