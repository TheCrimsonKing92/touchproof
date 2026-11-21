using Mcg.AgentOrchestrator.Infrastructure;
using static ImpactReplayChangedSupportReferenceScanTests;

// Each case owns the source files it scans.
public sealed class ImpactReplayChangedSupportFullRunFallbackTests
{
    [Theory]
    [InlineData(2, 20, "exceeds threshold", false, 3)]
    [InlineData(3, 20, null, false, 3)]
    [InlineData(3, 1, "truncated", true, 0)]
    [InlineData(0, 20, "invalid support closure threshold", false, 3)]
    public void UnboundedClosure_ForcesProjectFullRun(
        int threshold, int maximumVisited, string? reason, bool truncated, int closureCount)
    {
        using var fixture = new SourceFixture();
        fixture.Add("Support.cs", "namespace Support; public class Helpers { }");
        fixture.Add("Bridge.cs", "namespace Support; public class Bridge { Helpers value; }");
        fixture.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { Support.Helpers value; }");
        fixture.Add("BetaTests.cs", "namespace SampleTests; public class BetaTests { Support.Bridge value; }");
        fixture.Add("BothTests.cs", "namespace SampleTests; public class BothTests { Support.Bridge value; }");
        fixture.Add("elsewhere/Unrelated.cs", "public class Unrelated { }", infrastructure: false);

        var result = fixture.Select(["Support.cs"], threshold: threshold, maximumVisited: maximumVisited);

        Assert.Null(result.AbandonReason);
        var summary = Assert.Single(result.SupportSelections);
        Assert.Equal("Support.cs", summary.Path);
        Assert.Equal(1, summary.DirectMatchCount);
        Assert.Equal(closureCount, summary.ClosureCount);
        Assert.Equal(truncated, summary.Truncated);
        Assert.Equal(!truncated, summary.Complete);
        Assert.False(summary.Unresolved);
        Assert.Equal(reason is not null, summary.FullRunTriggered);
        Assert.Equal(truncated ? new[] { "Infrastructure.Tests.csproj", "Other.Tests.csproj" } :
            new[] { "Infrastructure.Tests.csproj" }, summary.AffectedProjects);
        if (reason is null) Assert.Empty(summary.Reasons);
        else Assert.Contains(reason, Assert.Single(summary.Reasons));
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("module")]
    [InlineData("registration-only")]
    [InlineData("initializer")]
    public void ChangedAssemblyHook_ForcesOnlyOwningProjectFullRun(string shape)
    {
        using var fixture = new SourceFixture();
        var source = shape switch
        {
            "assembly" => "[assembly: Hook] public class HookAttribute : System.Attribute { } public class Helpers { }",
            "module" => "[module: Hook] public class HookAttribute : System.Attribute { } public class Helpers { }",
            "registration-only" => "[assembly: Hook]",
            "initializer" => "using System.Runtime.CompilerServices; public static class Helpers { [ModuleInitializer] internal static void Init() { } }",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        fixture.Add("Support.cs", source);
        if (shape == "registration-only")
            fixture.Add("Hook.cs", "public class HookAttribute : System.Attribute { }");
        else
            fixture.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { object Type() => typeof(Helpers); }");
        fixture.Add("elsewhere/OtherTests.cs", "namespace SampleTests; public class OtherTests { }", infrastructure: false);

        var result = fixture.Select(["Support.cs"]);

        Assert.Null(result.AbandonReason);
        var summary = Assert.Single(result.SupportSelections);
        Assert.Equal("Support.cs", summary.Path);
        Assert.Equal(shape == "registration-only" ? 0 : 1, summary.DirectMatchCount);
        Assert.True(summary.FullRunTriggered);
        Assert.True(summary.Unresolved);
        Assert.False(summary.Complete);
        Assert.False(summary.Truncated);
        Assert.Equal("Infrastructure.Tests.csproj", Assert.Single(summary.AffectedProjects));
        Assert.Contains("assembly-wide hook", Assert.Single(summary.Reasons));
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("module")]
    [InlineData("initializer")]
    public void TransitiveAssemblyHook_ForcesFullRunInReferrerProject(string shape)
    {
        using var fixture = new SourceFixture();
        fixture.Add("Support.cs", "public class Helpers { }");
        fixture.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { Helpers value; }");
        fixture.Add("elsewhere/Bridge.cs", "public class Bridge { Helpers value; }", infrastructure: false);
        var hook = shape == "initializer"
            ? "using Init = System.Runtime.CompilerServices.ModuleInitializerAttribute; public static class Hook { [Init] internal static void Install() { _ = new Bridge(); } }"
            : $"[{shape}: Hook(typeof(Bridge))] public class HookAttribute(System.Type type) : System.Attribute {{ }}";
        fixture.Add("elsewhere/Hook.cs", hook, infrastructure: false);

        var result = fixture.Select(["Support.cs"]);

        Assert.Null(result.AbandonReason);
        Assert.Equal("SampleTests.AlphaTests", Assert.Single(result.Classes));
        var summary = Assert.Single(result.SupportSelections);
        Assert.Equal(1, summary.DirectMatchCount);
        Assert.Equal(1, summary.ClosureCount);
        Assert.True(summary.FullRunTriggered);
        Assert.True(summary.Unresolved);
        Assert.False(summary.Complete);
        Assert.Equal("Other.Tests.csproj", Assert.Single(summary.AffectedProjects));
        Assert.Contains("assembly-wide hook", Assert.Single(summary.Reasons));
    }

    [Theory]
    [InlineData("{}", 100)]
    [InlineData("""{"supportClosureClassThreshold":17}""", 17)]
    [InlineData("""{"supportClosureClassThreshold":"17"}""", 0)]
    [InlineData("""{"supportClosureClassThreshold":1.5}""", 0)]
    [InlineData("""{"supportClosureClassThreshold":null}""", 0)]
    [InlineData("""{"supportClosureClassThreshold":2147483648}""", 0)]
    public void Manifest_ProvidesDefaultOrPassesInvalidValueToSelection(string engine, int threshold)
    {
        var settings = AcceptanceGateEngineSettings.Parse("""{"engine":""" + engine + "}");

        Assert.Equal(threshold, settings.SupportClosureClassThreshold);
    }

    [Fact]
    public void LinkedClass_CountsOncePerProject()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Support.cs", "public class Helpers { }");
        fixture.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { Helpers value; }");
        var projects = fixture.Projects.ToArray();
        projects[1] = projects[1] with { Sources = projects[0].Sources };

        var result = ImpactReplayChangedSupportReferenceScan.Select(fixture.Root, ["Support.cs"], projects,
            () => fixture.Runnable, threshold: 1);

        var summary = Assert.Single(result.SupportSelections);
        Assert.Equal(2, summary.DirectMatchCount);
        Assert.Equal(2, summary.ClosureCount);
        Assert.True(summary.FullRunTriggered);
        Assert.Equal(new[] { "Infrastructure.Tests.csproj", "Other.Tests.csproj" }, summary.AffectedProjects);
    }
}
