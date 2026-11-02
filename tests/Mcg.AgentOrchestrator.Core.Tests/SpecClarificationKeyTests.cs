using System.Text;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure key operations and read-only source inventory; no shared mutations.
public sealed class SpecClarificationKeyTests
{
    [Xunit.Theory]
    [Xunit.InlineData("api-version")]
    [Xunit.InlineData(" Mixed Case:topic ")]
    public void Build_FixedGoalAndTopic_PreservesExistingKeyBytes(string topicKey)
    {
        var goalId = new GoalId("a4cc1408141e4d89b496e99d79bfa2ea");
        var expected = $"spec-clarification:{goalId.Value}:{topicKey}";

        var actual = SpecClarificationKey.Build(goalId, topicKey);

        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    [Xunit.Fact]
    public void Prefix_StoredKeyFamily_RetainsExistingText()
    {
        Assert.Equal("spec-clarification:", SpecClarificationKey.Prefix);
    }

    [Xunit.Fact]
    public void IsSpecClarification_BuiltKey_RecognizesFamily()
    {
        var key = SpecClarificationKey.Build(new GoalId("a4cc1408141e4d89b496e99d79bfa2ea"), "api-version");

        Assert.True(SpecClarificationKey.IsSpecClarification(key));
    }

    [Xunit.Fact]
    public void IsSpecClarification_StoredForkAndGuid_RecognizesFamily()
    {
        const string key = "spec-clarification:a4cc1408141e4d89b496e99d79bfa2ea:external-contract:92ae882ad51f4cf2a0ffed06fc15d1f4";

        Assert.True(SpecClarificationKey.IsSpecClarification(key));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    [Xunit.InlineData("pr-changes-requested:abc:1")]
    [Xunit.InlineData("SPEC-CLARIFICATION:abc:topic")]
    public void IsSpecClarification_NonFamilyKey_ReturnsFalse(string? key)
    {
        Assert.False(SpecClarificationKey.IsSpecClarification(key));
    }

    [Xunit.Fact]
    public void SourceInventory_QuotedPrefix_HasExactlyOneOwner()
    {
        var root = VerifiedRepositoryRoot.Find();
        const string token = "\"spec-clarification:";
        string[] excludedDirectories = ["bin", "obj", ".scratch", ".orchestrator-prototype", "TestResults", "playwright-report"];
        var matches = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => excludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Where(path => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
            .ToArray();

        var owner = Assert.Single(matches);
        Assert.Equal("src/Mcg.AgentOrchestrator.Core/Domain/SpecClarificationKey.cs",
            Path.GetRelativePath(root, owner).Replace('\\', '/'));
        Assert.Equal(1, File.ReadAllText(owner).Split(token, StringSplitOptions.None).Length - 1);
    }
}
