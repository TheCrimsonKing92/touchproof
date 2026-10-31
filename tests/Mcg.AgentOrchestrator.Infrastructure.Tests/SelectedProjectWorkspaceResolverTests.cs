using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each case owns its registry, data and repository directories.
public sealed class SelectedProjectWorkspaceResolverTests
{
    [Theory]
    [InlineData(ProjectDelivery.PullRequest, "default")]
    [InlineData(ProjectDelivery.Direct, "review")]
    public void Registered_delivery_reaches_startup_workspace_with_branch_and_tenant(string delivery, string tenant)
    {
        var root = Directory.CreateTempSubdirectory("selected-project-").FullName;
        try
        {
            var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), Path.Combine(root, "data"));
            var repository = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            registry.CreateProject("sample", repository, "trunk");
            registry.SetDeliveryAnswer("sample", new OwnerAnswer("delivery", delivery, "review policy", DateTimeOffset.UnixEpoch));
            registry.SelectProject("sample");
            var reopened = new OrchestratorProjectRegistry(registry.RegistryDirectory, registry.DataRootDirectory);
            var project = reopened.ResolveActiveProject(repository, null);
            Assert.Equal(delivery, project.Delivery);

            var workspace = SelectedProjectWorkspaceResolver.Resolve(project, Path.Combine(root, "other-execution"), tenant, reopened);

            Assert.Equal(delivery, workspace.Delivery);
            Assert.Equal("sample", workspace.ProjectName);
            Assert.True(workspace.IsProjectScoped);
            Assert.Equal(repository, workspace.RootDirectory);
            Assert.Equal(repository, workspace.ExecutionDirectory);
            Assert.Equal("trunk", workspace.IntegrationBranch);
            Assert.Equal(tenant, workspace.TenantName);
            Assert.StartsWith(registry.DataRootDirectory, workspace.OrchestratorDirectory);
            Assert.Equal(delivery == ProjectDelivery.PullRequest, PullRequestLandingDelivery.Applies(workspace));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Home_selection_keeps_direct_delivery_and_explicit_execution_directory()
    {
        var root = Directory.CreateTempSubdirectory("selected-home-").FullName;
        try
        {
            var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), Path.Combine(root, "data"));
            var execution = Path.Combine(root, "execution");
            var home = new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, root) { Delivery = ProjectDelivery.PullRequest };

            var workspace = SelectedProjectWorkspaceResolver.Resolve(home, execution, "review", registry);

            Assert.Equal(ProjectDelivery.Direct, workspace.Delivery);
            Assert.False(workspace.IsProjectScoped);
            Assert.Equal(execution, workspace.ExecutionDirectory);
            Assert.Equal("review", workspace.TenantName);
            Assert.False(PullRequestLandingDelivery.Applies(workspace));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
