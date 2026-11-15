using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectRefreshManifestCommand
{
    internal const string Usage = "Usage: project refresh-manifest [name] [--apply]";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output,
        Func<CrosswalkStackReader>? crosswalkReaderFactory = null)
    {
        string? name = null;
        var apply = false;
        foreach (var argument in parts.Skip(2))
        {
            if (argument == "--apply" && !apply) apply = true;
            else if (!string.IsNullOrWhiteSpace(argument) && !argument.StartsWith("-", StringComparison.Ordinal)
                && name is null) name = argument;
            else throw new ArgumentException(Usage);
        }

        OrchestratorProject project;
        try
        {
            name = name is null ? null : OrchestratorProjectSelection.NormalizeProjectName(name);
            project = name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride)
                : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                    ? new(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                    : registry.GetRequiredProject(name);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{Usage}{Environment.NewLine}{exception.Message}", exception);
        }

        if (project.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
            return Verdict("Skipped");
        try
        {
            var directory = ProjectDiscoveryModelLocation.ResolveDirectory(project, null, defaultRootDirectory,
                registry.DataRootDirectory);
            var live = Path.Combine(directory, "acceptance-manifest.json");
            byte[] installed;
            try
            {
                installed = File.ReadAllBytes(live);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return Verdict("NotInstalled");
            }

            var (result, error) = ProjectLearnedManifestSource.Learn(project, directory, crosswalkReaderFactory);
            if (error is not null) return Verdict("Unreadable", SingleLine(error));
            if (result!.Blockers.Count > 0)
            {
                foreach (var blocker in result.Blockers)
                    output.WriteLine($"Blocked {blocker.FactKey} {blocker.Reason}");
                foreach (var question in result.OwnerQuestions)
                    output.WriteLine($"OwnerQuestion {question.FactKey} {question.Question}");
                return Verdict("Refused", $"{result.Blockers.Count} facts the gate needs are unanswered");
            }

            var comparison = ProjectCheckManifestCommand.WriteComparison(project.Name, installed, result.Json!, output);
            if (comparison.Match) return 0;
            if (comparison.Difference is null) return Refuse("the difference could not be classified");
            if (!apply) return 0;
            if (comparison.Difference.Kind != ManifestDifferenceKind.NewSectionsOnly)
                return Refuse($"refresh-manifest installs only a NewSectionsOnly difference; kind is {comparison.Difference.Kind}");
            if (!project.ResolveWorkspace().IsProjectScoped)
                return Refuse("the workspace is not project-scoped");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory));
            if (live.Equals(root, StringComparison.OrdinalIgnoreCase)
                || live.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return Refuse("the live manifest is inside the project source root");

            var stamp = ProjectInstallManifestCommand.UtcNow().UtcDateTime
                .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var backup = live + ".bak-" + stamp;
            var temporary = Path.Combine(directory, $".acceptance-manifest.json-{Guid.NewGuid():N}.tmp");
            try
            {
                if (File.Exists(backup) || Directory.Exists(backup))
                    return Refuse($"backup already exists {backup}");
                File.Copy(live, backup, overwrite: false);
                File.WriteAllBytes(temporary, new UTF8Encoding(false).GetBytes(result.Json!));
                File.Move(temporary, live, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Refuse($"could not install {live}: {SingleLine(exception.Message)}");
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            using var manifest = JsonDocument.Parse(result.Json!);
            output.WriteLine($"Installed manifest: {live} ({manifest.RootElement.GetProperty("checks").GetArrayLength()} checks; previous saved as {backup})");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Verdict("Unreadable", SingleLine(exception.Message));
        }

        int Verdict(string verdict, string? detail = null)
        {
            output.WriteLine($"{verdict} {project.Name}{(detail is null ? "" : " " + detail)}");
            return 1;
        }

        int Refuse(string reason)
        {
            output.WriteLine($"Refused: {reason}");
            return 1;
        }
    }

    private static string SingleLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
