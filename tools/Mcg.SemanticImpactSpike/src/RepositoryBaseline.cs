using System.Diagnostics;

namespace Mcg.SemanticImpactSpike;

public static class RepositoryBaseline
{
    public static async Task<string[]> ReadAsync(string root)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "rev-parse", "HEAD", "HEAD:src", "HEAD:tests" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("git rev-parse did not exit before the hang deadline");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("git rev-parse failed: " + error);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    public static async Task<bool> MatchesAsync(string root, QueryData data)
    {
        var baseline = await ReadAsync(root);
        return baseline.Length == 3 && baseline[1] == data.SourceTree && baseline[2] == data.TestsTree;
    }
}
