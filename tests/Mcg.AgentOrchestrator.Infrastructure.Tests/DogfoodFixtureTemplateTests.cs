// Parallel-safe: the template is immutable and each call owns its repository and stores.
public sealed class DogfoodFixtureTemplateTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateFixture_CopiesHistoryAndIsolatesWrites(bool train)
    {
        using var second = GroupedDogfoodLandingEvidenceTests.CreateFixture(train, olderFailure: false);
        string firstRoot;
        using (var first = GroupedDogfoodLandingEvidenceTests.CreateFixture(train, olderFailure: false))
        {
            firstRoot = first.Root;
            Assert.NotEqual(first.Root, second.Root);
            Assert.NotEqual(first.Goal.Id, second.Goal.Id);
            Assert.NotSame(first.Kernel, second.Kernel);
            Assert.NotEqual(first.Group, second.Group);
            Assert.Equal((first.Main, first.Candidate, first.SecondCandidate, first.Promoted, first.Tree),
                (second.Main, second.Candidate, second.SecondCandidate, second.Promoted, second.Tree));
            foreach (var fixture in new[] { first, second })
            {
                Assert.True(Directory.Exists(Path.Combine(fixture.Root, ".git")));
                Assert.Equal(fixture.Main, RunGitOutput(fixture.Root, "rev-parse", "HEAD~2").Trim());
                Assert.Equal(fixture.Candidate, RunGitOutput(fixture.Root, "rev-parse", "HEAD~1^2").Trim());
                Assert.Equal(fixture.SecondCandidate, RunGitOutput(fixture.Root, "rev-parse", "HEAD^2").Trim());
                Assert.Equal(fixture.Promoted, RunGitOutput(fixture.Root, "rev-parse", "HEAD").Trim());
                Assert.Equal(fixture.Tree, RunGitOutput(fixture.Root, "rev-parse", "HEAD^{tree}").Trim());
                Assert.Equal("first", File.ReadAllText(Path.Combine(fixture.Root, "src", "First.cs")));
                Assert.Equal("second", File.ReadAllText(Path.Combine(fixture.Root, "src", "Second.cs")));
            }

            var template = DogfoodFixtureTemplate.TemplateDirectory;
            var seed = File.ReadAllText(Path.Combine(second.Root, "seed.txt"));
            var head = File.ReadAllText(Path.Combine(second.Root, ".git", "HEAD"));
            File.WriteAllText(Path.Combine(first.Root, "copy-only.txt"), "isolated");
            File.WriteAllText(Path.Combine(first.Root, "seed.txt"), "changed");
            File.WriteAllText(Path.Combine(first.Root, ".git", "HEAD"), first.Main + "\n");
            foreach (var untouched in new[] { second.Root, template })
            {
                Assert.False(File.Exists(Path.Combine(untouched, "copy-only.txt")));
                Assert.Equal(seed, File.ReadAllText(Path.Combine(untouched, "seed.txt")));
                Assert.Equal(head, File.ReadAllText(Path.Combine(untouched, ".git", "HEAD")));
            }
        }

        Assert.False(Directory.Exists(firstRoot));
        Assert.True(Directory.Exists(second.Root));
        Assert.True(Directory.Exists(DogfoodFixtureTemplate.TemplateDirectory));
        Assert.Equal(second.Promoted, RunGitOutput(second.Root, "rev-parse", "HEAD").Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateFixture_WarmCallLaunchesAtMostTwoGitProcesses(bool train)
    {
        using var first = GroupedDogfoodLandingEvidenceTests.CreateFixture(train, olderFailure: false);
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var second = GroupedDogfoodLandingEvidenceTests.CreateFixture(train, olderFailure: false);
        Assert.InRange(launches.Count, 0, 2);
    }
}
