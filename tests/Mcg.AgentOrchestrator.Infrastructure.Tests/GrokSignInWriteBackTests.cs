using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using FileOperations = Mcg.AgentOrchestrator.Infrastructure.GrokSignInWriteBack.GrokSignInFileOperations;

// Parallel-safe: every test owns its temporary directories; no processes or global state.
public sealed class GrokSignInWriteBackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mcg-grok-writeback", Guid.NewGuid().ToString("N"));
    private readonly string sandboxHome;
    private readonly string hostHome;
    private readonly byte[] originalHostBytes;
    private readonly DateTime originalHostWriteTime;
    private readonly List<string> diagnostics = [];

    private string SandboxPath => Path.Combine(sandboxHome, "auth.json");
    private string HostPath => Path.Combine(hostHome, "auth.json");

    public GrokSignInWriteBackTests()
    {
        sandboxHome = Path.Combine(root, "sandbox");
        hostHome = Path.Combine(root, "host");
        Directory.CreateDirectory(sandboxHome);
        Directory.CreateDirectory(hostHome);
        originalHostBytes = Encoding.UTF8.GetBytes("{\"expires_at\":1700000000, \"token\":\"original-fixture\"}\r\n");
        File.WriteAllBytes(HostPath, originalHostBytes);
        File.SetLastWriteTimeUtc(HostPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        originalHostWriteTime = File.GetLastWriteTimeUtc(HostPath);
    }

    [Theory]
    [InlineData("{\"expires_at\":\"2023-11-15T01:13:20+02:00\"}")]
    [InlineData("{\"expires_at\":\"2023-11-14T23:13:20Z\"}")]
    [InlineData("{\"expiry\":1700003600}")]
    [InlineData("{\"expires\":\"1700003600\"}")]
    [InlineData("{\"expiration\":1700003600000}")]
    [InlineData("{\"expiration_time\":\"1700003600000\"}")]
    [InlineData("{\"credentials\":{\"EXPIRES-AT\":1700003600}}")]
    [InlineData("{\"expires_at\":1700003600,\"other\":{\"retained\":true}}\r\n")]
    [InlineData("\uFEFF{\"expires_at\":1700003600}\n")]
    public void TryWriteBack_NewerExpiry_ReplacesWithExactSandboxBytes(string json)
    {
        var sandboxBytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(SandboxPath, sandboxBytes);

        WriteBack();

        Assert.Equal(sandboxBytes, File.ReadAllBytes(HostPath));
        Assert.Equal(sandboxBytes, File.ReadAllBytes(SandboxPath));
        Assert.Equal(HostPath, Assert.Single(Directory.GetFiles(hostHome)));
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"expires_in\":3600}")]
    [InlineData("{\"expires_at\":1700000000}")]
    [InlineData("{\"expires_at\":1699999999}")]
    [InlineData("{\"expires_at\":1700000000000}")]
    [InlineData("{\"expires_at\":\"2023-11-15T00:13:20+02:00\"}")]
    [InlineData("{\"expires_at\":\"2023-11-15T01:13:20\"}")]
    [InlineData("{\"expires_at\":\"unknown\"}")]
    [InlineData("{\"expires_at\":1700003600.5}")]
    [InlineData("{\"expires_at\":0}")]
    [InlineData("{\"expires_at\":-1700003600}")]
    [InlineData("{\"expires_at\":9223372036854775807}")]
    [InlineData("{\"expires_at\":null}")]
    [InlineData("{\"expires_at\":true}")]
    [InlineData("{\"expires_at\":{},\"nested\":{\"expires_at\":1700003600}}")]
    public void TryWriteBack_UnknownOrNotNewerSandbox_LeavesHostUnchanged(string? json)
    {
        if (json is not null)
            File.WriteAllText(SandboxPath, json);

        WriteBack();

        AssertHostUnchanged();
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed")]
    [InlineData("{}")]
    [InlineData("{\"expires_at\":\"unknown\"}")]
    public void TryWriteBack_UnknownHostExpiry_DoesNotRepairHost(string? hostJson)
    {
        if (hostJson is null)
            File.Delete(HostPath);
        else
            File.WriteAllText(HostPath, hostJson);
        var before = hostJson is null ? null : File.ReadAllBytes(HostPath);
        var timestamp = File.GetLastWriteTimeUtc(HostPath);
        File.WriteAllText(SandboxPath, "{\"expires_at\":1700003600}");

        WriteBack();

        if (before is null)
        {
            Assert.False(File.Exists(HostPath));
            Assert.Empty(Directory.GetFiles(hostHome));
        }
        else
        {
            Assert.Equal(before, File.ReadAllBytes(HostPath));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(HostPath));
            Assert.Equal(HostPath, Assert.Single(Directory.GetFiles(hostHome)));
        }
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TryWriteBack_WriteFailure_PreservesHostAndContainsDiagnostics(
        bool failReplace, bool diagnosticThrows)
    {
        File.WriteAllText(SandboxPath, "{\"expires_at\":1700003600}");
        var failureReached = false;
        var operations = new FileOperations
        {
            WriteStaged = (path, bytes) =>
            {
                if (failReplace)
                {
                    FileOperations.Default.WriteStaged(path, bytes);
                    return;
                }
                File.WriteAllBytes(path, [1, 2, 3]);
                failureReached = true;
                throw new IOException("synthetic-credential-must-not-be-logged");
            },
            Replace = (staged, _) =>
            {
                Assert.Equal(File.ReadAllBytes(SandboxPath), File.ReadAllBytes(staged));
                failureReached = true;
                throw new IOException("synthetic-credential-must-not-be-logged");
            }
        };

        var exception = Record.Exception(() => GrokSignInWriteBack.TryWriteBack(
            () => sandboxHome, () => hostHome, line =>
            {
                diagnostics.Add(line);
                if (diagnosticThrows)
                    throw new InvalidOperationException("diagnostic sink failed");
            }, operations));

        Assert.Null(exception);
        Assert.True(failureReached);
        AssertHostUnchanged();
        Assert.Equal("[dispatch-host] grok sign-in write-back skipped: IOException", Assert.Single(diagnostics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryWriteBack_ReadFailure_LeavesHostIntact(bool failHostRead)
    {
        File.WriteAllText(SandboxPath, "{\"expires_at\":1700003600}");
        var readReached = false;
        var operations = new FileOperations
        {
            ReadSnapshot = path =>
            {
                if (path == (failHostRead ? HostPath : SandboxPath))
                {
                    readReached = true;
                    throw new UnauthorizedAccessException();
                }
                return FileOperations.Default.ReadSnapshot(path);
            }
        };

        var exception = Record.Exception(() => WriteBack(operations));

        Assert.Null(exception);
        Assert.True(readReached);
        AssertHostUnchanged();
        Assert.Contains("UnauthorizedAccessException", Assert.Single(diagnostics));
    }

    [Fact]
    public void TryWriteBack_PathResolutionFailure_DoesNotEscape()
    {
        var exception = Record.Exception(() => GrokSignInWriteBack.TryWriteBack(
            () => throw new InvalidOperationException("invalid sandbox"), () => hostHome, diagnostics.Add));

        Assert.Null(exception);
        AssertHostUnchanged();
        Assert.Contains("InvalidOperationException", Assert.Single(diagnostics));
    }

    [Fact]
    public void TryWriteBack_SandboxChangesAfterRead_WritesComparedSnapshot()
    {
        var comparedBytes = Encoding.UTF8.GetBytes("{\"expires_at\":1700003600,\"token\":\"compared\"}");
        File.WriteAllBytes(SandboxPath, comparedBytes);
        var reads = new List<string>();
        var operations = new FileOperations
        {
            ReadSnapshot = path =>
            {
                reads.Add(path);
                return FileOperations.Default.ReadSnapshot(path);
            },
            WriteStaged = (path, bytes) =>
            {
                File.WriteAllText(SandboxPath, "{\"expires_at\":1699999999}");
                FileOperations.Default.WriteStaged(path, bytes);
            }
        };

        WriteBack(operations);

        Assert.Equal(new[] { SandboxPath, HostPath }, reads);
        Assert.Equal(comparedBytes, File.ReadAllBytes(HostPath));
        Assert.Equal(HostPath, Assert.Single(Directory.GetFiles(hostHome)));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TryWriteBack_OversizedSandbox_LeavesHostUnchanged()
    {
        File.WriteAllText(SandboxPath, "{\"expires_at\":1700003600,\"padding\":\"" + new string('x', 1024 * 1024) + "\"}");

        WriteBack();

        AssertHostUnchanged();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TryWriteBack_CleanupFailure_DoesNotEscape()
    {
        var sandboxBytes = Encoding.UTF8.GetBytes("{\"expires_at\":1700003600}");
        File.WriteAllBytes(SandboxPath, sandboxBytes);
        var cleanupReached = false;
        var exception = Record.Exception(() => WriteBack(new FileOperations
        {
            DeleteIfExists = _ =>
            {
                cleanupReached = true;
                throw new IOException("cleanup failed");
            }
        }));

        Assert.Null(exception);
        Assert.True(cleanupReached);
        Assert.Equal(sandboxBytes, File.ReadAllBytes(HostPath));
        Assert.Equal(HostPath, Assert.Single(Directory.GetFiles(hostHome)));
    }

    private void WriteBack(FileOperations? operations = null) =>
        GrokSignInWriteBack.TryWriteBack(() => sandboxHome, () => hostHome, diagnostics.Add, operations);

    private void AssertHostUnchanged()
    {
        Assert.Equal(originalHostBytes, File.ReadAllBytes(HostPath));
        Assert.Equal(originalHostWriteTime, File.GetLastWriteTimeUtc(HostPath));
        Assert.Equal(HostPath, Assert.Single(Directory.GetFiles(hostHome)));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
