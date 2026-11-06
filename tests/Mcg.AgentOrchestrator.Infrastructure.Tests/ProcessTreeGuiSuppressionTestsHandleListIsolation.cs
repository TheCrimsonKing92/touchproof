using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Mcg.AgentOrchestrator.Infrastructure;
using Support = ProcessTreeGuiSuppressionTestsHandleListSupport;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsHandleListIsolation
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact(Skip = "Requires Windows handle identity.", SkipUnless = nameof(IsWindows))]
    public void ParallelLaunches_InheritOnlyTheirListedObjects()
    {
        const int count = 16;
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var releaseName = "Local\\mcg-handle-list-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var allWindows = new Barrier(count);
        SafeFileHandle[] sentinels = [];
        var listed = new SafeFileHandle[count][];
        var children = new HandleListLaunchedProcess?[count];
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            try
            {
                children[i] = HandleListProcessLauncher.Start(Support.Probe(root, "handles", releaseName),
                    windowObserver: handles =>
                    {
                        listed[i] = handles.Select(h => Support.Duplicate(new IntPtr(-1), h)).ToArray();
                        Assert.True(allWindows.SignalAndWait(TimeSpan.FromSeconds(120)), "Launches did not all enter their inheritable windows.");
                    }, acquireAdditionalHandles: () => [Support.InheritableSentinel()]);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        }) { IsBackground = true }).ToArray();
        Process? raw = null;
        try
        {
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(180)), "Handle-list launch thread did not return from creation.");
            Assert.Empty(failures);
            for (var i = 0; i < count; i++)
            {
                var child = Assert.IsType<HandleListLaunchedProcess>(children[i]);
                var values = JsonSerializer.Deserialize<long[]>(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult()!)!;
                using var childHandle = Support.OpenForDuplication(child.Id);
                var matches = new HashSet<int>();
                var ownMatches = new HashSet<int>();
                foreach (var value in values)
                {
                    // The probe holds its inherited objects until release. Runtime handles may close.
                    SafeFileHandle duplicate;
                    try { duplicate = Support.Duplicate(childHandle.DangerousGetHandle(), new IntPtr(value)); }
                    catch (System.ComponentModel.Win32Exception) { continue; }
                    using (duplicate)
                        for (var owner = 0; owner < count; owner++)
                            for (var objectIndex = 0; objectIndex < listed[owner].Length; objectIndex++)
                                if (Support.Same(duplicate, listed[owner][objectIndex]))
                                { matches.Add(owner); if (owner == i) ownMatches.Add(objectIndex); }
                }
                Assert.Equal([i], matches.Order().ToArray());
                Assert.Equal(listed[i].Length, ownMatches.Count); // Includes all pipe ends and the sentinel.
            }
            // Control: raw Process.Start inherits all sixteen still-inheritable sentinels.
            sentinels = Enumerable.Range(0, count).Select(_ => Support.InheritableSentinel()).ToArray();
            raw = Process.Start(Support.Probe(root, "handles", releaseName))!;
            var rawValues = JsonSerializer.Deserialize<long[]>(raw.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult()!)!;
            using var rawHandle = Support.OpenForDuplication(raw.Id);
            var inherited = new HashSet<int>();
            foreach (var value in rawValues)
            {
                SafeFileHandle duplicate;
                try { duplicate = Support.Duplicate(rawHandle.DangerousGetHandle(), new IntPtr(value)); }
                catch (System.ComponentModel.Win32Exception) { continue; }
                using (duplicate)
                    for (var i = 0; i < count; i++)
                        if (Support.Same(duplicate, sentinels[i])) inherited.Add(i);
            }
            Assert.Equal(count, inherited.Count);
        }
        finally
        {
            release.Set();
            foreach (var child in children) Support.Stop(child);
            if (raw is not null) { try { if (!raw.WaitForExit(60_000)) raw.Kill(true); } finally { raw.Dispose(); } }
            foreach (var handles in listed) if (handles is not null) foreach (var handle in handles) handle.Dispose();
            foreach (var sentinel in sentinels) sentinel.Dispose();
            Directory.Delete(root, true);
        }
    }
}
