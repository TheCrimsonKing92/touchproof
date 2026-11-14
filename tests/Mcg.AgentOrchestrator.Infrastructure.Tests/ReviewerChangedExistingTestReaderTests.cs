using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using Xunit;

// Parallel-safe: private repositories; launch observers are chained and filtered by test identity.
public sealed class ReviewerChangedExistingTestReaderTests : WorkerDispatchTestSupport
{
    private const string PathInRepo = "tests/Feature.Tests/T.cs";
    private static readonly DateTimeOffset CommitTime = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsEditedAndRemovedMethodsButIgnoresTriviaAndNewMethods()
    {
        var root = CreateSeededDispatchRepository();
        var file = Path.Combine(root, PathInRepo);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            using Xunit;
            public class T
            {
                [Fact] public void Edited() { Assert.Equal(1, 1); }
                [Fact] public void Whitespace() { Assert.True(true); }
                [Fact] public void Removed() { Assert.True(true); }
            }
            """);
        Commit(root);
        var baseline = Head(root);
        File.WriteAllText(file, """
            using Xunit;
            public class T
            {
                [Fact]
                public void Edited()
                {
                    Assert.Equal(2, 2);
                }
                [Fact] public void Whitespace() { /* comment */ Assert.True( true ); }
                [Fact] public void Added() { Assert.True(true); }
            }
            """);
        Commit(root);
        var head = Head(root);
        var read = ReviewerChangedExistingTestReader.Read(root, baseline, head, [PathInRepo]);
        Assert.Null(read.Diagnostic);
        Assert.Equal(2, read.Entries.Count);
        var changed = Assert.Single(read.Entries.Where(entry => !entry.Removed));
        Assert.Equal("T", changed.TypeName);
        Assert.Equal("Edited", changed.MethodName);
        Assert.Equal(PathInRepo, changed.File);
        Assert.Equal(4, changed.StartLine);
        Assert.Equal(8, changed.EndLine);
        var removed = Assert.Single(read.Entries.Where(entry => entry.Removed));
        Assert.Equal("Removed", removed.MethodName);
        Assert.Equal(6, removed.StartLine);
        Assert.Equal(6, removed.EndLine);

        foreach (var missing in new string?[] { null, "missing-merge-base" })
        {
            var degraded = ReviewerChangedExistingTestReader.Read(root, missing, head, [PathInRepo]);
            Assert.Empty(degraded.Entries);
            Assert.Contains(missing is null ? "merge base is missing" : "missing-merge-base", degraded.Diagnostic!);
        }
        var missingHead = ReviewerChangedExistingTestReader.Read(root, baseline, null, [PathInRepo]);
        Assert.Empty(missingHead.Entries);
        Assert.Contains("candidate head is missing", missingHead.Diagnostic!);
    }

    [Fact]
    public void EntireDeletedFileReportsRemovedMethodsAndNewFileReportsNone()
    {
        var root = CreateSeededDispatchRepository();
        var file = Path.Combine(root, PathInRepo);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "class T { public void Gone() {} }");
        Commit(root);
        var baseline = Head(root);
        File.Delete(file);
        File.WriteAllText(Path.Combine(root, "tests/Feature.Tests/New.cs"), "class New { public void Added() {} }");
        Commit(root);
        var result = ReviewerChangedExistingTestReader.Read(root, baseline, Head(root), [PathInRepo, "tests/Feature.Tests/New.cs"]);
        Assert.Null(result.Diagnostic);
        var removed = Assert.Single(result.Entries);
        Assert.True(removed.Removed);
        Assert.Equal("Gone", removed.MethodName);
    }

    [Fact]
    public void OverloadsAreComparedByParameterTypesAndAttributeChangesCount()
    {
        var root = CreateSeededDispatchRepository();
        var file = Path.Combine(root, PathInRepo);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "class T { [Theory] public void M(int x) {} [Theory] public void M(string x) {} }");
        Commit(root);
        var baseline = Head(root);
        File.WriteAllText(file, "class T { [Theory(Skip = \"reason\")] public void M(int x) {} [Theory] public void M(string x) {} }");
        Commit(root);
        var read = ReviewerChangedExistingTestReader.Read(root, baseline, Head(root), [PathInRepo]);
        Assert.Null(read.Diagnostic);
        Assert.Equal("M", Assert.Single(read.Entries).MethodName);
        Assert.False(read.Entries[0].Removed);
    }

    [Theory]
    [InlineData("rev-parse")]
    [InlineData("ls-tree")]
    [InlineData("cat-file")]
    public void GitFailureDiscardsAllEntriesAndNamesTheFailedCommand(string failedCommand)
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo], (_, args) =>
            GitCli.SubcommandKey(args) == failedCommand ? new(2, "", "fixture error") : FakeGit(args),
            (_, _) => new(new(2, "", "fixture error"), []));
        Assert.Empty(read.Entries);
        Assert.Contains(failedCommand, read.Diagnostic!);
        Assert.Contains("fixture error", read.Diagnostic!);
    }

    [Fact]
    public void ThrowingReadAndIncompleteOutputDegradeWithoutThrowing()
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo],
            (_, _) => throw new IOException("reading denied"));
        Assert.Empty(read.Entries);
        Assert.Contains("reading denied", read.Diagnostic!);
        foreach (var result in new[]
                 {
                     new GitCli.GitResult(0, "partial", "", DrainTimedOut: true),
                     new GitCli.GitResult(0, "", "", ProcessStarted: false),
                     new GitCli.GitResult(0, "", "")
                 })
        {
            var incomplete = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo], (_, _) => result);
            Assert.Empty(incomplete.Entries);
            Assert.Contains("git rev-parse", incomplete.Diagnostic!);
        }
    }

    [Fact]
    public void LaterReadingFailureDiscardsPreviouslyFoundChanges()
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", ["tests/A.cs", "tests/Z.cs"],
            (_, args) => FakeGit(args), (_, _) => new(new(7, "", "later read denied"),
            ["class A { void M() { X(1); } }", "class A { void M() { X(2); } }"]));
        Assert.Empty(read.Entries);
        Assert.Contains("git cat-file --batch", read.Diagnostic!);
        Assert.Contains("later read denied", read.Diagnostic!);
    }

    [Fact]
    public void NoChangedTestPathsNeedsNoGitOrBaseline()
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", null, null, ["src/Feature.cs", "tests/readme.md"],
            (_, _) => throw new InvalidOperationException("git must not run"));
        Assert.Empty(read.Entries);
        Assert.Null(read.Diagnostic);
    }

    [Fact]
    public void LaunchCountIsTheSameForOneAndSixChangedFiles()
    {
        var root = CreateSeededDispatchRepository();
        var paths = Enumerable.Range(0, 6).Select(index => $"tests/Feature.Tests/T{index}.cs").ToArray();
        foreach (var path in paths) Write(path, "class T { void M() { X(1); } }");
        Commit(root);
        var baseline = Head(root);
        foreach (var path in paths) Write(path, "class T { void M() { X(2); } }");
        Commit(root);
        var head = Head(root);
        var one = Measure([paths[0]]);
        var six = Measure(paths);
        Assert.Equal(one.Length, six.Length);
        Assert.Equal(new[] { "rev-parse", "rev-parse", "-c", "-c", "cat-file" }, one);
        Assert.Equal(one, six);
        // Even a nonempty eligible set absent on both sides keeps the same successful launch shape.
        var missing = CaptureLaunches(() =>
        {
            var result = ReviewerChangedExistingTestReader.Read(root, baseline, head, ["tests/Absent.cs"]);
            Assert.Null(result.Diagnostic);
            Assert.Empty(result.Entries);
        });
        Assert.Equal(one, missing);

        string[] Measure(string[] changed) => CaptureLaunches(() =>
        {
            var result = ReviewerChangedExistingTestReader.Read(root, baseline, head, changed);
            Assert.Null(result.Diagnostic);
            Assert.Equal(changed.Length, result.Entries.Count);
        });
        void Write(string path, string source)
        {
            var file = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, source);
        }
    }

    [Fact]
    public void MatchesFrozenPerFileReaderOnFixtureRepository()
    {
        var root = CreateSeededDispatchRepository();
        string[] paths = ["tests/A Changed.cs", "tests/BRemovedMethod.cs", "tests/CAdded.cs",
            "tests/DRemovedFile.cs", "tests/Z café.cs"];
        Write(paths[0], "class A { void Changed() { X(1); } }");
        Write(paths[1], "class B { void Removed() {} }");
        Write(paths[3], "class D { void Gone() {} }");
        Write(paths[4], "class Café { void Méthode() { X(1); } }", bom: true);
        Commit(root);
        var baseline = Head(root);
        Write(paths[0], "class A { void Changed() { X(2); } }");
        Write(paths[1], "class B { }");
        Write(paths[2], "class C { void Added() {} }");
        File.Delete(Path.Combine(root, paths[3]));
        Write(paths[4], "class Café { void Méthode() { X(2); } }", bom: true);
        Commit(root);
        var head = Head(root);
        foreach (var path in paths) Compare(baseline, [path]);
        var combined = Compare(baseline, [.. paths.Reverse(), paths[0], "src/Ignore.cs", "tests/readme.md"]);
        Assert.Equal(new[] { "Changed", "Removed", "Gone", "Méthode" }, combined.Entries.Select(entry => entry.MethodName));
        Assert.Null(combined.Diagnostic);
        var failed = Compare("unresolvable-fixture-revision", paths);
        Assert.Empty(failed.Entries);
        Assert.Contains("git rev-parse", failed.Diagnostic!);

        ReviewerChangedExistingTestRead Compare(string mergeBase, string[] changed)
        {
            var expected = FrozenPerFileReader.Read(root, mergeBase, head, changed);
            var actual = ReviewerChangedExistingTestReader.Read(root, mergeBase, head, changed);
            Assert.Equal(expected.Entries.ToArray(), actual.Entries.ToArray());
            Assert.Equal(expected.Diagnostic, actual.Diagnostic);
            return actual;
        }
        void Write(string path, string source, bool bom = false)
        {
            var file = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, source, new UTF8Encoding(bom));
        }
    }

    [Theory]
    [InlineData("ls-tree")]
    [InlineData("content")]
    public void ListingAndContentFailuresKeepFrozenDiagnosticFormatAndDiscardInventory(string stage)
    {
        GitCli.GitResult Git(string _, string[] args) =>
            GitCli.SubcommandKey(args) == (stage == "content" ? "show" : "ls-tree")
                ? new(7, "partial", "controlled error\nsecond line") : FakeGit(args);
        var expected = FrozenPerFileReader.Read("fixture", "base", "head", [PathInRepo], Git);
        var actual = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo], Git,
            (_, _) => new(new(7, "partial", "controlled error\nsecond line"), ["partial content"]));
        Assert.Empty(expected.Entries);
        Assert.Empty(actual.Entries);
        var tail = " failed (exit 7, started=True, drainTimedOut=False): controlled error second line";
        Assert.StartsWith("Changed existing tests unavailable: git ", expected.Diagnostic!);
        Assert.StartsWith("Changed existing tests unavailable: git ", actual.Diagnostic!);
        Assert.EndsWith(tail, expected.Diagnostic!);
        Assert.EndsWith(tail, actual.Diagnostic!);
        Assert.Contains(stage == "content" ? "git cat-file --batch" : "git -c core.quotePath=true ls-tree", actual.Diagnostic!);
    }

    [Theory]
    [InlineData("garbage\n")]
    [InlineData("100644 blob 1111111111111111111111111111111111111111\ttests/Unexpected.cs\n")]
    [InlineData("100644 blob 1111111111111111111111111111111111111111\ttests/Feature.Tests/T.cs")]
    public void MalformedListingFailsBeforeBatch(string listing)
    {
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo],
            (_, args) => GitCli.SubcommandKey(args) == "ls-tree" ? new(0, listing, "") : FakeGit(args),
            (_, _) => throw new InvalidOperationException("batch must not run"));
        Assert.Empty(read.Entries);
        Assert.Contains("git ls-tree returned", read.Diagnostic!);
    }

    [Fact]
    public void QuotedPathsPreserveTabsQuotesAndUtf8Bytes()
    {
        const string path = "tests/café\t\"quoted\".cs";
        const string quoted = "\"tests/caf\\303\\251\\t\\\"quoted\\\".cs\"";
        var batchCalls = 0;
        var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [path], (_, args) =>
        {
            if (args[0] == "rev-parse") return FakeGit(args);
            return new(0, $"100644 blob {new string(args[3] == "base" ? '1' : '2', 40)}\t{quoted}\n", "");
        }, (_, ids) =>
        {
            batchCalls++;
            Assert.Equal(2, ids.Count);
            return new(new(0, "", ""), ["class A { void M() { X(1); } }", "class A { void M() { X(2); } }"]);
        });
        Assert.Equal(1, batchCalls);
        Assert.Null(read.Diagnostic);
        Assert.Equal(path, Assert.Single(read.Entries).File);
    }

    [Fact]
    public void IncompleteOrUnsuccessfulBatchDiscardsAllContent()
    {
        foreach (var result in new[]
        {
            new GitBlobBatchReader.Result(new(0, "", ""), []),
            new GitBlobBatchReader.Result(new(0, "", "", DrainTimedOut: true), ["partial"]),
            new GitBlobBatchReader.Result(new(0, "", "", ProcessStarted: false), ["partial"])
        })
        {
            var read = ReviewerChangedExistingTestReader.Read("fixture", "base", "head", [PathInRepo],
                (_, args) => FakeGit(args), (_, _) => result);
            Assert.Empty(read.Entries);
            Assert.Contains("git cat-file --batch", read.Diagnostic!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealBatchRejectsMissingOrNonBlobAfterValidContent(bool nonBlob)
    {
        var root = CreateSeededDispatchRepository();
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, PathInRepo.Replace("Feature.Tests/", "")), "class A {}");
        Commit(root);
        var good = GitCli.Run(root, "rev-parse", "HEAD:tests/T.cs");
        var tree = GitCli.Run(root, "rev-parse", "HEAD^{tree}");
        Assert.True(good.Succeeded, good.Error);
        Assert.True(tree.Succeeded, tree.Error);
        var read = GitBlobBatchReader.Read(root, [good.Output.Trim(), nonBlob ? tree.Output.Trim() : new string('0', 40)]);
        Assert.False(read.Git.Succeeded);
        Assert.True(read.Git.ProcessStarted);
        Assert.Empty(read.Contents);
        Assert.NotEmpty(read.Git.Error);
    }

    private static GitCli.GitResult FakeGit(string[] args) => args[0] switch
    {
        "rev-parse" => new(0, args[2].StartsWith("base", StringComparison.Ordinal) ? "base" : "head", ""),
        "-c" => new(0, string.Concat(args.Skip(5).Select(path =>
            $"100644 blob {new string(args[3] == "base" ? '1' : '2', 40)}\t{path}\n")), ""),
        "ls-tree" => new(0, string.Concat(args.Skip(4).Select(path =>
            $"100644 blob {new string(args[2] == "base" ? '1' : '2', 40)}\t{path}\0")), ""),
        "show" => new(0, "class A { void M() {} }", ""),
        _ => throw new InvalidOperationException("Unexpected git command")
    };

    private static string[] CaptureLaunches(Action action)
    {
        var launches = new List<string>();
        var testName = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.False(string.IsNullOrEmpty(testName));
        Assert.NotEqual("<unknown>", testName);
        Action<string>? previous;
        Action<string> observer;
        do
        {
            previous = Volatile.Read(ref GitCli.LaunchObserver);
            var chained = previous;
            observer = command =>
            {
                chained?.Invoke(command);
                if (HostCapacitySlotTimingWriter.CurrentTestName() == testName) launches.Add(command);
            };
        } while (Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, previous) != previous);
        try { action(); }
        finally { Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer); }
        return launches.ToArray();
    }

    // Frozen from 9add975436f2553722e082b5faf676a7c1b9a1b5; only the containing type is renamed.
    private static class FrozenPerFileReader
    {
        internal static ReviewerChangedExistingTestRead Read(
            string workingDirectory, string? mergeBase, string? headCommit, IReadOnlyList<string>? changedPaths,
            Func<string, string[], GitCli.GitResult>? git = null)
        {
            try
            {
                var paths = (changedPaths ?? []).Select(path => path.Replace('\\', '/'))
                    .Where(path => path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (paths.Length == 0) return new([], null);
                if (string.IsNullOrWhiteSpace(mergeBase))
                    return new([], "Changed existing tests unavailable because the reviewer merge base is missing.");
                if (string.IsNullOrWhiteSpace(headCommit))
                    return new([], "Changed existing tests unavailable because the candidate head is missing.");
                git ??= (directory, args) => GitCli.Run(directory, args);
                // Resolve once: comparisons must use these immutable commits, even if a branch moves.
                var baseline = Run("rev-parse", "--verify", mergeBase + "^{commit}").Trim();
                var candidate = Run("rev-parse", "--verify", headCommit + "^{commit}").Trim();
                if (baseline.Length == 0 || candidate.Length == 0)
                    throw new IOException("git rev-parse returned an empty commit identity.");
                var entries = new List<ChangedExistingTest>();
                foreach (var path in paths)
                {
                    var before = Methods(ReadFile(baseline, path));
                    var after = Methods(ReadFile(candidate, path));
                    foreach (var (key, method) in before)
                    {
                        var removed = !after.TryGetValue(key, out var headMethod);
                        if (!removed && method.IsEquivalentTo(headMethod, topLevel: false)) continue;
                        var location = removed ? method : headMethod!;
                        var span = location.SyntaxTree.GetLineSpan(location.Span);
                        entries.Add(new(TypeName(method), method.Identifier.ValueText, path,
                            span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, removed));
                    }
                }
                return new(entries, null);

                string Run(params string[] args)
                {
                    var result = git(workingDirectory, args);
                    if (!result.Succeeded || !result.ProcessStarted || result.DrainTimedOut)
                        throw new IOException($"git {string.Join(" ", args)} failed (exit {result.ExitCode}, started={result.ProcessStarted}, drainTimedOut={result.DrainTimedOut}): {result.Error}");
                    return result.Output;
                }

                string ReadFile(string commit, string path)
                {
                    // Absence is a normal addition/deletion; failures of ls-tree/show are not absence.
                    if (string.IsNullOrEmpty(Run("ls-tree", "--name-only", "-z", commit, "--", path))) return string.Empty;
                    return Run("show", commit + ":" + path);
                }
            }
            catch (Exception exception)
            {
                // Partial results would falsely present a complete inventory to the Reviewer.
                return new([], "Changed existing tests unavailable: " + exception.Message.ReplaceLineEndings(" "));
            }
        }

        private static Dictionary<string, MethodDeclarationSyntax> Methods(string text) =>
            CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .ToDictionary(method => TypeName(method) + "." + method.Identifier.ValueText + "(" +
                    string.Join(",", method.ParameterList.Parameters.Select(parameter =>
                        string.Concat(parameter.Type?.DescendantTokens().Select(token => token.Text) ?? []))) + ")",
                    StringComparer.Ordinal);

        private static string TypeName(MethodDeclarationSyntax method) =>
            method.Ancestors().OfType<BaseTypeDeclarationSyntax>().First().Identifier.ValueText;
    }

    private static void Commit(string root)
    {
        RunGit(root, ["add", "tests"], CommitTime);
        RunGit(root, ["commit", "-m", "Test method fixture"], CommitTime);
    }

    private static string Head(string root)
    {
        var result = GitCli.Run(root, "rev-parse", "HEAD");
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
