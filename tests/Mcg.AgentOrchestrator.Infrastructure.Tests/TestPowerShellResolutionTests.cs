using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class TestPowerShellResolutionTests
{
    [Fact]
    public void EveryPwshFixtureUsesARealInstalledShell()
    {
        using var junit = new JunitCommandEnvironmentFixture("FIXTURE_" + Guid.NewGuid().ToString("N"), false);
        foreach (var executable in new[]
        {
            TestPowerShell.Executable,
            DotnetBuildEnvironmentManagerTests.ResolvePowerShell(),
            TestPowerShell.ForTheoryToken("pwsh"),
            MtpTestRunnerScriptTestsEnvironmentState.OuterShell,
            junit.Shell
        })
        {
            Assert.True(Path.IsPathFullyQualified(executable),
                $"Install a real pwsh 7; resolved shell is not absolute: {executable}");
            Assert.True(File.Exists(executable),
                $"Install a real pwsh 7; resolved shell does not exist: {executable}");
            Assert.False(WorkerShell.IsWindowsAppsPath(executable));
        }

        Assert.Equal("powershell.exe", TestPowerShell.ForTheoryToken("powershell.exe"));
    }

    [Fact]
    public void EnvironmentState_OuterShellUsesSharedResolverWithoutPathScan()
    {
        Assert.Equal(TestPowerShell.Executable, MtpTestRunnerScriptTestsEnvironmentState.OuterShell);
        var snapshot = RepositorySourceSnapshotCache.Get(VerifiedRepositoryRoot.Find());
        var root = snapshot.GetSyntaxTree(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTestsEnvironmentState.cs").GetRoot();
        var launch = Assert.Single(root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(node => node.Left.ToString() == "FileName"));
        Assert.Equal("OuterShell", launch.Right.ToString());
        var accessor = Assert.Single(root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText == "OuterShell"));
        Assert.Equal("TestPowerShell.Executable", accessor.ExpressionBody?.Expression.ToString());
        Assert.DoesNotContain(root.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            node => node.Name.Identifier.ValueText == "PathSeparator");
        Assert.DoesNotContain(root.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            node => node.Token.ValueText == "pwsh.exe");
        Assert.DoesNotContain(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            node => node.Identifier.ValueText == "ResolvePwsh");
    }
}
