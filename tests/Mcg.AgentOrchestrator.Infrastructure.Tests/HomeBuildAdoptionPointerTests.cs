using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every test owns its home record, pointer, and staged-output directory.
public sealed class HomeBuildAdoptionPointerTests
{
    [Xunit.Fact]
    public void Adopted_WritesExactPointerBytes_AndRevertedRemovesPointer()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var publisher = new HomeBuildAdoptionPublisher(fixture.Root, fixture.RecordPath);
        var build = Build(fixture) with { CommitSha = new string('B', 40) };
        var pointer = HomeBuildAdoptionRecord.LauncherPointerPath(fixture.RecordPath);

        publisher.Record("adopted", build);

        var expected = Encoding.UTF8.GetBytes(
            $"{build.CommitSha.ToLowerInvariant()}\r\n{Path.GetFullPath(fixture.Candidate.AppDllPath)}\r\n");
        Assert.Equal(expected, File.ReadAllBytes(pointer));
        Assert.Equal("adopted", HomeBuildAdoptionRecord.Read(fixture.RecordPath)!.Status);

        publisher.Record("reverted", build);

        Assert.False(File.Exists(pointer));
        Assert.Equal("reverted", HomeBuildAdoptionRecord.Read(fixture.RecordPath)!.Status);
    }

    [Xunit.Fact]
    public void AdoptedAgain_ReplacesPointer_WithoutTemporarySiblings()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var publisher = new HomeBuildAdoptionPublisher(fixture.Root, fixture.RecordPath);
        var build = Build(fixture);
        publisher.Record("adopted", build);
        var nextDll = Path.Combine(fixture.Root, "next run", "Mcg.AgentOrchestrator.App.dll");
        var next = build with { CommitSha = new string('c', 40), CommandPrefix = ["dotnet", nextDll] };

        publisher.Record("adopted", next);

        var pointer = HomeBuildAdoptionRecord.LauncherPointerPath(fixture.RecordPath);
        Assert.Equal($"{next.CommitSha}\r\n{nextDll}\r\n", File.ReadAllText(pointer));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(pointer)!, "*.tmp"));
    }

    [Xunit.Fact]
    public void RevertedOtherCommit_KeepsAdoptedPointer()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var publisher = new HomeBuildAdoptionPublisher(fixture.Root, fixture.RecordPath);
        var build = Build(fixture);
        publisher.Record("adopted", build);
        var pointer = HomeBuildAdoptionRecord.LauncherPointerPath(fixture.RecordPath);
        var expected = File.ReadAllBytes(pointer);

        publisher.Record("reverted", build with { CommitSha = fixture.Running.CommitSha });

        Assert.Equal(expected, File.ReadAllBytes(pointer));
    }

    [Xunit.Theory]
    [Xunit.InlineData("staged-only")]
    [Xunit.InlineData("invalid-commit")]
    [Xunit.InlineData("missing-command")]
    [Xunit.InlineData("unrelated-status")]
    public void UnpublishedOrIneligibleBuild_DoesNotCreatePointer(string scenario)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var publisher = new HomeBuildAdoptionPublisher(fixture.Root, fixture.RecordPath);
        var build = Build(fixture);
        if (scenario == "invalid-commit") build = build with { CommitSha = "invalid" };
        if (scenario == "missing-command") build = build with { CommandPrefix = ["dotnet"] };
        if (scenario != "staged-only")
            publisher.Record(scenario == "unrelated-status" ? "prepared" : "adopted", build);

        Assert.True(File.Exists(fixture.Candidate.AppDllPath));
        Assert.False(File.Exists(HomeBuildAdoptionRecord.LauncherPointerPath(fixture.RecordPath)));
    }

    private static ConductorActivationBuild Build(HomeBuildAdoptionFixture fixture) => new(
        fixture.Candidate.AdoptedCommit, fixture.Candidate.RunDirectory,
        ["dotnet", fixture.Candidate.AppDllPath], null);
}
