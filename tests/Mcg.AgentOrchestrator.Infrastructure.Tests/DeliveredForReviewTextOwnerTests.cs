using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Fixture-owned git repositories, a fake forge, and an isolated build-root class fixture.
public sealed class DeliveredForReviewTextOwnerTests : Xunit.IClassFixture<IsolatedDotnetRootFixture>
{
    [Theory]
    [InlineData(null, "delivered for review: https://github.com/octo/sample/pull/7")]
    [InlineData("touchproof[bot]", "delivered for review: https://github.com/octo/sample/pull/7 as touchproof[bot]")]
    [InlineData("", "delivered for review: https://github.com/octo/sample/pull/7 as ")]
    [InlineData(" ", "delivered for review: https://github.com/octo/sample/pull/7 as  ")]
    public void Text_LoginVariants_PreservesExactFormat(string? login, string expected)
    {
        Assert.Equal(expected, DeliveredForReviewHold.Text(PullRequestLandingTestFixture.Url, login));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("touchproof[bot]")]
    public void Text_DeliveryRecord_EqualsValueOverload(string? login)
    {
        var delivery = new GoalDeliveredForReview(7, PullRequestLandingTestFixture.Url,
            new string('a', 40), PullRequestLandingTestFixture.Attempt,
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), login);

        Assert.Equal(DeliveredForReviewHold.Text(delivery.Url, login), DeliveredForReviewHold.Text(delivery));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("touchproof[bot]")]
    public void Deliver_LoginVariants_ProducesCurrentHoldOnPublicationAndRepeat(string? login)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var workspace = fixture.Workspace with { ForgeIdentity = login is null ? null : "configured-settings" };
        var provider = new Provider(login);
        var publisher = new GitHubPullRequestPublisher(fixture.Forge, provider);
        var result = login is null ? fixture.Land() :
            LandingExecutor.Execute(fixture.Kernel, fixture.Goal, workspace, pullRequestPublisher: publisher);

        Assert.NotEmpty(fixture.Forge.Calls);
        Assert.Equal(login is null ? 0 : 1, provider.Calls);
        Assert.Equal(login is null ? null : "configured-settings", provider.Path);
        Assert.Equal(login, fixture.Goal.DeliveredForReview!.ActingLogin);
        Assert.Equal(DeliveredForReviewHold.Text(PullRequestLandingTestFixture.Url, login), result.Message);
        Assert.Equal(result.Message, fixture.Goal.CurrentHold!.Blocker);
        Assert.True(DeliveredForReviewHold.IsCurrent(fixture.Goal));

        var calls = fixture.Forge.Calls.Count;
        var repeated = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, workspace,
            pullRequestPublisher: publisher);

        Assert.Equal(result.Message, repeated.Message);
        Assert.Equal(calls, fixture.Forge.Calls.Count);
        Assert.Equal(login is null ? 0 : 1, provider.Calls);
        Assert.Equal(repeated.Message, fixture.Goal.CurrentHold.Blocker);
        Assert.True(DeliveredForReviewHold.IsCurrent(fixture.Goal));
    }

    [Theory]
    [InlineData("delivered for review: ", true)]
    [InlineData("delivered for review: https://github.com/octo/sample/pull/7", true)]
    [InlineData("delivered for review: arbitrary text", true)]
    [InlineData("Delivered for review: https://github.com/octo/sample/pull/7", false)]
    [InlineData("delivered for review:", false)]
    [InlineData("pull request refreshed: x", false)]
    [InlineData("", false)]
    public void Recognizer_ReasonVariants_PreservesOrdinalPrefixMatch(string reason, bool expected)
    {
        Assert.Equal("delivered for review: ", DeliveredForReviewHold.Prefix);
        Assert.Equal(expected, DeliveredForReviewHold.IsDeliveredForReviewText(reason));
        Assert.Equal(expected, PullRequestLandingDelivery.IsDeliveredForReview(reason));
    }

    [Fact]
    public void Recognizer_NullReason_PreservesException()
    {
        Assert.Throws<NullReferenceException>(() => DeliveredForReviewHold.IsDeliveredForReviewText(null!));
        Assert.Throws<NullReferenceException>(() => PullRequestLandingDelivery.IsDeliveredForReview(null!));
    }

    [Fact]
    public void Source_DeliveryLiteral_OnlyOwnerAndConsoleDisplayContainIt()
    {
        const string literal = "\"delivered for review: ";
        var source = Path.Combine(VerifiedRepositoryRoot.Find(), "src");
        var owner = Path.Combine(source, "Mcg.AgentOrchestrator.Core", "Domain", "DeliveredForReviewHold.cs");
        var console = Path.Combine(source, "Mcg.AgentOrchestrator.App", "Console", "OwnerConsoleDeliveredForReviewText.cs");
        var matches = SourceFiles(source)
            .SelectMany(path => File.ReadLines(path).Where(line => line.Contains(literal, StringComparison.Ordinal))
                .Select(line => (Path: path, Line: line))).ToArray();

        Assert.Contains(matches, match => match.Path == owner);
        Assert.Contains(matches, match => match.Path == console);
        Assert.All(matches, match =>
        {
            Assert.True(match.Path == owner || match.Path == console, $"Duplicate delivery literal: {match.Path}");
            if (match.Path == console)
                Assert.DoesNotContain(literal, match.Line.Replace(literal + "PR {", "", StringComparison.Ordinal));
        });
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or
                "TestResults" or "playwright-report") continue;
            foreach (var file in SourceFiles(child)) yield return file;
        }
    }

    private sealed class Provider(string? login) : IForgeIdentityProvider
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }

        public ForgeIdentityPublish Acquire(string settingsPath)
        {
            Calls++;
            Path = settingsPath;
            return new(new Dictionary<string, string> { ["GH_TOKEN"] = "ghs_testOnly" }, login!);
        }
    }
}
