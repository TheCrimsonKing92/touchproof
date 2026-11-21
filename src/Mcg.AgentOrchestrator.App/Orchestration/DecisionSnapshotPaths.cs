using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DecisionSnapshotPaths
{
    internal static string ResolveDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
            throw new DirectoryNotFoundException(directory.FullName);

        if (!OperatingSystem.IsWindows())
        {
            var resolved = directory.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null) return ResolveDirectory(resolved.FullName);
            return directory.Parent is { } parent
                ? Path.Combine(ResolveDirectory(parent.FullName), directory.Name)
                : directory.FullName;
        }

        using var handle = CreateFileW(directory.FullName, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Cannot open directory '{directory.FullName}'.", new Win32Exception());

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException($"Cannot resolve directory '{directory.FullName}'.", new Win32Exception());

        var resolvedPath = buffer.ToString();
        if (resolvedPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + resolvedPath[8..];
        return resolvedPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolvedPath[4..] : resolvedPath;
    }

    internal static string ResolveOutput(string resolvedStateDirectory, string output)
    {
        var path = Path.GetFullPath(output);
        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Output must identify a file.", nameof(output));

        var directory = ResolveDirectory(parent);
        var resolvedOutput = Path.Combine(directory, name);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var copy = Path.TrimEndingDirectorySeparator(resolvedStateDirectory);
        var prefix = Path.EndsInDirectorySeparator(copy) ? copy : copy + Path.DirectorySeparatorChar;
        if (resolvedOutput.Equals(copy, comparison) || resolvedOutput.StartsWith(prefix, comparison))
            throw new ArgumentException("Snapshot output must be outside the state directory.", nameof(output));

        if (new FileInfo(resolvedOutput).LinkTarget is not null)
            throw new ArgumentException("Snapshot output must identify a file outside the state directory without a symbolic link.", nameof(output));
        return resolvedOutput;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
