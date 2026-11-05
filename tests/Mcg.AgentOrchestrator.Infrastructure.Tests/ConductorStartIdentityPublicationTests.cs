using System.Diagnostics;

// Parallel-safe: unique fixture roots/events, allow-listed child environment,
// and retained, identity-validated conductor handles; no shared process state.
public sealed class ConductorStartIdentityPublicationTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(Skip = "Requires Windows caller processes.", SkipUnless = nameof(IsWindows))]
    [InlineData(23, true)]
    [InlineData(0, true)]
    [InlineData(23, false)]
    public void Cleanup_MissingIdentity_ReportsCallerFailureAndKeepsRoot(int exitCode, bool publishReceipt)
    {
        var removalReached = false;
        var releaseReached = false;
        // This controlled caller never launches a child. Its exit is the signal
        // that output and the optional receipt are final; no publication can follow.
        using var fixture = new ConductorStartHandleIsolationTests.LaunchFixture(
            beforeRootRemoval: _ => removalReached = true,
            beforeConductorRelease: (_, _) => releaseReached = true,
            configureCallerStart: start =>
            {
                var receipt = publishReceipt ? "echo controlled-launch-receipt>launch-receipt.json& " : "";
                // Override the usual merged pipe so both captured streams carry
                // distinct evidence through the same fixture diagnostic path.
                start.Arguments = "/d /s /c \"echo controlled-caller-stdout& " +
                    "echo controlled-caller-stderr 1>&2& " + receipt + $"exit /b {exitCode}\"";
            });
        try
        {
            Assert.True(fixture.Caller.WaitForExit(60000), "Controlled caller did not exit.");
            Assert.Equal(exitCode, fixture.Caller.ExitCode);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "launch-identity.json")));
            Assert.Equal(publishReceipt, File.Exists(Path.Combine(fixture.Root, "launch-receipt.json")));

            var failure = Assert.Throws<AggregateException>(() => fixture.Dispose());
            var diagnostic = Assert.IsType<InvalidOperationException>(Assert.Single(failure.InnerExceptions));
            Assert.Contains(Path.Combine(fixture.Root, "launch-identity.json"), diagnostic.Message);
            Assert.Contains($"Caller exit code: {exitCode}", diagnostic.Message);
            Assert.Contains("Caller stdout: controlled-caller-stdout", diagnostic.Message);
            Assert.Contains("Caller stderr: controlled-caller-stderr", diagnostic.Message);
            Assert.Contains(Path.Combine(fixture.Root, "launch-receipt.json"), diagnostic.Message);
            Assert.Contains(publishReceipt ? "controlled-launch-receipt" : "unavailable:", diagnostic.Message);
            Assert.False(releaseReached);
            Assert.False(removalReached);
            Assert.True(Directory.Exists(fixture.Root));
            if (publishReceipt)
                Assert.Equal("controlled-launch-receipt" + Environment.NewLine,
                    File.ReadAllText(Path.Combine(fixture.Root, "launch-receipt.json")));
        }
        finally
        {
            // The controlled caller starts no descendants; disposal closed its
            // handles. Remove the retained diagnostic root only after assertions.
            fixture.Dispose();
            if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact(Skip = "Requires Windows conductor ownership.", SkipUnless = nameof(IsWindows))]
    public void Cleanup_PublishedIdentity_ReleasesConductorAndRemovesRoot()
    {
        Process? conductor = null;
        var releaseReached = false;
        var removalReached = false;
        using var fixture = new ConductorStartHandleIsolationTests.LaunchFixture(
            beforeConductorRelease: (_, owned) =>
            {
                Assert.NotNull(conductor);
                Assert.Equal(conductor.Id, owned.Id);
                Assert.False(owned.HasExited);
                releaseReached = true;
            },
            beforeRootRemoval: removing =>
            {
                Assert.True(releaseReached);
                Assert.NotNull(conductor);
                Assert.True(conductor.HasExited);
                Assert.Equal(0, conductor.ExitCode);
                Assert.True(Directory.Exists(removing.Root));
                removalReached = true;
            });
        try
        {
            Assert.True(fixture.Caller.WaitForExit(60000), "CLI caller did not exit.");
            Assert.Equal(0, fixture.Caller.ExitCode);
            Assert.True(fixture.Ready.WaitOne(0), "Caller exited without conductor readiness.");
            conductor = fixture.OpenConductor();
            Assert.False(conductor.HasExited);

            fixture.Dispose();

            Assert.True(releaseReached);
            Assert.True(removalReached);
            Assert.True(conductor.HasExited);
            Assert.Equal(0, conductor.ExitCode);
            Assert.False(Directory.Exists(fixture.Root));
        }
        finally
        {
            fixture.Dispose();
            conductor?.Dispose();
        }
    }
}
