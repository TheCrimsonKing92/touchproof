using Xunit;

[CollectionDefinition(ProcessErrorModeCollection.Name, DisableParallelization = true)]
public sealed class ProcessErrorModeCollection
{
    internal const string Name = "ProcessErrorModeRepairNoticeTests";
}
