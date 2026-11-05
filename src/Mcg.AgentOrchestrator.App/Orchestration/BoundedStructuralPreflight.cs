namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoundedStructuralPreflight
{
    internal static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(10);

    internal static DeveloperCompletionStructuralFindings Run(Func<DeveloperCompletionStructuralFindings> evaluate,
        TimeSpan? bound = null, CancellationToken cancellationToken = default, TimeProvider? time = null)
    {
        var work = Task.Run(evaluate, CancellationToken.None);
        try
        {
            return work.WaitAsync(bound ?? DefaultBound, time ?? TimeProvider.System, cancellationToken)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // Abandoned scans may finish, but can never publish evidence to the caller.
            _ = work.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
