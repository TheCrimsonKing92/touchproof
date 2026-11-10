namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class BuildCpuMeter
{
    internal int Runs { get; private set; }
    internal double CpuMs { get; private set; }
    internal string? UnavailableReason { get; private set; }
    internal HashSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal AcceptanceDotnetBuildPhaseHooks Wrap(AcceptanceDotnetBuildPhaseHooks hooks) => hooks with
    {
        RunManagedCheckAsync = async (arguments, name, prepare, environment) =>
        {
            var run = await hooks.RunManagedCheckAsync(arguments, name, prepare, environment).ConfigureAwait(false);
            Runs++;
            foreach (var argument in arguments.Where(value => value.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ||
                         value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))) Projects.Add(argument);
            var sample = GateLaneReceipt.FromSummary(name, 0, TimeSpan.Zero, run.Result.ResultSummary,
                GateLoadSample.Unavailable("not-sampled")).CpuMilliseconds;
            if (sample.Value is { } cpu) CpuMs += cpu;
            else UnavailableReason ??= sample.UnavailableReason ?? "accounting-unavailable";
            return run;
        }
    };
}
