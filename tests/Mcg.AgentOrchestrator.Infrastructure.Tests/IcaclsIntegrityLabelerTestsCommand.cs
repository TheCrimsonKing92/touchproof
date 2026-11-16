using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsCommand
{
    [Theory]
    [InlineData("file", false, 1)]
    [InlineData("file", true, 1)]
    [InlineData("directory", false, 1)]
    [InlineData("tree", true, 4)]
    public void Set_WithTripwire_LabelsEntriesWithoutStartingAChild(
        string shape, bool recursive, int expectedEntries)
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Directory.CreateTempSubdirectory("native-il-").FullName;
        try
        {
            var calls = 0;
            var labeler = new IcaclsIntegrityLabeler(_ =>
            {
                calls++;
                throw new InvalidOperationException("The set path must not start a child.");
            });
            var target = Path.Combine(root, "target");
            if (shape == "file") File.WriteAllText(target, "fixture");
            else Directory.CreateDirectory(target);
            if (shape == "tree")
            {
                var nested = Directory.CreateDirectory(Path.Combine(target, "nested")).FullName;
                File.WriteAllText(Path.Combine(target, "root.txt"), "fixture");
                File.WriteAllText(Path.Combine(nested, "leaf.txt"), "fixture");
            }

            var result = labeler.SetIntegrityWithDiagnostics(target, "(OI)(CI)L", recursive);

            Assert.True(result.Succeeded, result.Diagnostic);
            Assert.Equal(0, calls);
            using var json = JsonDocument.Parse(result.Diagnostic);
            Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal(expectedEntries, json.RootElement.GetProperty("entriesLabeled").GetInt32());
            var entries = new[] { target }.Concat(shape == "tree"
                ? Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories) : []);
            foreach (var entry in entries)
            {
                var state = labeler.Query(entry);
                Assert.True(state.Exists);
                Assert.True(state.Low, entry);
                Assert.Equal(Directory.Exists(entry), state.Inheritable);
                Assert.Null(state.NativeQueryError);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Set_MissingPath_ReturnsNativeFailureAndLegacyBooleanFalse()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var path = Path.Combine(Path.GetTempPath(), "missing-il-" + Guid.NewGuid().ToString("N"));
        var labeler = new IcaclsIntegrityLabeler();
        var error = AssertFailure(labeler.SetIntegrityWithDiagnostics(path, "L", false), path);
        Assert.Contains(error, new[] { 2, 3 });
        Assert.False(labeler.SetIntegrity(path, "L", false));
    }

    [Fact]
    public void Set_DeniedWriteOwner_ReturnsAccessDeniedAndPreservesLabel()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Directory.CreateTempSubdirectory("denied-il-").FullName;
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "fixture");
        var labeler = new IcaclsIntegrityLabeler();
        try
        {
            Assert.True(labeler.SetIntegrity(path, "M", false));
            using var denial = new WriteOwnerDenial(new FileInfo(path));
            Assert.Equal(5, AssertFailure(labeler.SetIntegrityWithDiagnostics(path, "L", false), path));
            Assert.True(labeler.Query(path).Medium);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Set_RecursiveDeniedEntry_StopsAndRetainsEarlierWrites()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Directory.CreateTempSubdirectory("partial-il-").FullName;
        var blocked = Directory.CreateDirectory(Path.Combine(root, "blocked")).FullName;
        var unvisited = Path.Combine(blocked, "unvisited.txt");
        File.WriteAllText(unvisited, "fixture");
        var labeler = new IcaclsIntegrityLabeler();
        try
        {
            Assert.True(labeler.SetIntegrity(root, "(OI)(CI)M", true));
            using var denial = new WriteOwnerDenial(new DirectoryInfo(blocked));
            // Bare L avoids automatic inheritance changing unvisited entries.
            var result = labeler.SetIntegrityWithDiagnostics(root, "L", true);
            Assert.Equal(5, AssertFailure(result, blocked));
            using var json = JsonDocument.Parse(result.Diagnostic);
            Assert.Equal(1, json.RootElement.GetProperty("entriesLabeled").GetInt32());
            Assert.True(labeler.Query(root).Low);
            Assert.True(labeler.Query(blocked).Medium);
            Assert.True(labeler.Query(unvisited).Medium);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("H")]
    [InlineData("l")]
    [InlineData("(OI)L")]
    public void Set_InvalidLevel_ReportsInvalidParameterAndWritesNothing(string level)
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Directory.CreateTempSubdirectory("invalid-il-").FullName;
        try
        {
            var labeler = new IcaclsIntegrityLabeler();
            var before = labeler.Query(root);
            var result = labeler.SetIntegrityWithDiagnostics(root, level, true);
            Assert.Equal(87, AssertFailure(result, root));
            Assert.Equal(before, labeler.Query(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Set_LabelReplacement_PreservesExistingDacl()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Directory.CreateTempSubdirectory("dacl-il-").FullName;
        var file = new FileInfo(Path.Combine(root, "file.txt"));
        File.WriteAllText(file.FullName, "fixture");
        try
        {
            var access = file.GetAccessControl(AccessControlSections.Access);
            access.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ReadAttributes, AccessControlType.Allow));
            file.SetAccessControl(access);
            var before = file.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var labeler = new IcaclsIntegrityLabeler();
            foreach (var level in new[] { "L", "M", "L" })
            {
                var result = labeler.SetIntegrityWithDiagnostics(file.FullName, level, false);
                Assert.True(result.Succeeded, result.Diagnostic);
                Assert.Equal(level == "L", labeler.Query(file.FullName).Low);
                Assert.Equal(level == "M", labeler.Query(file.FullName).Medium);
                Assert.Equal(before, file.GetAccessControl(AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Set_WithAuditAccess_PreservesUnrelatedSaclEntries()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Directory.CreateTempSubdirectory("sacl-il-").FullName;
        var file = new FileInfo(Path.Combine(root, "file.txt"));
        File.WriteAllText(file.FullName, "fixture");
        try
        {
            try
            {
                var audit = file.GetAccessControl(AccessControlSections.Audit);
                audit.AddAuditRule(new FileSystemAuditRule(WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.ReadData, AuditFlags.Success));
                file.SetAccessControl(audit);
            }
            catch (Exception exception) when (exception is PrivilegeNotHeldException or UnauthorizedAccessException)
            {
                Assert.Skip("Constructing an audit SACL requires security access on the acceptance host.");
            }

            var before = AuditRules(file);
            Assert.NotEmpty(before);
            var labeler = new IcaclsIntegrityLabeler();
            foreach (var level in new[] { "L", "M" })
            {
                var result = labeler.SetIntegrityWithDiagnostics(file.FullName, level, false);
                Assert.True(result.Succeeded, result.Diagnostic);
                Assert.Equal(before, AuditRules(file));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] AuditRules(FileInfo file) => file.GetAccessControl(AccessControlSections.Audit)
        .GetAuditRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAuditRule>()
        .Select(rule => $"{rule.IdentityReference.Value}:{rule.FileSystemRights}:{rule.AuditFlags}:" +
            $"{rule.InheritanceFlags}:{rule.PropagationFlags}:{rule.IsInherited}")
        .Order(StringComparer.Ordinal).ToArray();

    private static int AssertFailure(IntegrityLabelCommandResult result, string path)
    {
        Assert.False(result.Succeeded, result.Diagnostic);
        using var json = JsonDocument.Parse(result.Diagnostic);
        Assert.Equal(path, json.RootElement.GetProperty("failingPath").GetString());
        var error = json.RootElement.GetProperty("win32Error").GetInt32();
        Assert.NotEqual(0, error);
        return error;
    }

    private sealed class WriteOwnerDenial : IDisposable
    {
        private readonly FileSystemInfo entry;
        private readonly byte[] original;

        internal WriteOwnerDenial(FileSystemInfo entry)
        {
            this.entry = entry;
            original = Read().GetSecurityDescriptorBinaryForm();
            var denied = Read();
            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.TakeOwnership, AccessControlType.Deny));
            Write(denied);
        }

        private FileSystemSecurity Read() => entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Access)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Access);

        private void Write(FileSystemSecurity security)
        {
            if (entry is DirectoryInfo directory) directory.SetAccessControl((DirectorySecurity)security);
            else ((FileInfo)entry).SetAccessControl((FileSecurity)security);
        }

        public void Dispose()
        {
            var restored = Read();
            restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            Write(restored);
        }
    }
}
