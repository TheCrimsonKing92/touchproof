using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit.v3;

[assembly: TestClassCpuActivity]

// Only in-memory membership changes; the assembly fixture owns the sampler and output.
internal sealed class TestClassCpuActivityAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (LaunchLockTelemetry.Attribution.IsActive)
            LaunchLockClassCpuLedger.Live.Enter(Xunit.TestContext.Current.TestClass?.TestClassName);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (LaunchLockTelemetry.Attribution.IsActive)
            LaunchLockClassCpuLedger.Live.Exit(Xunit.TestContext.Current.TestClass?.TestClassName);
    }
}
