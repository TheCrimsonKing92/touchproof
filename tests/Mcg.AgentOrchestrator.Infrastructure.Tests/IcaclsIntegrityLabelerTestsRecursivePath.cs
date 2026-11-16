using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsRecursivePath
{
    [Fact]
    public void RecursivePathsPrefixShortRootsAndPreserveOtherPathForms()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows native paths required.");
        Assert.Equal(@"C:\short", IcaclsIntegrityLabeler.NativeFilePath(@"C:\short"));
        Assert.Equal(@"\\?\C:\short\", IcaclsIntegrityLabeler.NativeFilePath(@"C:\short\", recursive: true));
        Assert.Equal(@"\\?\UNC\server\share\short\", IcaclsIntegrityLabeler.NativeFilePath(@"\\server\share\short\", recursive: true));
        Assert.Equal(@"\\?\C:\short", IcaclsIntegrityLabeler.NativeFilePath(@"\\?\C:\short", recursive: true));
        Assert.Equal(@"\\.\C:\short", IcaclsIntegrityLabeler.NativeFilePath(@"\\.\C:\short", recursive: true));
        Assert.Equal(@"\\?\" + Path.GetFullPath("relative-root"), IcaclsIntegrityLabeler.NativeFilePath("relative-root", recursive: true));
    }

    [Fact]
    public void Set_RecursiveShortRoot_LabelsLongDescendants()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcg-il-tree-" + Guid.NewGuid().ToString("N")));
        Assert.True(root.Length < 260, "The fixture must start at a short root.");
        var leaf = Path.Combine(root, new string('d', 130), new string('f', 130) + ".cache");
        Assert.True(leaf.Length > 260);
        Directory.CreateDirectory(Path.GetDirectoryName(leaf)!);
        File.WriteAllText(leaf, "owned recursive path control");
        try
        {
            var labeler = new IcaclsIntegrityLabeler();
            var result = labeler.SetIntegrityWithDiagnostics(root, "(OI)(CI)L", recursive: true);
            Assert.True(result.Succeeded, result.Diagnostic);
            var label = labeler.Query(leaf);
            Assert.True(label.Exists);
            Assert.True(label.Low);
            Assert.Null(label.NativeQueryError);
            Assert.Equal("owned recursive path control", File.ReadAllText(leaf));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Set_RecursiveJunction_SkipsDescendantAndRefusesRoot(bool dangling)
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("native-il-junction-").FullName;
        var root = Directory.CreateDirectory(Path.Combine(fixture, "root")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(fixture, "outside")).FullName;
        var outsideFile = Path.Combine(outside, "untouched.txt");
        File.WriteAllText(outsideFile, "fixture");
        var link = Path.Combine(nested, "link");
        try
        {
            var labeler = new IcaclsIntegrityLabeler();
            Assert.True(labeler.SetIntegrity(outside, "(OI)(CI)M", true));
            var targetBefore = labeler.Query(outside);
            var fileBefore = labeler.Query(outsideFile);
            Assert.True(targetBefore.Medium);
            Assert.True(fileBefore.Medium);
            Assert.False(targetBefore.Low);
            Assert.False(fileBefore.Low);
            Assert.Null(targetBefore.NativeQueryError);
            Assert.Null(fileBefore.NativeQueryError);
            var created = await AcceptanceProcessTestContext.RunManifestCommandAsync(
                [WorkerShell.Executable, .. WorkerShell.BaseArguments(),
                    $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' -ErrorAction Stop | Out-Null"],
                fixture, TimeSpan.FromSeconds(30), new Dictionary<string, string?>(),
                resolveBareCommand: false, CancellationToken.None);
            Assert.False(created.TimedOut);
            Assert.True(created.ExitCode == 0, created.Output);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            if (dangling)
            {
                File.Delete(outsideFile);
                Directory.Delete(outside);
            }

            var result = labeler.SetIntegrityWithDiagnostics(root, "(OI)(CI)L", true);

            Assert.True(result.Succeeded, result.Diagnostic);
            Assert.True(labeler.Query(root).Low);
            Assert.True(labeler.Query(nested).Low);
            using var success = JsonDocument.Parse(result.Diagnostic);
            Assert.Equal(1, success.RootElement.GetProperty("reparsePointsSkipped").GetInt32());
            var refused = labeler.SetIntegrityWithDiagnostics(link, "(OI)(CI)L", true);
            Assert.False(refused.Succeeded, refused.Diagnostic);
            using var failure = JsonDocument.Parse(refused.Diagnostic);
            Assert.Equal("root-reparse-point", failure.RootElement.GetProperty("reason").GetString());
            Assert.Equal(link, failure.RootElement.GetProperty("failingPath").GetString());
            Assert.Equal(4395, failure.RootElement.GetProperty("win32Error").GetInt32());
            Assert.Equal(0, failure.RootElement.GetProperty("entriesLabeled").GetInt32());
            if (dangling) Assert.False(Directory.Exists(outside));
            else
            {
                Assert.Equal(targetBefore, labeler.Query(outside));
                Assert.Equal(fileBefore, labeler.Query(outsideFile));
            }
        }
        finally
        {
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
            Directory.Delete(fixture, recursive: true);
        }
    }
}
