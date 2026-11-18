using Mcg.AgentOrchestrator.Infrastructure;

// Private roots and process-wide override mutations run in the serial build-slot collection.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsBaselineAdoption(IsolatedDotnetRootFixture fixture)
{
    [Xunit.Fact]
    public void CollectionFixture_WithAssemblyBaseline_BorrowsTheSameRoot()
    {
        var baseline = RequireBaseline();
        AssertSamePath(baseline, fixture.RootPath);
        var original = ReadOverride();
        using (var holder = new IsolatedDotnetRootFixture())
        {
            AssertSamePath(baseline, holder.RootPath);
            Xunit.Assert.Equal(original, ReadOverride());
        }
        Xunit.Assert.Equal(original, ReadOverride());
    }

    [Xunit.Fact]
    public void SharedFactory_LastRelease_PreservesBaselineAndOverride()
    {
        var baseline = RequireBaseline();
        var original = ReadOverride();
        var shared = new SharedIsolatedDotnetRoot(IsolatedDotnetRootFixture.CreateProcessSharedOwner);
        Xunit.Assert.Equal(0, shared.Holders);
        using (var holder = new IsolatedDotnetRootFixture(shared))
        {
            AssertSamePath(baseline, holder.RootPath);
            Xunit.Assert.Equal(1, shared.Holders);
            Xunit.Assert.Equal(original, ReadOverride());
        }
        Xunit.Assert.Equal(0, shared.Holders);
        Xunit.Assert.Equal(original, ReadOverride());
        Xunit.Assert.True(Directory.Exists(baseline));

        using (var holder = new IsolatedDotnetRootFixture(shared))
        {
            AssertSamePath(baseline, holder.RootPath);
            Xunit.Assert.Equal(original, ReadOverride());
        }
        Xunit.Assert.Equal(0, shared.Holders);
        Xunit.Assert.Equal(original, ReadOverride());
        Xunit.Assert.True(Directory.Exists(baseline));
    }

    [Xunit.Fact]
    public void AdoptedFixture_Dispose_PreservesClaimContentsAndHeldOwnerLock()
    {
        var basePath = Directory.CreateTempSubdirectory("baseline-adoption-").FullName;
        var original = ReadOverride();
        try
        {
            using var owner = new IsolatedDotnetRootFixture(basePath, null);
            var root = owner.RootPath;
            var ownerLock = root + IsolatedDotnetRootFixture.OwnerLockSuffix;
            var sentinel = Path.Combine(root, "sentinel.txt");
            File.WriteAllText(sentinel, "owned baseline");
            Xunit.Assert.True(File.Exists(ownerLock));

            using (var adopter = IsolatedDotnetRootFixture.AdoptBaseline(root))
            {
                Xunit.Assert.Equal(root, adopter.RootPath);
                Xunit.Assert.Equal(root, ReadOverride());
            }

            Xunit.Assert.Equal(root, ReadOverride());
            Xunit.Assert.True(Directory.Exists(root));
            Xunit.Assert.Equal("owned baseline", File.ReadAllText(sentinel));
            Xunit.Assert.True(File.Exists(ownerLock));
            Xunit.Assert.Throws<IOException>(() =>
            {
                using var competingClaim = new FileStream(
                    ownerLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, original);
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void AdoptedFixture_Dispose_LeavesTheAssemblyBaselineInPlace()
    {
        var baseline = RequireBaseline();
        var original = ReadOverride();
        Xunit.Assert.True(Directory.Exists(baseline));
        var ownerLock = baseline + IsolatedDotnetRootFixture.OwnerLockSuffix;
        var hadOwnerLock = File.Exists(ownerLock);
        using (var adopter = IsolatedDotnetRootFixture.AdoptBaseline(baseline))
        {
            Xunit.Assert.Equal(baseline, adopter.RootPath);
            Xunit.Assert.Equal(original, ReadOverride());
        }
        Xunit.Assert.Equal(original, ReadOverride());
        Xunit.Assert.True(Directory.Exists(baseline));
        Xunit.Assert.Equal(hadOwnerLock, File.Exists(ownerLock));
    }

    [Xunit.Theory]
    [Xunit.InlineData(typeof(IsolatedProcessSpawningCollection))]
    [Xunit.InlineData(typeof(DotnetBuildSlotsCollection))]
    [Xunit.InlineData(typeof(ProcessSpawningCollection))]
    public void CollectionDefinition_SharedRoot_UsesTheAdoptingFixture(Type collection)
    {
        Xunit.Assert.Contains(typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>), collection.GetInterfaces());
    }

    private static string RequireBaseline()
    {
        var baseline = AssemblyDotnetBuildStorageRootIsolation.BaselineRoot;
        Xunit.Assert.NotNull(baseline);
        return baseline;
    }

    private static void AssertSamePath(string expected, string actual) =>
        Xunit.Assert.True(string.Equals(
            Path.GetFullPath(expected).TrimEnd('\\', '/'),
            Path.GetFullPath(actual).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private static string? ReadOverride() =>
        Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
}
