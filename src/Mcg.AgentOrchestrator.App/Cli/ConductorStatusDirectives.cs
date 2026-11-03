using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStatusDirectives
{
    internal static void Print(OrchestratorWorkspace workspace, TextWriter output, TimeProvider time)
    {
        string line;
        try
        {
            var now = time.GetUtcNow();
            var active = new SqliteOperatorDirectiveStore(workspace.OperatorDirectivesStorePath)
                .List(now).Where(row => !row.Expired).ToArray();
            if (output is OwnerConsoleStructuredWriter structured)
            {
                var fields = new List<OwnerConsoleField> { new("count", "Active directives", active.Length.ToString()) };
                for (var i = 0; i < active.Length; i++)
                {
                    fields.Add(new(i + "/kind", "Directive kind", active[i].Kind.ToString()));
                    fields.Add(new(i + "/text", "Directive text", active[i].Text));
                }
                structured.Mark(new("directives", [.. fields]));
            }
            line = $"Owner directives: {active.Length} active";
            if (active.Length is >= 1 and <= 3)
                line += ": " + string.Join("; ", active.Select(row => $"{row.Kind}: {FirstLine(row.Text)}"));
        }
        catch (Exception) { line = "Owner directives: unavailable"; }
        output.WriteLine(line);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? text : text[..end]).Trim();
        return line.Length <= 80 ? line : line[..80];
    }
}
