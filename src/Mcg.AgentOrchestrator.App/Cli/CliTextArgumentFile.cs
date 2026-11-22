namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliTextArgumentFile
{
    internal static string Read(string flag, string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{flag} not found: {path}");

        return File.ReadAllText(path, System.Text.Encoding.UTF8);
    }
}
