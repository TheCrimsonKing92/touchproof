using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;
using static BrokerDeliveryFixtureObservation;

// Owns only pristine committed repositories. Case state is never returned to this cache.
// Shared's root is under AssemblyTempRedirect's process root, released by the assembly
// fixture (and its process-exit fallback). Separate owners allow deterministic lifetime tests.
internal sealed class BrokerDeliveryRepositoryTemplate : IDisposable
{
    private static readonly Lazy<BrokerDeliveryRepositoryTemplate> ProcessOwner = new(() => new());
    internal static BrokerDeliveryRepositoryTemplate Shared => ProcessOwner.Value;

    internal readonly record struct Variant(bool Dotnet, bool Go, bool Node, string? PythonMarker)
    {
        internal void Validate()
        {
            if (PythonMarker is not (null or "pyproject.toml" or "requirements.txt"))
                throw new ArgumentException("Unsupported broker Python marker.", nameof(PythonMarker));
        }

        internal string Identity => $"dotnet={Dotnet};go={Go};node={Node};python={PythonMarker ?? "none"}";
    }

    private sealed class Slot
    {
        internal Prepared? Repository;
    }

    private sealed record Prepared(string Root, string Head);
    private readonly ConcurrentDictionary<Variant, Slot> slots = new();
    private readonly ReaderWriterLockSlim lifetime = new();
    private readonly Func<string, Variant, string> initialize;
    private bool disposed;
    internal string Root { get; } = SharedTestSupport.CreateTempDirectory();

    internal BrokerDeliveryRepositoryTemplate(Func<string, Variant, string>? initialize = null) =>
        this.initialize = initialize ?? CreateFresh;

    internal string CopyTo(string destination, Variant variant, BrokerDeliveryFixtureObservation? observation = null)
    {
        variant.Validate();
        lifetime.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("Broker repository copy requires a fresh destination.");
            observation?.SetRepositoryVariant(variant.Identity);
            var slot = slots.GetOrAdd(variant, _ => new Slot());
            Prepared prepared;
            MeasureAction(Phase.RepositoryTemplateWait, () => Monitor.Enter(slot));
            try
            {
                // Publish only successful initialization. A failed attempt leaves the slot retryable.
                prepared = slot.Repository ??= Measure(Phase.RepositoryTemplateInitialization, () =>
                {
                    var staging = Path.Combine(Root, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    try { return new Prepared(staging, initialize(staging, variant)); }
                    catch (Exception failure)
                    {
                        CleanupAfterFailure(staging, failure);
                        throw;
                    }
                });
            }
            finally { Monitor.Exit(slot); }

            MeasureAction(Phase.RepositoryCopy, () =>
            {
                try
                {
                    Directory.CreateDirectory(destination);
                    // Same full-copy convention as CliAcceptanceGitRepositoryTemplate: ordinary
                    // file copies include .git, with no links, alternates or shared writable state.
                    foreach (var directory in Directory.EnumerateDirectories(prepared.Root, "*", SearchOption.AllDirectories))
                        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(prepared.Root, directory)));
                    foreach (var file in Directory.EnumerateFiles(prepared.Root, "*", SearchOption.AllDirectories))
                        File.Copy(file, Path.Combine(destination, Path.GetRelativePath(prepared.Root, file)));
                }
                catch (Exception failure)
                {
                    CleanupAfterFailure(destination, failure);
                    throw;
                }
            });
            return prepared.Head;
        }
        finally { lifetime.ExitReadLock(); }

        T Measure<T>(Phase phase, Func<T> action) => observation is null ? action() : observation.Measure(phase, action);
        void MeasureAction(Phase phase, Action action)
        {
            if (observation is null) action(); else observation.Measure(phase, action);
        }
    }

    internal static string CreateFresh(string root, Variant variant)
    {
        variant.Validate();
        Directory.CreateDirectory(root);
        Git(root, "init", "--initial-branch=main");
        File.WriteAllText(Path.Combine(root, "fixture.txt"), "broker fixture");
        if (variant.Dotnet)
        {
            // Node precedence is pinned against a root csproj; .NET/Go retain their nested layout.
            var projectDirectory = variant.Node ? root : Path.Combine(root, "src", "Fixture");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        }
        if (variant.Go)
            File.WriteAllText(Path.Combine(root, "go.mod"), "module example.invalid/broker\n\ngo 1.21\n");
        if (variant.Node)
            File.WriteAllText(Path.Combine(root, "package.json"),
                """{"name":"broker-fixture","private":true,"scripts":{"test":"node --test","build":"node build.js","lint":"eslint ."}}""");
        if (variant.PythonMarker is { } marker)
            File.WriteAllText(Path.Combine(root, marker), marker == "pyproject.toml"
                ? "[project]\nname = \"broker-fixture\"\nversion = \"0.0.0\"\n[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n"
                : "pytest\n");
        if (!variant.Dotnet && !variant.Go && !variant.Node)
            File.WriteAllText(Path.Combine(root, "README.md"),
                "# Broker fixture\nDiscover the project-standard build and test commands from the project CI configuration.\n");
        Git(root, "add", ".");
        Git(root, "-c", "user.name=Broker Tests", "-c", "user.email=broker@example.invalid", "commit", "--quiet", "-m", "fixture");
        return Git(root, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut,
            $"git fixture failed: exit={result.ExitCode}; stderr={result.Error}");
        return result.Output;
    }

    private static void CleanupAfterFailure(string root, Exception failure)
    {
        try { SharedTestSupport.RemoveTempDirectory(root); }
        catch (Exception cleanupFailure)
        {
            // Surface both failures rather than losing the initialization/copy diagnosis.
            throw new AggregateException("Broker repository preparation and cleanup failed.", failure, cleanupFailure);
        }
    }

    public void Dispose()
    {
        // Keep copies and initialization alive until their reads finish; teardown cannot delete
        // a source while it is being copied. Case repositories live outside this owned root.
        lifetime.EnterWriteLock();
        try
        {
            if (disposed) return;
            SharedTestSupport.RemoveTempDirectory(Root);
            slots.Clear();
            disposed = true;
        }
        finally { lifetime.ExitWriteLock(); }
    }
}
