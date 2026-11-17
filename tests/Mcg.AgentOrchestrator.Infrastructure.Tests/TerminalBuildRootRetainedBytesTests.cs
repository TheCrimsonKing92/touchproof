using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: files, junction targets and the fixture process are owned by each test instance.
public sealed class TerminalBuildRootRetainedBytesTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("retained-bytes-").FullName;

    [Fact]
    public void Measure_NestedAndEmptyDirectories_SumsLogicalFileLengths()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "nested", "empty"));
        File.WriteAllBytes(Path.Combine(_temp, "one.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(_temp, "nested", "two.bin"), new byte[23]);
        var size = TerminalBuildRootRetainedBytes.Measure(_temp);
        Assert.Equal(34, size.Bytes);
        Assert.True(size.Complete);
    }

    [Fact]
    public void Measure_MissingRoot_ReportsUnknownRatherThanZero()
    {
        var size = TerminalBuildRootRetainedBytes.Measure(Path.Combine(_temp, "absent"));
        Assert.Null(size.Bytes);
        Assert.False(size.Complete);
        Assert.Equal("unknown", size.Format());
    }

    [Fact]
    public void RetainedFigures_IncompleteRoots_MarkOnlyAffectedFieldsAsLowerBounds()
    {
        var retained = new TerminalBuildRootRetainedBytes();
        retained.Add(new(12, false), TerminalBuildRootRetainedBytes.Category.NotTerminal);
        retained.Add(new(7, true), TerminalBuildRootRetainedBytes.Category.Window);
        retained.Add(new(null, false), TerminalBuildRootRetainedBytes.Category.LiveOwner);
        Assert.Equal(19, retained.GoalRootBytes);
        Assert.Null(retained.OwnerBytes);
        Assert.Equal(2, retained.IncompleteRoots);
        Assert.Equal("size-budget-bytes=0 goal-root-bytes=>=19 retained-not-terminal-bytes=>=12 retained-window-bytes=7 retained-owner-bytes=unknown incomplete-roots=2",
            retained.FormatReceiptFields(0));

        retained.Add(new(5, true), TerminalBuildRootRetainedBytes.Category.RegisteredOwner);
        Assert.Equal(5, retained.OwnerBytes);
        Assert.Contains("retained-owner-bytes=>=5", retained.FormatReceiptFields(null));
    }

    [Fact]
    public void RetainedFigures_UnknownRoot_KeepsAggregateUnknown()
    {
        var retained = new TerminalBuildRootRetainedBytes();
        retained.Add(new(null, false), TerminalBuildRootRetainedBytes.Category.NotTerminal);
        Assert.Null(retained.GoalRootBytes);
        Assert.Null(retained.NotTerminalBytes);
        Assert.Equal("size-budget-bytes=none goal-root-bytes=unknown retained-not-terminal-bytes=unknown retained-window-bytes=0 retained-owner-bytes=0 incomplete-roots=1",
            retained.FormatReceiptFields(null));
    }

    [Fact]
    public void Measure_JunctionChildAndRoot_ExcludeTargetBytes()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "root")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_temp, "target")).FullName;
        File.WriteAllBytes(Path.Combine(root, "own.bin"), new byte[31]);
        File.WriteAllBytes(Path.Combine(target, "large.bin"), new byte[5_000_000]);
        var junction = Path.Combine(root, "outside");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "/d", "/c", "mklink", "/J", junction, target }
            }) ?? throw new InvalidOperationException("Could not start the directory-junction fixture process.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Directory-junction fixture did not exit.");
            }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0, $"Junction fixture failed: {output.Result} {error.Result}");
        }
        else Directory.CreateSymbolicLink(junction, target);
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        try
        {
            var size = TerminalBuildRootRetainedBytes.Measure(root);
            Assert.True(size.Complete);
            Assert.Equal(31, size.Bytes);
            Assert.Equal(0, TerminalBuildRootRetainedBytes.Measure(junction).Bytes);
            Assert.Equal(5_000_000, new FileInfo(Path.Combine(target, "large.bin")).Length);
        }
        finally { Directory.Delete(junction); }
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);
}
