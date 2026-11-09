using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    internal WorkerSkillTranscriptReader SkillTranscripts { get; init; } = WorkerSkillTranscriptReader.CreateDefault();

    private static void RecordSkillsReceipt(AgentOrchestratorKernel kernel, GoalId goal, TaskId task, string receipt)
    {
        try { kernel.RecordTaskNote(goal, task, receipt); }
        catch (Exception) { /* Telemetry must never fail refresh. */ }
    }
}
