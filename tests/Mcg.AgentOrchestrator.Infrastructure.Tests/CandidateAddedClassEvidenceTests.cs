using Mcg.AgentOrchestrator.Infrastructure;
using static TestClassRenameEvidenceTests;

// Parallel-safe: every test owns its git repository and changes no shared git/process state.
public sealed class CandidateAddedClassEvidenceTests
{
    [Fact]
    public void AddedAndModifiedFiles_ReturnNewClassNamesAndPaths()
    {
        using var repo = new Repository(oldOnMain: true);
        const string path = "tests/Fixture/New classes.cs";
        Write(repo.Root, path, "class Outer { class Inner { } }");
        Write(repo.Root, OldPath, Source("OldNameTests") + "class Extra { }");
        Commit(repo.Root, "Add classes", path, OldPath);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        Write(repo.Root, path, "class Uncommitted { }");

        var classes = CandidateAddedClassEvidence.Read(repo.Root, "main", candidate);

        Assert.Equal(new[] { "Extra", "Inner", "Outer" }, classes.Select(item => item.Name));
        Assert.Equal(new[] { OldPath, path, path }, classes.Select(item => item.Path));
    }

    [Fact]
    public void MovedClassAndExistingPartial_AreNotAdded()
    {
        using var repo = new Repository(oldOnMain: true);
        Git(repo.Root, "mv", OldPath, NewPath);
        const string partialPath = "tests/Fixture/Partial.cs";
        Write(repo.Root, partialPath, "partial class OldNameTests { } class AddedTests { }");
        Commit(repo.Root, "Move and extend", OldPath, NewPath, partialPath);

        var added = Assert.Single(CandidateAddedClassEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD")));

        Assert.Equal("AddedTests", added.Name);
        Assert.Equal(partialPath, added.Path);
    }

    [Fact]
    public void NewPartialClass_ReturnsBothDeclaringPaths()
    {
        using var repo = new Repository(oldOnMain: true);
        Write(repo.Root, "b.cs", "partial class NewTests { }");
        Write(repo.Root, "a.cs", "partial class NewTests { }");
        Commit(repo.Root, "Add partials", "b.cs", "a.cs");

        var classes = CandidateAddedClassEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD"));

        Assert.Equal(new[] { "NewTests", "NewTests" }, classes.Select(item => item.Name));
        Assert.Equal(new[] { "a.cs", "b.cs" }, classes.Select(item => item.Path));
    }

    [Fact]
    public void DivergedIntegrationBranch_UsesMergeBaseAndCandidate()
    {
        using var repo = new Repository(oldOnMain: true);
        Write(repo.Root, NewPath, Source("NewTests"));
        Commit(repo.Root, "Candidate", NewPath);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        Git(repo.Root, "checkout", "main");
        Write(repo.Root, OtherPath, Source("NewTests"));
        Commit(repo.Root, "Independent main class", OtherPath);

        var added = Assert.Single(CandidateAddedClassEvidence.Read(repo.Root, "main", candidate));

        Assert.Equal("NewTests", added.Name);
        Assert.Equal(NewPath, added.Path);
    }

    [Fact]
    public void NoGrepMatch_RecordsRealGitExitOneAsAbsence()
    {
        using var repo = new Repository(oldOnMain: true);
        var reached = false;

        var absent = CandidateAddedClassEvidence.ProvesClassAbsent(repo.Root, "HEAD", "MissingTests", (directory, args) =>
        {
            var result = GitCli.Run(directory, args);
            if (args[0] == "grep")
            {
                reached = true;
                Assert.Equal(1, result.ExitCode);
                Assert.Equal("", result.Output);
                Assert.False(result.DrainTimedOut);
            }
            return result;
        });

        Assert.True(reached);
        Assert.True(absent);
    }

    [Theory]
    [InlineData("main", "OldNameTests", false)]
    [InlineData("HEAD", "OldNameTests", false)]
    [InlineData("HEAD", "NestedTests", false)]
    [InlineData("HEAD", "MentionOnlyTests", true)]
    [InlineData("missing-revision", "MissingTests", false)]
    [InlineData("HEAD", "", false)]
    [InlineData("HEAD", "Tests.Check()", false)]
    public void AbsenceProof_RequiresValidSyntaxAndNoDeclaration(string revision, string name, bool expected)
    {
        using var repo = new Repository(oldOnMain: true);
        Write(repo.Root, NewPath, "// MentionOnlyTests\nclass Container { class NestedTests { } }");
        Commit(repo.Root, "Add nested class", NewPath);

        Assert.Equal(expected, CandidateAddedClassEvidence.ProvesClassAbsent(repo.Root, revision, name));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("merge-base")]
    public void SyntaxError_DiscardsAllAddedEvidence(string location)
    {
        using var repo = new Repository(oldOnMain: true);
        if (location == "merge-base")
        {
            Git(repo.Root, "checkout", "main");
            Write(repo.Root, "broken.cs", "class NewTests { ");
            Commit(repo.Root, "Invalid baseline", "broken.cs");
            Git(repo.Root, "checkout", "goal");
            Git(repo.Root, "merge", "main", "--no-edit");
        }
        Write(repo.Root, NewPath, Source("NewTests") + (location == "candidate" ? "class Broken { " : ""));
        Commit(repo.Root, "Candidate", NewPath);

        Assert.Empty(CandidateAddedClassEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD")));
    }

    [Fact]
    public void MalformedMatchingFile_CannotProveAbsence()
    {
        using var repo = new Repository(oldOnMain: true);
        Write(repo.Root, NewPath, "// PlannedTests\nclass Broken {");
        Commit(repo.Root, "Broken syntax", NewPath);

        Assert.False(CandidateAddedClassEvidence.ProvesClassAbsent(repo.Root, "HEAD", "PlannedTests"));
    }

    [Fact]
    public void EscapedIdentifier_ProvesClassExistsAndExcludesExistingName()
    {
        using var repo = new Repository(oldOnMain: true);
        Git(repo.Root, "checkout", "main");
        Write(repo.Root, "escaped.cs", @"class Pl\u0061nnedTests { }");
        Commit(repo.Root, "Escaped baseline identifier", "escaped.cs");
        Git(repo.Root, "checkout", "goal");
        Git(repo.Root, "merge", "main", "--no-edit");
        Write(repo.Root, NewPath, "partial class PlannedTests { } class NewTests { }");
        Commit(repo.Root, "New declaration", NewPath);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");

        Assert.False(CandidateAddedClassEvidence.ProvesClassAbsent(repo.Root, "main", "PlannedTests"));
        Assert.Equal("NewTests", Assert.Single(CandidateAddedClassEvidence.Read(repo.Root, "main", candidate)).Name);
    }

    [Theory]
    [InlineData("merge-base", "failure")]
    [InlineData("diff", "failure")]
    [InlineData("diff", "unexpected-status")]
    [InlineData("show", "failure")]
    [InlineData("grep", "failure")]
    [InlineData("grep", "drain")]
    [InlineData("grep", "throw")]
    public void UnavailableGit_ReturnsNoAddedEvidence(string command, string fault)
    {
        using var repo = new Repository(oldOnMain: true);
        Write(repo.Root, NewPath, Source("NewTests"));
        Commit(repo.Root, "Add class", NewPath);
        var reached = false;

        var classes = CandidateAddedClassEvidence.Read(repo.Root, "main", Git(repo.Root, "rev-parse", "HEAD"), (directory, args) =>
        {
            if (args[0] != command) return GitCli.Run(directory, args);
            reached = true;
            return fault switch
            {
                "throw" => throw new IOException("Fixture git unavailable"),
                "drain" => new GitCli.GitResult(0, "", "", DrainTimedOut: true),
                "unexpected-status" => new GitCli.GitResult(0, "T\0type.cs\0", ""),
                _ => new GitCli.GitResult(2, "", "Fixture git failure")
            };
        });

        Assert.True(reached);
        Assert.Empty(classes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableGrep_CannotProveAbsence(bool drain)
    {
        var reached = false;
        var absent = CandidateAddedClassEvidence.ProvesClassAbsent("unused", "HEAD", "PlannedTests", (_, args) =>
        {
            Assert.Equal("grep", args[0]);
            reached = true;
            return new GitCli.GitResult(drain ? 1 : 2, "", "unavailable", DrainTimedOut: drain);
        });

        Assert.True(reached);
        Assert.False(absent);
    }
}
