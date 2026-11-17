namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static string? ResolveBacklogUpdateDescription(
        IReadOnlyList<string> parts, Dictionary<string, string> values, string? description)
    {
        var fileFlags = new[] { "--text-file", "--body-file" };
        foreach (var flag in fileFlags)
        {
            if (description is not null && values.ContainsKey(flag))
                throw new ArgumentException($"Provide either --description or {flag} <path>, not both.");
        }

        try
        {
            return ResolveTextArgumentOrDefault(parts, parts.Count, description, fileFlags);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var flag = fileFlags.First(values.ContainsKey);
            throw new InvalidOperationException($"{flag} could not be read: {values[flag]}", exception);
        }
    }
}
