using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DispatchHostStartInfoFactory
{
    public static ProcessStartInfo Create(string workingDirectory, string assemblyPath, string parametersPath,
        string startGatePath, ConductorChildEnvironment environment)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        environment.Apply(startInfo);
        if (OperatingSystem.IsWindows()) startInfo.CreateNewProcessGroup = true;
        startInfo.Environment[DispatchProcessHost.StartGatePathVariable] = startGatePath;
        foreach (var argument in new[] { "exec", assemblyPath, DispatchProcessHost.SubcommandName, parametersPath })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
