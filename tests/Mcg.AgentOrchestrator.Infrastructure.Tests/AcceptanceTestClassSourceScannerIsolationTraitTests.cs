using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory source fixtures.
public sealed class AcceptanceTestClassSourceScannerIsolationTraitTests
{
    [Fact]
    public void ResolvesDirectAndInheritedTraitsAlongsideCollections()
    {
        var scanned = AcceptanceTestClassSourceScanner.ScanSourceTexts([
            """
            namespace Tests;
            internal static class ConstraintNames { internal const string Spawn = "ProcessSpawning"; }
            [Xunit.Collection("Shared")]
            [Xunit.TraitAttribute("Isolation", ConstraintNames.Spawn)]
            public abstract class Base { [Xunit.Fact] public void InheritedFact() {} }
            """,
            """
            namespace Tests;
            public class Derived : Base {}
            [Xunit.Trait("Isolation", "EnvMutation")]
            public class Overridden : Base {}
            [Trait("Isolation", "ProcessSpawning"), Trait("Category", "Unit")]
            public class Direct { [Fact] public void Runs() {} }
            [Trait("Category", "ProcessSpawning")]
            public class Untagged { [Fact] public void Runs() {} }
            """
        ]);

        Assert.Equal<AcceptanceTestClassDescriptor>(
            [new("Tests.Derived", "Shared", "ProcessSpawning"), new("Tests.Direct", null, "ProcessSpawning"),
                new("Tests.Overridden", "Shared", "EnvMutation"), new("Tests.Untagged", null)], scanned);
    }

    [Fact]
    public void ConflictingPartialDeclarationsReportClassAndBothConstraints()
    {
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceTestClassSourceScanner.ScanSourceTexts([
            """[Trait("Isolation", "ProcessSpawning")] public partial class Conflict { [Fact] public void Runs() {} }""",
            """[Trait("Isolation", "EnvMutation")] public partial class Conflict {}"""
        ]));
        Assert.Contains("Conflict", error.Message);
        Assert.Contains("ProcessSpawning", error.Message);
        Assert.Contains("EnvMutation", error.Message);
    }

    [Fact]
    public void UnresolvableIsolationTraitReportsClass()
    {
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceTestClassSourceScanner.ScanSourceTexts([
            """[Trait("Isolation", Missing.Name)] public class Unresolved { [Fact] public void Runs() {} }"""
        ]));
        Assert.Contains("Cannot resolve Isolation trait on 'Unresolved'", error.Message);
    }
}
