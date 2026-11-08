using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectEventSourcesCommand
{
    internal const string Usage = "Usage: project event-sources";
    private const int DiagnosticLimit = 4096;
    private static readonly UTF8Encoding Encoding = new(false, true);

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string repositoryRoot, Stream output, TextWriter error, string? tenant = null,
        OrchestratorWorkspace? homeWorkspace = null, WorkspaceInvocationBinding? binding = null,
        Func<AttentionWorkspaceCatalog>? readCatalog = null)
    {
        byte[] payload;
        try
        {
            if (parts.Count != 2) throw new ArgumentException(Usage);
            var catalog = readCatalog is not null ? readCatalog() : ReadCatalog();
            if (catalog.RegistryError is { } registryError) throw registryError;
            foreach (var entry in catalog.Projects)
            {
                if (entry.Error is { } resolutionError) throw resolutionError;
                if (entry.Workspace is null)
                    throw new InvalidOperationException("A fleet workspace could not be resolved.");
                ValidatePath(entry.Workspace.SqliteStatePath);
                ValidatePath(entry.Workspace.ConductEventsLogPath);
            }

            // Same authoritative store identity and canonical registration order as fleet attention.
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var records = catalog.Projects
                .GroupBy(entry => WorkspaceInvocationBinding.CanonicalPath(entry.Workspace!.SqliteStatePath), comparer)
                .Select(group => group.OrderBy(entry => entry.Name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Name, StringComparer.Ordinal).First())
                .OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var text = new StringBuilder();
            foreach (var entry in records)
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Contains('=') ||
                    ContainsLineBreak(entry.Name) || !labels.Add(entry.Name))
                    throw new InvalidOperationException("A fleet label cannot be represented in the source list.");
                text.Append(entry.Name).Append('=').Append(entry.Workspace!.ConductEventsLogPath).Append('\n');
            }
            payload = Encoding.GetBytes(text.ToString());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteError(error, exception);
            return 1;
        }

        // No stdout access until every resolution, validation and encoding step has succeeded.
        output.Write(payload);
        return 0;

        AttentionWorkspaceCatalog ReadCatalog()
        {
            var home = homeWorkspace ?? OrchestratorWorkspace.ForDirectory(repositoryRoot, null, tenant, registry);
            if (home.IsProjectScoped) throw new InvalidOperationException("The supplied home binding is project scoped.");
            binding?.Verify(home);
            return OwnerConsoleWorkspaceCatalog.ReadWorkspaceCatalog(repositoryRoot, tenant, registry, home);
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || ContainsLineBreak(path) || !Path.IsPathFullyQualified(path) ||
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidOperationException("A fleet binding must supply a representable absolute path.");
        _ = Path.GetFullPath(path); // Validate syntax only; never probe or open the log/store.
    }

    private static bool ContainsLineBreak(string value) => value.IndexOfAny(['\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029']) >= 0;

    private static void WriteError(TextWriter error, Exception exception)
    {
        var diagnostic = new StringBuilder("project event-sources: ");
        foreach (var character in exception.Message)
        {
            var escaped = character switch
            {
                '\r' => "\\r", '\n' => "\\n", '\v' => "\\v", '\f' => "\\f",
                '\u0085' => "\\u0085", '\u2028' => "\\u2028", '\u2029' => "\\u2029",
                _ => character.ToString()
            };
            if (diagnostic.Length + escaped.Length >= DiagnosticLimit) break;
            diagnostic.Append(escaped);
        }
        error.Write(diagnostic.Append('\n').ToString());
    }
}
