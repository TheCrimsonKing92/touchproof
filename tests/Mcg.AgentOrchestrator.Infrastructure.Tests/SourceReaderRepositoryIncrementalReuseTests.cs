using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SourceReaderRepositoryIncrementalReuseTests
{
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
            var repositoryReuseCopies = repositoryReuseOriginals.Select(original =>
            {
                var repositoryReuseDestination = Path.Combine(repositoryReuseCopyRoot, Path.GetRelativePath(repositoryReuseRoot, original));
                Directory.CreateDirectory(Path.GetDirectoryName(repositoryReuseDestination)!);
                File.Copy(original, repositoryReuseDestination);
                return repositoryReuseDestination;
            }).ToArray();
            var repositoryReuseCacheRoot = Path.Combine(repositoryReuseTemp.FullName, "cache");
            var repositoryReuseBaseline = new ImpactReplayTestSourceIndex(repositoryReuseCopies, repositoryRoot: repositoryReuseCopyRoot);
            var repositoryReuseBaselineCache = new SourceReaderAnalysisCache(repositoryReuseCacheRoot);
            repositoryReuseBaselineCache.GetOrAnalyze(repositoryReuseBaseline,
                SourceReadingGuardTestClasses.Extract, SourceReadingGuardTestClasses.Evaluate);
            Assert.Equal(repositoryReuseCopies.Length, repositoryReuseBaselineCache.SemanticFileCount);

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
