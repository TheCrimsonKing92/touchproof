using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum ShadowScratchRetention { Retain, Disposable }

internal sealed record ShadowStartHooks(
    string? RegistryDirectory = null,
    string? StagingParent = null,
    Func<string, ShadowStartVolume>? MapVolume = null,
    Func<ShadowStartVolume, long>? AvailableBytes = null,
    Action<ShadowStartStore>? BeforeBackup = null,
    Action<ShadowStartStore>? BeforeCopy = null);

internal sealed record ShadowStartStore(string Source, string Destination, string Staged);
internal sealed record ShadowStartVolume(string Identity, string Root);
internal sealed record ShadowStartCapacity(string Volume, long RequiredBytes, long AvailableBytes)
{
    internal long ShortfallBytes => Math.Max(0, RequiredBytes - AvailableBytes);
}

internal sealed record ShadowStartCopyPlan(ShadowScratchRetention Retention, IReadOnlyList<ShadowStartStore> Stores,
    IReadOnlyList<ShadowStartCapacity> Capacity)
{
    internal const long MiB = 1024 * 1024;
    internal const long Reserve = 1024 * MiB;
    internal const long WorkingSpace = 256 * MiB;
    internal static IReadOnlyList<string> Suffixes { get; } = ["", "-wal", "-shm", "-journal"];

    internal static IEnumerable<string> DatabaseNames => StoreSchemaRegistry.Inventory.Select(entry => entry.Database)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(database => database is not ("per-database" or "impact-replay.checkpoint.db"));

    internal static ShadowStartCopyPlan Create(string source, string state, string staging, string registry,
        ShadowScratchRetention retention, ShadowStartHooks hooks)
    {
        var stores = new List<ShadowStartStore>();
        long stagedBytes = 0;
        var stateBytes = WorkingSpace;
        foreach (var name in DatabaseNames)
        {
            var path = Path.Combine(source, name);
            if (!TryReadLength(path, out var main)) continue;

            _ = TryReadLength(path + "-wal", out var wal);
            _ = TryReadLength(path + "-shm", out var shm);
            _ = TryReadLength(path + "-journal", out var journal);
            stagedBytes = checked(stagedBytes + main + wal + shm + journal);
            stateBytes = checked(stateBytes + Math.Max(16 * MiB, 2 * main + wal + shm + journal));
            stores.Add(new(path, Path.Combine(state, name), Path.Combine(staging, name)));
        }

        var map = hooks.MapVolume ?? ShadowStartVolumes.Map;
        var readAvailable = hooks.AvailableBytes ?? ShadowStartVolumes.AvailableBytes;
        var requirements = new List<(ShadowStartVolume Volume, long Bytes)> { (map(state), stateBytes) };
        if (stores.Count != 0)
            requirements.Add((map(staging), stagedBytes));
        if (retention == ShadowScratchRetention.Disposable)
            requirements.Add((map(registry), 16 * MiB)); // Ownership metadata and bounded diagnostic receipt space.
        var capacity = requirements.GroupBy(item => item.Volume.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var volume = group.First().Volume;
                return new ShadowStartCapacity(volume.Root, checked(Reserve + group.Sum(item => item.Bytes)),
                    readAvailable(volume));
            }).ToArray();
        return new(retention, stores, capacity);
    }

    private static bool TryReadLength(string path, out long length)
    {
        try { length = new FileInfo(path).Length; return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { length = 0; return false; }
    }
}

internal static class ShadowStartVolumes
{
    internal static ShadowStartVolume Map(string path)
    {
        while (!Directory.Exists(path))
            path = Path.GetDirectoryName(path) ?? throw new IOException("No existing volume path for shadow-start.");

        if (!OperatingSystem.IsWindows())
        {
            var root = DriveInfo.GetDrives().Select(drive => drive.Name)
                .Where(name => CliShadowStartCommand.ContainsPath(name, path))
                .OrderByDescending(name => name.Length).First();
            return new(root, root);
        }

        var mount = new StringBuilder(32768);
        if (!GetVolumePathNameW(path, mount, (uint)mount.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot resolve volume for {path}.");
        var rootPath = mount.ToString();
        var identity = new StringBuilder(64);
        if (GetVolumeNameForVolumeMountPointW(rootPath, identity, (uint)identity.Capacity))
            return new(identity.ToString(), rootPath);
        // Volume GUIDs are unavailable for network shares. An unresolved local volume is an error.
        if (rootPath.StartsWith(@"\\", StringComparison.Ordinal))
            return new(rootPath, rootPath);
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot identify volume {rootPath}.");
    }

    internal static long AvailableBytes(ShadowStartVolume volume)
    {
        if (!OperatingSystem.IsWindows())
            return new DriveInfo(volume.Root).AvailableFreeSpace;
        if (!GetDiskFreeSpaceExW(volume.Root, out var free, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read free space for {volume.Root}.");
        return (long)Math.Min(free, (ulong)long.MaxValue);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetDiskFreeSpaceExW(string path, out ulong available, out ulong total, out ulong free);
}
