using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json.Nodes;

// Parallel-safe: every test owns its cache directory and in-memory candidate trees.
public sealed class SourceReaderIncrementalAnalysisTests
{
    private const string Snapshot = "public static class RepositorySourceSnapshotCache { public static object Get(string root) => null; }";

    [Fact]
    public void AttributeSuffixOnly_DeclaresNonblankNamesAndValidFacts()
    {
        var sources = Sources(new Dictionary<string, string>
        {
            ["tests/Attribute.cs"] = "public class Attribute {} public class Helpers { public void Attribute() {} }"
        });
        var description = SourceReaderFileFacts.Describe(sources, Assert.Single(sources.Trees));
        Assert.Contains("Attribute", description.DeclaredNames);
        Assert.DoesNotContain(description.DeclaredNames, string.IsNullOrWhiteSpace);
        var facts = SourceReadingGuardTestClasses.Extract(sources, null);
        Assert.Contains(Assert.Single(facts).Members, member => member.Name == "Attribute");
        Assert.True(SourceReaderFileFacts.Valid(facts));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("edit")]
    [InlineData("remove")]
    public void BoundedChange_MatchesFullAnalysisAndReusesUnaffectedFiles(string change)
    {
        using var storage = new Storage();
        var baseline = Base();
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline);
        switch (change)
        {
            case "add": candidate.Add("tests/Added.cs", Reader("AddedTests", "AddedRead")); break;
            case "edit": candidate["tests/Reader.cs"] = "public class FirstTests { public void Reads() {} }"; break;
            case "remove": Assert.True(candidate.Remove("tests/Reader.cs")); break;
        }
        var cache = new SourceReaderAnalysisCache(storage.Path); // Prove persisted, cross-instance reuse.
        var actual = Analyze(cache, candidate);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(change == "remove" ? 0 : 1, cache.SemanticFileCount);
        Assert.True(cache.SemanticFileCount < candidate.Count);
        Assert.Equal(change == "add" ? new[] { "AddedTests", "FirstTests" } : [], actual.ReaderOwners);
        Assert.True(actual.DeclaresSnapshotCache);
    }

    [Fact]
    public void IsolatedEdit_InTenFileTree_AnalysesExactlyOneFile()
    {
        using var storage = new Storage();
        var baseline = Base();
        Assert.Equal(10, baseline.Count);
        var cache = new SourceReaderAnalysisCache(storage.Path);
        Analyze(cache, baseline);
        var before = cache.SemanticFileCount;
        var analyses = cache.AnalysisCount;
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Spare0.cs"] = Reader("Spare0", "Unique0")
        };
        var actual = Analyze(cache, candidate);
        Assert.Equal(1, cache.SemanticFileCount - before);
        Assert.Equal(1, cache.AnalysisCount - analyses);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(new[] { "FirstTests", "Spare0" }, actual.ReaderOwners);
    }

    [Theory]
    [InlineData("git", true)]
    [InlineData("git", false)]
    [InlineData("directory", true)]
    [InlineData("directory", false)]
    public void LeafEdgeChange_ReevaluatesReusedCallersAndArgumentFlow(string kind, bool creates)
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline["tests/Reader.cs"] = """
            public class FirstTests {
                public void Reads() { VerifiedRepositoryRoot.Find(); Bridge.Mid("ls-files -z"); }
            }
            """;
        baseline.Add("tests/Root.cs", "public static class VerifiedRepositoryRoot { public static string Find() => \"root\"; }");
        baseline.Add("tests/Bridge.cs", "public static class Bridge { public static void Mid(string arg) { Leaf.Run(arg); } }");
        baseline.Add("tests/Leaf.cs", Leaf(kind, !creates));
        var old = Analyze(new(storage.Path), baseline);
        Assert.Equal(creates ? Array.Empty<string>() : new[] { "FirstTests" }, old.ReaderOwners);
        var candidate = new Dictionary<string, string>(baseline) { ["tests/Leaf.cs"] = Leaf(kind, creates) };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        Assert.Equal(2, cache.SemanticFileCount); // Bridge and Leaf; FirstTests must be reused.
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(creates ? new[] { "FirstTests" } : [], actual.ReaderOwners);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("new")]
    public void RenamedDeclaration_InvalidatesOldAndNewNameReferences(string referencedVersion)
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Helper.cs", "public class OldHelper { public int Flag = 1; }");
        baseline.Add("tests/Consumer.cs", referencedVersion == "old"
            ? "public class Consumer { public OldHelper Item; }"
            : "public class Consumer { public NewHelper Item; }");
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Helper.cs"] = "public class NewHelper { public int Flag = 2; }"
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(Analyze(new(null), candidate), Analyze(cache, candidate));
        Assert.Equal(2, cache.SemanticFileCount);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("older-format")]
    [InlineData("missing-facts")]
    [InlineData("hash-mismatch")]
    [InlineData("wrong-key")]
    public void InvalidBaseline_AnalysesEveryCandidateFile(string damage)
    {
        using var storage = new Storage();
        var baseline = Base();
        var stored = new SourceReaderAnalysisCache(storage.Path);
        Analyze(stored, baseline);
        var entry = Assert.IsType<string>(stored.EntryPath(SourceReaderAnalysisCache.Key(Sources(baseline))));
        var text = File.ReadAllText(entry);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(text));
        switch (damage)
        {
            case "older-format": json["FormatVersion"] = SourceReaderAnalysisCache.FormatVersion - 1; break;
            case "missing-facts": Assert.True(json.Remove("Files")); break;
            case "hash-mismatch": json["Files"]![0]!["ContentHash"] = new string('0', 64); break;
            case "wrong-key": json["Key"] = new string('0', 64); break;
        }
        if (damage == "missing") File.Delete(entry);
        else File.WriteAllText(entry, damage switch
        {
            "corrupt" => "not json",
            "truncated" => text[..(text.Length / 2)],
            _ => json.ToJsonString()
        });
        var candidate = new Dictionary<string, string>(baseline) { ["tests/Spare0.cs"] = Reader("Spare0", "Unique0") };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(Analyze(new(null), candidate), Analyze(cache, candidate));
        Assert.Equal(candidate.Count, cache.SemanticFileCount);
    }

    [Fact]
    public void ExactAndRelocatedHits_PerformNoSemanticPass()
    {
        using var storage = new Storage();
        var baseline = Base();
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var expected = Analyze(cache, baseline);
        Assert.Equal(baseline.Count, cache.SemanticFileCount);
        var before = cache.SemanticFileCount;
        AssertEquivalent(expected, Analyze(cache, baseline));
        Assert.Equal(before, cache.SemanticFileCount);
        Assert.Equal(1, cache.AnalysisCount);
        var fresh = new SourceReaderAnalysisCache(storage.Path);
        var relocated = baseline.Reverse().ToDictionary(pair => "relocated/" + pair.Key, pair => pair.Value);
        AssertEquivalent(expected, Analyze(fresh, relocated));
        Assert.Equal(0, fresh.SemanticFileCount);
        Assert.Equal(0, fresh.AnalysisCount);
    }

    [Theory]
    [InlineData("global-using")]
    [InlineData("operator")]
    [InlineData("type-kind")]
    public void UnboundedChange_UsesFullAnalysis(string change)
    {
        using var storage = new Storage();
        var baseline = Base();
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Spare0.cs"] = change switch
            {
                "global-using" => "global using System; public class Spare0 { public void Unique0() {} }",
                "type-kind" => "public struct Spare0 { public void Unique0() {} }",
                _ => "public class Spare0 { public static Spare0 operator +(Spare0 a, Spare0 b) => a; }"
            }
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(Analyze(new(null), candidate), Analyze(cache, candidate));
        Assert.Equal(candidate.Count, cache.SemanticFileCount);
    }

    [Theory]
    [InlineData("base-list", "public class Spare0 : FirstTests { public void Unique0() {} }")]
    [InlineData("await-pattern", "public class Spare0 { public int GetResult() => 1; }")]
    [InlineData("deconstruct-pattern", "public class Spare0 { public void Deconstruct(out int x) { x = 1; } }")]
    [InlineData("query-operator", "public class Spare0 { public void Select() {} }")]
    [InlineData("enumerator-field", "public class Spare0 { public int Current; }")]
    [InlineData("append-literal", "public class Spare0 { public void AppendLiteral(string text) {} }")]
    [InlineData("append-formatted", "public class Spare0 { public void AppendFormatted(int value) {} }")]
    [InlineData("count-property", "public class Spare0 { public int Count => 1; }")]
    public void NameBoundedChange_ReusesUnaffectedFiles(string change, string declaration)
    {
        using var storage = new Storage();
        var baseline = Base();
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline) { ["tests/Spare0.cs"] = declaration };
        var extractions = new List<HashSet<string>?>();
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate, extractions);
        var paths = Assert.IsType<HashSet<string>>(Assert.Single(extractions));
        Assert.Contains("tests/Spare0.cs", paths);
        Assert.True(paths.Count < candidate.Count, change);
        Assert.Equal(paths.Count, cache.SemanticFileCount);
        AssertEquivalent(Analyze(new(null), candidate), actual);
    }

    [Fact]
    public void AddedDerivedTest_ReusesBaselineAndMatchesFullSelection()
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Base.cs", """
            public class BaseTests {
                [Fact] public void Inherited() { RepositorySourceSnapshotCache.Get("root"); }
            }
            """);
        var before = Analyze(new(storage.Path), baseline);
        Assert.Contains("BaseTests", before.ReaderOwners);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Derived.cs"] = "public class DerivedTests : BaseTests {}"
        };
        var extractions = new List<HashSet<string>?>();
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate, extractions);
        var paths = Assert.IsType<HashSet<string>>(Assert.Single(extractions));
        Assert.Contains("tests/Derived.cs", paths);
        Assert.Contains("tests/Base.cs", paths);
        Assert.True(paths.Count < candidate.Count);
        Assert.Equal(paths.Count, cache.SemanticFileCount);
        Assert.Equal(1, cache.AnalysisCount);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Contains("DerivedTests", actual.ReaderOwners);
    }

    [Theory]
    [InlineData("public class Bag { public static Bag operator +(Bag a, Bag b) => a; }")]
    [InlineData("public class Bag { public static implicit operator int(Bag value) => 0; }")]
    [InlineData("public class Bag { public int this[int index] => 0; }")]
    [InlineData("public delegate void Callback();")]
    [InlineData("global using System; public class Bag {}")]
    [InlineData("public struct Bag {}")]
    [InlineData("[CollectionBuilder(typeof(Bag), \"Create\")] public class Bag {}")]
    [InlineData("[InterpolatedStringHandler] public class Bag {}")]
    [InlineData("[AsyncMethodBuilder(typeof(Bag))] public class Bag {}")]
    [InlineData("public class Bag { public int Count => 0; public int this[int index] => 0; }")]
    public void IndependentUnboundedCause_KeepsNullPlanInEitherVersion(string declaration)
    {
        var before = Sources(new Dictionary<string, string> { ["tests/Bag.cs"] = "public class Bag {}" });
        var after = Sources(new Dictionary<string, string> { ["tests/Bag.cs"] = declaration + " // changed" });
        var baseline = before.Trees.Select(tree => SourceReaderFileFacts.Describe(before, tree)).ToArray();
        var candidate = after.Trees.Select(tree => SourceReaderFileFacts.Describe(after, tree)).ToArray();
        Assert.Null(SourceReaderFileFacts.Plan(baseline, candidate));
        Assert.Null(SourceReaderFileFacts.Plan(candidate, baseline));
        // A name-only change is bounded; an independent cause above must override that permission.
        var bounded = Sources(new Dictionary<string, string>
        {
            ["tests/Bag.cs"] = "public class Bag { public int Count => 0; }"
        });
        Assert.NotNull(SourceReaderFileFacts.Plan(baseline,
            bounded.Trees.Select(tree => SourceReaderFileFacts.Describe(bounded, tree)).ToArray()));
    }

    [Fact]
    public void ChangedBaseReader_RecomputesInheritedOwnersFromMergedFacts()
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Base.cs", "public class BaseTests { [Fact] public void Inherited() {} }");
        baseline.Add("tests/Derived.cs", "public class DerivedTests : BaseTests {} ");
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Base.cs"] = "public class BaseTests { [Fact] public void Inherited() { RepositorySourceSnapshotCache.Get(\"root\"); } }"
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        Assert.Equal(2, cache.SemanticFileCount);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(new[] { "BaseTests", "DerivedTests", "FirstTests" }, actual.ReaderOwners);
    }

    [Fact]
    public void LostBinding_FallsBackAndCountsBothSemanticPasses()
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Helper.cs", Reader("Helper", "Run"));
        baseline.Add("tests/Consumer.cs", "public class Consumer { public void Act() { new Helper().Run(); } }");
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Helper.cs"] = Reader("Helper", "Renamed")
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(candidate.Count + 2, cache.SemanticFileCount);
        Assert.DoesNotContain("Consumer", actual.ReaderOwners);
    }

    [Fact]
    public void PersistedIdentities_KeepOverloadsAndNestedGenericOwnersDistinct()
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Overloads.cs", """
            public static class Calls {
                public static void Read(string value) { RepositorySourceSnapshotCache.Get(value); }
                public static void Read(int value) {}
            }
            public class Outer<T> {
                public class Nested { public void Act() { Calls.Read("root"); } }
            }
            public class Numeric { public void Act() { Calls.Read(1); } }
            """);
        var before = Analyze(new(storage.Path), baseline);
        Assert.Equal(new[] { "Calls", "FirstTests", "Outer+Nested" }, before.ReaderOwners);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Spare0.cs"] = "public class Spare0 { public void Unique0() { int changed = 1; } }"
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        Assert.Equal(1, cache.SemanticFileCount);
        AssertEquivalent(before, actual);
        AssertEquivalent(Analyze(new(null), candidate), actual);
    }

    [Fact]
    public void NestedValueType_MergesSourceMemberIdentities()
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Wrapper.cs", """
            public class Wrapper {
                public struct ValueHelper {
                    public static void Fetch() { RepositorySourceSnapshotCache.Get("root"); }
                }
            }
            public class Consumer { public void Act() { Wrapper.ValueHelper.Fetch(); } }
            """);
        var before = Analyze(new(storage.Path), baseline);
        Assert.Equal(new[] { "Consumer", "FirstTests", "Wrapper" }, before.ReaderOwners);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Spare0.cs"] = "public class Spare0 { public void Unique0() { int changed = 1; } }"
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        Assert.Equal(1, cache.SemanticFileCount);
        AssertEquivalent(before, actual);
        AssertEquivalent(Analyze(new(null), candidate), actual);
    }

    [Theory]
    [InlineData("indexer", true)]
    [InlineData("indexer", false)]
    [InlineData("range-slice", true)]
    [InlineData("range-slice", false)]
    [InlineData("range-partial-length", true)]
    [InlineData("range-partial-length", false)]
    [InlineData("range-base-length", true)]
    [InlineData("range-base-length", false)]
    [InlineData("foreach", true)]
    [InlineData("foreach", false)]
    [InlineData("delegate-lambda", true)]
    [InlineData("delegate-lambda", false)]
    public void ImplicitBindingChange_CoversConsumersAndMatchesFullAnalysis(string kind, bool creates)
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Plain.cs", "public class Plain { public void Run() {} }");
        baseline.Add("tests/Reading.cs", Reader("Reading", "Run"));
        baseline.Add("tests/Binding.cs", Binding(!creates));
        if (kind == "range-partial-length")
            baseline.Add("tests/Bag.cs", "public partial class Bag { public Reading Slice(int start, int length) => new Reading(); }");
        if (kind == "range-base-length")
            baseline.Add("tests/Bag.cs", "public class Bag : BagBase { public Reading Slice(int start, int length) => new Reading(); }");
        baseline.Add("tests/Factory.cs", kind == "delegate-lambda"
            ? "public static class Fixture { public static void Register(Callback callback) {} }"
            : "public static class Factory { public static Bag Make() => new Bag(); }");
        var caller = kind switch
        {
            "indexer" => "Factory.Make()[0].Run();",
            "range-slice" or "range-partial-length" or "range-base-length" => "Factory.Make()[0..1].Run();",
            "foreach" => "foreach (var item in Factory.Make()) { item.Run(); }",
            _ => "Fixture.Register(p => p.Run());"
        };
        baseline.Add("tests/Uses.cs", $"public class UsesTests {{ public void Act() {{ {caller} }} }}");
        if (kind is not ("indexer" or "delegate-lambda"))
        {
            var explicitUse = kind switch
            {
                "foreach" => "Factory.Make().GetEnumerator().Current.Run();",
                "range-slice" => "Factory.Make().Slice(0, 1).Run();",
                _ => "var length = Factory.Make().Length;"
            };
            baseline.Add("tests/Named.cs", $"public class NamedTests {{ public void Act() {{ {explicitUse} }} }}");
        }
        var before = Analyze(new(storage.Path), baseline);
        Assert.Equal(!creates, before.ReaderOwners.Contains("UsesTests"));
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Binding.cs"] = Binding(creates)
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var extractions = new List<HashSet<string>?>();
        var actual = Analyze(cache, candidate, extractions);
        if (kind is "indexer" or "delegate-lambda")
        {
            Assert.Null(Assert.Single(extractions));
            Assert.Equal(candidate.Count, cache.SemanticFileCount);
        }
        else
        {
            // Removing the range prerequisite loses the previously resolved Run invocation.
            // Its bounded plan must still run before the cache's lost-resolution safeguard.
            var losesResolution = !creates && kind is "range-partial-length" or "range-base-length";
            Assert.Equal(losesResolution ? 2 : 1, extractions.Count);
            var paths = Assert.IsType<HashSet<string>>(extractions[0]);
            if (losesResolution) Assert.Null(extractions[1]);
            Assert.Contains("tests/Binding.cs", paths);
            Assert.Contains("tests/Uses.cs", paths); // Does not spell the implicitly bound member.
            Assert.Contains("tests/Named.cs", paths);
            var memberNames = kind switch
            {
                "foreach" => new[] { "GetEnumerator", "Current", "MoveNext" },
                "range-slice" => new[] { "Length", "Slice" },
                _ => new[] { "Length" }
            };
            var sources = Sources(candidate);
            foreach (var file in sources.Trees.Select(tree => SourceReaderFileFacts.Describe(sources, tree))
                         .Where(file => file.ReferencedNames.Intersect(memberNames, StringComparer.Ordinal).Any()))
                Assert.Contains(file.Path, paths);
            Assert.True(paths.Count < candidate.Count);
            Assert.Equal(paths.Count + (losesResolution ? candidate.Count : 0), cache.SemanticFileCount);
        }
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(creates, actual.ReaderOwners.Contains("UsesTests"));
        // The merged result must also be safe when loaded by another selection.
        var persisted = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(actual, Analyze(persisted, candidate));
        Assert.Equal(0, persisted.SemanticFileCount);

        string Binding(bool reads)
        {
            var type = reads ? "Reading" : "Plain";
            return kind switch
            {
                "indexer" => $"public class Bag {{ public {type} this[int index] => new {type}(); }}",
                "range-slice" => $"public class Bag {{ public int Length => 1; public {type} Slice(int start, int length) => new {type}(); }}",
                "range-partial-length" => "public partial class Bag { " + (reads ? "public int Length => 1;" : "") + " }",
                "range-base-length" => "public class BagBase { " + (reads ? "public int Length => 1;" : "") + " }",
                "foreach" => $$"""
                    public class Bag {
                        public Enumerator GetEnumerator() => new Enumerator();
                        public class Enumerator {
                            public {{type}} Current => new {{type}}();
                            public bool MoveNext() => false;
                        }
                    }
                    """,
                _ => $"public delegate void Callback({type} value);"
            };
        }
    }

    [Theory]
    [InlineData("InterpolatedStringHandler", true)]
    [InlineData("InterpolatedStringHandler", false)]
    [InlineData("CollectionBuilder", true)]
    [InlineData("CollectionBuilder", false)]
    [InlineData("AsyncMethodBuilder", true)]
    [InlineData("AsyncMethodBuilder", false)]
    [InlineData("AsyncMethodBuilderAttribute", true)]
    [InlineData("AsyncMethodBuilderAttribute", false)]
    public void ImplicitAttributeChange_UsesFullAnalysis(string attribute, bool removes)
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline["tests/Spare0.cs"] = Declaration(removes);
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Spare0.cs"] = Declaration(!removes)
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(Analyze(new(null), candidate), Analyze(cache, candidate));
        Assert.Equal(candidate.Count, cache.SemanticFileCount);

        string Declaration(bool attributed) => (attributed
            ? $"[System.Runtime.CompilerServices.{attribute}" +
                (attribute == "CollectionBuilder" ? "(typeof(Spare0), \"Unique0\")" :
                    attribute.StartsWith("AsyncMethodBuilder", StringComparison.Ordinal) ? "(typeof(Spare0))" : "") + "] "
            : "") + "public class Spare0 { public void Unique0() {} }";
    }

    [Fact]
    public void ChangedBuilder_WithUnchangedCollectionAttribute_UsesFullAnalysis()
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Collection.cs", """
            [System.Runtime.CompilerServices.CollectionBuilder(typeof(Builders), nameof(Builders.Create))]
            public class Bag { public int Value; }
            """);
        baseline.Add("tests/Builders.cs", """
            public static class Builders {
                public static Bag Create(System.ReadOnlySpan<int> values) => new Bag();
            }
            """);
        Analyze(new(storage.Path), baseline);
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Builders.cs"] = """
                public static class Builders {
                    public static Bag Create(System.ReadOnlySpan<int> values) => new Bag { Value = 1 };
                }
                """
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(Analyze(new(null), candidate), Analyze(cache, candidate));
        Assert.Equal(candidate.Count, cache.SemanticFileCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstantValueChange_InvalidatesCallersThroughUnchangedConstants(bool creates)
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Consts.cs", Constants(!creates));
        baseline.Add("tests/Mid.cs", "public class Mid { public const int X = Consts.Value; }");
        baseline.Add("tests/Calls.cs", """
            public static class Calls {
                public static void Read(byte value) {}
                public static void Read(long value) { RepositorySourceSnapshotCache.Get("root"); }
            }
            """);
        baseline.Add("tests/Uses.cs", "public class UsesTests { public void Act() { Calls.Read(Mid.X); } }");
        var before = Analyze(new(storage.Path), baseline);
        Assert.Equal(!creates, before.ReaderOwners.Contains("UsesTests"));
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Consts.cs"] = Constants(creates)
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(creates, actual.ReaderOwners.Contains("UsesTests"));
        Assert.Equal(3, cache.SemanticFileCount); // Consts, Mid and the indirectly affected Uses.
        var persisted = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(actual, Analyze(persisted, candidate));
        Assert.Equal(0, persisted.SemanticFileCount);

        static string Constants(bool reads) =>
            $"public class Consts {{ public const int Value = {(reads ? 1000 : 1)}; }}";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NestedTypeChange_InvalidatesCallersThroughUnchangedSignatures(bool creates)
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Root.cs", Root(!creates));
        baseline.Add("tests/Bag.cs", "public class Bag {} ");
        baseline.Add("tests/Data.cs", "public class Data : Root { public static Bag Make() => null; }");
        baseline.Add("tests/Calls.cs", """
            public static class Calls {
                public static void Read(Bag value) {}
                public static void Read(object value) { RepositorySourceSnapshotCache.Get("root"); }
            }
            """);
        baseline.Add("tests/Uses.cs", "public class UsesTests { public void Act() { Calls.Read(Data.Make()); } }");
        var before = Analyze(new(storage.Path), baseline);
        Assert.Equal(!creates, before.ReaderOwners.Contains("UsesTests"));
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Root.cs"] = Root(creates)
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(creates, actual.ReaderOwners.Contains("UsesTests"));
        Assert.Equal(5, cache.SemanticFileCount); // Root, Bag, Data, Calls and the indirectly affected Uses.
        var persisted = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(actual, Analyze(persisted, candidate));
        Assert.Equal(0, persisted.SemanticFileCount);

        static string Root(bool nested) => nested
            ? "public class Root { public class Bag {} }"
            : "public class Root {}";
    }

    [Theory]
    [InlineData("constructor", true)]
    [InlineData("constructor", false)]
    [InlineData("primary-constructor", true)]
    [InlineData("primary-constructor", false)]
    [InlineData("constructor-attribute", true)]
    [InlineData("constructor-attribute", false)]
    [InlineData("primary-constructor-attribute", true)]
    [InlineData("primary-constructor-attribute", false)]
    [InlineData("required-property", true)]
    [InlineData("required-property", false)]
    [InlineData("required-field", true)]
    [InlineData("required-field", false)]
    public void ConstructorConstraintChange_InvalidatesIndirectCallers(string kind, bool creates)
    {
        using var storage = new Storage();
        var baseline = Base();
        baseline.Add("tests/Bag.cs", Bag(!creates));
        baseline.Add("tests/Factory.cs", "public static class Factory { public static Bag Make() => null; }");
        baseline.Add("tests/Pick.cs", """
            public static class Pick {
                public static Reading From<T>(T value) where T : new() => null;
                public static Plain From(object value) => null;
            }
            """);
        baseline.Add("tests/Plain.cs", "public class Plain { public void Run() {} }");
        baseline.Add("tests/Reading.cs", Reader("Reading", "Run"));
        baseline.Add("tests/Uses.cs", "public class UsesTests { public void Act() { Pick.From(Factory.Make()).Run(); } }");
        var before = Analyze(new(storage.Path), baseline);
        Assert.Equal(!creates, before.ReaderOwners.Contains("UsesTests"));
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Bag.cs"] = Bag(creates)
        };
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var actual = Analyze(cache, candidate);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(creates, actual.ReaderOwners.Contains("UsesTests"));
        Assert.Equal(3, cache.SemanticFileCount); // Bag, Factory and Uses; other facts remain reusable.
        var persisted = new SourceReaderAnalysisCache(storage.Path);
        AssertEquivalent(actual, Analyze(persisted, candidate));
        Assert.Equal(0, persisted.SemanticFileCount);

        string Bag(bool eligible) => kind switch
        {
            "constructor" => "public class Bag { public Bag(int value) {} " +
                (eligible ? "public Bag() {}" : "") + " }",
            "primary-constructor" => eligible ? "public class Bag() {}" : "public class Bag(int value) {}",
            "constructor-attribute" => "public class Bag { public required int Value; " +
                (eligible ? "[System.Diagnostics.CodeAnalysis.SetsRequiredMembers] " : "") + "public Bag() {} }",
            "primary-constructor-attribute" =>
                (eligible ? "[method: System.Diagnostics.CodeAnalysis.SetsRequiredMembers] " : "") +
                "public class Bag() { public required int Value; }",
            "required-property" => "public class Bag { public " + (eligible ? "" : "required ") +
                "int Value { get; init; } }",
            "required-field" => "public class Bag { public " + (eligible ? "" : "required ") + "int Value; }",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static Dictionary<string, string> Base()
    {
        var files = new Dictionary<string, string>
        {
            ["tests/Snapshot.cs"] = Snapshot,
            ["tests/Reader.cs"] = Reader("FirstTests", "Reads")
        };
        for (var index = 0; index < 8; index++)
            files.Add($"tests/Spare{index}.cs", $"public class Spare{index} {{ public void Unique{index}() {{}} }}");
        return files;
    }

    private static string Reader(string owner, string member) =>
        $"public class {owner} {{ public void {member}() {{ RepositorySourceSnapshotCache.Get(\"root\"); }} }}";

    private static string Leaf(string kind, bool reads)
    {
        // Keep synthetic launches out of the raw-source direct-git-launch guard.
        const string executable = "git";
        var body = !reads ? "" : kind == "git" ? $"System.Diagnostics.Process.Start(\"{executable}\", arg);"
            : "System.IO.Directory.GetFiles(arg, \"*\", System.IO.SearchOption.AllDirectories);";
        return $"public static class Leaf {{ public static void Run(string arg) {{ {body} }} }}";
    }

    private static ImpactReplayTestSourceIndex Sources(IReadOnlyDictionary<string, string> files) =>
        new(files.Select(pair => (SyntaxTree)CSharpSyntaxTree.ParseText(pair.Value, path: pair.Key)));

    private static SourceReaderAnalysisCache.Analysis Analyze(SourceReaderAnalysisCache cache,
        IReadOnlyDictionary<string, string> files, List<HashSet<string>?>? extractions = null) =>
        cache.GetOrAnalyze(Sources(files), (sources, paths) =>
        {
            extractions?.Add(paths is null ? null : new(paths, StringComparer.Ordinal));
            return SourceReadingGuardTestClasses.Extract(sources, paths);
        }, SourceReadingGuardTestClasses.Evaluate);

    private static void AssertEquivalent(SourceReaderAnalysisCache.Analysis expected, SourceReaderAnalysisCache.Analysis actual)
    {
        Assert.Equal(expected.DeclaresSnapshotCache, actual.DeclaresSnapshotCache);
        Assert.Equal(expected.ReaderOwners, actual.ReaderOwners);
    }

    private sealed class Storage : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("source-reader-incremental-");
        internal string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
