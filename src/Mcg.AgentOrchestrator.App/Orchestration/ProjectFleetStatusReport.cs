using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProjectFleetStatusRow(
    string Name, string State, int? ProcessId, string? BuildCommit, string ToolVariables, string Missing, string? Error);

internal static class ProjectFleetStatusReport
{
    public static IReadOnlyList<ProjectFleetStatusRow> BuildRows(ProjectFleetStartPass pass) => pass.Children
        .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
        .Select(child => new ProjectFleetStatusRow(child.Name, child.State, child.ProcessId, child.BuildCommit,
            string.Join(", ", child.Resolution.ToolVariables.Select(pair => pair.Key + "=" + pair.Value)),
            string.Join(", ", child.Resolution.Missing), child.Error)).ToArray();

    public static void Write(TextWriter output, IReadOnlyList<ProjectFleetStatusRow> rows)
    {
        if (rows.Count == 0) { output.WriteLine("No enabled projects."); return; }
        output.WriteLine("NAME\tSTATE\tPID\tBUILD\tTOOL VARIABLES\tMISSING\tERROR");
        foreach (var row in rows)
            output.WriteLine(string.Join('\t', new[] { row.Name, row.State,
                row.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-", row.BuildCommit ?? "-",
                row.ToolVariables, row.Missing, row.Error ?? "" }.Select(Cell)));
    }

    private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    internal static void WriteFile(string path, IReadOnlyList<ProjectFleetStatusRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temporary)) Write(writer, rows);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
