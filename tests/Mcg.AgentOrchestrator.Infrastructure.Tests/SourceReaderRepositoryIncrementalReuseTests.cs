using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SourceReaderRepositoryIncrementalReuseTests
{
    internal static string[] CopySourcesWithDistinctContents(string repository, string[] originals, string copyRoot)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        return originals.Select(original =>
        {
            var relativePath = Path.GetRelativePath(repository, original);
            var destination = Path.Combine(copyRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(original, destination);
            // The checkout includes identical global using files. Give duplicate fixture copies distinct
            // hashes without changing their syntax, so the deliberate one-file edit is the only Plan miss.
            if (!hashes.Add(SourceReaderFileFacts.Hash(File.ReadAllText(destination))))
                File.AppendAllText(destination, $"{Environment.NewLine}// Fixture path: {relativePath}");
            return destination;
        }).ToArray();
    }

    [Fact]
    public void AppendedDisposableTest_ReusesRepositoryBaselineAndMatchesFullExtract()
    {
        // Keep declared names unique here. Names in the edited file seed the incremental plan.
        var repositoryReuseTemp = Directory.CreateTempSubdirectory("source-reader-repository-reuse-");
        try
        {
            var repositoryReuseRoot = VerifiedRepositoryRoot.Find();
            var repositoryReuseLaneRoots = LaneTestSourceRootResolver.Resolve(repositoryReuseRoot);
            Assert.NotEmpty(repositoryReuseLaneRoots);
            Assert.All(repositoryReuseLaneRoots, laneRoot => Assert.StartsWith("tests/", laneRoot));
            var repositoryReuseOriginals = LaneTestSourceRootResolver.SourceFiles(repositoryReuseRoot, repositoryReuseLaneRoots);
            Assert.True(repositoryReuseOriginals.Length > 1000);
            var repositoryReuseCopyRoot = Path.Combine(repositoryReuseTemp.FullName, "sources");
            var repositoryReuseCopies = CopySourcesWithDistinctContents(repositoryReuseRoot, repositoryReuseOriginals,
                repositoryReuseCopyRoot);
            var repositoryReuseCacheRoot = Path.Combine(repositoryReuseTemp.FullName, "cache");
            var repositoryReuseBaseline = new ImpactReplayTestSourceIndex(repositoryReuseCopies, repositoryRoot: repositoryReuseCopyRoot);
            var repositoryReuseBaselineCache = new SourceReaderAnalysisCache(repositoryReuseCacheRoot);
            repositoryReuseBaselineCache.GetOrAnalyze(repositoryReuseBaseline,
                SourceReadingGuardTestClasses.Extract, SourceReadingGuardTestClasses.Evaluate);
            Assert.Equal(repositoryReuseCopies.Length, repositoryReuseBaselineCache.SemanticFileCount);
            var baselineKey = SourceReaderAnalysisCache.Key(repositoryReuseBaseline);
            var envelope = Assert.IsType<string>(repositoryReuseBaselineCache.EntryPath(baselineKey));
            Assert.True(File.Exists(envelope));
            File.Delete(envelope);
            Assert.Empty(Directory.GetFiles(repositoryReuseCacheRoot, "*.json"));
            Assert.True(File.Exists(new SourceReaderFileFactsStore(repositoryReuseCacheRoot).PublicationPath(baselineKey)));

            var repositoryReuseEditedPath = Assert.Single(repositoryReuseCopies,
                copy => Path.GetFileName(copy) == nameof(SourceReaderRepositoryIncrementalReuseTests) + ".cs");
            File.AppendAllText(repositoryReuseEditedPath, """

                public sealed class RepositoryReuseAppendedDisposableProbeTests : System.IDisposable
                {
                    [Fact] public void RepositoryReuseAppendedDisposableProbe() {}
                    public void Dispose() {}
                }
                """);
            var repositoryReuseCandidate = new ImpactReplayTestSourceIndex(repositoryReuseCopies, repositoryRoot: repositoryReuseCopyRoot);
            var repositoryReuseIncrementalCache = new SourceReaderAnalysisCache(repositoryReuseCacheRoot);
            var repositoryReuseExtractions = new List<HashSet<string>?>();

            var repositoryReuseIncremental = repositoryReuseIncrementalCache.GetOrAnalyze(repositoryReuseCandidate, (sources, paths) =>
            {
                repositoryReuseExtractions.Add(paths);
                return SourceReadingGuardTestClasses.Extract(sources, paths);
            }, SourceReadingGuardTestClasses.Evaluate);

            var repositoryReusePlan = Assert.IsType<HashSet<string>>(Assert.Single(repositoryReuseExtractions));
            Assert.Contains(Path.GetRelativePath(repositoryReuseCopyRoot, repositoryReuseEditedPath).Replace('\\', '/'), repositoryReusePlan);
            Assert.Equal(1, repositoryReuseIncrementalCache.AnalysisCount);
            Assert.Equal(repositoryReusePlan.Count, repositoryReuseIncrementalCache.SemanticFileCount);
            Assert.True(repositoryReuseIncrementalCache.SemanticFileCount < repositoryReuseCandidate.Trees.Count);
            Assert.True(repositoryReuseIncrementalCache.SemanticFileCount < 100,
                $"Expected fewer than 100 semantic files, found {repositoryReuseIncrementalCache.SemanticFileCount}.");

            var repositoryReuseFullCache = new SourceReaderAnalysisCache(null);
            var repositoryReuseFull = repositoryReuseFullCache.GetOrAnalyze(repositoryReuseCandidate,
                SourceReadingGuardTestClasses.Extract, SourceReadingGuardTestClasses.Evaluate);
            Assert.Equal(repositoryReuseCopies.Length, repositoryReuseFullCache.SemanticFileCount);
            Assert.Equal(repositoryReuseFull.DeclaresSnapshotCache, repositoryReuseIncremental.DeclaresSnapshotCache);
            Assert.Equal(repositoryReuseFull.ReaderOwners, repositoryReuseIncremental.ReaderOwners);
            Assert.Equal(repositoryReuseFull.AssemblyScans.Count, repositoryReuseIncremental.AssemblyScans.Count);
            foreach (var (expectedScan, actualScan) in repositoryReuseFull.AssemblyScans.Zip(repositoryReuseIncremental.AssemblyScans))
            {
                Assert.Equal(expectedScan.ContentHash, actualScan.ContentHash);
                Assert.Equal(expectedScan.TextLength, actualScan.TextLength);
                Assert.Equal(expectedScan.CallPositions, actualScan.CallPositions);
            }
        }
        finally
        {
            repositoryReuseTemp.Delete(recursive: true);
        }
    }
}
