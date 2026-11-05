using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-local policy, candidate, database, compiler processes and slot grid.
public sealed class WorkerBuildRequestStructuralFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultHostEvaluatorIncludesUncommittedLaneDeclarations(bool untracked)
    {
        const string relative = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Collision.cs";
        const string testProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixture.csproj";
        const string name = "WorkerBuildRequestCandidateCollisionTests";
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            fakeCompilerProjects: [WorkerBuildRequestFixture.Project, testProject],
            seedRepository: root =>
            {
                // Scope admission requires an owner for every changed compiled source.
                Write(root, testProject,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                Write(root, "config/acceptance-manifest.json", """
                    { "version": 1, "engine": {
                      "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
                      "infrastructureTestLanes": [
                      { "name": "Dotnet build slots", "filter": "FullyQualifiedName~ConductorBatchLoopTests", "ownedCollections": ["DotnetBuildSlots"] },
                      { "name": "Balance", "filter": "FullyQualifiedName~WorkerBuildRequest" },
                      { "name": "Remainder", "filter": "FullyQualifiedName!~WorkerBuildRequest&FullyQualifiedName!~ConductorBatchLoopTests" }
                    ] } }
                    """);
                Write(root, TestCollectionGuardRules.ExemptionsRelativePath, "[]");
                if (!untracked) Write(root, relative, "public class HarborBaselineFixture { [Xunit.Fact] public void Case() { } }");
            });
        Write(fixture.RepositoryRoot, relative, $"public class {name} {{ [Xunit.Fact] public void Case() {{ }} }}");
        var invocation = await fixture.RunHelper(); // Exercise the production project-home/baseline binding.
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(0, invocation.CompilerExitCode);
        Assert.Equal(new[] { WorkerBuildRequestFixture.Project, testProject }, invocation.Projects);
        Assert.Equal(WorkerBuildStructuralStatus.Violation, invocation.StructuralCheck!.Status);
        Assert.Contains("[lane-substring-collision]", invocation.StructuralCheck.Diagnostic);
        Assert.Contains(name, invocation.StructuralCheck.Diagnostic);
        Assert.Contains(relative, invocation.StructuralCheck.Diagnostic);
        Assert.Equal(1, Assert.Single(fixture.Responses).ExitCode);
        Assert.True(invocation.CustodyReleased);
        fixture.AssertSlotsFree();
    }

    [Fact]
    public async Task DefaultHostEvaluatorTreatsMissingGuardedSourceAsViolation()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            seedRepository: root =>
            {
                WorkerBuildRequestFixture.SeedSizePolicy(root);
                File.AppendAllText(Path.Combine(root, SourceSizeRatchet.SourcePath),
                    "new SourceSizeCeiling(\"renamed-or-deleted.cs\", 10)\n");
            });
        var invocation = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(WorkerBuildStructuralStatus.Violation, invocation.StructuralCheck!.Status);
        Assert.Contains("renamed-or-deleted.cs", invocation.StructuralCheck.Diagnostic);
        Assert.Contains("does not exist", invocation.StructuralCheck.Diagnostic);
        Assert.Contains("update SourceSizeRatchet.SeededCeilings", invocation.StructuralCheck.Diagnostic);
        Assert.DoesNotContain("host/operator owns recovery", invocation.StructuralCheck.Diagnostic);
        Assert.Equal(1, Assert.Single(fixture.Responses).ExitCode);
        Assert.True(invocation.CustodyReleased);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityParserIgnoresCommentsButRejectsUnsupportedRows(bool unsupported)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            seedRepository: root =>
            {
                WorkerBuildRequestFixture.SeedSizePolicy(root);
                File.WriteAllText(Path.Combine(root, SourceSizeRatchet.SourcePath),
                    $"new SourceSizeCeiling(\"{WorkerBuildRequestFixture.Source}\", 100);\n" +
                    (unsupported ? "new SourceClassCeiling(\"Feature\", UnknownLimit, 1);"
                        : "// new SourceSizeCeiling(\"ignored.cs\", 1)\n// new SourceClassCeiling(\"Ignored\", 1, 1)"));
            });
        var invocation = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(unsupported ? WorkerBuildStructuralStatus.Unavailable : WorkerBuildStructuralStatus.Passed,
            invocation.StructuralCheck!.Status);
        Assert.Equal(unsupported ? 1 : 0, Assert.Single(fixture.Responses).ExitCode);
    }

    [Fact]
    public void IncompleteMeasurementsKeepKnownSourceViolationsActionable()
    {
        using var fixture = new WorkerBuildRequestFixture(seedRepository: root =>
        {
            WorkerBuildRequestFixture.SeedSizePolicy(root);
            File.WriteAllText(Path.Combine(root, WorkerBuildRequestFixture.Source), "public class Feature { }\n// over ceiling");
            Write(root, "src/Locked.cs", "// measurement unavailable");
            File.AppendAllText(Path.Combine(root, SourceSizeRatchet.SourcePath), "new SourceSizeCeiling(\"src/Locked.cs\", 1)\n");
        });
        using var locked = new FileStream(Path.Combine(fixture.RepositoryRoot, "src/Locked.cs"), FileMode.Open, FileAccess.Read, FileShare.None);
        var findings = DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
        Assert.False(findings.EvaluationComplete);
        Assert.True(findings.HasViolation);
        Assert.Contains("ceiling of 1", findings.UnavailableDiagnostic);
        Assert.Contains(WorkerBuildRequestFixture.Source, findings.UnavailableDiagnostic);
        Assert.Contains("src/Locked.cs", findings.UnavailableDiagnostic);
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("skipped")]
    [InlineData("malformed")]
    [InlineData("cancelled")]
    public async Task UnavailableEvaluationPreservesCompilerSuccessWithoutSourceRetry(string mode)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var reached = false;
        var capacityReleasedDuringScan = false;
        var invocation = await fixture.RunHelper(structuralEvaluator: () =>
        {
            reached = true;
            fixture.AssertSlotsFree();
            capacityReleasedDuringScan = true;
            return mode switch
            {
                "unreadable" => throw new UnauthorizedAccessException("fixture policy unreadable"),
                "cancelled" => throw new OperationCanceledException("fixture scan cancelled"),
                "malformed" => null!,
                _ => new(false, "fixture scan skipped", EvaluationComplete: false)
            };
        });
        Assert.True(reached);
        Assert.True(capacityReleasedDuringScan);
        AssertUnavailable(fixture, invocation);
        Assert.Contains("host/operator owns recovery", invocation.StructuralCheck!.Diagnostic);
        Assert.Contains("Do not edit unrelated source or repeatedly rebuild", invocation.StructuralCheck.Diagnostic);
        var consumption = fixture.Consume();
        Assert.False(consumption.CompilerFailed);
        Assert.False(consumption.MissingEvidence);
        Assert.Equal(WorkTaskStatus.Running, fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).Status);
        Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
    }

    [Fact]
    public async Task TimeoutUsesWorkerBoundAndLateWorkCannotPublishPass()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Time.TimerScheduled = due => { if (due == WorkerBuildStructuralPreflight.EvaluationBound) timer.TrySetResult(); };
        var running = fixture.RunHelper(structuralEvaluator: () =>
        {
            entered.SetResult();
            try
            {
                Assert.True(release.Wait(TimeSpan.FromMinutes(3)), "Structural scan release event did not arrive.");
                return new(false, "late pass");
            }
            finally { finished.SetResult(); }
        });
        try
        {
            await Task.WhenAll(entered.Task, timer.Task).WaitAsync(TimeSpan.FromMinutes(3));
            fixture.AssertSlotsFree();
            fixture.Time.Advance(WorkerBuildStructuralPreflight.EvaluationBound);
            var invocation = await running.WaitAsync(TimeSpan.FromMinutes(3));
            AssertUnavailable(fixture, invocation);
            Assert.Contains("exceeded 30000 ms bound", invocation.StructuralCheck!.Diagnostic);
            var version = invocation.Version;
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
            var stored = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
            Assert.Equal(version, stored.Version);
            Assert.Equal(invocation.StructuralCheck, stored.StructuralCheck);
        }
        finally
        {
            release.Set();
            await running.WaitAsync(TimeSpan.FromMinutes(3));
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
        }
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("class-authority")]
    [InlineData("manifest")]
    public async Task CorruptRequiredPolicyIsUnavailable(string kind)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            seedRepository: root =>
            {
                if (kind == "manifest") Write(root, "config/acceptance-manifest.json", "{ invalid json");
                else
                {
                    WorkerBuildRequestFixture.SeedSizePolicy(root);
                    var authority = Path.Combine(root, SourceSizeRatchet.SourcePath);
                    File.WriteAllText(authority, kind == "authority" ? "invalid authority"
                        : $"new SourceSizeCeiling(\"{WorkerBuildRequestFixture.Source}\", 100)\nnew SourceClassCeiling(\"Feature\", broken, 1)");
                }
            });
        var reached = false;
        var invocation = await fixture.RunHelper(structuralEvaluator: () =>
        {
            reached = true;
            return DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
        });
        Assert.True(reached);
        AssertUnavailable(fixture, invocation);
    }

    [Theory]
    [InlineData(false, "isolated-collection")]
    [InlineData(true, "lane-membership")]
    public async Task CanonicalCollectionAndLaneFindingsReachAdapter(bool isolated, string rule)
    {
        const string relative = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GuardFixture.cs";
        var name = isolated ? "ConductorLegacyHolderAdmissionFixture" : "ConductorBatchLoopTestsMisfiledFixture";
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake, seedRepository: root =>
        {
            Write(root, "config/acceptance-manifest.json", """
                { "version": 1, "engine": {
                  "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
                  "infrastructureTestLanes": [
                  { "name": "Dotnet build slots", "filter": "FullyQualifiedName~ConductorBatchLoopTests", "ownedCollections": ["DotnetBuildSlots"] },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~ConductorBatchLoopTests" }
                ] } }
                """);
            Write(root, TestCollectionGuardRules.ExemptionsRelativePath, "[]");
            Write(root, relative, (isolated ? "[Xunit.Collection(\"DotnetBuildSlots\")] " : "") +
                $"public class {name} {{ [Xunit.Fact] public void Case() {{ }} }}");
        });
        var descriptor = Assert.Single(AcceptanceTestClassSourceScanner.Scan(fixture.RepositoryRoot));
        Assert.Equal(name, descriptor.FullName);
        var expected = DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
        Assert.True(expected.EvaluationComplete);
        Assert.True(expected.HasViolation);
        var reached = false;
        var invocation = await fixture.RunHelper(structuralEvaluator: () =>
        {
            reached = true;
            return DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
        });
        Assert.True(reached);
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(WorkerBuildStructuralStatus.Violation, invocation.StructuralCheck!.Status);
        Assert.Equal(expected.Message, invocation.StructuralCheck.Diagnostic);
        Assert.Contains(relative, expected.Message);
        Assert.Contains(name, expected.Message);
        Assert.Contains($"[{rule}]", expected.Message);
        Assert.Contains(expected.Message, File.ReadAllText(Path.Combine(fixture.Root, "worker.out.log")));
        Assert.Equal(1, Assert.Single(fixture.Responses).ExitCode);
        Assert.True(invocation.CustodyReleased);
    }

    [Fact]
    public async Task MissingRequiredPolicyDoesNotBecomeNonApplicable()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var reference = fixture.Reference with { StructuralPolicy = new(null, "main", true, false) };
        var host = new WorkerBuildRequestHost(reference, fixture.Time, storageRoot: fixture.Storage, launchOverride: fixture.LaunchOverride);
        var invocation = await fixture.RunHelper(service: host);
        AssertUnavailable(fixture, invocation);
        Assert.Contains("Required structural authority is missing", invocation.StructuralCheck!.Diagnostic);
    }

    [Fact]
    public async Task LegacyAndMalformedResponsesHaveExplicitUnavailableStatus()
    {
        var legacy = JsonSerializer.Deserialize<WorkerBuildResponse>("""
            {"InvocationId":"legacy", "Outcome":0, "Diagnostic":"compiler succeeded"}
            """)!;
        Assert.Equal("Unavailable", legacy.StructuralStatus);
        Assert.Equal(0, legacy.ExitCode);
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var invocation = await fixture.RunHelper(service: new MalformedResponseService(fixture.Host()));
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(0, invocation.CompilerExitCode);
        Assert.Equal("Unavailable", Assert.Single(fixture.Responses).StructuralStatus);
        Assert.Equal(1, fixture.Worker!.TryReadOwnedExitCode());
        Assert.Contains("Malformed structural evidence", File.ReadAllText(Path.Combine(fixture.Root, "worker.out.log")));
    }

    private sealed record MalformedResponseService(WorkerBuildRequestHost Host) : IWorkerBuildRequestService
    {
        public async Task<WorkerBuildResponse> ExecuteAsync(WorkerBuildRequest request, WorkerBuildProcess worker, CancellationToken lifetime)
        {
            var response = await Host.ExecuteAsync(request, worker, lifetime);
            return response with { StructuralCheck = response.StructuralCheck! with { InvocationId = "foreign-invocation" } };
        }
    }

    [Fact]
    public async Task ProjectHomePolicyTakesPrecedenceAndDriftInvalidatesEvidence()
    {
        var home = Path.Combine(Path.GetTempPath(), "mcg-structural-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, "acceptance-manifest.json"), "{}");
            using (var control = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
                projectHomeDirectory: home, seedRepository: root => Write(root, "config/acceptance-manifest.json", "{invalid")))
            {
                var accepted = await control.RunHelper();
                Assert.Equal(WorkerBuildStructuralStatus.Passed, accepted.StructuralCheck!.Status);
                Assert.Equal(home, accepted.StructuralCheck.ProjectHomeDirectory);
                Assert.Equal(0, Assert.Single(control.Responses).ExitCode);
            }
            using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
                projectHomeDirectory: home, seedRepository: root => Write(root, "config/acceptance-manifest.json", "{invalid"));
            var reached = false;
            var invocation = await fixture.RunHelper(structuralEvaluator: () =>
            {
                reached = true;
                var findings = DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot, home);
                Assert.True(findings.EvaluationComplete);
                Assert.False(findings.HasViolation);
                File.WriteAllText(Path.Combine(home, "acceptance-manifest.json"), "{ \"version\": 1 }");
                return findings;
            });
            Assert.True(reached);
            AssertUnavailable(fixture, invocation);
            Assert.Equal(home, invocation.StructuralCheck!.ProjectHomeDirectory);
            Assert.Contains("Structural policy changed during evaluation", invocation.StructuralCheck.Diagnostic);
            Assert.DoesNotContain("host/operator owns recovery", invocation.StructuralCheck.Diagnostic);
            Assert.DoesNotContain("Do not edit unrelated source or repeatedly rebuild", invocation.StructuralCheck.Diagnostic);
        }
        finally { SharedTestSupport.RemoveTempDirectory(home); }
    }

    [Fact]
    public async Task CanonicalUnreadableMeasurementCannotBecomePass()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            seedRepository: root => WorkerBuildRequestFixture.SeedSizePolicy(root));
        var reached = false;
        SourceSizeRatchetPreflightResult? measurement = null;
        var invocation = await fixture.RunHelper(structuralEvaluator: () =>
        {
            reached = true;
            measurement = SourceSizeRatchetPreflight.Evaluate(fixture.RepositoryRoot,
                readLines: _ => throw new IOException("fixture measurement unreadable"));
            return new(false, "measurement unavailable", EvaluationComplete: measurement.EvaluationComplete);
        });
        Assert.True(reached);
        Assert.NotNull(measurement);
        Assert.False(measurement.EvaluationComplete);
        Assert.False(measurement.HasBlockingViolation);
        Assert.Contains("fixture measurement unreadable", Assert.Single(measurement.Violations).Message);
        AssertUnavailable(fixture, invocation);
    }

    [Fact]
    public void EnrollmentPinsBaselineAndCanonicalManifestPriority()
    {
        using var fixture = new WorkerBuildRequestFixture(seedRepository: root =>
        {
            Write(root, ".gitignore", "!.orchestrator/\n.orchestrator/*\n!.orchestrator/acceptance-manifest.json\n");
            Write(root, "config/acceptance-manifest.json", "{}");
            Write(root, ".orchestrator/acceptance-manifest.json", "{}");
        });
        Assert.Contains(".orchestrator/acceptance-manifest.json",
            fixture.Git("ls-tree", "--name-only", "HEAD", "--", ".orchestrator/acceptance-manifest.json"));
        var policy = fixture.Reference.StructuralPolicy;
        Assert.NotNull(policy);
        Assert.Equal("config/acceptance-manifest.json", policy.RequiredManifestPath);
        Assert.Equal(fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!.WorkerBuildScope!.GoalBaseline,
            policy.BaselineRevision);
    }

    [Fact]
    public async Task UnresolvedEnrollmentReportsUnavailableWithoutPreventingCompilerRun()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var dispatch = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!;
        var policy = WorkerBuildStructuralPreflight.CapturePolicy(dispatch with
        {
            WorkerBuildScope = dispatch.WorkerBuildScope! with { GoalBaseline = "unresolvable-fixture-baseline" }
        }, null, "main");
        Assert.Contains("could not read baseline policy membership", policy.UnavailableDiagnostic);
        var host = new WorkerBuildRequestHost(fixture.Reference with { StructuralPolicy = policy }, fixture.Time,
            storageRoot: fixture.Storage, launchOverride: fixture.LaunchOverride);
        var invocation = await fixture.RunHelper(service: host);
        AssertUnavailable(fixture, invocation);
        Assert.Contains(policy.UnavailableDiagnostic!, invocation.StructuralCheck!.Diagnostic);
    }

    private static void AssertUnavailable(WorkerBuildRequestFixture fixture, WorkerBuildInvocation invocation)
    {
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(0, invocation.CompilerExitCode);
        Assert.Equal(WorkerBuildStructuralStatus.Unavailable, invocation.StructuralCheck!.Status);
        Assert.Equal(1, Assert.Single(fixture.Responses).ExitCode);
        Assert.True(invocation.CustodyReleased);
        fixture.AssertSlotsFree();
        Assert.Throws<InvalidOperationException>(() => invocation.RequireSuccessor(invocation with
        {
            Version = invocation.Version + 1,
            StructuralCheck = invocation.StructuralCheck with { Status = WorkerBuildStructuralStatus.Passed }
        }));
    }

    [Fact]
    public async Task CompilerFailureSkipsScanWithExplicitUnavailableEvidence()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerFailure: true,
            compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var reached = false;
        var invocation = await fixture.RunHelper(structuralEvaluator: () =>
        { reached = true; return new(false, "unexpected scan"); });
        Assert.False(reached);
        Assert.Equal(WorkerBuildOutcome.CompilerFailure, invocation.Outcome);
        Assert.Equal(1, invocation.CompilerExitCode);
        Assert.Equal(WorkerBuildStructuralStatus.Unavailable, invocation.StructuralCheck!.Status);
        Assert.Contains("skipped after unsuccessful compilation", invocation.StructuralCheck.Diagnostic);
        Assert.DoesNotContain("host/operator owns recovery", invocation.StructuralCheck.Diagnostic);
        Assert.DoesNotContain("Do not edit unrelated source or repeatedly rebuild", invocation.StructuralCheck.Diagnostic);
        var output = File.ReadAllText(Path.Combine(fixture.Root, "worker.out.log"));
        // The channel serializes compiler outcomes as numeric enum values.
        Assert.Contains($"outcome={(int)WorkerBuildOutcome.CompilerFailure};", output);
        Assert.Contains("compiler_exit=1; structural_status=Unavailable; verification_exit=1;", output);
        Assert.DoesNotContain("host/operator owns recovery", output);
        Assert.DoesNotContain("Do not edit unrelated source or repeatedly rebuild", output);
        Assert.True(invocation.CustodyReleased);
        fixture.AssertSlotsFree();
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
