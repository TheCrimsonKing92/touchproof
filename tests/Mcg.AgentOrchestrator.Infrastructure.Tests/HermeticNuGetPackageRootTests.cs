using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only local dictionaries change; no process environment is mutated.
public sealed class HermeticNuGetPackageRootTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Configure_InheritedRoot_IsReplacedFromResolvedPackages(
        bool inheritPackages, bool inheritRoot)
    {
        var packages = Path.Combine(Path.GetTempPath(), "configured-packages");
        var decoy = Path.Combine(Path.GetTempPath(), "decoy-packages") + Path.DirectorySeparatorChar;
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = "inherited-profile",
            ["HOME"] = "inherited-home"
        };
        if (inheritPackages)
            environment["NUGET_PACKAGES"] = packages;
        if (inheritRoot)
            environment["NuGetPackageRoot"] = decoy;

        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
            environment, Path.GetTempPath());

        var resolvedPackages = environment["NUGET_PACKAGES"];
        Assert.False(string.IsNullOrWhiteSpace(resolvedPackages));
        if (inheritPackages)
            Assert.Equal(packages, resolvedPackages);
        var expectedRoot = resolvedPackages!.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.Equal(expectedRoot, environment["NuGetPackageRoot"]);
        Assert.NotEqual(decoy, environment["NuGetPackageRoot"]);
        var expectedProfile = Path.Combine(Path.GetTempPath(), "mcg-hvp");
        Assert.Equal(expectedProfile, environment["USERPROFILE"]);
        Assert.Equal(expectedProfile, environment["HOME"]);
        Assert.True(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(
            "nugetpackageroot"));

        // Nested gate construction must recompute the same value rather than inherit a root override.
        environment["NuGetPackageRoot"] = decoy;
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
            environment, Path.GetTempPath());
        Assert.Equal(resolvedPackages, environment["NUGET_PACKAGES"]);
        Assert.Equal(expectedRoot, environment["NuGetPackageRoot"]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Configure_TrailingSeparators_NormalizesOnlyPackageRoot(
        int separatorCount, bool alternateSeparator)
    {
        var packages = Path.Combine(Path.GetTempPath(), "configured-packages");
        var separator = alternateSeparator ? Path.AltDirectorySeparatorChar : Path.DirectorySeparatorChar;
        var inheritedPackages = packages + new string(separator, separatorCount);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = inheritedPackages
        };

        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
            environment, Path.GetTempPath());

        Assert.Equal(inheritedPackages, environment["NUGET_PACKAGES"]);
        Assert.Equal(packages + Path.DirectorySeparatorChar, environment["NuGetPackageRoot"]);
    }
}
