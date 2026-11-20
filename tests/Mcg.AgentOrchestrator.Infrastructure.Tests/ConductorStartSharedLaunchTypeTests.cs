using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using System.Reflection;

// Parallel-safe: immutable IL/reflection inputs only.
public sealed class ConductorStartSharedLaunchTypeTests
{
    [Fact]
    public void StartAndSelfRelaunch_CallTheSameHandleListLauncher()
    {
        var assembly = typeof(CliConductorCommand).Assembly;
        var index = DependencyIndexCache.Get(assembly);
        var cli = Assert.Single(index.Types, type => type.FullName == typeof(CliConductorCommand).FullName);
        Assert.Contains(cli.Methods.SelectMany(method => method.Calls), call =>
            call.DeclaringType.FullName == typeof(HandleListConductorProcessLauncher).FullName);
        foreach (var caller in new[] { typeof(ConductorLoopHandoff), typeof(HandleListConductorProcessLauncher) })
        {
            // Include compiler-generated local functions, where self-relaunch invokes the native call.
            var calls = index.Types.Where(type => type.FullName == caller.FullName ||
                    type.FullName.StartsWith(caller.FullName + "+", StringComparison.Ordinal) ||
                    type.FullName.StartsWith(caller.FullName + "/", StringComparison.Ordinal))
                .SelectMany(type => type.Methods.SelectMany(method => method.Calls)).ToArray();
            Assert.Contains(calls, call => call.DeclaringType.FullName ==
                typeof(Mcg.AgentOrchestrator.Infrastructure.HandleListProcessLauncher).FullName &&
                call.Name == "Launch");
        }
    }

    [Theory]
    [InlineData("CreateProcessW")]
    [InlineData("UpdateProcThreadAttribute")]
    public void NativeLaunchInterop_IsDeclaredOnlyBySharedType(string name)
    {
        var declarations = typeof(Mcg.AgentOrchestrator.Infrastructure.HandleListProcessLauncher).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.Name == name && (method.Attributes & MethodAttributes.PinvokeImpl) != 0).ToArray();
        Assert.Equal(typeof(Mcg.AgentOrchestrator.Infrastructure.HandleListProcessLauncher), Assert.Single(declarations).DeclaringType);
    }
}
