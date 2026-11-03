using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliOwnerDigestLessons
{
    internal static void WriteText(TextWriter writer, OrchestratorWorkspace workspace,
        DateTimeOffset since, DateTimeOffset until)
    {
        if (!File.Exists(workspace.OperatorLessonsStorePath)) return;
        var lessons = new SqliteOperatorLessonStore(workspace.OperatorLessonsStorePath).List(includeRetired: true);
        var recorded = lessons.Where(lesson => lesson.RecordedAt >= since && lesson.RecordedAt < until)
            .OrderBy(lesson => lesson.RecordedAt).ThenBy(lesson => lesson.Id).ToArray();
        var retired = lessons.Count(lesson => lesson.RetiredAt >= since && lesson.RetiredAt < until);
        if (writer is OwnerConsoleStructuredWriter summary) summary.Mark(OwnerConsoleFieldSnapshot.Create("lessons",
            new("recorded", "Recorded lessons", recorded.Length.ToString()), new("retired", "Retired lessons", retired.ToString())));
        writer.WriteLine($"Lessons: recorded={recorded.Length} retired={retired}");
        foreach (var lesson in recorded)
        {
            if (writer is OwnerConsoleStructuredWriter structured) structured.Mark(OwnerConsoleFieldSnapshot.Create(lesson.Id,
                new("id", "Lesson ID", lesson.Id), new("rule", "Rule", lesson.Rule), new("recorded", "Recorded at", lesson.RecordedAt.ToString("O")),
                new("retired", "Retired at", lesson.RetiredAt?.ToString("O"))));
            writer.WriteLine($"{lesson.Id} | {(lesson.Rule.Length <= 80 ? lesson.Rule : lesson.Rule[..79] + "…")}");
        }
    }
}
