public sealed class DependencyIndexNamespaceCycleTests
{
    [Fact]
    public void Counts_each_direction_once_and_ignores_self_edges_and_external_assemblies()
    {
        var foreign = DependencyIndexFixtureAssembly.Compile(
            "namespace External { public sealed class OnlyInForeignAssembly { } }", "ForeignCycleFixture");
        var image = DependencyIndexFixtureAssembly.Compile("""
            namespace A
            {
                public sealed class Root
                {
                    public Root? Self;
                    public B.Second? Next;
                    public C.Third? Other;
                    public External.OnlyInForeignAssembly? External;
                }
            }
            namespace B
            {
                public sealed class Second { public A.Root? Next; public C.Third? Other; }
            }
            namespace C
            {
                public sealed class Third { public A.Root? Next; public B.Second? Other; }
            }
            """, "CycleFixture", foreign);
        var index = DependencyIndexCache.Process.GetOrBuildImages(image, foreign);

        Assert.Equal(new[] { "A -> B", "A -> B -> C", "A -> C", "A -> C -> B", "B -> C" },
            index.NamespaceCycles("CycleFixture").Select(cycle => string.Join(" -> ", cycle)));
        Assert.Empty(index.NamespaceCycles("ForeignCycleFixture"));
    }
}
