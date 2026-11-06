using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerProcessRunnerShellRetentionTests
{
    [Fact]
    public async Task CommandString_BufferedRun_RetainsShellArgumentsAndExitCode()
    {
        var directory = Directory.CreateTempSubdirectory("shell-retention-");
        try
        {
            const string command = "[Environment]::GetCommandLineArgs() | ForEach-Object { [Console]::WriteLine($_) }; exit 3";
            var info = WorkerProcessRunner.BuildPowerShellStartInfo(command, directory.FullName);
            Assert.Equal(WorkerShell.Executable, info.FileName);
            Assert.Equal(WorkerShell.BaseArguments().Append(command), info.ArgumentList);
            var result = await WorkerProcessRunner.RunBufferedAsync(new(command, directory.FullName, TimeSpan.FromMinutes(1)),
                CancellationToken.None);
            Assert.Equal(3, result.ExitCode);
            Assert.Equal(WorkerShell.BaseArguments().Append(command),
                result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Skip(1));
        }
        finally { directory.Delete(recursive: true); }
    }
}
