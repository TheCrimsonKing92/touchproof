using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SourceReaderRepositoryIncrementalReuseTests
{
    internal static string[] CopySources(string repository, string[] originals, string copyRoot)
    {
        return originals.Select(original =>
        {
            var relativePath = Path.GetRelativePath(repository, original);
            var destination = Path.Combine(copyRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(original, destination);
            return destination;
        }).ToArray();
    }

    [Fact]
    public void AppendedDisposableTest_ReusesRepositoryBaselineAndMatchesFullExtract()
    {
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
            var repositoryReuseCopiedSources = CopySources(repositoryReuseRoot, repositoryReuseOriginals,
                repositoryReuseCopyRoot);
            // Names from both versions seed Plan. Give the append a file with a unique declaration.
            const string repositoryReuseEditedRelativePath = "tests/SourceReaderRepositoryReuseProbe.cs";
            var repositoryReuseEditedPath = Path.Combine(repositoryReuseCopyRoot, repositoryReuseEditedRelativePath);
            File.WriteAllText(repositoryReuseEditedPath, "internal sealed class SourceReaderRepositoryReuseFixtureAnchor {}");
            string[] repositoryReuseCopies = [.. repositoryReuseCopiedSources, repositoryReuseEditedPath];
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
            Assert.Equal(repositoryReuseEditedRelativePath, Assert.Single(repositoryReusePlan));
            Assert.Equal(1, repositoryReuseIncrementalCache.AnalysisCount);
            Assert.Equal(repositoryReusePlan.Count, repositoryReuseIncrementalCache.SemanticFileCount);
            Assert.True(repositoryReuseIncrementalCache.SemanticFileCount < repositoryReuseCandidate.Trees.Count);

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
