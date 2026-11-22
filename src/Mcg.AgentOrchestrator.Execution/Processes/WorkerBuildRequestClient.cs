using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A per-invocation transport client. Only the host's canonical record is build evidence.
internal sealed class WorkerBuildRequestClient(TextWriter output, TextWriter error,
    TimeProvider? timeProvider = null, WorkerBuildRequestClient.IPipeOpener? pipeOpener = null)
{
    private const int BrokenPipeHResult = unchecked((int)0x8007006D);
    private const int PipeClosingHResult = unchecked((int)0x800700E8);
    private const int PipeNotConnectedHResult = unchecked((int)0x800700E9);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IPipeOpener _pipes = pipeOpener ?? new WindowsPipeOpener();
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal int Run(string directory, WorkerBuildRequest request, int busyTimeoutSeconds)
    {
        try
        {
            var descriptorPath = Path.Combine(directory, WorkerBuildRequestChannel.DescriptorRelativePath);
            JsonDocument endpoint;
            try
            {
                using var descriptorStream = new FileStream(descriptorPath,
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                endpoint = JsonDocument.Parse(descriptorStream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ReportUnavailable("descriptor", descriptorPath, exception.Message);
            }

            using var descriptor = endpoint;
            var root = descriptor.RootElement;
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("pipe", out var pipe) || pipe.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(pipe.GetString()!, @"\Amcg-worker-build-[a-f0-9]{32}\z", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsupported managed worker build endpoint descriptor.");

            var pipePath = @"\\.\pipe\" + pipe.GetString();
            Stream? connection;
            try { connection = Open(pipePath, busyTimeoutSeconds); }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
            {
                return ReportUnavailable("pipe", pipePath, exception.Message);
            }

            using var stream = connection;
            if (stream is null) return 1;
            var encoding = new UTF8Encoding(false);
            using var reader = new StreamReader(stream, encoding, false, 4096, leaveOpen: true);
            var requestBytes = encoding.GetBytes(JsonSerializer.Serialize(request) + "\n");
            string? line;
            try
            {
                // A StreamWriter's disposal flush after host EOF can obscure the terminal-response diagnostic.
                stream.Write(requestBytes);
                line = reader.ReadLine();
            }
            catch (IOException exception) when (exception is EndOfStreamException ||
                exception.HResult is BrokenPipeHResult or PipeClosingHResult or PipeNotConnectedHResult)
            {
                return ReportUnavailable("pipe", pipePath, exception.Message);
            }
            if (string.IsNullOrWhiteSpace(line))
                return ReportUnavailable("pipe", pipePath, "Managed worker build host closed without a terminal response.");
            using var document = JsonDocument.Parse(line);
            var wire = document.RootElement;
            var response = ReadResponse(wire);
            output.WriteLine($"worker_build_invocation={response.InvocationId}; outcome={Text(wire, "Outcome")}; {response.Diagnostic}");
            var compilerExit = wire.TryGetProperty("CompilerExitCode", out _) ? Text(wire, "CompilerExitCode") : "unavailable";
            output.WriteLine($"compiler_exit={compilerExit}; structural_status={response.StructuralStatus}; verification_exit={response.ExitCode}; {response.StructuralDiagnostic}");
            output.WriteLine("structural_elapsed_ms=" + (response.StructuralElapsedMilliseconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable"));
            return response.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            Win32Exception or JsonException or InvalidOperationException or ArgumentException)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }

    private int ReportUnavailable(string pathKind, string path, string cause)
    {
        error.WriteLine($"worker_build_channel_unavailable {pathKind}=\"{path.ReplaceLineEndings(" ")}\" cause={cause.ReplaceLineEndings(" ")}");
        return 1;
    }

    private Stream? Open(string path, int timeoutSeconds)
    {
        var budget = TimeSpan.FromSeconds(timeoutSeconds);
        var started = _time.GetTimestamp();
        var busy = false;
        while (true)
        {
            if (busy && _time.GetElapsedTime(started) >= budget) break;
            var stream = _pipes.TryOpen(path, out var nativeError);
            if (stream is not null) return stream;
            if (nativeError != 231) throw new Win32Exception(nativeError);
            if (!busy)
            {
                busy = true;
                error.WriteLine($"worker_build_channel_wait pipe=\"{path}\" busy_timeout_seconds={timeoutSeconds}");
            }
            var remaining = budget - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            if (!_pipes.WaitForAvailability(path, (uint)Math.Ceiling(remaining.TotalMilliseconds), out nativeError) &&
                nativeError is not (121 or 231)) throw new Win32Exception(nativeError);
        }
        var seconds = _time.GetElapsedTime(started).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        error.WriteLine($"worker_build_channel_busy pipe=\"{path}\" waited_seconds={seconds}");
        return null;
    }

    private static WorkerBuildResponse ReadResponse(JsonElement wire)
    {
        WorkerBuildStructuralCheck? check = null;
        if (wire.TryGetProperty("StructuralCheck", out var structural) && structural.ValueKind != JsonValueKind.Null)
        {
            try
            {
                if (structural.ValueKind == JsonValueKind.Object && structural.TryGetProperty("Status", out _))
                    check = structural.Deserialize<WorkerBuildStructuralCheck>(WireOptions);
            }
            catch (JsonException) { /* Preserve malformed evidence as a failed check, never a legacy success. */ }
            check ??= new(WorkerBuildStructuralStatus.Unavailable, "", "", "", "", "", null, "");
        }
        return new(Text(wire, "InvocationId"),
            wire.TryGetProperty("Outcome", out var outcome) ? outcome.Deserialize<WorkerBuildOutcome?>(WireOptions) : null,
            Text(wire, "Diagnostic"),
            wire.TryGetProperty("CompilerExitCode", out var compiler) && compiler.ValueKind != JsonValueKind.Null
                ? compiler.GetInt32() : null, check);
    }

    private static string Text(JsonElement value, string name) =>
        !value.TryGetProperty(name, out var token) || token.ValueKind == JsonValueKind.Null ? "" : token.ToString();

    internal interface IPipeOpener
    {
        Stream? TryOpen(string path, out int error);
        bool WaitForAvailability(string path, uint milliseconds, out int error);
    }

    private sealed class WindowsPipeOpener : IPipeOpener
    {
        public Stream? TryOpen(string path, out int error)
        {
            // Do not request FILE_CREATE_PIPE_INSTANCE, which GenericWrite would include.
            // Null security attributes also keep the new handle non-inheritable.
            var handle = CreateFileW(path, 0x0012019b, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            error = Marshal.GetLastWin32Error();
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            try { return new NamedPipeClientStream(PipeDirection.InOut, false, true, handle); }
            catch { handle.Dispose(); throw; }
        }

        public bool WaitForAvailability(string path, uint milliseconds, out int error)
        {
            var available = WaitNamedPipeW(path, milliseconds);
            error = Marshal.GetLastWin32Error();
            return available;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateFileW(string name, uint access, uint share,
            IntPtr attributes, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipeW(string name, uint timeoutMilliseconds);
    }
}
