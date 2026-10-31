using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its directory and the labeler override is AsyncLocal.
public sealed class AcceptanceGateBuildArtifactIntegrityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PrepareBuildArtifacts_CreatesFreshRootAndNormalizesOutputs(
        bool existingOutputs, bool ownedPreparation)
    {
        var root = Directory.CreateTempSubdirectory("gate-artifact-integrity-").FullName;
        try
        {
            var artifacts = Path.Combine(root, "artifacts", "main-coverage-baseline");
            var executable = Path.Combine(artifacts, "bin", "probe.exe");
            if (existingOutputs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                File.WriteAllText(executable, "fixture");
            }
            Assert.Equal(existingOutputs, Directory.Exists(artifacts));
            var environment = new DotnetBuildEnvironment(
                "fixture", root, artifacts, Path.Combine(root, "execution.lock"),
                ["--artifacts-path", artifacts], "fixture-owner");
            var labeler = new RecordingLabeler();
            using var labels = AcceptanceGateIntegrityNormalizer.PushIntegrityLabelerForTests(labeler);

            if (ownedPreparation)
            {
                await using var owner = new AcceptanceFocusedVerificationOwner(
                    new AcceptanceFocusedVerificationIdentity("fixture-focused", "fixture-goal", root,
                        "fixture-candidate", Path.Combine(root, "focused"), SlotIndex: null),
                    new AcceptanceGateEngineSettings());
                // The focused runner prepares the lease directly, before any build creates it.
                owner.PrepareBuildArtifactIntegrity(artifacts);
            }
            else
            {
                AcceptanceGateIntegrityNormalizer.PrepareBuildArtifacts(environment, context: null);
            }

            Assert.True(Directory.Exists(artifacts));
            var rootCall = Assert.Single(labeler.Calls, call => call.Path == artifacts);
            Assert.Equal("(OI)(CI)M", rootCall.Level);
            Assert.False(rootCall.Recursive);
            Assert.True(labeler.Query(artifacts).Medium);
            if (existingOutputs)
            {
                var outputCall = Assert.Single(labeler.Calls, call => call.Path == Path.GetDirectoryName(executable));
                Assert.Equal("(OI)(CI)M", outputCall.Level);
                Assert.True(outputCall.Recursive);
                Assert.True(labeler.Query(executable).Medium);
                Assert.False(labeler.Query(executable).Low);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingLabeler : IWorkerIntegrityLabeler
    {
        internal List<(string Path, string Level, bool Recursive)> Calls { get; } = [];

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            // The real labeler cannot label a path that the first build has yet to create.
            Assert.True(Directory.Exists(path) || File.Exists(path), path);
            Calls.Add((path, level, recursive));
            return true;
        }

        public IntegrityLabelState Query(string path)
        {
            var medium = Calls.Any(call => call.Path == path ||
                call.Recursive && path.StartsWith(call.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            return new IntegrityLabelState(Exists: Directory.Exists(path) || File.Exists(path),
                Low: !medium, Inheritable: Directory.Exists(path), Medium: medium);
        }
    }
}
