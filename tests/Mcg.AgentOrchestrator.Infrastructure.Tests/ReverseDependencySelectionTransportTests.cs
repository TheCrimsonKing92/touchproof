using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

// Parallel-safe: private source and response-file trees; command composition only, no processes.
public sealed class ReverseDependencySelectionTransportTests
{
    [Theory]
    [InlineData(8, false)]
    [InlineData(96, true)]
    [InlineData(300, true)]
    [InlineData(1000, true)]
    [InlineData(2000, true)]
    [InlineData(5000, true)]
    public void PlannerCheck_AtCapacity_UsesExistingLauncherTransport(int count, bool spills)
    {
        using var fixture = Create(count, out var names);
        var check = InfrastructureCheck(fixture);
        Assert.Equal(names, check.TestClassSelections);
        var filter = check.Command[check.Command.ToList().IndexOf("--filter") + 1];
        Assert.Equal(string.Join("|", names.Select(name => "FullyQualifiedName~" + name)), filter);

        var manifest = DotnetTestCheckTranslator.FromImpactCheck(fixture.Root, check);
        var settings = new AcceptanceGateEngineSettings { MtpInvocations = [new()
        {
            Project = Fixture.Project,
            ExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
            Arguments = ["{executable}", "--minimum-expected-tests", "1", "--results-directory", "{resultsDirectory}",
                "--report-trx-filename", "{trxFileName}"]
        }] };
        var environment = new DotnetBuildEnvironment("fixture", fixture.Root, fixture.Root,
            Path.Combine(fixture.Root, "lock"), [], "fixture");
        var args = AcceptanceCheckCommandBuilder.BuildMtpTestArguments(manifest, settings, environment,
            new([Path.Combine(fixture.Root, "results", "transport.trx")], []), false,
            (_, expression) => AcceptanceCheckCommandBuilder.TranslateMtpFilter(expression));
        var quotedLength = args.Sum(argument => (long)argument.Length + 2) + args.Length - 1;
        Assert.Equal(spills, quotedLength > AcceptanceMtpFilterResponseFile.InlineCommandBudget);

        var invocation = AcceptanceMtpFilterResponseFile.Apply(args);
        var expected = names.SelectMany(name => new[] { "--filter-class", "*" + name + "*" }).ToArray();
        var effective = invocation.SelectMany(argument => argument.StartsWith('@')
            ? File.ReadAllText(argument[1..]).Split('\n') : new[] { argument }).ToArray();
        Assert.Equal(expected, effective.Skip(Array.IndexOf(effective, "--filter-class")));
        if (spills)
        {
            var reference = Assert.Single(invocation.Where(argument => argument.StartsWith('@')));
            Assert.DoesNotContain("--filter-class", invocation);
            Assert.Equal(expected, File.ReadAllText(reference[1..]).Split('\n'));
        }
        else
        {
            Assert.Equal(args, invocation);
            Assert.DoesNotContain(invocation, argument => argument.StartsWith('@'));
            Assert.Equal(expected, invocation.Skip(Array.IndexOf(invocation, "--filter-class")));
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.rsp", SearchOption.AllDirectories));
        }
        if (OperatingSystem.IsWindows())
        {
            var start = AcceptanceShellProcessStartInfo.Build(args, fixture.Root,
                @"\\.\pipe\transport-out", @"\\.\pipe\transport-err", true, null, false, out _);
            Assert.InRange(start.Arguments.Length, 1, AcceptanceCmdCommandLineLimit.MaxCmdCommandLineLength);
            Assert.Equal(spills, start.Arguments.Contains('@'));
        }

        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems("Infrastructure.Tests", filter, out var items));
        var expressions = items.Select(item => item[(item.IndexOf(':') + 1)..]).ToArray();
        Assert.All(expressions, expression => Assert.InRange(expression.Length, 1, 1024));
        Assert.Equal(filter, string.Join("|", expressions.Select(expression => expression.Trim())));
    }

    [Fact]
    public void PlannerCheck_OneThousandAndOneClasses_PreservesCompleteSelection()
    {
        using var fixture = Create(1001, out var names);
        var check = InfrastructureCheck(fixture);
        Assert.Contains("--filter", check.Command);
        Assert.Equal(names, check.TestClassSelections);
        Assert.Contains(string.Join("|", names.Select(name => "FullyQualifiedName~" + name)), check.Command);
    }

    [Fact]
    public void Selection_AcrossFilesAndHops_PreservesEveryClass()
    {
        using var fixture = Create(1001, out var names);
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ZSupport.cs",
            "public class Support { private NewContract contract; }");
        fixture.Test("LateConsumerTests", "Support");
        var selected = ReverseDependencyTestImpactReader.Read(fixture.Root, [Fixture.Source], bypassCache: true);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Null(selected.DegradationKind);
        Assert.Equal(names.Append("LateConsumerTests").Order(StringComparer.Ordinal), selected.TestClassNames);
        Assert.Equal(1002, selected.Headroom!.SelectedTestClassCount);
    }

    private static RepositoryTestImpactCheck InfrastructureCheck(Fixture fixture) =>
        Assert.Single(RepositoryTestImpactPlanner.Plan([Fixture.Source], fixture.Root).Checks,
            check => check.TestProject == RepositoryTestProject.Infrastructure);

    private static Fixture Create(int count, out string[] names)
    {
        var fixture = new Fixture();
        fixture.Write(Fixture.Source, "public class NewContract {}");
        names = Enumerable.Range(0, count).Select(index => $"TransportConsumerContractFixture{index:D4}Tests").ToArray();
        var batch = 0;
        foreach (var chunk in names.Chunk(250))
            fixture.Write($"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Consumers{batch++}.cs",
                string.Join("\n", chunk.Select(name => $"public class {name} {{ [Fact] public void Works() {{ _ = new NewContract(); }} }}")));
        return fixture;
    }
}
