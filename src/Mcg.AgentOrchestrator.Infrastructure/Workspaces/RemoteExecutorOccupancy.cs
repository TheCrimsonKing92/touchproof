using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Advisory cross-process occupancy: a pid alone is never proof of a living owner.
internal static class RemoteExecutorOccupancy
{
    private sealed record Marker(int OwnerPid, DateTimeOffset OwnerStartUtc);
    internal static string Folder(string root, string executor) =>
        Path.Combine(root, ".orchestrator", "remote-executor-occupancy", executor);
    private static bool SafeName(string value) => !string.IsNullOrWhiteSpace(value) &&
        value is not ("." or "..") && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    internal static DateTimeOffset? DefaultStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception) { return null; }
    }

    internal static IDisposable? TryClaimExclusive(string root, string executor)
        => TryClaimExclusive(root, executor, 0);

    internal static IDisposable? TryClaimExclusive(string root, string executor, int slotIndex)
    {
        try
        {
            if (slotIndex < 0 || !SafeName(executor)) return null;
            var folder = Folder(root, executor);
            Directory.CreateDirectory(folder);
            var name = slotIndex == 0 ? "claim.lock" : $"slot-{slotIndex}.lock";
            return new FileStream(Path.Combine(folder, name), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception) { return null; }
    }

    internal static void Claim(string root, string executor, string attempt)
        => Claim(root, executor, attempt, 0);

    internal static void Claim(string root, string executor, string attempt, int slotIndex,
        RemoteExecutorClaimClass? claimClass = null)
    {
        string? temporary = null;
        try
        {
            if (slotIndex < 0 || !SafeName(executor) || !SafeName(attempt) ||
                DefaultStartTime(Environment.ProcessId) is not { } start) return;
            var folder = Folder(root, executor);
            Directory.CreateDirectory(folder);
            temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            var marker = JsonSerializer.SerializeToNode(new Marker(Environment.ProcessId, start))!;
            if (claimClass is not null) marker["Class"] = claimClass.ToString();
            File.WriteAllText(temporary, marker.ToJsonString());
            File.Move(temporary, Path.Combine(folder, MarkerName(attempt, slotIndex)), overwrite: true);
        }
        catch (Exception) { }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            catch (Exception) { }
        }
    }

    internal static void Release(string root, string executor, string attempt)
        => Release(root, executor, attempt, 0);

    internal static void Release(string root, string executor, string attempt, int slotIndex)
    {
        try
        {
            if (slotIndex >= 0 && SafeName(executor) && SafeName(attempt))
                File.Delete(Path.Combine(Folder(root, executor), MarkerName(attempt, slotIndex)));
        }
        catch (Exception) { }
    }

    private static string MarkerName(string attempt, int slotIndex) =>
        slotIndex == 0 ? attempt + ".json" : $"{attempt}.slot-{slotIndex}.json";

    internal static bool IsOccupied(string root, string executor, Func<int, DateTimeOffset?>? startTimeOf = null)
    {
        var occupied = false;
        try
        {
            if (!SafeName(executor)) return false;
            var folder = Folder(root, executor);
            if (!Directory.Exists(folder)) return false;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var text = ReadMarkerText(path);
                    var marker = JsonSerializer.Deserialize<Marker>(text);
                    if (IsLiveOrRemoveDead(path, text, marker, startTimeOf)) occupied = true;
                }
                catch (Exception) { }
            }
            SweepDeadDemands(folder, startTimeOf);
        }
        catch (Exception) { }
        return occupied;
    }

    private static bool IsLive(Marker? marker, Func<int, DateTimeOffset?>? startTimeOf) =>
        marker is not null && (startTimeOf ?? DefaultStartTime)(marker.OwnerPid) is { } start &&
        start == marker.OwnerStartUtc;

    internal static IReadOnlyList<(string Attempt, string? Class)> LiveMarkers(string root, string executor,
        Func<int, DateTimeOffset?>? startTimeOf = null, bool requireReadableDirectory = false)
    {
        var markers = new List<(string, string?)>();
        try
        {
            if (!SafeName(executor))
                throw new ArgumentException("Invalid executor id.", nameof(executor));
            var folder = Folder(root, executor);
            if (!requireReadableDirectory && !Directory.Exists(folder)) return markers;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var text = ReadMarkerText(path);
                    using var json = JsonDocument.Parse(text);
                    var marker = json.RootElement.Deserialize<Marker>();
                    if (IsLiveOrRemoveDead(path, text, marker, startTimeOf)) markers.Add((Path.GetFileNameWithoutExtension(path),
                        json.RootElement.TryGetProperty("Class", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null));
                }
                catch (Exception) { }
            }
            SweepDeadDemands(folder, startTimeOf);
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception) when (!requireReadableDirectory) { }
        return markers;
    }

    private static string ReadMarkerText(string path)
    {
        // A concurrent cleanup can open this file with DELETE access to validate its contents.
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private static bool IsLiveOrRemoveDead(string path, string text, Marker? marker, Func<int, DateTimeOffset?>? startTimeOf)
    {
        if (IsLive(marker, startTimeOf)) return true;
        if (marker is null || !OperatingSystem.IsWindows()) return false;

        try
        {
            using var file = OpenForCleanup(path);
            if (file is not null && file.ReadText() == text) file.DeleteIfUncontended();
        }
        catch (Exception) { }
        return false;
    }

    internal static CleanupFile? OpenForCleanup(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            fullPath = fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;

        var file = new CleanupFile();
        try
        {
            if (file.TryOpen(fullPath)) return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }

        file.Dispose();
        return null;
    }

    internal sealed class CleanupFile : IDisposable
    {
        private const int RequestSize = 12;
        private const int ResponseSize = 24;
        private readonly EventWaitHandle _oplockBreak = new(false, EventResetMode.ManualReset);
        private SafeFileHandle? _handle;
        private FileStream? _stream;
        private IntPtr _request;
        private bool _pending;

        internal bool TryOpen(string fullPath)
        {
            const uint genericRead = 0x80000000;
            const uint deleteAccess = 0x00010000;
            const uint openRequiringOplock = 0x00040000;
            const uint openReparsePoint = 0x00200000;
            const uint requestOplock = 0x00090240;
            const int ioPending = 997;
            const int cacheReadAndHandle = 3;
            const int requestFlag = 1;

            var overlappedSize = Marshal.SizeOf<NativeOverlapped>();
            _request = Marshal.AllocHGlobal(overlappedSize + RequestSize + ResponseSize);
            // The low bit keeps the oplock's completion off the stream's .NET completion port.
            Marshal.StructureToPtr(new NativeOverlapped
            {
                EventHandle = _oplockBreak.SafeWaitHandle.DangerousGetHandle() | 1
            }, _request, fDeleteOld: false);
            var input = _request + overlappedSize;
            Marshal.WriteInt16(input, 0, 1); // REQUEST_OPLOCK_CURRENT_VERSION
            Marshal.WriteInt16(input, 2, RequestSize);
            Marshal.WriteInt32(input, 4, cacheReadAndHandle);
            Marshal.WriteInt32(input, 8, requestFlag);

            // Reserve the oplock in the open itself, so a legacy overwrite can wait for
            // release even if it arrives before the DeviceIoControl request.
            var options = new CreateFileOptions
            {
                Size = (uint)Marshal.SizeOf<CreateFileOptions>(),
                Flags = (uint)FileOptions.Asynchronous | openRequiringOplock | openReparsePoint
            };
            _handle = CreateFile2(fullPath, genericRead | deleteAccess, FileShare.Read | FileShare.Delete,
                FileMode.Open, ref options);
            if (_handle.IsInvalid) return false;

            _pending = !DeviceIoControl(_handle, requestOplock, input, RequestSize,
                input + RequestSize, ResponseSize, IntPtr.Zero, _request) &&
                Marshal.GetLastPInvokeError() == ioPending;
            if (!_pending) return false;

            _stream = new FileStream(_handle, FileAccess.Read, bufferSize: 4096, isAsync: true);
            return true;
        }

        internal string ReadText()
        {
            using var reader = new StreamReader(_stream!, leaveOpen: true);
            return reader.ReadToEnd();
        }

        internal bool WaitForBreak(TimeSpan timeout) => _oplockBreak.WaitOne(timeout);

        internal void DeleteIfUncontended()
        {
            // Closing the handle acknowledges a break and lets the waiting operation proceed.
            if (WaitForBreak(TimeSpan.Zero)) return;
            // Remove the name when this handle closes, even if a reader still holds the old file.
            const int fileDispositionInfoEx = 21;
            const uint delete = 1;
            const uint posixSemantics = 2;
            var flags = delete | posixSemantics;
            _ = SetFileInformationByHandle(_handle!, fileDispositionInfoEx, ref flags, sizeof(uint));
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); }
            finally
            {
                _handle?.Dispose();
                // The native request can still reference the buffer until close completes it.
                if (_pending) _oplockBreak.WaitOne();
                _pending = false;
                Marshal.FreeHGlobal(_request);
                _request = IntPtr.Zero;
                _oplockBreak.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateFileOptions
    {
        public uint Size;
        public uint Attributes;
        public uint Flags;
        public uint SecurityQosFlags;
        public IntPtr SecurityAttributes;
        public IntPtr TemplateFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile2(string path, uint access, FileShare share,
        FileMode creationDisposition, ref CreateFileOptions options);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, IntPtr bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass,
        ref uint information, uint size);

    private static void SweepDeadDemands(string folder, Func<int, DateTimeOffset?>? startTimeOf)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.demand"))
            {
                try
                {
                    var text = ReadMarkerText(path);
                    IsLiveOrRemoveDead(path, text, JsonSerializer.Deserialize<Marker>(text), startTimeOf);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
