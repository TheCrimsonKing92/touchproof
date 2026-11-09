using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core.Conductor;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: private registry/policy directories and uninitialized GUI instances; no processes or global state.
public sealed class OwnerConsoleProjectsSettingsTabTests
{
    [Fact]
    public void Refresh_RegisteredProjects_ShowsScopeBranchesAndPushStates()
    {
        using var context = new Projects();
        context.Register("beta", "develop", false);
        context.Register("alpha", "release", true);
        context.Register("gamma", "main");
        var tab = context.Tab();

        Assert.Equal("Projects", tab.Title);
        Assert.Equal(new[] { "scope: all projects", "alpha: branch release  push on",
            "beta: branch develop  push off", "gamma: branch main  push unreadable" }, tab.Lines);
        Assert.False(Directory.Exists(context.Registry.GetRequiredProject("gamma")
            .ResolveWorkspace().OrchestratorDirectory));
    }

    [Theory]
    [InlineData("malformed", "unreadable")]
    [InlineData("invalid", "unreadable")]
    [InlineData("omitted", "off")]
    [InlineData("directory", "unreadable")]
    public void Refresh_UnavailableOrDefaultPolicy_KeepsOtherProjects(string policy, string state)
    {
        using var context = new Projects();
        var alpha = context.Register("alpha", "main");
        context.Register("beta", "develop", true);
        var path = Projects.PolicyPath(alpha);
        Directory.CreateDirectory(alpha.OrchestratorDirectory);
        if (policy == "directory") Directory.CreateDirectory(path);
        else if (policy == "malformed") File.WriteAllText(path, "{");
        else
        {
            var document = JsonNode.Parse(ConductorAutonomyPolicy.Conservative.ToJson())!.AsObject();
            if (policy == "invalid") document["adoptedBuildPushEnabled"] = "yes";
            else Assert.True(document.Remove("adoptedBuildPushEnabled"));
            File.WriteAllText(path, document.ToJsonString());
        }

        Assert.Equal(new[] { "scope: all projects", $"alpha: branch main  push {state}",
            "beta: branch develop  push on" }, context.Tab().Lines);
    }

    [Fact]
    public void Refresh_ChangedRegistryAndPolicy_RebuildsRows()
    {
        using var context = new Projects();
        var tab = context.Tab();
        Assert.Equal(new[] { "scope: all projects" }, tab.Lines);
        var alpha = context.Register("alpha", "release", true);
        tab.Refresh();
        Assert.Equal("alpha: branch release  push on", tab.Lines[1]);
        Projects.WritePolicy(alpha, false);
        context.Register("beta", "develop", true);
        tab.Refresh();
        Assert.Equal(new[] { "scope: all projects", "alpha: branch release  push off",
            "beta: branch develop  push on" }, tab.Lines);
        File.Delete(Projects.PolicyPath(alpha));
        Assert.Equal("alpha: branch release  push unreadable", tab.Lines[1]);
    }

    [Fact]
    public void Refresh_UnreadableRegistry_ShowsDiagnosticAndClampsSelection()
    {
        using var context = new Projects();
        context.Register("alpha", "main", false);
        context.Register("beta", "develop", true);
        var tab = context.Tab();
        tab.HandleKey(Key.End);
        Assert.Equal(2, tab.SelectedLine);
        File.WriteAllText(context.Registry.RegistryPath, "{");
        tab.Refresh();
        Assert.Equal("scope: all projects", tab.Lines[0]);
        Assert.StartsWith("registry: unreadable:", tab.Lines[1]);
        Assert.Equal(2, tab.Lines.Count);
        Assert.Equal(1, tab.SelectedLine);
        File.Delete(context.Registry.RegistryPath);
        tab.Refresh();
        Assert.Single(tab.Lines);
        Assert.Equal(0, tab.SelectedLine);
    }

    [Theory]
    [InlineData(" beta, ALPHA,beta ", "scope: alpha, beta", "alpha,beta")]
    [InlineData("all", "scope: all projects", "alpha,beta,gamma,home")]
    [InlineData("", "scope: gamma", "gamma")]
    public void ScopeEdit_SubmittedText_DelegatesUnchangedAndUpdatesBoard(
        string input, string message, string names)
    {
        using var context = new Projects();
        context.Register("alpha", "main");
        context.Register("beta", "develop");
        context.Register("gamma", "main");
        var source = context.Source();
        Assert.True(source.HandleScope("gamma").Success);
        var submissions = new List<string>();
        var tab = new OwnerConsoleProjectsSettingsTab(context.Registry, argument =>
        {
            if (argument is not null) submissions.Add(argument);
            return source.HandleScope(argument);
        });
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Edit(view, input);

        Assert.Equal(new[] { input }, submissions);
        Assert.False(tab.Editing);
        Assert.Equal(message, tab.StatusText);
        Assert.Equal(message, tab.Lines[0]);
        Assert.Equal(names.Split(','), source.InScopeProjects().Select(project => project.Name)
            .OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ScopeEdit_UnknownName_PreservesScopeAndShowsHandlerRejection()
    {
        using var context = new Projects();
        context.Register("alpha", "main");
        var source = context.Source();
        source.HandleScope("alpha");
        var expected = source.HandleScope("nope");
        Assert.False(expected.Success);
        var tab = context.Tab(source);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Edit(view, "nope");

        Assert.True(tab.Editing);
        Assert.Contains("scope: unknown project 'nope'; known projects:", tab.StatusText);
        Assert.Equal(expected.Message, tab.Message);
        Assert.Equal("scope: alpha", tab.Lines[0]);
        Assert.Equal("alpha", Assert.Single(source.InScopeProjects()).Name);
        Assert.False(view.HandleKey(Key.Esc));
        Assert.False(tab.Editing);
        Assert.True(view.IsOpen);
        Assert.Equal("Edit cancelled.", tab.StatusText);
        Assert.Equal("scope: alpha", tab.Lines[0]);
    }

    [Fact]
    public void ScopeEdit_RefreshAndBackspace_PreservesPendingInput()
    {
        using var context = new Projects();
        var alpha = context.Register("alpha", "main", true);
        context.Register("beta", "develop");
        var source = context.Source();
        var tab = context.Tab(source);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        view.HandleKey(Key.Enter);
        Type(view, "betax");
        view.HandleKey(Key.Backspace);
        source.HandleScope("alpha");
        Projects.WritePolicy(alpha, false);
        view.Refresh();

        Assert.True(tab.Editing);
        Assert.StartsWith("scope: beta_", tab.StatusText);
        Assert.Equal("scope: alpha", tab.Lines[0]);
        Assert.Equal("alpha: branch main  push off", tab.Lines[1]);
        view.HandleKey(Key.Enter);
        Assert.False(tab.Editing);
        Assert.Equal("scope: beta", tab.StatusText);
        Assert.Equal("beta", Assert.Single(source.InScopeProjects()).Name);

        view.HandleKey(Key.Enter);
        Type(view, "q");
        Assert.True(view.IsOpen);
        view.HandleKey(Key.Esc);
        Assert.Equal("scope: beta", tab.Lines[0]);
        Assert.Equal("beta", Assert.Single(source.InScopeProjects()).Name);
    }

    [Fact]
    public void ScopeEdit_LongValidInput_ReachesHandlerWithoutTruncation()
    {
        using var context = new Projects();
        context.Register("alpha", "main");
        var input = new string(' ', 1100) + "alpha";
        var source = context.Source();
        string? submitted = null;
        var tab = new OwnerConsoleProjectsSettingsTab(context.Registry, argument =>
        {
            if (argument is not null) submitted = argument;
            return source.HandleScope(argument);
        });
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Edit(view, input);
        Assert.Equal(input, submitted);
        Assert.Equal("scope: alpha", tab.StatusText);
        Assert.Equal("alpha", Assert.Single(source.InScopeProjects()).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectRows_AllKeys_LeaveRegistryAndPoliciesByteIdentical(bool throughView)
    {
        using var context = new Projects();
        context.Register("alpha", "main", true);
        context.Register("beta", "develop", false);
        context.Register("gamma", "release");
        var tab = context.Tab();
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        var entries = Directory.GetFileSystemEntries(context.Root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var files = Directory.GetFiles(context.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
        Assert.Equal(3, files.Count); // Registry and both existing policies are observed.
        Key[] keys = [Key.Enter, Key.Esc, Key.CursorUp, Key.CursorDown, Key.CursorLeft, Key.CursorRight,
            Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Backspace, Key.Delete, new Key(' '),
            .. "abcdefghijklmnopqrstuvwxyz0123456789".Select(character => new Key(character))];
        void Send(Key key) { if (throughView) view.HandleKey(key); else tab.HandleKey(key); }

        for (var row = 1; row <= 3; row++)
        foreach (var key in keys)
        {
            view.Open(OwnerConsolePane.Board);
            Send(Key.Home);
            for (var index = 0; index < row; index++) Send(Key.CursorDown);
            Assert.Equal(row, tab.SelectedLine);
            Send(key);
            Assert.False(tab.Editing);
        }

        Assert.Equal(entries, Directory.GetFileSystemEntries(context.Root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal));
        foreach (var (path, before) in files)
        {
            Assert.Equal(before.Bytes, File.ReadAllBytes(path));
            Assert.Equal(before.Written, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData("registry")]
    [InlineData("empty-registry")]
    [InlineData("direct")]
    [InlineData("none")]
    public void FullScreen_SessionRegistry_AppendsProjectsAfterExistingTabs(string kind)
    {
        using var context = new Projects();
        using var settings = new OwnerConsoleSettingsTestContext();
        if (kind == "registry") context.Register("alpha", "release", true);
        var source = kind == "none" ? null : kind == "direct"
            ? new OwnerConsoleBoardSource("home", OwnerConsoleProjectScope.All, () => [], settings.Clock)
            : context.Source();
        using IApplication app = Application.Create();
        Assert.False(app.Initialized);
        using var view = new OwnerConsoleFullScreenView(app, settings.Controller,
            () => Task.CompletedTask, boardSource: source);
        var modal = view.SettingsView;
        modal.Open(OwnerConsolePane.Board);
        Assert.StartsWith("calm:", modal.Lines[0]);
        modal.HandleKey(Key.CursorLeft); // Wrap to the last registered tab.
        if (kind is "registry" or "empty-registry")
        {
            Assert.Equal(1, modal.ActiveTab);
            Assert.Equal("scope: all projects", modal.Lines[0]);
            Assert.Equal(kind == "registry" ? 2 : 1, modal.Lines.Count);
            modal.HandleKey(Key.Enter);
            Type(modal, kind == "registry" ? "alpha" : "all");
            modal.HandleKey(Key.Enter);
            Assert.Equal(kind == "registry" ? "scope: alpha" : "scope: all projects", source!.HandleScope(null).Message);
            modal.HandleKey(Key.CursorRight);
        }
        Assert.Equal(0, modal.ActiveTab);
        Assert.StartsWith("calm:", modal.Lines[0]);
    }

    private static void Edit(OwnerConsoleSettingsView view, string input)
    {
        view.HandleKey(Key.Enter);
        Type(view, input);
        view.HandleKey(Key.Enter);
    }

    private static void Type(OwnerConsoleSettingsView view, string input)
    {
        foreach (var character in input) view.HandleKey(new Key(character));
    }

    private sealed class Projects : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal OrchestratorProjectRegistry Registry { get; }
        private readonly OrchestratorWorkspace _home;

        internal Projects()
        {
            Registry = new(Path.Combine(Root, "registry"), Path.Combine(Root, "data"));
            var home = Path.Combine(Root, "home");
            Directory.CreateDirectory(home);
            _home = OrchestratorWorkspace.ForDirectory(home, null, null, Registry);
        }

        internal OrchestratorWorkspace Register(string name, string branch, bool? push = null)
        {
            var root = Path.Combine(Root, "repos", name);
            Directory.CreateDirectory(root);
            var workspace = Registry.CreateProject(name, root, integrationBranch: branch).ResolveWorkspace();
            if (push is { } enabled) WritePolicy(workspace, enabled);
            return workspace;
        }

        internal OwnerConsoleBoardSource Source() =>
            OwnerConsoleBoardSource.FromRegistry(_home, _home.RootDirectory, Registry, TimeProvider.System);

        internal OwnerConsoleProjectsSettingsTab Tab(OwnerConsoleBoardSource? source = null) =>
            new(Registry, (source ?? Source()).HandleScope);

        internal static string PolicyPath(OrchestratorWorkspace workspace) =>
            Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json");

        internal static void WritePolicy(OrchestratorWorkspace workspace, bool push)
        {
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(PolicyPath(workspace),
                (ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = push }).ToJson());
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
