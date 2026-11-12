using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SandboxPreparationStartStatus
{
    internal static void RecordNote(Goal goal, TaskId taskId, Action<GoalId, TaskId, string> record)
    {
        if (goal.Tasks.FirstOrDefault(task => task.Id == taskId) is { } task &&
            SandboxPreparationFailure.ReadLatest(task) is { } failure)
            record(goal.Id, taskId, $"Preparation failed: {failure.FirstLine}");
    }

    internal static string RecordFailedNotes(Goal goal, Action<GoalId, TaskId, string> record)
    {
        var tasks = goal.Tasks.Where(task => task.Status == WorkTaskStatus.Failed).ToArray();
        foreach (var task in tasks) RecordNote(goal, task.Id, record);
        return string.Concat(tasks.Select(SandboxPreparationFailure.Suffix));
    }

    internal static string? RunningReason(Goal goal)
    {
        foreach (var task in goal.Tasks)
        {
            if (task.LastProcess is not { IsRunning: true, ChildProcessId: null } process ||
                !process.StandardOutputPath.EndsWith(".out.log", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = process.StandardOutputPath[..^8];
            try
            {
                var prep = JsonSerializer.Deserialize<DispatchProcessHost.DispatchPrepRecord>(
                    File.ReadAllText(stem + ".prep.json"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (prep is null || prep.GoalId != goal.Id.Value || prep.TaskId != task.Id.Value ||
                    !string.Equals(prep.WorkingDirectory, process.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(prep.ExitCodePath))
                {
                    if (File.ReadAllText(prep.ExitCodePath).Trim() == "0") continue;
                    var failed = SandboxPreparationFailure.Read(new TaskVerificationRecord(process.Command,
                        process.WorkingDirectory, 1, "", "", process.StartedAt,
                        StandardErrorPath: process.StandardErrorPath));
                    return "Worker sandbox preparation failed; awaiting reconciliation." +
                        (failed is null ? SandboxPreparationFailure.Suffix(task) : $" Preparation failed: {failed.FirstLine}");
                }
                return "Worker sandbox preparation in progress." + SandboxPreparationFailure.Suffix(task);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return null;
    }
}
