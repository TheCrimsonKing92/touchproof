using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerRepositoryDiffDeliveryTests
{
    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void TypedPackage_SupplementalDiffs_AreMandatoryFilesWithCompleteBytes(AgentRole role)
    {
        var root = Path.Combine(Path.GetTempPath(), "repository-diff-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var contents = new Dictionary<string, byte[]>
            {
                ["diff.crosswalk.patch"] = Encoding.UTF8.GetBytes("+candidate-patch-marker\n" + new string('x', 41000)),
                ["diff-summary.crosswalk.md"] = Encoding.UTF8.GetBytes("secondary-summary-marker"),
                ["future-context.md"] = Encoding.UTF8.GetBytes("unknown-artifact-marker")
            };
            foreach (var (name, bytes) in contents) File.WriteAllBytes(Path.Combine(root, name), bytes);
            File.WriteAllText(Path.Combine(root, "artifact-registry.json"), JsonSerializer.Serialize(new
            {
                artifacts = contents.Select(pair => new
                {
                    path = pair.Key, exists = true, hashVerified = true,
                    sha256 = WorkerContextArtifact.Hash(pair.Value), roleVisibility = new[] { role.ToString() }
                })
            }));
            var task = new TaskSpec(TaskId.New(), "Inspect repository evidence", role);
            var goal = new AgentOrchestratorKernel().CreateGoal("Deliver repository evidence", [task]);
            var brief = new TaskBrief(goal.Id, task.Id, role, task.Description, "## Instructions\nInspect the supplied evidence.");

            var package = WorkerProfileDispatcher.BuildContextPackage(goal, task, root, root, brief);
            var rendered = WorkerContextPackageBuilder.Render(package);

            foreach (var name in new[] { "diff.crosswalk.patch", "diff-summary.crosswalk.md" })
            {
                var artifact = Assert.Single(package.Artifacts, item => item.Identity.Value == "context/" + name);
                Assert.Equal(ContextDeliveryMode.MandatoryFile, artifact.DeliveryMode);
                Assert.Equal(contents[name], File.ReadAllBytes(Path.Combine(root, artifact.MandatoryRelativePath!)));
                Assert.Contains("MANDATORY READ: identity=context/" + name, rendered);
                Assert.DoesNotContain(Encoding.UTF8.GetString(contents[name]), rendered);
            }
            Assert.Equal(ContextDeliveryMode.InlineFull,
                Assert.Single(package.Artifacts, item => item.Identity.Value == "context/future-context.md").DeliveryMode);
            Assert.Contains("unknown-artifact-marker", rendered);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
