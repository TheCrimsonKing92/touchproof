using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each scene owns an isolated kernel, controller and temporary SQLite stores.
public sealed class OwnerConsoleStoreReadFailureFormatterTests
{
    private const string RestartText = "This console build is older than the shared store. You must restart the console on a current build.";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewerStore_LoadRefreshAndBoardSayRestartWithoutMutatingStore()
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        await scene.ResolveSpec("Which direction?", "Use the current build.", OwnerQuestionResolutionDialogTests.Scene.At);
        SetVersion(scene.DirectoryPath, StoreSchemaRegistry.OperatorIntents.CurrentVersion!.Value + 1);
        var before = Snapshot(scene.DirectoryPath);

        await scene.Controller.ShowGoalDetailAsync(scene.Goal.Id.Value, cancellationToken: Token);
        var load = Assert.Single(scene.Dialogs.Texts).Text;
        Assert.Equal(OwnerConsoleGoalDetailLoading.UnavailableText + Environment.NewLine + "Load failed: " + RestartText, load);
        Assert.DoesNotContain("setup", load, StringComparison.OrdinalIgnoreCase);

        var refresh = await RefreshFailure(scene, async token =>
        {
            await scene.Reader.ListForGoalAsync(scene.Goal.Id.Value, token);
            throw new InvalidOperationException("Expected the newer store read to fail.");
        });
        Assert.Equal("refresh failed: " + RestartText, refresh);
        Assert.DoesNotContain("setup", refresh!, StringComparison.OrdinalIgnoreCase);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            SqliteOperatorIntentStore.OpenExisting(scene.DirectoryPath, scene.DirectoryPath));
        Assert.Contains("schema is Newer", failure.Message);
        var board = OwnerConsoleBoardSource.Diagnostic("beta", failure);
        Assert.Equal("beta: state read failed: " + RestartText, board.Title);
        Assert.DoesNotContain("setup", board.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(board.IsDiagnostic);
        Assert.True(board.Dimmed);
        Assert.Equal("beta", board.Project);
        scene.Controller.Dispose();
        Assert.Equal(before, Snapshot(scene.DirectoryPath));
    }

    [Fact]
    public async Task CurrentStore_LoadsGoalDetailAndAppliedResolution()
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        const string question = "Which current direction?";
        await scene.ResolveSpec(question, "Use the current build.", OwnerQuestionResolutionDialogTests.Scene.At);
        Assert.Single(await scene.Reader.ListForGoalAsync(scene.Goal.Id.Value, Token));

        await scene.Controller.ShowGoalDetailAsync(scene.Goal.Id.Value, cancellationToken: Token);
        var text = Assert.Single(scene.Dialogs.Texts).Text;
        Assert.Contains(scene.Goal.Id.Value, text);
        Assert.Contains(scene.Goal.Objective, text);
        Assert.Contains(question, text);
        Assert.DoesNotContain("Load failed:", text);
        Assert.DoesNotContain(OwnerConsoleGoalDetailLoading.UnavailableText, text);
        Assert.DoesNotContain(RestartText, text);
    }

    [Theory]
    [InlineData("store offline")]
    [InlineData("Newer data is older than the console; restart suggested")]
    [InlineData("offline\ntry later")]
    public async Task NonSchemaFailure_PreservesTextOnAllThreeSurfaces(string message)
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        var failure = new InvalidOperationException(message);
        Assert.Equal(StoreSchemaDiagnostics.Message(failure), OwnerConsoleStoreReadFailureFormatter.Message(failure));
        Assert.Equal(OwnerConsoleGoalDetailLoading.UnavailableText + Environment.NewLine + "Load failed: " + message,
            OwnerConsoleGoalDetailLoading.Unavailable(failure).Text);
        Assert.Equal("refresh failed: " + message, await RefreshFailure(scene,
            _ => Task.FromException<OwnerConsoleGoalDialog.Content?>(failure)));
        Assert.Equal("beta: state read failed: " + message.ReplaceLineEndings(" "),
            OwnerConsoleBoardSource.Diagnostic("beta", failure).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAndOlderStores_KeepSetupMessage(bool older)
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        if (older)
        {
            SqliteOperatorIntentStore.Setup(Path.Combine(scene.DirectoryPath, SqliteOperatorIntentStore.DatabaseFileName));
            SetVersion(scene.DirectoryPath, 0);
        }
        var failure = Assert.Throws<InvalidOperationException>(() =>
            SqliteOperatorIntentStore.OpenExisting(scene.DirectoryPath, scene.DirectoryPath));
        Assert.Contains(older ? "schema is Older" : "schema is Missing", failure.Message);
        var message = OwnerConsoleStoreReadFailureFormatter.Message(failure);
        Assert.Equal(StoreSchemaDiagnostics.Message(failure), message);
        Assert.Contains("run setup", message);
    }

    [Fact]
    public void WrappedNewerFailure_KeepsOuterDiagnosticRecognition()
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        SqliteOperatorIntentStore.Setup(Path.Combine(scene.DirectoryPath, SqliteOperatorIntentStore.DatabaseFileName));
        SetVersion(scene.DirectoryPath, StoreSchemaRegistry.OperatorIntents.CurrentVersion!.Value + 1);
        var newer = Assert.Throws<InvalidOperationException>(() =>
            SqliteOperatorIntentStore.OpenExisting(scene.DirectoryPath, scene.DirectoryPath));
        var outer = new InvalidOperationException("outer read failure", newer);
        Assert.Equal(StoreSchemaDiagnostics.Message(outer), OwnerConsoleStoreReadFailureFormatter.Message(outer));
        Assert.Equal("outer read failure", OwnerConsoleStoreReadFailureFormatter.Message(outer));
    }

    private static async Task<string?> RefreshFailure(OwnerQuestionResolutionDialogTests.Scene scene,
        Func<CancellationToken, Task<OwnerConsoleGoalDialog.Content?>> load)
    {
        var retained = new OwnerConsoleGoalDialog.Content("Previously loaded detail", [], [], null, null, null);
        using var dialog = new OwnerConsoleGoalDialog(scene.Controller, retained, load,
            _ => Task.CompletedTask, scene.Dialogs.ShowTextAsync, Token);
        dialog.Start(action => action());
        scene.Controller.Apply(new(new(ConductorLiveness.Running, 1, 0, 0, null, 0), [], [], []));
        await dialog.LastLoad.WaitAsync(Token);
        Assert.Same(retained, dialog.Current);
        return dialog.TitleStatus;
    }

    private static void SetVersion(string directory, int version)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, SqliteOperatorIntentStore.DatabaseFileName),
            Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE store_schema_versions SET version = $version WHERE store_name = $store";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$store", StoreSchemaRegistry.OperatorIntents.StoreName);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) +
            (File.Exists(path) ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : ":directory")).ToArray();
}
