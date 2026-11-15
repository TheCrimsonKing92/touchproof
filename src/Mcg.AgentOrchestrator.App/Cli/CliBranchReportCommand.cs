using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBranchReportCommand
{
    internal const string Usage = "Usage: branch-report [--json]";

    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("branch-report", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Run(args, workspace.ExecutionDirectory, workspace.IntegrationBranch,
            () => File.Exists(workspace.SqliteStatePath)
                ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .LoadAsync().GetAwaiter().GetResult().Goals
                : [], Console.Out, Console.Error);

    internal static int Run(IReadOnlyList<string> args, string repositoryDirectory,
        string integrationBranch, Func<IReadOnlyCollection<Goal>> loadGoals,
        TextWriter output, TextWriter error,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        if (!IsCommand(args) || args.Count > 2 ||
            (args.Count == 2 && !args[1].Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            error.WriteLine(Usage);
            return 1;
        }
        try
        {
            var inventory = BranchWorktreeInventory.Read(repositoryDirectory, integrationBranch, loadGoals(), gitRunner);
            var branchCounts = BranchWorktreeInventory.BranchClasses.ToDictionary(
                kind => kind, kind => inventory.Branches.Count(branch => branch.Class == kind));
            var worktreeCounts = BranchWorktreeInventory.WorktreeClasses.ToDictionary(
                kind => kind, kind => inventory.Worktrees.Count(tree => tree.Class == kind));
            string report;
            if (args.Count == 2)
            {
                report = JsonSerializer.Serialize(new
                {
                    Branches = inventory.Branches.Select(branch => new
                    {
                        branch.Name, branch.Class, branch.Owner, branch.CommitsNotOnMain
                    }),
                    Worktrees = inventory.Worktrees.Select(tree => new
                    {
                        tree.Path, tree.Class, tree.DirectoryExists, tree.GoalId, tree.GoalTerminal, tree.Branch
                    }),
                    BranchClassCounts = branchCounts,
                    WorktreeClassCounts = worktreeCounts
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            }
            else
            {
                var text = new StringBuilder();
                text.AppendLine($"Branches ({inventory.Branches.Count})");
                foreach (var (kind, count) in branchCounts)
                {
                    text.AppendLine($"  {kind} ({count})");
                    foreach (var branch in inventory.Branches.Where(branch => branch.Class == kind))
                        text.AppendLine($"    {branch.Name} | owner={branch.Owner ?? "(none)"} | commits_not_on_main={branch.CommitsNotOnMain}");
                }
                text.AppendLine($"Worktrees ({inventory.Worktrees.Count})");
                foreach (var (kind, count) in worktreeCounts)
                {
                    text.AppendLine($"  {kind} ({count})");
                    foreach (var tree in inventory.Worktrees.Where(tree => tree.Class == kind))
                        text.AppendLine($"    {tree.Path} | directory={(tree.DirectoryExists ? "present" : "missing")} | branch={tree.Branch ?? "detached"}" +
                            (tree.Class == "goal" ? $" | goal={tree.GoalId} | terminal={tree.GoalTerminal?.ToString().ToLowerInvariant() ?? "unknown"}" : ""));
                }
                report = text.ToString().TrimEnd('\r', '\n');
            }
            output.WriteLine(report);
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
