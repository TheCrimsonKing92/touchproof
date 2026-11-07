using System.Reflection;

// Parallel safe: inspects effective xUnit metadata and creates only in-memory attributes.
public sealed class LandingGitRunnerCollectionParallelismTests
{
    [Xunit.Fact]
    public void LandingGitRunnerDefinitionsAllowParallelization()
    {
        var definitions = typeof(LandingGitRunnerCollectionParallelismTests).Assembly.GetTypes()
            .Select(type => type.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>())
            .OfType<Xunit.CollectionDefinitionAttribute>();

        Xunit.Assert.True(DefinitionsAllowParallelization(definitions),
            "LandingGitRunner must not disable parallelization if its definition is restored.");
    }

    [Xunit.Fact]
    public void RunnableClassesDoNotShareLegacyCollection()
    {
        // Removing a definition alone leaves an ad-hoc collection whose members still run serially.
        var runnableTypes = typeof(LandingGitRunnerCollectionParallelismTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
            .Where(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic)
                .Any(method => method.GetCustomAttributes(inherit: true)
                    .Any(attribute => attribute is Xunit.FactAttribute)));

        Xunit.Assert.DoesNotContain(runnableTypes, type =>
            type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name ==
                TestCollections.LandingGitRunner);
    }

    [Xunit.Fact]
    public void GuardRejectsSerialLegacyDefinitionAndAllowsRetirement()
    {
        Xunit.Assert.False(DefinitionsAllowParallelization(
            [new(TestCollections.LandingGitRunner) { DisableParallelization = true }]));
        Xunit.Assert.True(DefinitionsAllowParallelization(
            [new(TestCollections.LandingGitRunner) { DisableParallelization = false }]));
        Xunit.Assert.True(DefinitionsAllowParallelization([]));
        Xunit.Assert.True(DefinitionsAllowParallelization(
            [new("UnrelatedCollection") { DisableParallelization = true }]));
    }

    private static bool DefinitionsAllowParallelization(
        IEnumerable<Xunit.CollectionDefinitionAttribute> definitions) =>
        definitions.All(definition => definition.Name != TestCollections.LandingGitRunner ||
            !definition.DisableParallelization);
}
