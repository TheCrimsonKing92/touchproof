using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit.v3;

[assembly: TestClassCpuActivity]

// Track class activity and memory boundaries. The assembly fixture owns teardown output.
internal sealed class TestClassCpuActivityAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (LaunchLockTelemetry.Attribution.IsActive)
        {
            var key = Xunit.TestContext.Current.TestClass?.TestClassName;
            LaunchLockClassCpuLedger.Live.Enter(key);
            LaunchLockClassMemoryLedger.Live.Enter(key);
        }
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (LaunchLockTelemetry.Attribution.IsActive)
        {
            var key = Xunit.TestContext.Current.TestClass?.TestClassName;
            LaunchLockClassMemoryLedger.Live.Exit(key);
            LaunchLockClassCpuLedger.Live.Exit(key);
        }
    }
}
