using System.Security;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: scope reads and file probes are fakes; the process variable has a unique name.
public sealed class ProjectEnvironmentResolverTests
{
    [Fact]
    public void Resolve_UserOverrideAndPathMerge_UsesOnlyExplicitScopes()
    {
        var resolver = new ProjectEnvironmentResolver(
            () => new Dictionary<string, string> { ["JAVA_HOME"] = "machine", ["Path"] = "machine-a;;machine-b" },
            () => new Dictionary<string, string> { ["java_home"] = "user", ["PATH"] = "user-a;user-b;" });
        var result = resolver.Resolve([new("JDK", "JAVA_HOME")]);
        Assert.Equal("user", result.Environment["JAVA_HOME"]);
        Assert.Equal("machine-a;machine-b;user-a;user-b", result.Environment["Path"]);
        Assert.Equal("user", result.ToolVariables["JAVA_HOME"]);
        Assert.Empty(result.Missing);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Resolve_ProcessOnlyVariable_RemainsMissingAndCannotExpandScopeValues()
    {
        var name = "MCG_FLEET_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "process-only");
        try
        {
            Assert.Equal("process-only", Environment.GetEnvironmentVariable(name));
            var resolver = new ProjectEnvironmentResolver(() => new Dictionary<string, string>
                { ["INDIRECT"] = "%" + name + "%" }, () => new Dictionary<string, string>());
            var result = resolver.Resolve([new("JDK", name), new("indirect-tool", "INDIRECT")]);
            Assert.False(result.Environment.ContainsKey(name));
            Assert.Empty(result.ToolVariables);
            Assert.Equal(new[] { $"JDK ({name})", "indirect-tool (INDIRECT)" }, result.Missing);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Resolve_ExpansionAndToolOnlyRequirement_UsesMergedPathAndPathExt()
    {
        var probed = new List<string>();
        var resolver = new ProjectEnvironmentResolver(
            () => new Dictionary<string, string> { ["JAVA_HOME"] = "old", ["Path"] = "%JAVA_HOME%/bin" },
            () => new Dictionary<string, string> { ["JAVA_HOME"] = "new", ["PATHEXT"] = ".EXE" },
            file => { probed.Add(file); return file == Path.Combine("new/bin", "lua.EXE"); });
        var result = resolver.Resolve([new("lua"), new("gradle")]);
        Assert.Equal("new/bin", result.Environment["Path"]);
        Assert.Contains(Path.Combine("new/bin", "lua.EXE"), probed);
        Assert.Equal(new[] { "gradle" }, result.Missing);
    }

    [Fact]
    public void Resolve_AbsentVariable_ReportsToolAndVariableNames()
    {
        var resolver = new ProjectEnvironmentResolver(() => new Dictionary<string, string>(),
            () => new Dictionary<string, string>());
        Assert.Equal(new[] { "JDK (JAVA_HOME)" }, resolver.Resolve([new("JDK", "JAVA_HOME")]).Missing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_UnreadableScope_ReturnsFailureInsteadOfPartialSuccess(bool userFails)
    {
        IReadOnlyDictionary<string, string> Fail() => throw new SecurityException("scope denied");
        IReadOnlyDictionary<string, string> Good() => new Dictionary<string, string> { ["JAVA_HOME"] = "valid" };
        var resolver = new ProjectEnvironmentResolver(userFails ? Good : Fail, userFails ? Fail : Good);
        var result = resolver.Resolve([new("JDK", "JAVA_HOME")]);
        Assert.Equal("scope denied", result.Error);
        Assert.Equal(new[] { "JDK (JAVA_HOME)" }, result.Missing);
    }

    [Fact]
    public void Resolve_CyclicExpansion_ReturnsMissingInsteadOfRecursingForever()
    {
        var resolver = new ProjectEnvironmentResolver(() => new Dictionary<string, string>
            { ["A"] = "%B%", ["B"] = "%A%" }, () => new Dictionary<string, string>());
        Assert.Equal(new[] { "tool (A)" }, resolver.Resolve([new("tool", "A")]).Missing);
    }
}
