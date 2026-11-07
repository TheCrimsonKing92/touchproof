using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Parallel-safe: read-only repository access and uniquely owned temporary directories.
public sealed class OwnerHomePathLiteralGuardTests
{
    [Fact]
    public async Task TrackedFiles_CandidateTree_ContainsNoOwnerHomeRoot()
    {
        var root = VerifiedRepositoryRoot.Find();
        var paths = await ListTrackedFiles(root);
        var offenders = Scan(root, paths);
        Assert.True(offenders.Count == 0,
            $"Owner home-path literals found in tracked files:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("\\", false)]
    [InlineData("\\\\", false)]
    [InlineData("/", false)]
    [InlineData("\\\\\\\\", false)]
    [InlineData("//", false)]
    [InlineData("\\", true)]
    public void Scanner_RuntimeAssembledOwnerRoot_ReportsFile(string separator, bool upperCase)
    {
        var root = Directory.CreateTempSubdirectory("owner-path-guard-").FullName;
        try
        {
            var literal = string.Join(separator, "C:", UsersFolder(), OwnerAccount());
            if (upperCase) literal = literal.ToUpperInvariant();
            File.WriteAllText(Path.Combine(root, "offender.txt"), literal + separator + "project");
            File.WriteAllText(Path.Combine(root, "root-only.txt"), literal);
            Assert.Equal(new[] { "offender.txt", "root-only.txt" },
                Scan(root, new[] { "offender.txt", "root-only.txt" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scanner_CleanFile_ReportsNothing()
    {
        var root = Directory.CreateTempSubdirectory("owner-path-guard-").FullName;
        try
        {
            var nearMiss = string.Join("\\", "C:", UsersFolder(), OwnerAccount() + "tone", "project");
            File.WriteAllText(Path.Combine(root, "clean.txt"), @"C:\example\repo\project" + "\n" + nearMiss);
            Assert.Empty(Scan(root, new[] { "clean.txt" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scanner_MissingTrackedFile_FailsWithPath()
    {
        var root = Directory.CreateTempSubdirectory("owner-path-guard-").FullName;
        try
        {
            var exception = Assert.Throws<IOException>(() => Scan(root, new[] { "missing.txt" }));
            Assert.Contains("missing.txt", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string UsersFolder() => string.Concat("Us", "ers");
    private static string OwnerAccount() => string.Concat("mi", "les");

    private static IReadOnlyList<string> Scan(string root, IEnumerable<string> relativePaths)
    {
        var separator = @"[\\/]+";
        var pattern = new Regex(string.Concat("C:", separator, Regex.Escape(UsersFolder()),
            separator, Regex.Escape(OwnerAccount()), @"(?![\p{L}\p{N}_.-])"),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var offenders = new List<string>();
        foreach (var path in relativePaths)
        {
            string content;
            try
            {
                // Latin-1 preserves ASCII bytes in UTF-8 and binary files. UTF-16 and
                // Unicode-escaped separators are outside this literal guard's scope.
                content = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(root, path)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not inspect tracked file '{path}': {exception.Message}", exception);
            }
            if (pattern.IsMatch(content)) offenders.Add(path);
        }
        return offenders;
    }

    private static async Task<string[]> ListTrackedFiles(string root)
    {
        // Capture the entire NUL-delimited list; RunGitProbe truncates stdout at 4 KiB.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "git.exe" : "git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "ls-files", "-z" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var invocation = $"git ls-files -z at '{root}'";
        try
        {
            Assert.True(process.Start(), $"Could not start {invocation}");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Assert.Fail($"Could not start {invocation}: {exception.Message}");
        }
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var hangDetector = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(hangDetector.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"Timed out waiting for tracked-file enumeration to exit: {invocation}");
        }
        await Task.WhenAll(output, error);
        Assert.True(process.ExitCode == 0, $"{invocation} exited {process.ExitCode}: {await error}");
        var records = await output;
        Assert.True(records.EndsWith('\0'), $"{invocation} returned no complete NUL-delimited file list.");
        var paths = records.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(paths);
        return paths;
    }
}
