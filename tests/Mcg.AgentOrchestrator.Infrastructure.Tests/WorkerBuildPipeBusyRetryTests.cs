using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: GUID pipe names, GUID temp roots, test-owned child processes.
public sealed class WorkerBuildPipeBusyRetryTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task BusyInstance_ReleasedAfterWait_ConnectsAndReportsInvocation()
    {
        await using var fixture = new PipeFixture();
        using var server = fixture.CreateServer();
        using var holder = await fixture.HoldInstanceAsync(server);
        fixture.Start(); // Exercise the production default without a timeout argument.
        await fixture.WaitForBusyAsync();

        holder.Dispose();
        server.Disconnect();
        await TestHangGuard.WaitAsync(server.WaitForConnectionAsync(), "retry client connection");
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true);
        var requestLine = await TestHangGuard.WaitAsync(reader.ReadLineAsync(), "build request line");
        Assert.NotNull(requestLine);
        using var request = JsonDocument.Parse(requestLine);
        Assert.Equal(1, request.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(PipeFixture.Project,
            Assert.Single(request.RootElement.GetProperty("Projects").EnumerateArray()).GetString());
        const string invocation = "busy-retry-test-invocation";
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            InvocationId = invocation, Outcome = "Success", Diagnostic = "test host response", ExitCode = 0
        }));
        await writer.FlushAsync();

        var result = await fixture.FinishAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"worker_build_invocation={invocation}; outcome=Success; test host response",
            Assert.Single(result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)));
        Assert.Single(fixture.Errors, line => line.StartsWith("worker_build_channel_wait ", StringComparison.Ordinal));
        Assert.DoesNotContain("worker_build_channel_busy", result.StandardError);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task BusyInstance_DeadlineExpires_ReportsBusyWithoutInvocation()
    {
        await using var fixture = new PipeFixture();
        using var server = fixture.CreateServer();
        using var holder = await fixture.HoldInstanceAsync(server);
        fixture.Start(busyTimeoutSeconds: 1);
        var result = await fixture.FinishAsync();

        Assert.Equal(1, result.ExitCode);
        var busy = Assert.Single(fixture.Errors,
            line => line.StartsWith("worker_build_channel_busy ", StringComparison.Ordinal));
        Assert.StartsWith($"worker_build_channel_busy pipe=\"\\\\.\\pipe\\{fixture.PipeName}\" waited_seconds=", busy);
        var reportedSeconds = double.Parse(busy.Split("waited_seconds=")[1], CultureInfo.InvariantCulture);
        Assert.True(reportedSeconds >= 1, $"Busy exhaustion reported before its budget: {busy}");
        Assert.DoesNotContain("worker_build_invocation=", result.StandardOutput);
        Assert.DoesNotContain(new Win32Exception(231).Message, result.StandardError);
        Assert.True(holder.IsConnected);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task MissingPipe_FailsWithOriginalErrorWithoutWaiting()
    {
        await using var fixture = new PipeFixture();
        fixture.Start();
        var result = await fixture.FinishAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(new Win32Exception(2).Message, result.StandardError);
        Assert.DoesNotContain("worker_build_channel_wait", result.StandardError);
        Assert.DoesNotContain("worker_build_channel_busy", result.StandardError);
        Assert.DoesNotContain("worker_build_invocation=", result.StandardOutput);
    }

    private sealed class PipeFixture : IAsyncDisposable
    {
        internal const string Project = "src/Feature/Feature.csproj";
        internal readonly string PipeName = "mcg-worker-build-" + Guid.NewGuid().ToString("N");
        internal readonly ConcurrentQueue<string> Errors = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-pipe-retry-" + Guid.NewGuid().ToString("N"));
        private readonly TaskCompletionSource _busy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Process? _process;
        private Task<string>? _output;
        private Task? _error;
        private Task<CliChildProcessResult>? _completion;

        internal NamedPipeServerStream CreateServer() =>
            new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        internal async Task<NamedPipeClientStream> HoldInstanceAsync(NamedPipeServerStream server)
        {
            var holder = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var guard = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
                var accept = server.WaitForConnectionAsync(guard.Token);
                await TestHangGuard.WaitAsync(holder.ConnectAsync(guard.Token), "holder client connection");
                await TestHangGuard.WaitAsync(accept, "holder accepted by single-instance server");
                Assert.True(server.IsConnected);
                Assert.True(holder.IsConnected);
                return holder;
            }
            catch { holder.Dispose(); throw; }
        }

        internal void Start(int? busyTimeoutSeconds = null)
        {
            var context = Directory.CreateDirectory(Path.Combine(_root, ".orchestrator-context"));
            File.WriteAllText(Path.Combine(context.FullName, "worker-build-channel.json"),
                JsonSerializer.Serialize(new { version = 1, pipe = PipeName }));
            var repository = VerifiedRepositoryRoot.Find();
            var start = new ProcessStartInfo(WorkerShell.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(start.Environment, repository);
            start.Environment["TEMP"] = _root;
            start.Environment["TMP"] = _root;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
                Path.Combine(repository, "scripts", "Request-HostWorkerBuild.ps1"), "-Projects", Project })
                start.ArgumentList.Add(argument);
            if (busyTimeoutSeconds is { } seconds)
            {
                start.ArgumentList.Add("-BusyTimeoutSeconds");
                start.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));
            }
            _process = Process.Start(start) ?? throw new InvalidOperationException("Pipe client process did not start.");
            _process.StandardInput.Close();
            _output = _process.StandardOutput.ReadToEndAsync();
            _error = DrainErrorsAsync(_process.StandardError);
            _completion = CollectAsync();
        }

        private async Task DrainErrorsAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                Errors.Enqueue(line);
                if (line.StartsWith("worker_build_channel_wait ", StringComparison.Ordinal)) _busy.TrySetResult();
            }
        }

        private async Task<CliChildProcessResult> CollectAsync()
        {
            await _process!.WaitForExitAsync();
            var output = await _output!;
            await _error!;
            return new(_process.ExitCode, output, string.Join(Environment.NewLine, Errors));
        }

        internal async Task WaitForBusyAsync()
        {
            await TestHangGuard.WaitAsync(Task.WhenAny(_busy.Task, _completion!), "busy observation or client exit");
            Assert.True(_busy.Task.IsCompletedSuccessfully,
                "Client exited before observing the held pipe: " + string.Join(Environment.NewLine, Errors));
        }

        internal Task<CliChildProcessResult> FinishAsync() =>
            TestHangGuard.WaitAsync(_completion!, "pipe client terminal output and exit");

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_process is not null)
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await TestHangGuard.WaitAsync(_completion!, "pipe client cleanup and output drain");
                }
            }
            finally
            {
                _process?.Dispose();
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
        }
    }
}
