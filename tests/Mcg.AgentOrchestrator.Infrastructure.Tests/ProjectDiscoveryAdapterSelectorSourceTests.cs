// Parallel-safe: read-only inspection of the explicitly verified candidate repository.
public sealed class ProjectDiscoveryAdapterSelectorSourceTests
{
    [Fact]
    public void Selector_DelegatesStackKnowledge_WithoutCopiedDetectionRules()
    {
        var directory = Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.Infrastructure", "Onboarding");
        var source = File.ReadAllText(Path.Combine(directory, "ProjectDiscoveryAdapterSelector.cs"));

        Assert.DoesNotContain("DetectLua", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceExtensions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lua go js jsx ts tsx", source, StringComparison.Ordinal);
        Assert.DoesNotContain("detect.go", source, StringComparison.Ordinal);
        foreach (var retired in new[] { "ProjectMarkers", "NoMarkerRefusal", "Detection" })
            Assert.DoesNotContain(retired, source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(?:public|internal|private)\s+(?:static\s+)?[\w<>?]+\s+Detect\s*\(", source);

        // Stack choice lives at the adapter construction seam; adapter-specific discovery may read files.
        var choosers = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path),
                @"\bnew\s+\w*ProjectDiscoveryAdapter\s*\("))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "ProjectDiscoveryAdapterSelector.cs" }, choosers);
        Assert.DoesNotMatch(@"\bDirectory\s*\.", source);
        Assert.Contains("CrosswalkEvidenceStackAmbiguity.ThrowIfAmbiguous", source, StringComparison.Ordinal);

        // Permit only the existing Go adapter availability guard, never another marker read.
        const string exists = "File.Exists(Path.Combine(repositoryRoot, \"go.mod\"))";
        const string module = "GoModReader.Read(Path.Combine(repositoryRoot, \"go.mod\"))";
        Assert.Contains(exists, source, StringComparison.Ordinal);
        Assert.Contains(module, source, StringComparison.Ordinal);
        var withoutGuard = source.Replace(exists, "", StringComparison.Ordinal).Replace(module, "", StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(?:File|GoModReader)\s*\.", withoutGuard);

        var ambiguity = File.ReadAllText(Path.Combine(directory, "CrosswalkEvidenceStackAmbiguity.cs"));
        Assert.DoesNotMatch(@"\b(?:File|Directory|GoModReader)\s*\.", ambiguity);
    }
}
