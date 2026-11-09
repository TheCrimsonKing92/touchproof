using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBuildLandingFixtureLocks
{
    internal const string LandingTestsRootDirectoryName = DotnetBuildStorageLayout.LandingTestsRootDirectoryName;
    private const string LandingTestFixtureMarkerFileName = ".mcg-landing-fixture.json";
    private static readonly TimeSpan LandingFixtureMarkerStaleAge = TimeSpan.FromHours(2);
    private static readonly object CurrentLandingFixtureRootsGate = new();
    private static readonly HashSet<string> CurrentLandingFixtureRoots = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryCreateCurrentLandingFixtureAttribution(
        string path,
        out BuildLockAttribution attribution)
    {
        attribution = null!;
        if (!TryGetLandingTestFixtureRoot(path, out var fixtureRoot))
        {
            return false;
        }

        if (TryReadLandingTestFixtureMarker(fixtureRoot) is { } marker)
        {
            if (IsLandingFixtureMarkerStale(marker))
            {
                return false;
            }

            attribution = new BuildLockAttribution(
                path,
                [CreateLandingFixtureMarkerHolder(marker)],
                "landing-fixture-marker",
                "artifact-prep",
                "prepare-artifacts");
            return true;
        }

        lock (CurrentLandingFixtureRootsGate)
        {
            if (!CurrentLandingFixtureRoots.Contains(fixtureRoot))
            {
                return false;
            }
        }

        attribution = new BuildLockAttribution(
            path,
            [DotnetBuildLeaseInspection.CreateProcessHolder(Environment.ProcessId, "current landing fixture root", false)],
            "landing-fixture-registration",
            "artifact-prep",
            "prepare-artifacts");
        return true;
    }

    private static BuildLockHolder CreateLandingFixtureMarkerHolder(LandingTestFixtureMarker marker) =>
        new(
            marker.CreatorProcessId,
            "landing-test-fixture",
            $"landing fixture purpose={marker.Purpose} machine={marker.MachineName} createdAt={marker.CreatedAt:O}",
            false);

    internal static void RegisterCurrentLandingTestFixtureRoot(string path)
    {
        if (!TryGetLandingTestFixtureRoot(path, out var fixtureRoot))
        {
            return;
        }

        lock (CurrentLandingFixtureRootsGate)
        {
            CurrentLandingFixtureRoots.Add(fixtureRoot);
        }
    }

    internal static void ClearCurrentLandingTestFixtureRootsForTests()
    {
        lock (CurrentLandingFixtureRootsGate)
        {
            CurrentLandingFixtureRoots.Clear();
        }
    }

    internal static LandingFixtureLockDisposition GetLandingFixtureDisposition(string path)
    {
        if (!TryGetLandingTestFixtureRoot(path, out var fixtureRoot))
        {
            return LandingFixtureLockDisposition.None;
        }

        if (TryReadLandingTestFixtureMarker(fixtureRoot) is { } marker)
        {
            if (IsLandingFixtureMarkerStale(marker))
            {
                return LandingFixtureLockDisposition.StaleDebris;
            }

            return LandingFixtureLockDisposition.CurrentTransient;
        }

        lock (CurrentLandingFixtureRootsGate)
        {
            return CurrentLandingFixtureRoots.Contains(fixtureRoot)
                ? LandingFixtureLockDisposition.CurrentTransient
                : LandingFixtureLockDisposition.None;
        }
    }

    internal static void WriteLandingTestFixtureMarkerForTests(string fixtureRoot, int? ownerProcessId = null, DateTimeOffset? createdAt = null)
    {
        WriteLandingTestFixtureMarker(fixtureRoot, "landing-test-fixture", ownerProcessId, createdAt);
    }

    private static void WriteLandingTestFixtureMarker(
        string fixtureRoot,
        string purpose,
        int? ownerProcessId = null,
        DateTimeOffset? createdAt = null)
    {
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            var marker = new LandingTestFixtureMarker(
                1,
                purpose,
                ownerProcessId ?? Environment.ProcessId,
                Environment.MachineName,
                createdAt ?? DateTimeOffset.UtcNow);
            File.WriteAllText(
                Path.Combine(fixtureRoot, LandingTestFixtureMarkerFileName),
                JsonSerializer.Serialize(marker, DotnetBuildEnvironmentManager.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static LandingTestFixtureMarker? TryReadLandingTestFixtureMarker(string fixtureRoot)
    {
        try
        {
            var markerPath = Path.Combine(fixtureRoot, LandingTestFixtureMarkerFileName);
            return File.Exists(markerPath)
                ? JsonSerializer.Deserialize<LandingTestFixtureMarker>(File.ReadAllText(markerPath), DotnetBuildEnvironmentManager.JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsLandingFixtureMarkerStale(LandingTestFixtureMarker marker) =>
        DateTimeOffset.UtcNow - marker.CreatedAt >= LandingFixtureMarkerStaleAge &&
        !DotnetBuildLeaseInspection.IsProcessRunning(marker.CreatorProcessId);

    private static bool TryGetLandingTestFixtureRoot(string path, out string fixtureRoot)
    {
        fixtureRoot = string.Empty;
        try
        {
            var landingTestsRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), LandingTestsRootDirectoryName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!DotnetBuildLeaseInspection.PathIsUnderDirectory(fullPath, landingTestsRoot))
            {
                return false;
            }

            var relative = Path.GetRelativePath(landingTestsRoot, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative) ||
                string.IsNullOrWhiteSpace(relative) ||
                relative.Equals(".", StringComparison.Ordinal))
            {
                return false;
            }

            var firstSeparator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var fixtureName = firstSeparator < 0 ? relative : relative[..firstSeparator];
            if (string.IsNullOrWhiteSpace(fixtureName))
            {
                return false;
            }

            fixtureRoot = Path.Combine(landingTestsRoot, fixtureName);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record LandingTestFixtureMarker(
        int Version,
        string Purpose,
        int CreatorProcessId,
        string MachineName,
        DateTimeOffset CreatedAt);

    internal enum LandingFixtureLockDisposition
    {
        None,
        CurrentTransient,
        StaleDebris
    }
}
