using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Keep the exact test selectors in the Process spawning lane for the shared launch lock.
namespace Mcg.AgentOrchestrator.Infrastructure.Tests.ProcessTreeGuiSuppressionTests;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessErrorModeEventObserverTests : IDisposable
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-process-error-mode-");
    private string LogPath => Path.Combine(_root.FullName, ConductEventLogWriter.CurrentFileName);

    [Fact]
    public void Record_AttachedObserver_AppendsProcessErrorModeEvent()
    {
        var notice = $"PROCESS_ERROR_MODE_REPAIRED {Guid.NewGuid():N}";
        using (var observer = new ProcessErrorModeEventObserver(new ConductEventLogWriter(LogPath)))
        {
            var previousObserver = ProcessErrorModeRepairLog.Observer;
            ProcessErrorModeRepairLog.Observer = observer.Observe;
            try
            {
                ProcessErrorModeRepairLog.Record(notice);
            }
            finally
            {
                ProcessErrorModeRepairLog.Observer = previousObserver;
            }
        }

        using var entry = JsonDocument.Parse(Assert.Single(File.ReadLines(LogPath)));
        Assert.Equal("process-error-mode", entry.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(JsonValueKind.Null, entry.RootElement.GetProperty("goalId").ValueKind);
        Assert.Equal(notice, entry.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Record_ConsumerAndLaunchBlocked_ReturnsWithoutAppendOnCaller()
    {
        using var releaseConsumer = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        var consumerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchLockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerThreads = new ConcurrentQueue<int>();
        const string firstNotice = "first repair";
        const string secondNotice = "second repair";
        var writer = new ConductEventLogWriter(LogPath,
            beforeRequiredEventDrain: () => writerThreads.Enqueue(Environment.CurrentManagedThreadId));
        int firstCallerThread;
        int secondCallerThread;
        using (var observer = new ProcessErrorModeEventObserver(writer, beforeAppend: notice =>
        {
            if (notice == firstNotice)
            {
                consumerEntered.SetResult();
                releaseConsumer.Wait();
            }
        }))
        {
            var previousObserver = ProcessErrorModeRepairLog.Observer;
            ProcessErrorModeRepairLog.Observer = observer.Observe;
            var launch = Task.Factory.StartNew(() =>
            {
                var initialized = 0;
                var access = new ProcessTreeGuiSuppression.ErrorModeAccess(() =>
                {
                    launchLockHeld.SetResult();
                    releaseLaunch.Wait();
                    return 0;
                }, static value => value);
                ProcessTreeGuiSuppression.InitializeProcessErrorMode(access, ref initialized);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var firstRecord = RecordOnDedicatedThread(firstNotice);
            Task<int>? secondRecord = null;
            try
            {
                await WaitForAsync(firstRecord, "first Record return");
                await WaitForAsync(consumerEntered.Task, "consumer reaching the pre-append gate");
                await WaitForAsync(launchLockHeld.Task, "competing launch holding the launch lock");
                secondRecord = RecordOnDedicatedThread(secondNotice);
                await WaitForAsync(secondRecord, "second Record return with both gates closed");

                firstCallerThread = await firstRecord;
                secondCallerThread = await secondRecord;
                Assert.Empty(writerThreads);
                Assert.False(File.Exists(LogPath));
            }
            finally
            {
                ProcessErrorModeRepairLog.Observer = previousObserver;
                releaseConsumer.Set();
                releaseLaunch.Set();
                await WaitForAsync(launch, "competing launch exit after gate release");
                await WaitForAsync(firstRecord, "first Record return after gate release");
                if (secondRecord is not null)
                    await WaitForAsync(secondRecord, "second Record return after gate release");
            }
        }

        Assert.Equal(new[] { firstNotice, secondNotice }, ReadDetails());
        var appendThread = Assert.Single(writerThreads.Distinct());
        Assert.NotEqual(firstCallerThread, appendThread);
        Assert.NotEqual(secondCallerThread, appendThread);
    }

    [Fact]
    public async Task RepeatedNotice_WithinAndAfterOneMinute_EmitsOncePerWindow()
    {
        const string notice = "repair";
        const string distinctNotice = "REPAIR";
        const string laterNotice = "later repair";
        var clock = new ManualClock();
        clock.Advance(TimeSpan.FromSeconds(30));
        var distinctReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var observer = new ProcessErrorModeEventObserver(new ConductEventLogWriter(LogPath), clock, detail =>
        {
            if (detail == distinctNotice)
                distinctReached.SetResult();
            if (detail == laterNotice)
                laterReached.SetResult();
        }))
        {
            observer.Observe(notice);
            observer.Observe(notice);
            observer.Observe(distinctNotice);
            await WaitForAsync(distinctReached.Task, "distinct notice reaching append");

            clock.Advance(TimeSpan.FromSeconds(59));
            observer.Observe(notice);
            observer.Observe(laterNotice);
            await WaitForAsync(laterReached.Task, "later notice reaching append");

            clock.Advance(TimeSpan.FromSeconds(2));
            observer.Observe(notice);
        }

        Assert.Equal(new[] { notice, distinctNotice, laterNotice, notice }, ReadDetails());
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string?[] ReadDetails() => File.ReadLines(LogPath).Select(line =>
    {
        using var entry = JsonDocument.Parse(line);
        return entry.RootElement.GetProperty("detail").GetString();
    }).ToArray();

    private static Task<int> RecordOnDedicatedThread(string notice) => Task.Factory.StartNew(() =>
    {
        ProcessErrorModeRepairLog.Record(notice);
        return Environment.CurrentManagedThreadId;
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task WaitForAsync(Task signal, string eventName)
    {
        try
        {
            await signal.WaitAsync(DeadlockGuard);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Timed out waiting for {eventName}.");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
