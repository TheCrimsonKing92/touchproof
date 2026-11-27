using System.Text.Json;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterResolverCorpus;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterResolverReplayCommand
{
    internal const string Name = "pretester-resolver-replay";

    internal static int Run(string[] args, TextWriter error)
    {
        if (args.Length != 6)
        {
            error.WriteLine($"Usage: {Name} <corpus> <expected-sha256> <new-receipt> <candidate-build.json> <oracle-build.json>");
            return 2;
        }

        try
        {
            return Replay(args[1], args[2], args[3], ReadBuild(args[4]), ReadBuild(args[5])) ? 0 : 1;
        }
        catch (Exception failure)
        {
            error.WriteLine($"{Name} failed: {failure.GetBaseException().Message}");
            return 2;
        }
    }

    private static ReplayBuild ReadBuild(string path) =>
        JsonSerializer.Deserialize<ReplayBuild>(File.ReadAllBytes(path))
        ?? throw new InvalidDataException($"Missing replay identity in {path}.");
}
