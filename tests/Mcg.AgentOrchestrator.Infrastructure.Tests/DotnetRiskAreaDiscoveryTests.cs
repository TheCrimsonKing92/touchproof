using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns a unique on-disk fixture and never changes process-global state.
public sealed class DotnetRiskAreaDiscoveryTests
{
    private const string Unit = "src/Sample.Library/Sample.Library.csproj";

    [Fact]
    public void Discover_ExplicitRisks_PreservesEvidencePathsAndLines()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Unit), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(fixture.Root, "src/Sample.Library/appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(fixture.Root, "src/Sample.Library/PublicAPI.Shipped.txt"), "Sample.Api");

        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(3, model.RiskAreas.Count);
        AssertRisk(model, ProjectRiskAreaKind.Persistence, "Microsoft.EntityFrameworkCore.Sqlite", Unit, 3);
        AssertRisk(model, ProjectRiskAreaKind.Settings, "src/Sample.Library/appsettings.json", "src/Sample.Library/appsettings.json", 1);
        AssertRisk(model, ProjectRiskAreaKind.PublicApi, "src/Sample.Library/PublicAPI.Shipped.txt", "src/Sample.Library/PublicAPI.Shipped.txt", 1);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.StartsWith("riskAreas/", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_NoDeclarations_ProducesNoRiskAreas()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        Assert.Empty(new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).RiskAreas);
    }

    [Fact]
    public void Discover_OnlyTrackedSidecarsBesideUnit_AreLearned()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        try
        {
            File.WriteAllText(Path.Combine(fixture.Root, "src/Sample.Library/appsettings.Development.json"), "{}");
            foreach (var arguments in new[] { new[] { "init", "-q" }, new[] { "add", "-A" } })
            {
                var git = GitCli.Run(fixture.Root, arguments);
                Assert.True(git.ProcessStarted && git.Succeeded && !git.DrainTimedOut, git.Error);
            }
            File.WriteAllText(Path.Combine(fixture.Root, "src/Sample.Library/appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(fixture.Root, "src/Sample.Library/PublicAPI.Shipped.txt"), "Untracked.Api");
            File.WriteAllText(Path.Combine(fixture.Root, "appsettings.json"), "{}");

            var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
            Assert.Single(model.RiskAreas);
            AssertRisk(model, ProjectRiskAreaKind.Settings, "src/Sample.Library/appsettings.Development.json",
                "src/Sample.Library/appsettings.Development.json", 1);
            Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == "discovery/tracked-files");
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(Path.Combine(fixture.Root, ".git"));
        }
    }

    [Fact]
    public void Discover_ConditionalPackageAndWebSdk_AskRatherThanClaimCertainty()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Unit), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup Condition="'$(UseDatabase)' == 'true'">
                <PackageReference Include="Dapper" Version="2.0" />
              </ItemGroup>
            </Project>
            """);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(2, model.RiskAreas.Count);
        Assert.All(model.RiskAreas, area => Assert.Equal(FactConfidence.Low, area.Evidence.Confidence));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"riskAreas/{Unit}/persistence" && question.Source == new FactSource(Unit, 3));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"riskAreas/{Unit}/publicApi" && question.Source == new FactSource(Unit, 1));
    }

    [Fact]
    public void Discover_ReliableEvidenceWinsAndPackagePrefixMustHaveBoundary()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Unit), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Dapper" Condition="'$(UseDatabase)' == 'true'" />
                <PackageReference Include="Npgsql" />
                <PackageReference Include="Dapperish" />
              </ItemGroup>
            </Project>
            """);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Single(model.RiskAreas);
        AssertRisk(model, ProjectRiskAreaKind.Persistence, "Npgsql", Unit, 4);
        File.WriteAllText(Path.Combine(fixture.Root, Unit), "<Project><ItemGroup><PackageReference Include=\"Dapperish\" /></ItemGroup></Project>");
        Assert.Empty(new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).RiskAreas);
    }

    private static void AssertRisk(ProjectModel model, ProjectRiskAreaKind kind, string value, string path, int line)
    {
        var risk = Assert.Single(model.RiskAreas, area => area.Kind == kind);
        Assert.Equal(Unit, risk.UnitId);
        Assert.Equal(value, risk.Evidence.Value);
        Assert.Equal(new FactSource(path, line), risk.Evidence.Source);
        Assert.Equal(FactConfidence.High, risk.Evidence.Confidence);
    }
}
