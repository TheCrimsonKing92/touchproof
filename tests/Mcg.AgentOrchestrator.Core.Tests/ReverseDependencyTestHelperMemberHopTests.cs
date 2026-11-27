using Mcg.AgentOrchestrator.Core;

public sealed class ReverseDependencyTestHelperMemberHopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelperCallers_KeepReachAndExcludeUnrelatedMembers(bool partial)
    {
        using var repository = new Repository();
        var modifier = partial ? "partial " : "";
        repository.WriteHelper("Helper", $$"""
            internal static {{modifier}}class Helper
            {
                internal static int Read() => Forward();
                internal static int Forward() => Write();
                internal static int Write() => Store.Save();
                internal static int Ignore() => Store.Load();
            }
            """);
        if (partial)
        {
            repository.WriteHelper("Helper.Other", """
                internal static partial class Helper
                {
                    internal static int Unreached() => 0;
                }
                """);
            repository.WriteTest("UnreachedTests", "Helper.Unreached()");
        }

        repository.WriteTest("DirectCallerTests", "Helper.Forward()");
        repository.WriteTest("IgnoreTests", "Helper.Ignore()");
        repository.WriteTest("NameOnlyTests", "typeof(Helper)");
        repository.WriteTest("ReadTests", "Helper.Read()");
        repository.WriteTest("WriteTests", "Helper.Write()");

        Assert.Equal(["DirectCallerTests", "IgnoreTests", "NameOnlyTests", "ReadTests", "WriteTests"],
            repository.ReadClasses(propagateShippingSourceReach: false));
        Assert.Equal(["DirectCallerTests", "NameOnlyTests", "ReadTests", "WriteTests"], repository.ReadClasses());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameHopPeer_KeepsForwardingAndFallsBackForFieldUse(bool shippingPeer)
    {
        using var repository = new Repository();
        const string peer = """
            internal static class Peer
            {
                internal static int Run() => Store.Save();
            }
            """;
        if (shippingPeer)
        {
            repository.Write("src/Fixture/Peer.cs", peer);
        }
        else
        {
            repository.WriteHelper("Peer", peer);
        }

        void WriteHelper(string extraMember) => repository.WriteHelper("Helper", $$"""
            internal static class Helper
            {
                {{extraMember}}
                internal static int Direct() => Store.Save();
                internal static int Forward() => Peer.Run();
                internal static int Ignore() => 0;
            }
            """);

        repository.WriteTest("DirectTests", "Helper.Direct()");
        repository.WriteTest("ForwardTests", "Helper.Forward()");
        repository.WriteTest("IgnoreTests", "Helper.Ignore()");

        WriteHelper("");
        Assert.Equal(["DirectTests", "ForwardTests"], repository.ReadClasses());

        WriteHelper("private static readonly int cached = Peer.Run();");
        Assert.Equal(["DirectTests", "ForwardTests", "IgnoreTests"], repository.ReadClasses());
    }

    [Theory]
    [InlineData("", "private static readonly int cached = Store.Save();")]
    [InlineData("[System.Obsolete(nameof(Store.Save))]", "")]
    [InlineData("", "static Helper() { _ = Store.Save(); }")]
    public void UnattributedChangeReference_KeepsWholeTypeReach(string attribute, string extraMember)
    {
        using var repository = new Repository();

        void WriteHelper(string typeAttribute, string member) => repository.WriteHelper("Helper", $$"""
            {{typeAttribute}}
            internal static class Helper
            {
                {{member}}
                internal static int Write() => Store.Save();
                internal static int Ignore() => 0;
            }
            """);

        repository.WriteTest("IgnoreTests", "Helper.Ignore()");
        repository.WriteTest("WriteTests", "Helper.Write()");

        WriteHelper("", "");
        Assert.Equal(["WriteTests"], repository.ReadClasses());

        WriteHelper(attribute, extraMember);
        Assert.Equal(["IgnoreTests", "WriteTests"], repository.ReadClasses());
    }

    [Fact]
    public void ReachedPartialParts_KeepAllMembersAndCrossPartCallers()
    {
        using var repository = new Repository();
        repository.WriteHelper("Helper", """
            internal static partial class Helper
            {
                internal static int Write() => Store.Save();
                internal static int AcrossParts() => ReadSecond();
                internal static int FirstUnrelated() => 0;
            }
            """);
        repository.WriteHelper("Helper.Unreached", """
            internal static partial class Helper
            {
                internal static int Unreached() => 0;
            }
            """);

        void WriteSecondPart(string expression) => repository.WriteHelper("Helper.Second", $$"""
            internal static partial class Helper
            {
                internal static int ReadSecond() => {{expression}};
                internal static int SecondUnrelated() => 0;
            }
            """);

        repository.WriteTest("AcrossPartsTests", "Helper.AcrossParts()");
        repository.WriteTest("FirstUnrelatedTests", "Helper.FirstUnrelated()");
        repository.WriteTest("ReadSecondTests", "Helper.ReadSecond()");
        repository.WriteTest("SecondUnrelatedTests", "Helper.SecondUnrelated()");
        repository.WriteTest("UnreachedTests", "Helper.Unreached()");
        repository.WriteTest("WriteTests", "Helper.Write()");

        WriteSecondPart("0");
        Assert.Equal(["WriteTests"], repository.ReadClasses());

        WriteSecondPart("Store.Save()");
        Assert.Equal(["AcrossPartsTests", "FirstUnrelatedTests", "ReadSecondTests", "SecondUnrelatedTests", "WriteTests"],
            repository.ReadClasses());
    }

    [Fact]
    public void MissingMemberIdentifiers_KeepWholeTypeReach()
    {
        // Source scans always populate member identifiers. Exercise the shared filter with incomplete evidence.
        var storeMembers = new HashSet<string>(StringComparer.Ordinal) { "Save" };
        var store = new ReverseDependencyTestImpactReader.FrontierSymbol(
            "Store", ReverseDependencyTestImpactReader.TestSelectionMode.AllReferencingClasses, storeMembers);
        var writeIdentifiers = new HashSet<string>(StringComparer.Ordinal) { "Store", "Save" };
        var emptyIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var write = new SourceMemberDeclaration("Write", 2, 2, writeIdentifiers);
        var ignore = new SourceMemberDeclaration("Ignore", 3, 3, emptyIdentifiers);
        var helper = new CSharpTestClassScanner.SourceTypeDeclaration("Helper", false, writeIdentifiers, 1, 4,
            [write, ignore], [], UnattributedIdentifiers: emptyIdentifiers);
        var knownMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { ["Store"] = storeMembers };
        var helperPeers = new HashSet<string>(StringComparer.Ordinal) { "Helper" };

        IReadOnlySet<string>? Filter(CSharpTestClassScanner.SourceTypeDeclaration declaration) =>
            ReverseDependencyTestImpactReader.ConsumerFilter(declaration, [store], knownMembers, helperPeers,
                hasMultipleReachedParts: false);

        var preciseFilter = Filter(helper);
        Assert.NotNull(preciseFilter);
        Assert.Equal("Write", Assert.Single(preciseFilter));
        Assert.Null(Filter(helper with { Members = [write, ignore with { ReferencedIdentifiers = null }] }));
    }

    private sealed class Repository : IDisposable
    {
        private const string Store = "src/Fixture/Store.cs";
        private const string TestDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        private string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-helper-member-hop-{Guid.NewGuid():N}");

        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write($"{TestDirectory}/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
            Write(Store, """
                internal static class Store
                {
                    internal static int Save() => 1;
                    internal static int Load() => 0;
                }
                """);
        }

        internal IReadOnlyList<string> ReadClasses(bool propagateShippingSourceReach = true)
        {
            var selection = ReverseDependencyTestImpactReader.Read(Root, [Store], bypassCache: true,
                propagateShippingSourceReach: propagateShippingSourceReach,
                changedLineRanges: new Dictionary<string, IReadOnlyList<(int, int)>> { [Store] = [(3, 3)] });
            Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
            return selection.TestClassNames;
        }

        internal void WriteHelper(string name, string source) => Write($"{TestDirectory}/{name}.cs", source);

        internal void WriteTest(string name, string expression) =>
            WriteHelper(name, $"public class {name} {{ [Xunit.Fact] public void Test() {{ _ = {expression}; }} }}");

        internal void Write(string path, string source)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, source);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
