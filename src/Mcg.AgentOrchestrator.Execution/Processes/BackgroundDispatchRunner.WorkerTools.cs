using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private static string WorkerToolsArtifactPrefix(AgentRole role, string prefix) =>
        WorkerToolsPromptSection.AppliesTo(role) ? $"{prefix}-{Guid.NewGuid():N}" : prefix;

    private void ApplyWorkerToolsEnvironment(AgentOrchestratorKernel kernel, GoalId goalId, TaskSpec task,
        TaskDispatchRecord dispatch, ProcessStartInfo info, string stdoutPath, bool lowIntegrity,
        IReadOnlyList<DeclaredToolHome>? toolHomes)
    {
        DeclaredToolHomeEnvironment.Apply(info.Environment, toolHomes);
        ConfigureWorkerToolsEnvironment(kernel, goalId, task, dispatch, info, stdoutPath, lowIntegrity);
    }

    private void ConfigureWorkerToolsEnvironment(AgentOrchestratorKernel kernel, GoalId goalId, TaskSpec task,
        TaskDispatchRecord dispatch, ProcessStartInfo info, string stdoutPath, bool lowIntegrity)
    {
        if (!WorkerToolsPromptSection.AppliesTo(task.RequiredRole))
            return;

        var telemetry = WorkerToolsTelemetry.PathFor(stdoutPath);
        info.Environment[WorkerToolsTelemetry.EnvironmentVariable] = telemetry;
        if (dispatch.WorkerToolsDirectory is { } directory)
        {
            info.Environment.TryGetValue("PATH", out var path);
            info.Environment["PATH"] = string.IsNullOrEmpty(path) ? directory : directory + Path.PathSeparator + path;
        }

        try
        {
            PrepareWorkerToolsDirectory(Path.GetDirectoryName(telemetry)!, lowIntegrity);
            if (lowIntegrity && dispatch.WorkerToolsDirectory is not null)
            {
                // The pinned wt uses this LOCALAPPDATA path for its index. Only that cache
                // is writable by Low workers. The published command stays at host integrity.
                info.Environment.TryGetValue("LOCALAPPDATA", out var inheritedLocalAppData);
                var localAppData = inheritedLocalAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                PrepareWorkerToolsDirectory(Path.Combine(localAppData, "Mcg.AgentOrchestrator", "tools", "worker-tools", "cache"), true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            kernel.RecordTaskNote(goalId, task.Id, $"WORKER_TOOLS_ARTIFACT_FAILED path={telemetry} error={ex.Message}");
        }
    }

    private void PrepareWorkerToolsDirectory(string path, bool lowIntegrity)
    {
        Directory.CreateDirectory(path);
        if (lowIntegrity && WriteIntegrityLabeler.Query(path) is not { Low: true, Inheritable: true } &&
            !WriteIntegrityLabeler.SetIntegrity(path, WorkerSandboxPreparer.LowInheritableLevel, recursive: false))
            throw new IOException($"Could not grant Low-integrity access to '{path}'.");
    }
}
