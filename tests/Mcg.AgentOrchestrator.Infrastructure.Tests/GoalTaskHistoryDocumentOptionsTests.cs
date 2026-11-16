using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory only; each test owns its source options, store and synchronization gates.
public sealed class GoalTaskHistoryDocumentOptionsTests
{
    [Fact]
    public void PrepareWrite_FiftyVariedGoals_RunsTaskShapeModifierOnce()
    {
        var source = new JsonSerializerOptions(GoalTaskHistoryTestFixture.Options);
        var store = new GoalTaskHistoryStore(source);
        Assert.Equal(0, store.TaskShapeModifierRunCount);
        // Construction must stay lazy so changes made before the first save remain visible.
        source.WriteIndented = true;
        var goals = Enumerable.Range(0, 60).Select(index => VariedGoal(index)).ToArray();
        var documents = goals.Select(goal => store.PrepareWrite(goal).Document).ToArray();

        Assert.Equal(60, documents.Length);
        Assert.Contains(documents, document => document.Contains("\"LatestVerificationInTable\"", StringComparison.Ordinal));
        Assert.Contains(documents, document => !document.Contains("\"LatestVerificationInTable\"", StringComparison.Ordinal));
        Assert.Equal(Encoding.UTF8.GetBytes(ReferenceDocument(goals[0], source)), Encoding.UTF8.GetBytes(documents[0]));
        Assert.Equal(1, store.TaskShapeModifierRunCount);

        var secondStore = new GoalTaskHistoryStore(source);
        foreach (var goal in goals.Reverse()) secondStore.PrepareWrite(goal);
        Assert.Equal(1, secondStore.TaskShapeModifierRunCount);
        Assert.Equal(1, store.TaskShapeModifierRunCount);

        var independentStore = new GoalTaskHistoryStore(new JsonSerializerOptions(source));
        Assert.Equal(0, independentStore.TaskShapeModifierRunCount);
        independentStore.PrepareWrite(goals[3]);
        Assert.Equal(1, independentStore.TaskShapeModifierRunCount);
        Assert.Equal(1, store.TaskShapeModifierRunCount);
    }

    [Fact]
    public void PrepareWrite_VariedGoals_MatchesPerSaveConstructionBytes()
    {
        var goals = Enumerable.Range(0, 12).Select(index => VariedGoal(index)).ToList();
        var equalTask = goals[3].Tasks[0];
        var twin = equalTask with { };
        Assert.Equal(equalTask, twin);
        Assert.NotSame(equalTask, twin);
        goals.Add(goals[3] with { Tasks = [equalTask, twin] });
        Assert.Contains(goals, goal => goal.Tasks.Count == 0);
        var tasks = goals.SelectMany(goal => goal.Tasks).ToArray();
        Assert.Contains(tasks, task => task.VerificationHistory is null && task.DispatchHistory is { Count: 0 });
        Assert.Contains(tasks, task => task.VerificationHistory is { Count: > 0 } && task.LastVerification is null);
        Assert.Contains(tasks, task => task.VerificationHistory is { Count: 0 });

        var compactSource = new JsonSerializerOptions(GoalTaskHistoryTestFixture.Options);
        var indentedSource = new JsonSerializerOptions(compactSource) { WriteIndented = true };
        var compactDocuments = CompareDocuments(compactSource);
        var indentedDocuments = CompareDocuments(indentedSource);
        Assert.NotEqual(compactDocuments[0], indentedDocuments[0]);

        string[] CompareDocuments(JsonSerializerOptions source)
        {
            var store = new GoalTaskHistoryStore(source);
            var documents = new List<string>();
            foreach (var goal in goals.Concat(goals.AsEnumerable().Reverse()))
            {
                var expected = ReferenceDocument(goal, source);
                var actual = store.PrepareWrite(goal).Document;
                Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
                documents.Add(actual);
            }
            Assert.Equal(1, store.TaskShapeModifierRunCount);
            return documents.ToArray();
        }
    }

    [Fact]
    public void PrepareWrite_OverlappingSavesOnFourThreads_MatchSoloDocuments()
    {
        var store = new GoalTaskHistoryStore(new JsonSerializerOptions(GoalTaskHistoryTestFixture.Options));
        using var barrier = new Barrier(4);
        using var cancellation = new CancellationTokenSource();
        var gates = Enumerable.Range(0, 4).Select(_ => new GatedFeedback(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(60), cancellation.Token))
                throw new TimeoutException("All four document writes did not reach the feedback gate.");
        })).ToArray();
        var goals = gates.Select((gate, index) =>
        {
            var goal = VariedGoal(index + 1, index + 2);
            return goal with { Tasks = goal.Tasks.Select((task, taskIndex) => taskIndex == 0
                ? task with { CriterionRetryFeedback = gate } : task).ToArray() };
        }).ToArray();
        var solo = goals.Select(goal => store.PrepareWrite(goal).Document).ToArray();
        // Pin reuse before arming the overlap gate, so the old per-save path cannot satisfy this test.
        Assert.Equal(1, store.TaskShapeModifierRunCount);
        foreach (var gate in gates) gate.Arm();

        var errors = new ConcurrentQueue<Exception>();
        var concurrent = new string?[4];
        var threads = goals.Select((goal, index) => new Thread(() =>
        {
            try { concurrent[index] = store.PrepareWrite(goal).Document; }
            catch (Exception error)
            {
                errors.Enqueue(error);
                cancellation.Cancel();
            }
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads) thread.Start();
        try
        {
            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "A document-writing thread did not finish after the feedback gate.");
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.Empty(errors);
        Assert.Equal(1L, barrier.CurrentPhaseNumber);
        for (var index = 0; index < goals.Length; index++)
        {
            Assert.NotNull(concurrent[index]);
            Assert.Equal(Encoding.UTF8.GetBytes(solo[index]), Encoding.UTF8.GetBytes(concurrent[index]!));
        }
    }

    private static GoalSnapshot VariedGoal(int index, int? taskCount = null)
    {
        var seed = GoalTaskHistoryTestFixture.Snapshot(entries: 1 + index % 4, taskCount: taskCount ?? index % 6);
        return seed with
        {
            Id = index.ToString("x32"),
            Objective = $"Goal {index} café 日本語",
            Tasks = seed.Tasks.Select((task, taskIndex) => ((index + taskIndex) % 8) switch
            {
                0 => task with { VerificationHistory = null, DispatchHistory = null, LastVerification = null, LastDispatch = null },
                1 => task with { VerificationHistory = [], DispatchHistory = [] },
                2 => task with { VerificationHistory = null, DispatchHistory = [], LastVerification = null, LastDispatch = null },
                3 => task,
                4 => task with { LastVerification = task.LastVerification! with { Command = "different" },
                    LastDispatch = task.LastDispatch! with { Command = "different" } },
                5 => task with { LastVerification = null, LastDispatch = null },
                6 => task with { LastDispatch = task.LastDispatch! with { Command = "different" } },
                _ => task with { LastVerification = task.LastVerification! with { Command = "different" } }
            }).ToArray()
        };
    }

    // Independent copy of the per-save construction at 3d0d1b8cf, including member order.
    private static string ReferenceDocument(GoalSnapshot goal, JsonSerializerOptions source)
    {
        var counts = new Dictionary<TaskSnapshot, ReferenceCounts>(ReferenceEqualityComparer.Instance);
        foreach (var task in goal.Tasks)
        {
            string? Newest<T>(IReadOnlyList<T>? entries)
            {
                string? newest = null;
                if (entries is not null)
                    for (var index = 0; index < entries.Count; index++)
                        newest = JsonSerializer.Serialize(entries[index], source);
                return newest;
            }
            bool Matches<T>(T? latest, string? newest) where T : class =>
                latest is not null && newest is not null &&
                string.Equals(JsonSerializer.Serialize(latest, source), newest, StringComparison.Ordinal);

            var verification = Newest(task.VerificationHistory);
            var dispatch = Newest(task.DispatchHistory);
            counts[task] = new(task.VerificationHistory?.Count, task.DispatchHistory?.Count,
                Matches(task.LastVerification, verification), Matches(task.LastDispatch, dispatch));
        }
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(TaskSnapshot)) return;
            foreach (var property in type.Properties.Where(property =>
                property.AttributeProvider is System.Reflection.PropertyInfo
                    { Name: "VerificationHistory" or "DispatchHistory" }).ToArray())
                type.Properties.Remove(property);
            foreach (var property in type.Properties)
            {
                if (property.AttributeProvider is not System.Reflection.PropertyInfo member) continue;
                if (member.Name == nameof(TaskSnapshot.LastVerification))
                    property.ShouldSerialize = (value, _) => !counts[(TaskSnapshot)value].LatestVerificationInTable;
                else if (member.Name == nameof(TaskSnapshot.LastDispatch))
                    property.ShouldSerialize = (value, _) => !counts[(TaskSnapshot)value].LatestDispatchInTable;
            }
            var marker = type.CreateJsonPropertyInfo(typeof(ReferenceCounts), "HistoryInTable");
            marker.Get = value => counts[(TaskSnapshot)value];
            type.Properties.Add(marker);
        });
        return JsonSerializer.Serialize(goal, new JsonSerializerOptions(source) { TypeInfoResolver = resolver });
    }

    private sealed record ReferenceCounts(int? Verification, int? Dispatch,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LatestVerificationInTable,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LatestDispatchInTable);

    private sealed class GatedFeedback(Action gate) : IReadOnlyList<string>
    {
        private int _armed;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        private void Enter()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1) gate();
        }
        public int Count { get { Enter(); return 1; } }
        public string this[int index]
        {
            get
            {
                Enter();
                return index == 0 ? "feedback café" : throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        public IEnumerator<string> GetEnumerator()
        {
            Enter();
            yield return "feedback café";
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
