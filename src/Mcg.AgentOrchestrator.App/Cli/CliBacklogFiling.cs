using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBacklogFiling
{
    internal static string ValidateAdd(IReadOnlyList<string> parts, string body)
    {
        var bundledReason = ReadAddReason(parts);
        RefuseUnjustifiedBundle(body, bundledReason);
        return bundledReason ?? "";
    }

    internal static BacklogItemUpdate ParseUpdate(
        IReadOnlyList<string> parts,
        Func<IReadOnlyList<string>, Dictionary<string, string>, string?, string?> resolveDescription)
    {
        var values = CliArgumentParser.ReadBacklogUpdateValues(parts);
        var pin = values.ContainsKey("--next");
        var unpin = values.ContainsKey("--no-next");
        if (pin && unpin)
            throw new ArgumentException("--next and --no-next are mutually exclusive.");

        var title = values.GetValueOrDefault("--title");
        var priority = values.GetValueOrDefault("--priority") is { } rawPriority
            ? BacklogPriorityLevels.ParseOrThrow(rawPriority)
            : null;
        var tags = values.GetValueOrDefault("--tags");
        var description = resolveDescription(parts, values, values.GetValueOrDefault("--description"));
        var bundledReason = values.TryGetValue("--bundled-reason", out var reason) ? ParseReason(reason) : null;
        if (description is not null)
        {
            RefuseUnjustifiedBundle(description, bundledReason);
            bundledReason ??= "";
        }

        BacklogItemStatus? status = null;
        if (values.GetValueOrDefault("--status") is { } statusValue)
        {
            if (!Enum.TryParse<BacklogItemStatus>(statusValue, ignoreCase: true, out var parsed))
                throw new ArgumentException("--status must be one of: open, done, superseded.");
            status = parsed;
        }

        if (title is null && description is null && priority is null && tags is null &&
            bundledReason is null && status is null && !pin && !unpin)
            throw new ArgumentException(CliCommandHelp.BacklogUpdateUsage);

        return new BacklogItemUpdate(title, description, priority, tags, status,
            PinnedAt: pin ? DateTimeOffset.UtcNow : null, PinnedAtSpecified: pin || unpin, BundledReason: bundledReason);
    }

    private static string? ReadAddReason(IReadOnlyList<string> parts)
    {
        const string flag = "--bundled-reason";
        var options = parts.Where(part => part.Equals(flag, StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (options.Length == 0)
            return null;
        if (options.Length > 1)
            throw new ArgumentException($"Provide {flag} only once.");

        var reason = CliCommandHandlers.GetFlagValue(parts, flag);
        if (options[0].Equals(flag, StringComparison.OrdinalIgnoreCase) &&
            reason?.StartsWith("--", StringComparison.Ordinal) == true)
            reason = null;
        return ParseReason(reason);
    }

    private static string ParseReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("--bundled-reason requires nonblank text.");
        return reason.Trim();
    }

    private static void RefuseUnjustifiedBundle(string body, string? bundledReason)
    {
        var bundledParts = BacklogBundleDetector.FindParts(body);
        if (bundledParts.Count > 0 && bundledReason is null)
            throw new ArgumentException(
                $"Wanted contains multiple parts: {BacklogBundleDetector.RenderParts(bundledParts)}. " +
                "File one item per independently landable part, or supply --bundled-reason <text>.");
    }
}
