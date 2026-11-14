using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test owns a unique fixture directory; discovery does not execute learned commands.
public sealed class DotnetVerificationKindDiscoveryTests
{
    private const string Library = "src/Sample.Library/Sample.Library.csproj";
    private const string Tests = "tests/Sample.VstestTests/Sample.VstestTests.csproj";

    [Fact]
    public void Discover_LearnedRunnerDesktopAndServiceHealth_PreserveEvidenceAndUncertainty()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Library), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        var unitTests = Assert.Single(model.Verifications, verification => verification.UnitId == Tests);
        Assert.Equal(UnitVerificationKind.UnitTests, unitTests.Kind);
        var runner = model.TestSetups.Single(setup => setup.UnitId == Tests).Runner;
        Assert.Equal("VSTest", unitTests.Evidence.Value);
        Assert.Equal(runner, unitTests.Evidence);
        Assert.Equal(FactConfidence.High, unitTests.Evidence.Confidence);
        Assert.Equal(new FactSource(Tests, 8), unitTests.Evidence.Source);

        var desktop = Assert.Single(model.Verifications, verification => verification.UnitId == Library && verification.Kind == UnitVerificationKind.DesktopUi);
        Assert.Equal("UseWPF", desktop.Evidence.Value);
        Assert.Equal(new FactSource(Library, 3), desktop.Evidence.Source);
        Assert.Equal(FactConfidence.High, desktop.Evidence.Confidence);

        var health = Assert.Single(model.Verifications, verification => verification.UnitId == Library && verification.Kind == UnitVerificationKind.ServiceHealth);
        Assert.Equal("Microsoft.NET.Sdk.Web", health.Evidence.Value);
        Assert.Equal(new FactSource(Library, 1), health.Evidence.Source);
        Assert.Equal(FactConfidence.Low, health.Evidence.Confidence);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"verifications/{Library}/serviceHealth" && question.Source == health.Evidence.Source);
    }

    [Fact]
    public void Discover_UndeterminedRunner_DoesNotInventUnitTestVerification()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal("undetermined", Assert.Single(model.TestSetups).Runner.Value);
        Assert.Empty(model.Verifications);
    }

    [Theory]
    [InlineData("<PropertyGroup><UseWindowsForms>true</UseWindowsForms></PropertyGroup>", "UseWindowsForms")]
    [InlineData("<PropertyGroup><UseMaui>true</UseMaui></PropertyGroup>", "UseMaui")]
    [InlineData("<ItemGroup><PackageReference Include=\"Avalonia\" /></ItemGroup>", "Avalonia")]
    [InlineData("<ItemGroup><PackageReference Include=\"Avalonia.Desktop\" /></ItemGroup>", "Avalonia.Desktop")]
    public void Discover_OtherExplicitDesktopDeclarations_AreHighConfidence(string declaration, string evidence)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Library), $"<Project>\n{declaration}\n</Project>");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var desktop = Assert.Single(model.Verifications, verification => verification.UnitId == Library);
        Assert.Equal(UnitVerificationKind.DesktopUi, desktop.Kind);
        Assert.Equal(evidence, desktop.Evidence.Value);
        Assert.Equal(FactConfidence.High, desktop.Evidence.Confidence);
        Assert.Equal(new FactSource(Library, 2), desktop.Evidence.Source);
    }

    [Fact]
    public void Discover_ConditionalDesktopAndWorkerSdk_RequireOwnerConfirmation()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Library), """
            <Project Sdk="Microsoft.NET.Sdk.Worker">
              <PropertyGroup Condition="'$(Desktop)' == 'true'">
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var verifications = model.Verifications.Where(verification => verification.UnitId == Library).ToArray();
        Assert.Equal(2, verifications.Length);
        Assert.All(verifications, verification => Assert.Equal(FactConfidence.Low, verification.Evidence.Confidence));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"verifications/{Library}/desktopUi" && question.Source == new FactSource(Library, 3));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"verifications/{Library}/serviceHealth" && question.Source == new FactSource(Library, 1));
    }

    [Fact]
    public void Discover_DisabledDesktopAndMalformedProject_ProduceNoNewVerification()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Library), "<Project><PropertyGroup><UseWPF>false</UseWPF></PropertyGroup></Project>");
        Assert.DoesNotContain(new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).Verifications, verification => verification.UnitId == Library);
        File.WriteAllText(Path.Combine(fixture.Root, Library), "<Project>");
        Assert.DoesNotContain(new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).Verifications, verification => verification.UnitId == Library);
    }
}
