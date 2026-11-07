using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: nested scenarios retain their private roots and fixture owners. One outer
// capacity lease covers the run; nested InitializeAsync must never acquire a second lease.
public sealed class HeavyFixtureGitLaunchReductionTests(ITestOutputHelper output) : HostCapacityBoundTestBase
{
    private const int ScenarioCompletionHangFailsafeMilliseconds = 1_800_000;

    [Fact(Timeout = ScenarioCompletionHangFailsafeMilliseconds)] // Hang detector: all 169 scenario rows must complete.
    public async Task FiveClasses_ReduceHistoricalAggregateLaunchesByTwentyFivePercent()
    {
        // gate 208e909d-0-20261106160206117, main 0798b6829, first-run slot-timing rows.
        // Includes all setup and reader-start LaunchObserver events; retries excluded.
        // Bound operator answer f9e25b4c9cf64f44a92318e95bcfd93d.
        // Launches stay historical; Rows pins current coverage, including cases added since the baseline.
        var baseline = new (Type Class, int Launches, int Rows)[]
        {
            (typeof(AcceptanceCohortWorkflowTestsTrainRedAttribution), 1530, 43),
            (typeof(MergeTrainFocusedRecheckReceiptAcceptanceTests), 1349, 31),
            (typeof(AcceptanceCohortWorkflowTestsMergeTrain), 1042, 20),
            (typeof(PullRequestTrunkDriftTests), 959, 20),
            (typeof(CliCommandTestsTerminalSweepCommands), 930, 55)
        };
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(HeavyFixtureGitLaunchReductionTests));
        var total = 0;
        foreach (var entry in baseline)
        {
            using var candidates = new CohortCandidateTemplate();
            using var setup = new WorktreeSetupTemplate();
            var reads = new ConcurrentDictionary<string, int>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            void ObserveRead(string directory)
            {
                if (HostCapacitySlotTimingWriter.CurrentTestName() == identity)
                    reads.AddOrUpdate(directory, 1, (_, count) => count + 1);
            }
            GitRevisionReader.Shared.SuccessfulGitRead += ObserveRead;
            var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
            var rows = 0;
            try
            {
                foreach (var method in entry.Class.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(m => m.Name))
                {
                    var fact = method.GetCustomAttribute<FactAttribute>(inherit: true);
                    if (fact is null) continue;
                    Assert.Null(fact.Skip);
                    var inline = method.GetCustomAttributesData().Where(a => a.AttributeType == typeof(InlineDataAttribute)).ToArray();
                    Assert.False(fact is TheoryAttribute && inline.Length == 0,
                        $"The launch census must explicitly support data discovery for {entry.Class.Name}.{method.Name}.");
                    IEnumerable<object?[]> data = inline.Length == 0 ? [Array.Empty<object?>()] : inline.Select(attribute =>
                        ((IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments[0].Value!).Select(argument =>
                            argument.ArgumentType.IsEnum ? Enum.ToObject(argument.ArgumentType, argument.Value!) : argument.Value).ToArray());
                    foreach (var arguments in data)
                    {
                        var constructor = Assert.Single(entry.Class.GetConstructors());
                        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(CohortCandidateTemplate)
                            ? (object)candidates : parameter.ParameterType == typeof(WorktreeSetupTemplate) ? setup :
                            parameter.ParameterType == typeof(ITestOutputHelper) ? output :
                            throw new InvalidOperationException($"Unowned fixture parameter {parameter.ParameterType}.")).ToArray();
                        var instance = constructor.Invoke(parameters);
                        try
                        {
                            object? result;
                            try { result = method.Invoke(instance, arguments); }
                            catch (TargetInvocationException ex) when (ex.InnerException is not null)
                            {
                                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                                throw;
                            }
                            if (result is Task task) await task;
                            else if (result is ValueTask valueTask) await valueTask;
                            else Assert.Null(result);
                            rows++;
                        }
                        finally
                        {
                            if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                            else if (instance is IDisposable disposable) disposable.Dispose();
                        }
                    }
                }
            }
            finally
            {
                GitRevisionReader.Shared.SuccessfulGitRead -= ObserveRead;
                var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
                output.WriteLine($"{entry.Class.Name}: rows={rows}; launches={delta.Launches}; historical={entry.Launches}");
                output.WriteLine("subcommands: " + string.Join(", ", delta.Subcommands.OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{pair.Key}={pair.Value}")));
                output.WriteLine("top callers: " + string.Join(", ", (delta.Callers ?? new Dictionary<string, int>())
                    .OrderByDescending(pair => pair.Value).Take(5).Select(pair => $"{pair.Key}={pair.Value}")));
                output.WriteLine($"successful file-miss reads by repository: once={reads.Count(pair => pair.Value == 1)}; " +
                    $"repeated={reads.Count(pair => pair.Value > 1)}; reads={reads.Values.Sum()}");
                total += delta.Launches;
            }
            Assert.Equal(entry.Rows, rows); // A smaller selected set must never masquerade as savings.
        }
        var historicalTotal = baseline.Sum(entry => entry.Launches);
        Assert.Equal(5810, historicalTotal);
        output.WriteLine($"aggregate={total}; historical={historicalTotal}; maximum={historicalTotal * 0.75}");
        Assert.True(total * 4L <= historicalTotal * 3L,
            $"Five-class git launches {total} exceed 75% of the committed 0798b6829 baseline {historicalTotal}.");
    }
}
