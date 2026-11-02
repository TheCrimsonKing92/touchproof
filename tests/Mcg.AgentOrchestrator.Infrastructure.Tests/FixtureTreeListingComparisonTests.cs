// Parallel-safe: pure in-memory strings; no processes or files.
public sealed class FixtureTreeListingComparisonTests
{
    [Fact]
    public void Compare_IdenticalListings_ReturnsThreeEmptyLists()
    {
        const string listing = "100644 blob a1\tfirst.txt\r\n100644 blob b2\tdir/second file.txt\r\n";

        var result = FixtureTreeListingComparison.Compare(listing, listing);

        Assert.Empty(result.OnlyInFreshFixture);
        Assert.Empty(result.OnlyInTemplateCopy);
        Assert.Empty(result.ChangedObjectId);
    }

    [Fact]
    public void Compare_AddedMissingAndChangedPaths_ReportsEachInItsList()
    {
        const string fresh = "100644 blob a1\tz fresh.txt\n"
            + "100644 blob a2\ta fresh.txt\n"
            + "100644 blob a3\tZ fresh.txt\n"
            + "100644 blob old1\tz changed.txt\n"
            + "100644 blob old2\ta changed.txt\n"
            + "100644 blob old3\tZ changed.txt\n"
            + "100644 blob same\tunchanged.txt\n";
        const string copy = "100644 blob b1\tz copy.txt\n"
            + "100644 blob b2\ta copy.txt\n"
            + "100644 blob b3\tZ copy.txt\n"
            + "100644 blob new1\tz changed.txt\n"
            + "100644 blob new2\ta changed.txt\n"
            + "100644 blob new3\tZ changed.txt\n"
            + "100644 blob same\tunchanged.txt\n";

        var result = FixtureTreeListingComparison.Compare(fresh, copy);

        Assert.Equal(new[] { "Z fresh.txt", "a fresh.txt", "z fresh.txt" }, result.OnlyInFreshFixture);
        Assert.Equal(new[] { "Z copy.txt", "a copy.txt", "z copy.txt" }, result.OnlyInTemplateCopy);
        Assert.Equal(new[] { "Z changed.txt", "a changed.txt", "z changed.txt" }, result.ChangedObjectId);
    }

    [Fact]
    public void Compare_QuotedPaths_PreservesEmittedTokensAndCase()
    {
        const string quotedPath = "\"dir/escaped\\tfile.txt\"";
        var fresh = $"100644 blob a1\t{quotedPath}\n100644 blob b2\tCase.txt\n";
        var copy = $"100644 blob a2\t{quotedPath}\n100644 blob b2\tcase.txt\n";

        var result = FixtureTreeListingComparison.Compare(fresh, copy);

        Assert.Equal(new[] { "Case.txt" }, result.OnlyInFreshFixture);
        Assert.Equal(new[] { "case.txt" }, result.OnlyInTemplateCopy);
        Assert.Equal(new[] { quotedPath }, result.ChangedObjectId);
    }

    [Fact]
    public void Compare_ModeOrTypeOnlyDifferences_ReturnsThreeEmptyLists()
    {
        var result = FixtureTreeListingComparison.Compare(
            "100644 blob same1\tmode.txt\n100644 blob same2\ttype.txt\n",
            "100755 blob same1\tmode.txt\n160000 commit same2\ttype.txt\n");

        Assert.Empty(result.OnlyInFreshFixture);
        Assert.Empty(result.OnlyInTemplateCopy);
        Assert.Empty(result.ChangedObjectId);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("goal worktree")]
    public void FailureMessage_Mismatch_NamesRepositoryTreeIdsAndEachPath(string repository)
    {
        var comparison = FixtureTreeListingComparison.Compare(
            "100644 blob a1\tfresh only.txt\n100644 blob old\tchanged.txt\n",
            "100644 blob b2\tcopy only.txt\n100644 blob new\tchanged.txt\n");

        var message = FixtureTreeListingComparison.FailureMessage(
            repository, "fresh-tree-id\r\n", "copy-tree-id\n", comparison);

        Assert.Equal(string.Join(Environment.NewLine,
            $"Fixture trees differ for repository: {repository}",
            "Fresh fixture tree: fresh-tree-id",
            "Template copy tree: copy-tree-id",
            "Only in fresh fixture:",
            "  fresh only.txt",
            "Only in template copy:",
            "  copy only.txt",
            "Changed object id:",
            "  changed.txt", ""), message);
    }

    [Fact]
    public void FailureMessage_EmptyPathLists_ShowsExplicitEmptyMarkers()
    {
        var comparison = FixtureTreeListingComparison.Compare("", "");

        var message = FixtureTreeListingComparison.FailureMessage(
            "main", "fresh-tree-id", "copy-tree-id", comparison);

        Assert.Equal(string.Join(Environment.NewLine,
            "Fixture trees differ for repository: main",
            "Fresh fixture tree: fresh-tree-id",
            "Template copy tree: copy-tree-id",
            "Only in fresh fixture:",
            "  (none)",
            "Only in template copy:",
            "  (none)",
            "Changed object id:",
            "  (none)", ""), message);
    }

    [Theory]
    [InlineData("100644 blob a1 path-without-tab")]
    [InlineData("100644 blob\tmissing-object-id.txt")]
    public void Compare_MalformedEntry_ThrowsWithTheInvalidLine(string line)
    {
        var exception = Assert.Throws<FormatException>(() => FixtureTreeListingComparison.Compare(line, ""));

        Assert.Contains(line, exception.Message);
    }
}
