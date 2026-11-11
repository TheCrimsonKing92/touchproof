using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each test owns its temporary tree; no processes or global state are used.
public sealed class PolicyTestCheckStackDispatchTests
{
    private const string CommandLine = "dotnet test --project tests/Sample.Tests/Sample.Tests.csproj";

    [Theory]
    [InlineData("crosswalk-test")]
    [InlineData("CROSSWALK-TEST")]
    public void ToManifestCheck_CrosswalkKind_PreservesIdentityAndArguments(string kind)
    {
        var input = new VerificationPolicyCheck("declared name", kind, true,
            "crosswalk-test web id-one id-two", "manifest check");
        var actual = Assert.IsType<AcceptanceManifestCheck>(
            PolicyTestCheckStackDispatch.ToManifestCheck("unused-worktree", input));

        Assert.Equal("declared name", actual.Name);
        Assert.Equal("crosswalk-test", actual.Type);
        Assert.Equal("web", actual.Project);
        Assert.Equal(new[] { "id-one", "id-two" }, actual.Arguments);
        Assert.Null(actual.Command);
    }

    [Fact]
    public void ToManifestCheck_CrosswalkRoot_DefaultsToAllDiscoveredTests()
    {
        var input = new VerificationPolicyCheck("root", "crosswalk-test", true, " crosswalk-test ", "root check");
        var actual = Assert.IsType<AcceptanceManifestCheck>(
            PolicyTestCheckStackDispatch.ToManifestCheck("unused-worktree", input));
        Assert.Null(actual.Project);
        Assert.Empty(actual.Arguments);
    }

    [Theory]
    [InlineData("crosswalk-test", "dotnet test")]
    [InlineData("future-test", "crosswalk-test .")]
    public void ToManifestCheck_UnknownKindOrMismatchedCrosswalk_ReturnsNull(string kind, string command)
    {
        Assert.Null(PolicyTestCheckStackDispatch.ToManifestCheck("unused-worktree",
            new("unsupported", kind, true, command, "negative control")));
    }

    [Fact]
    public void RequiredPolicy_CompiledCrosswalk_ReachesManifestDispatch()
    {
        var impact = new RepositoryTestImpactPlan(true, false, "crosswalk impact",
            [new("declared crosswalk", ["crosswalk-test", "web", "id-one"], "manifest check")]);
        var policy = VerificationPolicyCompiler.Compile(AgentRole.Developer, "tests", "tests", null, [], impact);

        Assert.True(policy.RequiresTests);
        Assert.Equal("crosswalk-test", Assert.Single(policy.Checks).Kind);
        var actual = Assert.Single(GoalAcceptanceVerifier.MapRequiredPolicyChecksForTests("unused-worktree", policy));
        Assert.Equal("declared crosswalk", actual.Name);
        Assert.Equal("crosswalk-test", actual.Type);
        Assert.Equal("web", actual.Project);
        Assert.Equal(new[] { "id-one" }, actual.Arguments);
    }

    [Theory]
    [InlineData("dotnet-test")]
    [InlineData("DOTNET-TEST")]
    public void ToManifestCheck_DotnetKind_MatchesAllTranslatorFields(string kind)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var input = new VerificationPolicyCheck("focused", kind, true, CommandLine, "focused tests");

            var actual = Assert.IsType<AcceptanceManifestCheck>(
                PolicyTestCheckStackDispatch.ToManifestCheck(root, input));
            var expected = Assert.IsType<AcceptanceManifestCheck>(
                DotnetTestCheckTranslator.TryFromCommandLine(root, input.Name, CommandLine));

            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Command, actual.Command);
            Assert.Equal(expected.Project, actual.Project);
            Assert.Equal(expected.Arguments.ToArray(), actual.Arguments.ToArray());
            Assert.Equal(expected.Runner, actual.Runner);
            Assert.Equal(expected.TimeoutMinutes, actual.TimeoutMinutes);
            Assert.Equal(expected.Advisory, actual.Advisory);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void ToManifestCheck_ExtraSpaces_ReturnsCheckWithoutProjectOrArguments()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var input = new VerificationPolicyCheck("spaces", "dotnet-test", true, "  dotnet   test  ", "tests");

            var check = Assert.IsType<AcceptanceManifestCheck>(
                PolicyTestCheckStackDispatch.ToManifestCheck(root, input));

            Assert.Null(check.Project);
            Assert.Empty(check.Arguments);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Theory]
    [InlineData("dotnet-test", "dotnet build x")]
    [InlineData("dotnet-test", "dotnet")]
    [InlineData("browser-smoke", "dotnet test")]
    [InlineData("command", "go test ./...")]
    [InlineData("go-test", "go test ./...")]
    public void ToManifestCheck_UnsupportedKindOrPrefix_ReturnsNull(string kind, string commandLine)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var input = new VerificationPolicyCheck("unsupported", kind, true, commandLine, "negative control");

            Assert.Null(PolicyTestCheckStackDispatch.ToManifestCheck(root, input));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void RequiredPolicy_MixedChecks_PreservesOnlySupportedRequiredChecksInOrder()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            VerificationPolicyCheck[] inputs =
            [
                new("focused", "dotnet-test", true, CommandLine, "tests"),
                new("upper", "DOTNET-TEST", true, CommandLine, "tests"),
                new("build", "dotnet-test", true, "dotnet build x", "negative control"),
                new("short", "dotnet-test", true, "dotnet", "negative control"),
                new("browser", "browser-smoke", true, "dotnet test", "negative control"),
                new("command", "command", true, "go test ./...", "negative control"),
                new("go", "go-test", true, "go test ./...", "negative control")
            ];
            var policy = new VerificationPolicy("mixed", true, false,
                [.. inputs, inputs[0] with { Name = "optional", Required = false }]);
            var first = Assert.IsType<AcceptanceManifestCheck>(
                PolicyTestCheckStackDispatch.ToManifestCheck(root, inputs[0]));
            var second = Assert.IsType<AcceptanceManifestCheck>(
                PolicyTestCheckStackDispatch.ToManifestCheck(root, inputs[1]));

            var actual = GoalAcceptanceVerifier.MapRequiredPolicyChecksForTests(root, policy);

            Assert.Collection(actual,
                check =>
                {
                    Assert.Equal(first.Name, check.Name);
                    Assert.Equal(first.Project, check.Project);
                },
                check =>
                {
                    Assert.Equal(second.Name, check.Name);
                    Assert.Equal(second.Project, check.Project);
                });
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
