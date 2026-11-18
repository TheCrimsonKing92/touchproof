using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ZeroContextDiffLineRangeParserTests
{
    [Fact]
    public void MultipleFilesAndHunks_ReadInclusiveNewSideRanges()
    {
        var ranges = ZeroContextDiffLineRangeParser.Parse("""
            diff --git a/src/One.cs b/src/One.cs
            --- a/src/One.cs
            +++ b/src/One.cs
            @@ -2 +3 @@
            -old
            +new
            @@ -8,2 +9,3 @@ method
            -old
            -old
            +new
            +new
            +new
            diff --git a/src/Two.cs b/src/Two.cs
            new file mode 100644
            --- /dev/null
            +++ b/src/Two.cs
            @@ -0,0 +1,2 @@
            +class Two
            +{ }
            """);

        Assert.Equal(2, ranges.Count);
        Assert.Equal([(3, 3), (9, 11)], ranges["src/One.cs"]);
        Assert.Equal([(1, 2)], ranges["src/Two.cs"]);
    }

    [Theory]
    [InlineData("@@ -4 +3,0 @@\n-removed")]
    [InlineData("@@ -2 +2 @@\n-old\n+new\n@@ -8 +7,0 @@\n-removed")]
    [InlineData("@@ -4 +3,0 @@\n-removed\n@@ -8 +7 @@\n-old\n+new")]
    [InlineData("@@ malformed @@")]
    [InlineData("@@ -1 +2147483647,2 @@")]
    public void UnusableHunk_DropsOnlyItsFile(string hunks)
    {
        var diff = "diff --git a/src/Bad.cs b/src/Bad.cs\n--- a/src/Bad.cs\n+++ b/src/Bad.cs\n" + hunks +
            "\ndiff --git a/src/Good.cs b/src/Good.cs\n--- a/src/Good.cs\n+++ b/src/Good.cs\n@@ -1 +1 @@\n-old\n+new\n";

        var ranges = ZeroContextDiffLineRangeParser.Parse(diff);

        Assert.Equal("src/Good.cs", Assert.Single(ranges).Key);
        Assert.Equal([(1, 1)], ranges["src/Good.cs"]);
    }

    [Theory]
    [InlineData("/dev/null")]
    [InlineData("\"b/src/quoted.cs\"")]
    [InlineData("src/no-prefix.cs")]
    public void UnusableNewSidePath_HasNoRestrictingEntry(string newPath)
    {
        var ranges = ZeroContextDiffLineRangeParser.Parse(
            "diff --git a/src/File.cs b/src/File.cs\n--- a/src/File.cs\n+++ " + newPath + "\n@@ -1 +1 @@\n-old\n+new\n");

        Assert.Empty(ranges);
    }

    [Fact]
    public void HeaderLikeContent_DoesNotReplaceFilePath()
    {
        var ranges = ZeroContextDiffLineRangeParser.Parse(
            "diff --git a/src/with space.cs b/src/with space.cs\n--- a/src/with space.cs\n+++ b/src/with space.cs\t\n@@ -1 +1 @@\n-old\n+++ b/src/NotAHeader.cs\n");

        Assert.Equal("src/with space.cs", Assert.Single(ranges).Key);
        Assert.Equal([(1, 1)], ranges["src/with space.cs"]);
    }
}
