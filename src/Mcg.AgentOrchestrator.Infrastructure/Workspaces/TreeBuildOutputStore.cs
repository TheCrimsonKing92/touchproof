using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TreeBuildOutputIdentity(
    string[] BuildArguments, string Configuration, string SdkVersion, string OperatingSystem, string Architecture)
{
    internal bool IsComplete => BuildArguments is { Length: > 0 } && BuildArguments.All(argument => argument is not null) &&
        !string.IsNullOrWhiteSpace(Configuration) &&
        !string.IsNullOrWhiteSpace(SdkVersion) && !string.IsNullOrWhiteSpace(OperatingSystem) &&
        !string.IsNullOrWhiteSpace(Architecture);
}

internal enum TreeBuildOutputRefusal { NotFound, IdentityDiffers, VerificationFailed }

internal readonly record struct TreeBuildOutputRestore(TreeBuildOutputRefusal? Refusal, int FileCount = 0)
{
    internal bool Hit => Refusal is null;
}

internal sealed class TreeBuildOutputStore(string root, TimeProvider? clock = null, Func<string, string>? contentHash = null)
{
    private const string ManifestName = "manifest.json";
    private const string OutputsName = "outputs";
    private const int EntryLimit = 48;
    private const int ManifestVersion = 1;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record Manifest(
        int Version, string TreeDigest, TreeBuildOutputIdentity Identity, Dictionary<string, string> Files);

    private sealed class RestoreRollbackException(string staging, Exception cause) :
        InvalidOperationException($"Could not roll back a build output restore. Backups remain at {staging}.", cause);

    internal static string HashKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string IdentityKey(TreeBuildOutputIdentity identity) => HashKey(JsonSerializer.Serialize(identity, Json));
    internal static bool HasTreeDigest(string digest) => BuildReceipt.Digest(digest) != "unresolved";

    internal bool Publish(string treeDigest, TreeBuildOutputIdentity identity, string artifactsPath,
        CancellationToken cancellationToken = default)
    {
        if (!HasTreeDigest(treeDigest) || !identity.IsComplete) return false;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RejectOverlappingRoots(artifactsPath);
            Directory.CreateDirectory(root);
            using var gate = LockStore();
            Prune();
            var entry = EntryPath(treeDigest, identity);
            var staging = Path.Combine(root, "_staging");
            DeleteDirectory(staging);
            try
            {
                var outputs = Path.Combine(staging, OutputsName);
                Directory.CreateDirectory(outputs);
                var files = ListFiles(artifactsPath, buildOutputsOnly: true);
                if (files.Count == 0) return false;
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (relative, source) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var copy = Path.Combine(outputs, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(source, copy);
                    hashes.Add(relative, HashFile(copy));
                }

                File.WriteAllText(Path.Combine(staging, ManifestName),
                    JsonSerializer.Serialize(new Manifest(ManifestVersion, treeDigest, identity, hashes), Json));
                cancellationToken.ThrowIfCancellationRequested();
                var tree = Path.GetDirectoryName(entry)!;
                Directory.CreateDirectory(tree);
                RequireDirectory(tree);
                // The previous snapshot remains available until the replacement is complete.
                DeleteDirectory(entry);
                Directory.SetLastWriteTimeUtc(staging, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
                Directory.Move(staging, entry);
                Prune();
                return true;
            }
            finally
            {
                CleanupDirectory(staging);
            }
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
            WarnPublishFailure(error);
            return false;
        }
    }

    internal TreeBuildOutputRestore TryRestore(string treeDigest, TreeBuildOutputIdentity? identity,
        string targetArtifactsPath, CancellationToken cancellationToken = default,
        Action<string>? prepareStagedTarget = null)
    {
        if (!HasTreeDigest(treeDigest)) return new(TreeBuildOutputRefusal.NotFound);
        if (identity is null || !identity.IsComplete) return new(TreeBuildOutputRefusal.VerificationFailed);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RejectOverlappingRoots(targetArtifactsPath);
            if (Attributes(root) is null) return new(TreeBuildOutputRefusal.NotFound);
            using var gate = LockStore();
            Prune();
            var entry = EntryPath(treeDigest, identity);
            var tree = Path.GetDirectoryName(entry)!;
            if (Attributes(tree) is null) return new(TreeBuildOutputRefusal.NotFound);
            RequireDirectory(tree);
            if (Attributes(entry) is null) return new(TreeBuildOutputRefusal.IdentityDiffers);
            RequireDirectory(entry);

            var manifestPath = Path.Combine(entry, ManifestName);
            if ((Attributes(manifestPath) & FileAttributes.ReparsePoint) is not (null or 0))
                return new(TreeBuildOutputRefusal.VerificationFailed);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), Json);
            if (manifest is not { Version: ManifestVersion, Files.Count: > 0, Identity.IsComplete: true } ||
                manifest.TreeDigest != treeDigest || IdentityKey(manifest.Identity) != IdentityKey(identity))
                return new(TreeBuildOutputRefusal.VerificationFailed);

            var outputs = ListFiles(Path.Combine(entry, OutputsName));
            if (outputs.Count != manifest.Files.Count ||
                outputs.Keys.Any(path => IsBuildMetadata(path.Split(Path.DirectorySeparatorChar, 2)[0])))
                return new(TreeBuildOutputRefusal.VerificationFailed);
            // Use only paths enumerated under the snapshot root. Prove every hash before the first copy.
            foreach (var (relative, source) in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!manifest.Files.TryGetValue(relative, out var expected) || HashFile(source) != expected)
                    return new(TreeBuildOutputRefusal.VerificationFailed);
            }

            StageAndInstall(outputs, manifest.Files, targetArtifactsPath, cancellationToken, prepareStagedTarget);
            return new(null, outputs.Count);
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
            return new(TreeBuildOutputRefusal.VerificationFailed);
        }
    }

    private void StageAndInstall(Dictionary<string, string> outputs, Dictionary<string, string> hashes,
        string targetPath, CancellationToken cancellationToken, Action<string>? prepareStagedTarget)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("The artifacts root has no parent.");
        CheckDirectoryPath(target);
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".tree-restore-{Guid.NewGuid():N}");
        var payload = Path.Combine(staging, OutputsName);
        Directory.CreateDirectory(payload);
        var keepBackups = false;
        try
        {
            foreach (var (relative, source) in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copy = Path.Combine(payload, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(source, copy);
                if (HashFile(copy) != hashes[relative])
                    throw new IOException("A stored build output changed during restore.");
            }

            prepareStagedTarget?.Invoke(payload);
            cancellationToken.ThrowIfCancellationRequested();
            InstallFiles(ListFiles(payload), target, staging);
        }
        catch (RestoreRollbackException)
        {
            keepBackups = true;
            throw;
        }
        finally
        {
            if (!keepBackups) CleanupDirectory(staging);
        }
    }

    private static void InstallFiles(Dictionary<string, string> files, string target, string staging)
    {
        var installed = new List<(string Destination, string? Backup)>();
        var createdDirectories = new List<string>();
        var destinations = files.Select(file => (Source: file.Value, Destination: Path.Combine(target, file.Key))).ToArray();
        foreach (var (_, destination) in destinations)
        {
            CheckDirectoryPath(Path.GetDirectoryName(destination)!);
            if ((Attributes(destination) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) is not (null or 0))
                throw new IOException("The restore would replace a directory or link.");
        }
        try
        {
            foreach (var (source, destination) in destinations)
            {
                CreateTargetDirectory(Path.GetDirectoryName(destination)!, createdDirectories);
                if (File.Exists(destination))
                {
                    var backup = Path.Combine(staging, $"backup-{installed.Count}");
                    File.Replace(source, destination, backup);
                    installed.Add((destination, backup));
                }
                else
                {
                    File.Move(source, destination);
                    installed.Add((destination, null));
                }
            }
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
            try
            {
                foreach (var (destination, backup) in Enumerable.Reverse(installed))
                {
                    if (backup is null) File.Delete(destination);
                    else File.Move(backup, destination, overwrite: true);
                }
                foreach (var directory in Enumerable.Reverse(createdDirectories)) Directory.Delete(directory);
            }
            catch (Exception rollbackError) when (IsCacheFailure(rollbackError))
            {
                throw new RestoreRollbackException(staging, new AggregateException(error, rollbackError));
            }
            throw;
        }
    }

    private static void CreateTargetDirectory(string path, List<string> created)
    {
        if (Directory.Exists(path)) return;
        CreateTargetDirectory(Path.GetDirectoryName(path)!, created);
        Directory.CreateDirectory(path);
        created.Add(path);
    }

    private static void CheckDirectoryPath(string path)
    {
        for (string? directory = path; directory is not null; directory = Path.GetDirectoryName(directory))
            if (Attributes(directory) is { } attributes &&
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("The build output path contains a file or link.");
    }

    private static Dictionary<string, string> ListFiles(string directory, bool buildOutputsOnly = false)
    {
        RequireDirectory(directory);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var parent))
        {
            foreach (var entry in new DirectoryInfo(parent).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(directory, entry.FullName);
                if (buildOutputsOnly && parent == directory && IsBuildMetadata(relative)) continue;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Build output snapshots cannot contain links.");
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else files.Add(relative, entry.FullName);
            }
        }
        return files;
    }

    private static bool IsBuildMetadata(string name) =>
        name.Equals(DotnetBuildEnvironmentManager.ArtifactsOwnerFileName, PathComparison) ||
        name.Equals(AcceptanceAttemptArtifactCustody.MarkerFileName, PathComparison) ||
        name.Equals("tmp", PathComparison) ||
        name.Equals("worker-host-builds", PathComparison);

    private FileStream LockStore()
    {
        RequireDirectory(root);
        // Coordinate publish, validation and eviction across processes. A busy store is a cache refusal.
        return new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string EntryPath(string treeDigest, TreeBuildOutputIdentity identity) =>
        Path.Combine(root, HashKey(treeDigest), IdentityKey(identity));

    private void Prune()
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var trees = new DirectoryInfo(root).EnumerateDirectories()
            .Where(directory => directory.Name.Length == 64 && directory.Name.All(Uri.IsHexDigit) &&
                (directory.Attributes & FileAttributes.ReparsePoint) == 0).ToArray();
        var entries = new List<DirectoryInfo>();
        foreach (var tree in trees)
        {
            RequireDirectory(tree.FullName);
            entries.AddRange(tree.EnumerateDirectories()
                .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0));
        }
        foreach (var (index, entry) in entries.OrderByDescending(entry => entry.LastWriteTimeUtc)
                     .ThenBy(entry => entry.FullName, StringComparer.Ordinal).Index())
            if (index >= EntryLimit || now - entry.LastWriteTimeUtc > MaxAge)
                DeleteDirectory(entry.FullName);
        foreach (var tree in trees)
            if (!tree.EnumerateFileSystemInfos().Any()) tree.Delete();
    }

    private void RejectOverlappingRoots(string artifactsPath)
    {
        var storeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var artifacts = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactsPath));
        if (storeRoot.Equals(artifacts, PathComparison) ||
            storeRoot.StartsWith(artifacts + Path.DirectorySeparatorChar, PathComparison) ||
            artifacts.StartsWith(storeRoot + Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("The output store and build artifacts must be separate.");
        CheckDirectoryPath(storeRoot);
        CheckDirectoryPath(artifacts);
    }

    private string HashFile(string path)
    {
        if (contentHash is not null) return contentHash(path);
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RequireDirectory(string path)
    {
        if ((Attributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("A build output directory is missing or is a link.");
    }

    private static void DeleteDirectory(string path)
    {
        if (Attributes(path) is null) return;
        RequireDirectory(path);
        Directory.Delete(path, recursive: true);
    }

    private static void CleanupDirectory(string path)
    {
        try { DeleteDirectory(path); }
        catch (Exception error) when (IsCacheFailure(error)) { }
    }

    internal static bool IsCacheFailure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException;

    internal static void WarnPublishFailure(Exception error)
    {
        try { Console.Error.WriteLine($"warning: tree build output publication skipped: {error.GetType().Name}"); }
        catch (IOException) { }
    }
}

internal sealed class TreeBuildOutputReuse(
    Func<TreeBuildOutputStore?> resolveStore,
    Func<DotnetTestBuildPhase, DotnetBuildEnvironment, Task<TreeBuildOutputIdentity?>> resolveIdentity,
    Func<string, bool>? matchesLiveTree = null,
    Action<DotnetBuildEnvironment, string>? prepareStagedTarget = null,
    CancellationToken cancellationToken = default)
{
    internal TreeBuildOutputReuse(TreeBuildOutputStore store,
        Func<DotnetTestBuildPhase, DotnetBuildEnvironment, Task<TreeBuildOutputIdentity?>> resolveIdentity)
        : this(() => store, resolveIdentity) { }

    internal static TreeBuildOutputReuse ForProduction(string worktree, IAcceptanceRunExecutionContext? context) =>
        new(() => ResolveProductionStore(worktree),
            (phase, environment) => ResolveProductionIdentityAsync(phase, environment, worktree, context),
            digest => WorktreeTreeDigest.TryCompute(worktree, out var live, out _) &&
                string.Equals(live, BuildReceipt.Digest(digest), StringComparison.Ordinal),
            (environment, staged) => PrepareProductionTarget(environment, staged, context?.ArtifactCustody),
            context?.CancellationToken ?? CancellationToken.None);

    internal static Task<Attempt?> TryRestoreAsync(DotnetTestBuildPhase phase, BuildReceiptSite? site) =>
        site?.Reuse is { } reuse ? reuse.RestoreAsync(phase, site.TreeDigest) : Task.FromResult<Attempt?>(null);

    private async Task<Attempt?> RestoreAsync(DotnetTestBuildPhase phase, string digest)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TreeBuildOutputStore.HasTreeDigest(digest))
        {
            phase.TreeOutputRestore = new(TreeBuildOutputRefusal.NotFound);
            return null;
        }

        var environment = phase.BuildEnvironment!;
        var wall = Stopwatch.StartNew();
        phase.TreeOutputRestore = new(TreeBuildOutputRefusal.VerificationFailed);
        try
        {
            if (matchesLiveTree?.Invoke(digest) == false || resolveStore() is not { } store ||
                await resolveIdentity(phase, environment).ConfigureAwait(false) is not { IsComplete: true } identity)
                return null;

            var restore = store.TryRestore(digest, identity, environment.ArtifactsPath, cancellationToken, staged =>
            {
                if (matchesLiveTree?.Invoke(digest) == false)
                    throw new IOException("The candidate tree changed during build output restore.");
                prepareStagedTarget?.Invoke(environment, staged);
            });
            phase.TreeOutputRestore = restore;
            (AcceptanceCheckResult Result, bool Retried)? buildResult = null;
            if (restore.Hit)
            {
                buildResult = (new AcceptanceCheckResult("build", true, 0, null,
                    environment.ArtifactsPath, "goal-acceptance-verifier", environment.LeaseId,
                    wall.ElapsedMilliseconds, ResultSummary:
                    $"tree-output-reuse status=hit tree={digest} identity={TreeBuildOutputStore.IdentityKey(identity)} files={restore.FileCount}"), false);
            }
            return new Attempt(store, digest, identity, environment.ArtifactsPath, matchesLiveTree, cancellationToken, buildResult);
        }
        catch (Exception error) when (TreeBuildOutputStore.IsCacheFailure(error) || error is Win32Exception)
        {
            return null;
        }
    }

    internal sealed record Attempt(TreeBuildOutputStore Store, string Digest, TreeBuildOutputIdentity Identity,
        string ArtifactsPath, Func<string, bool>? MatchesLiveTree, CancellationToken CancellationToken,
        (AcceptanceCheckResult Result, bool Retried)? BuildResult)
    {
        internal void PublishAfterBuild(AcceptanceCheckResult result)
        {
            if (!result.Passed || result.LockRemediationApplied ||
                !string.Equals(result.ArtifactsPath, ArtifactsPath, StringComparison.Ordinal)) return;
            CancellationToken.ThrowIfCancellationRequested();
            if (MatchesLiveTree?.Invoke(Digest) == false) return;
            Store.Publish(Digest, Identity, ArtifactsPath, CancellationToken);
        }
    }

    private static TreeBuildOutputStore? ResolveProductionStore(string worktree)
    {
        var result = GitCli.Run(worktree, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var common = result.Output.Trim();
        if (result.ExitCode != 0 || !Path.IsPathFullyQualified(common)) return null;
        var repository = TreeBuildOutputStore.HashKey(System.OperatingSystem.IsWindows() ? common.ToUpperInvariant() : common);
        return new(Path.Combine(DotnetBuildEnvironmentManager.CaptureStorageRoot().RootPath, "tree-output-store", repository));
    }

    private static async Task<TreeBuildOutputIdentity?> ResolveProductionIdentityAsync(DotnetTestBuildPhase phase,
        DotnetBuildEnvironment environment, string worktree, IAcceptanceRunExecutionContext? context)
    {
        var arguments = AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments(phase.BuildArguments, environment);
        if (arguments.Length < 3 || arguments[0] != "dotnet" || arguments[1] != "build" ||
            arguments[2].StartsWith('-') || arguments[2].StartsWith('@')) return null;
        var target = Path.GetFullPath(arguments[2], worktree);
        if (!File.Exists(target)) return null;
        var properties = DotnetBaseBuildCacheIdentityResolver.EvaluationArguments(phase.BuildArguments, environment.Arguments);
        if (properties is null) return null;

        // Only relocate the output-root argument. All other effective values stay in their original order.
        var artifactSwitch = Array.IndexOf(arguments, "--artifacts-path");
        if (artifactSwitch < 3 || artifactSwitch >= arguments.Length - 1 ||
            arguments[artifactSwitch + 1] != environment.ArtifactsPath ||
            Array.LastIndexOf(arguments, "--artifacts-path") != artifactSwitch) return null;
        arguments[2] = target;
        arguments[artifactSwitch + 1] = "<artifacts>";

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = context?.CancellationToken ?? CancellationToken.None;
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            string[] projects = [target];
            if (Path.GetExtension(target) is ".sln" or ".slnx")
            {
                // MSBuild's -getProperty accepts projects only. Use the solution's declared project inventory.
                var listing = await RunProbeAsync(["sln", target, "list"]).ConfigureAwait(false);
                var lines = listing?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (lines is not { Length: > 2 } || !lines[1].All(character => character == '-')) return null;
                projects = lines.Skip(2).Select(project => Path.GetFullPath(
                    project.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(target)!)).ToArray();
            }

            (string Configuration, string Sdk)? identity = null;
            foreach (var project in projects)
            {
                if (!Path.GetExtension(project).EndsWith("proj", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(project)) return null;
                var evaluated = await RunProbeAsync(["msbuild", project, .. properties,
                    "-getProperty:Configuration,NETCoreSdkVersion", "-nologo"]).ConfigureAwait(false);
                if (evaluated is null) return null;
                using var json = JsonDocument.Parse(evaluated);
                var configuration = ReadProperty(json.RootElement, "Configuration");
                var sdk = ReadProperty(json.RootElement, "NETCoreSdkVersion");
                if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(sdk)) return null;
                if (identity is { } value && value != (configuration, sdk)) return null;
                identity = (configuration, sdk);
            }

            return identity is { } selected ? new TreeBuildOutputIdentity(arguments, selected.Configuration, selected.Sdk,
                RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString()) : null;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        async Task<string?> RunProbeAsync(string[] command)
        {
            var result = await WorkerProcessRunner.RunBoundedDirectAsync(
                new("dotnet", command, worktree, TimeSpan.FromSeconds(10)), 64 * 1024, probe.Token,
                TimeProvider.System, null, null, start => AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
                    start.Environment, worktree, environment.RootPath, context?.Settings.DeclaredToolHomes)).ConfigureAwait(false);
            return result is { ExitCode: 0, TimedOut: false, CaptureLimited: false } ? result.StandardOutput : null;
        }
    }

    private static string? ReadProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Properties", out var properties) &&
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void PrepareProductionTarget(DotnetBuildEnvironment environment, string staged,
        AcceptanceAttemptArtifactCustodyContext? custody)
    {
        var artifacts = environment.ArtifactsPath;
        AcceptanceAttemptArtifactCustody.ThrowIfLiveCustodianBlocksTakeover(artifacts, custody?.AttemptId);
        var ownerName = DotnetBuildEnvironmentManager.ArtifactsOwnerFileName;
        var marker = Path.Combine(artifacts, ownerName);
        if (File.Exists(marker))
        {
            if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The build artifacts owner marker is a link.");
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("ownerToken", out var token) ||
                token.ValueKind != JsonValueKind.String || token.GetString() != environment.SlotOwnerToken)
                throw new IOException("The build artifacts belong to another lease.");
        }
        else
        {
            if (Directory.Exists(artifacts) && Directory.EnumerateFileSystemEntries(artifacts).Any())
                throw new IOException("The existing build artifacts have no owner marker.");
            var owner = new DotnetBuildEnvironmentManager.ArtifactsOwnerMarker(1, environment.SlotOwnerToken,
                Environment.ProcessId, Environment.MachineName, DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(staged, ownerName),
                JsonSerializer.Serialize(owner, DotnetBuildEnvironmentManager.JsonOptions));
        }

        if (custody is not null)
            AcceptanceAttemptArtifactCustody.Write(staged, custody.AttemptId, custody.LivenessCheckHint, custody.OwnerProcessId);
    }
}

internal static class TreeBuildOutputRefusalExtensions
{
    internal static string ToReceiptReason(this TreeBuildOutputRefusal refusal) => refusal switch
    {
        TreeBuildOutputRefusal.NotFound => "not-found",
        TreeBuildOutputRefusal.IdentityDiffers => "identity-differs",
        TreeBuildOutputRefusal.VerificationFailed => "verification-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal))
    };
}
