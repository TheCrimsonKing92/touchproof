using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class HostTestSettingsFixture : IDisposable
{
    internal const string Slots = "MCG_TEST_HOST_CAPACITY_SLOTS";
    internal readonly string Root = Directory.CreateTempSubdirectory("host-settings-").FullName;
    internal string ConfigurationPath => Path.Combine(Root, "executors.json");
    internal static AcceptanceGateEngineSettings Manifest => AcceptanceGateEngineSettings.Parse(
        """{"checks":[],"testSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"4","MCG_TEST_MANIFEST_ONLY":"kept"}}""");
    internal static RemoteLaneCandidateIdentity Identity(string manifest = "manifest") =>
        new("attempt", "goal", "commit", "tree", "main", manifest);
    internal RemoteLaneExecutorConfiguration Load(string json)
    {
        File.WriteAllText(ConfigurationPath, json);
        return RemoteLaneExecutorConfiguration.Load(ConfigurationPath);
    }
    internal RemoteLaneCoordinator Coordinator(RemoteLaneExecutorConfiguration configuration,
        AcceptanceGateEngineSettings? manifest = null, FakeRemoteLaneExecutor? executor = null) =>
        new(configuration, Identity(EffectiveAcceptanceManifestIdentity.Compute([], manifest ?? Manifest)),
            Root, null, executor ?? new(), TimeProvider.System, TimeSpan.FromSeconds(1), null)
        { TestSettings = (manifest ?? Manifest).DeclaredTestSettings };
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
