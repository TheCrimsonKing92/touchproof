namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProcessHandleFaultClassifier
{
    private const string MessagePrefix = "Process was not started by this object";
    private const int MaxInnerExceptionDepth = 8;

    internal static ProcessHandleFaultDisposition Classify(Exception? exception) =>
        FindFault(exception) is null
            ? ProcessHandleFaultDisposition.NotEnvironmental
            : ProcessHandleFaultDisposition.BoundedRetry;

    internal static Exception? FindFault(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth <= MaxInnerExceptionDepth; depth++, exception = exception.InnerException)
        {
            if (exception is InvalidOperationException and not RecordedAcceptanceFault { IsProcessHandleFault: false } &&
                exception.Message?.StartsWith(MessagePrefix, StringComparison.Ordinal) == true)
            {
                return exception;
            }
        }

        return null;
    }
}
