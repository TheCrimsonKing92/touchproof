using Mcg.AgentOrchestrator.Infrastructure;

internal static class TempDirectoryHolderAttribution
{
    private const string Heading = "Holder attribution:";

    internal static string Describe(string root, Func<string, BuildLockAttribution> probe)
    {
        string? file;
        try
        {
            // Skip directory links, including a linked root, to keep discovery inside the tree.
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return $"{Heading} unavailable; root is a reparse point.";
            }

            file = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).FirstOrDefault();
        }
        catch (Exception error)
        {
            return $"{Heading} unavailable; file selection failed: {error.GetType().Name}: {Escape(error.Message)}";
        }

        if (file is null)
        {
            return $"{Heading} unavailable; no remaining file could be selected.";
        }

        try
        {
            var attribution = probe(file);
            if (attribution is null || attribution.Holders.Count == 0)
            {
                return $"{Heading} unavailable; no holder found; source={Escape(attribution?.Source)}; path={Escape(file)}";
            }

            var holders = string.Join("; ", attribution.Holders.Select(holder =>
                $"pid={holder.ProcessId?.ToString() ?? "unknown"} name={Escape(holder.ProcessName)}"));
            return $"{Heading} source={Escape(attribution.Source)}; path={Escape(attribution.Path)}; {holders}";
        }
        catch (Exception error)
        {
            // Diagnostics must never replace the error that prevented cleanup.
            return $"{Heading} unavailable; probe failed: {error.GetType().Name}: {Escape(error.Message)}";
        }
    }

    private static string Escape(string? value) => (value ?? "unknown")
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
