using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class DecisionSnapshotCommand
{
    internal const string Name = "decision-snapshot";
    private const string Usage = "Usage: decision-snapshot --state-directory <copy> --output <file> [--as-of <UTC instant>]";

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 2 && args[1] is "--help" or "-h")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var start = DateTimeOffset.UtcNow;
        try
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < args.Length; i += 2)
            {
                var option = args[i].ToLowerInvariant();
                if (option is not ("--state-directory" or "--output" or "--as-of") ||
                    i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) ||
                    args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(option, args[i + 1]))
                {
                    throw new ArgumentException(Usage);
                }
            }

            if (!options.TryGetValue("--state-directory", out var state) || !options.TryGetValue("--output", out var path))
                throw new ArgumentException(Usage);

            var asOf = start;
            if (options.TryGetValue("--as-of", out var instant) &&
                (!(instant.EndsWith('Z') || instant.EndsWith("+00:00", StringComparison.Ordinal)) ||
                 !DateTimeOffset.TryParseExact(instant, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
                     CultureInfo.InvariantCulture, DateTimeStyles.None, out asOf)))
            {
                throw new ArgumentException("--as-of must be an ISO 8601 UTC instant.");
            }

            await DecisionSnapshotWriter.WriteAsync(state, path, asOf);
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Decision snapshot failed: {ex.Message}");
            return 1;
        }
    }
}
