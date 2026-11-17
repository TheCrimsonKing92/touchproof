using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ExperimentReplayRecordingContract
{
    internal sealed record Input(string Name, Func<TaskProcessSnapshot, bool> IsRecorded);

    internal const string WorkerDispatchCpuCapabilityId = "worker-dispatch-cpu";
    internal const string CpuMillisecondsInput = "cpu-milliseconds";
    internal const string PeakMemoryBytesInput = "peak-memory-bytes";
    internal const string AccountingSourceInput = "accounting-source";

    internal static ExperimentReplayRecordingContract WorkerDispatchCpu { get; } = new(
        WorkerDispatchCpuCapabilityId,
        [
            new(CpuMillisecondsInput, snapshot => snapshot.ResourceAccounting is { CpuMilliseconds: >= 0 }),
            new(PeakMemoryBytesInput, snapshot => snapshot.ResourceAccounting is { PeakMemoryBytes: > 0 }),
            new(AccountingSourceInput, snapshot => snapshot.ResourceAccounting is { } accounting
                && !string.IsNullOrWhiteSpace(accounting.AccountingSource))
        ]);

    internal ExperimentReplayRecordingContract(string capabilityId, IEnumerable<Input> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(inputs);

        var declarations = inputs.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in declarations)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Name) || input.IsRecorded is null)
                throw new ArgumentException("Each input requires a name and predicate.", nameof(inputs));
            if (!names.Add(input.Name))
                throw new ArgumentException("Input names must be unique.", nameof(inputs));
        }

        CapabilityId = capabilityId;
        Inputs = Array.AsReadOnly(declarations);
    }

    internal string CapabilityId { get; }
    internal IReadOnlyList<Input> Inputs { get; }

    internal IReadOnlyList<string> GetMissingInputs(TaskProcessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var missing = new List<string>();
        foreach (var input in Inputs)
        {
            if (!input.IsRecorded(snapshot))
                missing.Add(input.Name);
        }

        return missing.AsReadOnly();
    }
}
