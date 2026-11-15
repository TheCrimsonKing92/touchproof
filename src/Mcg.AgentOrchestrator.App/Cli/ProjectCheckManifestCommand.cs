using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectCheckManifestCommand
{
    internal const string Usage = "Usage: project check-manifest [name|--all]";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output,
        Func<CrosswalkStackReader>? crosswalkReaderFactory = null)
    {
        if (parts.Count > 3 || parts.Count == 3 &&
            (string.IsNullOrWhiteSpace(parts[2]) || parts[2].StartsWith("-", StringComparison.Ordinal) && parts[2] != "--all"))
            throw new ArgumentException(Usage);
        var all = parts.Count == 3 && parts[2] == "--all";
        OrchestratorProject[] projects;
        if (all)
            projects = registry.ListProjects()
                .Prepend(new(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory)))
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        else
        {
            try
            {
                var name = parts.Count == 3 ? OrchestratorProjectSelection.NormalizeProjectName(parts[2]) : null;
                projects = [name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride)
                    : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                        ? new(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                        : registry.GetRequiredProject(name)];
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{Usage}{Environment.NewLine}{exception.Message}", exception);
            }
        }

        var success = true;
        foreach (var project in projects)
        {
            var verdict = Check(project, registry.DataRootDirectory, defaultRootDirectory, output, crosswalkReaderFactory);
            success &= verdict == "Match" || all && verdict is "NotInstalled" or "Skipped";
        }
        return success ? 0 : 1;
    }

    private static string Check(OrchestratorProject project, string dataRootDirectory, string defaultRootDirectory,
        TextWriter output, Func<CrosswalkStackReader>? crosswalkReaderFactory)
    {
        if (project.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
            return Verdict("Skipped");
        try
        {
            var directory = ProjectDiscoveryModelLocation.ResolveDirectory(project, null, defaultRootDirectory, dataRootDirectory);
            byte[] installed;
            try
            {
                installed = File.ReadAllBytes(Path.Combine(directory, "acceptance-manifest.json"));
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

            return WriteComparison(project.Name, installed, result.Json!, output).Match ? "Match" : "Differs";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Verdict("Unreadable", SingleLine(exception.Message));
        }

        string Verdict(string verdict, string? detail = null)
        {
            output.WriteLine($"{verdict} {project.Name}{(detail is null ? "" : " " + detail)}");
            return verdict;
        }
    }

    internal static (bool Match, ManifestDifference? Difference) WriteComparison(
        string name, byte[] installed, string learnedJson, TextWriter output)
    {
        var learned = new UTF8Encoding(false).GetBytes(learnedJson);
        var offset = 0;
        while (offset < Math.Min(installed.Length, learned.Length) && installed[offset] == learned[offset]) offset++;
        if (offset == Math.Max(installed.Length, learned.Length))
        {
            output.WriteLine($"Match {name}");
            return (true, null);
        }
        var line = 1;
        for (var index = 0; index < offset; index++)
            if (learned[index] == (byte)'\n') line++;
        output.WriteLine($"Differs {name} at byte offset {offset} line {line}");
        output.WriteLine($"  installed: {Excerpt(installed, line)}");
        output.WriteLine($"  learned: {Excerpt(learned, line)}");
        var difference = ManifestDifferenceClassifier.Classify(Encoding.UTF8.GetString(installed), learnedJson);
        if (difference is not null)
        {
            output.WriteLine($"  kind: {difference.Kind}");
            output.WriteLine($"  sections: added={Names(difference.SectionsAdded)} missing={Names(difference.SectionsMissing)} changed={Names(difference.SectionsChanged)}");
            output.WriteLine($"  checks: added={Names(difference.ChecksAdded)} missing={Names(difference.ChecksMissing)} changed={Names(difference.ChecksChanged)}");
        }
        return (false, difference);
    }

    private static string SingleLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static string Names(IReadOnlyList<string> names) => names.Count == 0 ? "-" : SingleLine(string.Join(",", names));

    private static string Excerpt(byte[] bytes, int line)
    {
        var start = 0;
        for (var current = 1; current < line; current++)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', start);
            if (newline < 0) return "";
            start = newline + 1;
        }
        var end = Array.IndexOf(bytes, (byte)'\n', start);
        if (end < 0) end = bytes.Length;
        if (end > start && bytes[end - 1] == (byte)'\r') end--;
        var text = Encoding.UTF8.GetString(bytes, start, end - start);
        return string.Concat(text.EnumerateRunes().Take(200).Select(rune => rune.ToString()));
    }
}
