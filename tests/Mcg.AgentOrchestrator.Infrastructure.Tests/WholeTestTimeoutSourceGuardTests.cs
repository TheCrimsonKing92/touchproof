using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

// Parallel-safe: repository source reads only; no shared state is changed.
public sealed class WholeTestTimeoutSourceGuardTests
{
    [Fact]
    public void TestAttributesUseNamedHangFailsafes()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var tests = Path.Combine(root, "tests");
        Assert.True(Directory.Exists(tests), $"Test source directory missing: {tests}");
        var files = EnumerateSourceFiles(tests).ToArray();
        Assert.NotEmpty(files);
        var failures = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WholeTestTimeoutSourceGuardTests.cs")
                continue;
            var source = File.ReadAllText(file);
            if (!source.Contains("Timeout", StringComparison.Ordinal))
                continue;
            failures.AddRange(FindNumericTimeoutAttributes(source)
                .Select(line => $"{relative}:{line}: numeric test Timeout; use a named hang failsafe."));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("[Fact(Timeout = 30_000)]", 1)]
    [InlineData("[Xunit.Theory(Timeout = 30000)]", 1)]
    [InlineData("[Xunit.Fact(Timeout = 30_000, DisplayName = \"x\")]", 1)]
    [InlineData("[Fact(\n    Timeout = 60_000)]", 2)]
    [InlineData("[global::Xunit.FactAttribute(Timeout = (+0x7530))]", 1)]
    [InlineData("[Trait(\"a\", \"b\"), TheoryAttribute(Timeout = 30000L)]", 1)]
    [InlineData("[Fact(Timeout = 30 * 1000)]", 1)]
    [InlineData("[Fact(Timeout = (int)30_000)]", 1)]
    public void TimeoutDetectorRejectsNumericBounds(string attribute, int expectedLine)
    {
        var source = attribute + "\nclass C {}";
        Assert.Equal(new[] { expectedLine }, FindNumericTimeoutAttributes(source));
    }

    [Theory]
    [InlineData("[Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] class C {}")]
    [InlineData("[Fact(DisplayName = \"waits 30000 ms\")] class C {}")]
    [InlineData("class C { object Connection = new Builder { DefaultTimeout = 30 }; }")]
    [InlineData("class C { string Sample = \"[Fact(Timeout = 30_000)]\"; }")]
    [InlineData("// [Fact(Timeout = 30_000)]\nclass C {}")]
    [InlineData("[Fact(DisplayName = \"[Fact(Timeout = 30000)]\")] class C {}")]
    [InlineData("[Fact(Timeout = SomeConstant * 2)] class C {}")]
    public void TimeoutDetectorAllowsNamedBoundsAndUnrelatedText(string source)
    {
        Assert.Empty(FindNumericTimeoutAttributes(source));
    }

    [Fact]
    public void WholeTestFailsafeKeepsHungTestNamesBelowLaneStallLimit()
    {
        Assert.True(TestHangGuard.WholeTestMilliseconds < AcceptanceLaneStallPolicy.StallLimitSeconds * 1_000);
    }

    [Fact]
    public void HangGuardDoesNotUseFixedDurationTaskWaits()
    {
        var path = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "TestHangGuard.cs");
        Assert.True(File.Exists(path), $"Hang guard source missing: {path}");
        var source = File.ReadAllText(path);
        var methods = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().ToArray();
        Assert.Contains(methods, method => method.Identifier.ValueText == "CompletesWithinAsync");
        Assert.Contains(methods, method => method.Identifier.ValueText == "WaitAsync" && method.TypeParameterList is null);
        Assert.Contains(methods, method => method.Identifier.ValueText == "WaitAsync" && method.TypeParameterList is not null);
        Assert.Empty(FindFixedDurationTaskWaits(source));
    }

    [Theory]
    [InlineData("task.WaitAsync(Bound)")]
    [InlineData("task.WaitAsync(TestHangGuard.Bound)")]
    [InlineData("task.WaitAsync(TimeSpan.FromSeconds(30))")]
    [InlineData("task.WaitAsync(new TimeSpan(0, 0, 30))")]
    [InlineData("task.WaitAsync(new(0, 0, 30))")]
    [InlineData("task.WaitAsync(HangSafetyBound, token)")]
    [InlineData("task.WaitAsync(timeout: alias)")]
    [InlineData("task.Wait(30_000)")]
    [InlineData("task.Wait(WholeTestMilliseconds)")]
    [InlineData("task.Wait((int)Bound.TotalMilliseconds)")]
    [InlineData("task.WaitAsync(window)")]
    [InlineData("task.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, token)")]
    public void TaskWaitDetectorRejectsFixedDurationsAndAliases(string call)
    {
        var source = """
            class TestHangGuard
            {
                static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
                static readonly TimeSpan HangSafetyBound = TimeSpan.FromSeconds(120);
                const int WholeTestMilliseconds = 300_000;
                void M(Task task, TimeSpan window, CancellationToken token)
                {
                    var duration = Bound;
                    var alias = duration;
            """ + call + "; } }";
        Assert.Equal(call, Assert.Single(FindFixedDurationTaskWaits(source)));
    }

    [Theory]
    [InlineData("task.WaitAsync(token)")]
    [InlineData("task.WaitAsync(CancellationToken.None)")]
    [InlineData("task.Wait()")]
    [InlineData("Task.WhenAny(task, other)")]
    [InlineData("Task.Delay(TimeSpan.FromSeconds(1))")]
    [InlineData("Thread.Sleep(TimeSpan.FromSeconds(1))")]
    [InlineData("Monitor.Wait(sync, TimeSpan.FromSeconds(1))")]
    [InlineData("TestHangGuard.WaitAsync(task, eventName)")]
    [InlineData("var text = \"task.WaitAsync(Bound)\"")]
    [InlineData("// task.WaitAsync(Bound)\n task.WaitAsync(token)")]
    public void TaskWaitDetectorAllowsUntimedWaitsAndSampling(string body)
    {
        Assert.Empty(FindFixedDurationTaskWaits("class C { void M(Task task, CancellationToken token) { " + body + "; } }"));
    }

    [Fact]
    public void TaskWaitDetectorRejectsBothFormerHangGuardWaits()
    {
        const string formerSource = """
            internal static class TestHangGuard
            {
                internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
                internal static readonly TimeSpan HangSafetyBound = TimeSpan.FromSeconds(120);
                // Whole-test hang failsafe below the 600 s lane stall limit; never a speed check.
                internal const int WholeTestMilliseconds = 300_000;

                internal static async Task CompletesWithinAsync(Task task, TimeSpan bound, string operation)
                {
                    using var expiryCancellation = new CancellationTokenSource();
                    var expiry = Task.Delay(bound, expiryCancellation.Token);
                    // Wait for a terminal state without observing the task's own fault.
                    if (await Task.WhenAny(task, expiry) == expiry)
                    {
                        throw new TestHangGuardExpiredException(
                            $"Test hang-safety bound of {bound} expired: {operation} did not reach a terminal state.");
                    }

                    expiryCancellation.Cancel();
                }

                internal static async Task WaitAsync(Task task, string eventName)
                {
                    try { await task.WaitAsync(Bound); }
                    catch (TimeoutException ex)
                    {
                        throw new TimeoutException($"Hang guard: {eventName} did not complete within {Bound}.", ex);
                    }
                }

                internal static async Task<T> WaitAsync<T>(Task<T> task, string eventName)
                {
                    try { return await task.WaitAsync(Bound); }
                    catch (TimeoutException ex)
                    {
                        throw new TimeoutException($"Hang guard: {eventName} did not complete within {Bound}.", ex);
                    }
                }
            }

            internal sealed class TestHangGuardExpiredException(string message) : Exception(message);
            """;
        Assert.Equal(new[] { "task.WaitAsync(Bound)", "task.WaitAsync(Bound)" }, FindFixedDurationTaskWaits(formerSource));
    }

    [Fact]
    public void ExternalOwnedStartUsesSharedChildHangFailsafe()
    {
        var path = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "ConductorBatchLoopTestsParallelAcceptance.cs");
        Assert.True(File.Exists(path), $"External-child test source missing: {path}");
        var body = FindMethodBody(File.ReadAllText(path), "RunExternalOwnedStartScenario");
        Assert.False(string.IsNullOrWhiteSpace(body), $"RunExternalOwnedStartScenario declaration not found in {path}");
        Assert.Contains("OwnedAttemptExitWait.WaitForExit(externalProcess, initialAttempt)", body, StringComparison.Ordinal);
        var helperPath = Path.Combine(Path.GetDirectoryName(path)!, "OwnedAttemptExitWait.cs");
        Assert.True(File.Exists(helperPath), $"External-child wait helper source missing: {helperPath}");
        var helperBody = CSharpSyntaxTree.ParseText(File.ReadAllText(helperPath)).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "WaitForExit"
                && method.ParameterList.Parameters.Count == 5).Body?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(helperBody), $"WaitForExit declaration not found in {helperPath}");
        // The external child is governed by its observed progress tuple.
        Assert.Contains("TestHangGuard.HangSafetyBound", helperBody, StringComparison.Ordinal);
        Assert.Contains("current.Progress != previous.Progress", helperBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TestHangGuard.WholeTestMilliseconds", helperBody, StringComparison.Ordinal);
        // Guard the external child only; the separate finally cleanup wait is outside this change's scope.
        Assert.Empty(FindNumericLiteralWaitForExitCalls(body!, receiver: "externalProcess"));
    }

    [Theory]
    [InlineData("WaitForExit(60000)")]
    [InlineData("WaitForExit(60_000)")]
    [InlineData("externalProcess.WaitForExit(milliseconds: 60_000)")]
    [InlineData("externalProcess.WaitForExit((+60_000))")]
    public void ExitWaitDetectorRejectsNumericBounds(string call)
    {
        Assert.Single(FindNumericLiteralWaitForExitCalls(call + ";"));
    }

    [Theory]
    [InlineData("WaitForExit()")]
    [InlineData("externalProcess.WaitForExit(TestHangGuard.HangSafetyBound)")]
    [InlineData("externalProcess.WaitForExit((int)TestHangGuard.HangSafetyBound.TotalMilliseconds)")]
    public void ExitWaitDetectorAllowsUntimedAndNamedBounds(string call)
    {
        Assert.Empty(FindNumericLiteralWaitForExitCalls(call + ";"));
    }

    [Fact]
    public void ExitWaitDetectorSeparatesExternalChildFromCleanup()
    {
        const string body = "externalProcess.WaitForExit(60000); process.WaitForExit(5000);";
        Assert.Equal("externalProcess.WaitForExit(60000)",
            Assert.Single(FindNumericLiteralWaitForExitCalls(body, receiver: "externalProcess")));
        Assert.Equal(2, FindNumericLiteralWaitForExitCalls(body).Count);
    }

    [Fact]
    public void MethodBodyDetectorFindsStaticDeclarationRatherThanCalls()
    {
        const string source = "class C { void Call() { Target(); } private static void Target() { if (true) { WaitForExit(60000); } } }";
        var body = FindMethodBody(source, "Target");
        Assert.NotNull(body);
        Assert.Single(FindNumericLiteralWaitForExitCalls(body));
        Assert.Null(FindMethodBody(source, "Missing"));
    }

    internal static IReadOnlyList<int> FindNumericTimeoutAttributes(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var lines = new List<int>();
        foreach (var attribute in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = attribute.Name.ToString();
            if (name.StartsWith("global::", StringComparison.Ordinal))
                name = name[8..];
            if (name.StartsWith("Xunit.", StringComparison.Ordinal))
                name = name[6..];
            if (name is not ("Fact" or "Theory" or "FactAttribute" or "TheoryAttribute") ||
                attribute.ArgumentList is null)
                continue;
            foreach (var argument in attribute.ArgumentList.Arguments)
            {
                if (argument.NameEquals?.Name.Identifier.ValueText == "Timeout" && StartsWithNumericLiteral(argument.Expression))
                    lines.Add(tree.GetLineSpan(argument.Span).StartLinePosition.Line + 1);
            }
        }
        return lines;
    }

    internal static IReadOnlyList<string> FindFixedDurationTaskWaits(string source)
    {
        // The helper source relies on implicit BCL usings in its project. Supply them
        // here so receiver types distinguish task waits from monitor sampling waits.
        var tree = CSharpSyntaxTree.ParseText("using System; using System.Threading; using System.Threading.Tasks;\n" + source);
        var model = CSharpCompilation.Create("HangGuardSource", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]).GetSemanticModel(tree);
        var root = tree.GetRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax access &&
                access.Name.Identifier.ValueText is "WaitAsync" or "Wait" &&
                IsTask(model.GetTypeInfo(access.Expression).Type) &&
                call.ArgumentList.Arguments.Any(argument => IsDuration(argument.Expression, [])))
            .Select(call => call.ToString()).ToArray();

        static bool IsTask(ITypeSymbol? type)
        {
            for (var current = type; current is not null; current = current.BaseType)
                if (current.Name == "Task" && current.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
                    return true;
            return false;
        }

        bool IsDuration(ExpressionSyntax expression, HashSet<string> resolving)
        {
            switch (expression)
            {
                case LiteralExpressionSyntax literal:
                    return literal.IsKind(SyntaxKind.NumericLiteralExpression);
                case ParenthesizedExpressionSyntax parentheses:
                    return IsDuration(parentheses.Expression, resolving);
                case CastExpressionSyntax cast:
                    return IsDuration(cast.Expression, resolving);
                case PrefixUnaryExpressionSyntax unary:
                    return IsDuration(unary.Operand, resolving);
                case BinaryExpressionSyntax binary:
                    return IsDuration(binary.Left, new(resolving)) || IsDuration(binary.Right, new(resolving));
                case ObjectCreationExpressionSyntax creation:
                    return IsDurationType(creation.Type.ToString());
                case ImplicitObjectCreationExpressionSyntax:
                    return true; // The timeout argument supplies the TimeSpan target type.
                case InvocationExpressionSyntax invocation:
                    return invocation.Expression is MemberAccessExpressionSyntax factory &&
                        IsDurationType(factory.Expression.ToString());
                case MemberAccessExpressionSyntax member when IsDurationType(member.Expression.ToString()):
                    return true;
                case MemberAccessExpressionSyntax member when member.Name.Identifier.ValueText is "TotalMilliseconds" or "Ticks":
                    return IsDuration(member.Expression, resolving);
                case MemberAccessExpressionSyntax member:
                    return Resolve(member.Name.Identifier.ValueText, expression, resolving);
                case IdentifierNameSyntax identifier:
                    return Resolve(identifier.Identifier.ValueText, expression, resolving);
                default:
                    return false;
            }
        }

        bool Resolve(string name, ExpressionSyntax use, HashSet<string> resolving)
        {
            if (!resolving.Add(name)) return false;
            // Search the nearest lexical scope first so a token parameter cannot inherit
            // an unrelated method's duration alias. Initializers may themselves be aliases.
            foreach (var scope in use.Ancestors().Where(node =>
                node is BlockSyntax or MethodDeclarationSyntax or TypeDeclarationSyntax))
            {
                var variable = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(node => node.Identifier.ValueText == name &&
                        node.Ancestors().FirstOrDefault(ancestor => ancestor is BlockSyntax or TypeDeclarationSyntax) == scope);
                if (variable is not null)
                {
                    var type = (variable.Parent as VariableDeclarationSyntax)?.Type.ToString();
                    return IsDurationType(type) || variable.Initializer is not null &&
                        IsDuration(variable.Initializer.Value, resolving);
                }
                if (scope is MethodDeclarationSyntax method)
                {
                    var parameter = method.ParameterList.Parameters.FirstOrDefault(node => node.Identifier.ValueText == name);
                    if (parameter is not null) return IsDurationType(parameter.Type?.ToString());
                }
            }
            return false;
        }

        static bool IsDurationType(string? type) => type is
            "TimeSpan" or "System.TimeSpan" or "global::System.TimeSpan" or "int" or "System.Int32";
    }

    internal static string? FindMethodBody(string source, string methodName)
    {
        var method = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == methodName);
        return method?.Body?.ToString() ?? method?.ExpressionBody?.ToString();
    }

    internal static IReadOnlyList<string> FindNumericLiteralWaitForExitCalls(string body, string? receiver = null)
    {
        var root = CSharpSyntaxTree.ParseText("class C { void M() { " + body + " } }").GetRoot();
        var calls = new List<string>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var access = invocation.Expression as MemberAccessExpressionSyntax;
            var name = access?.Name.Identifier.ValueText ?? (invocation.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
            if (name == "WaitForExit" && (receiver is null || access?.Expression.ToString() == receiver) &&
                invocation.ArgumentList.Arguments.Count > 0 &&
                StartsWithNumericLiteral(invocation.ArgumentList.Arguments[0].Expression))
                calls.Add(invocation.ToString());
        }
        return calls;
    }

    private static bool StartsWithNumericLiteral(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.Kind() == SyntaxKind.NumericLiteralExpression,
        ParenthesizedExpressionSyntax parentheses => StartsWithNumericLiteral(parentheses.Expression),
        PrefixUnaryExpressionSyntax prefix when prefix.Kind() is SyntaxKind.UnaryPlusExpression or SyntaxKind.UnaryMinusExpression
            => StartsWithNumericLiteral(prefix.Operand),
        CastExpressionSyntax cast => StartsWithNumericLiteral(cast.Expression),
        BinaryExpressionSyntax binary => StartsWithNumericLiteral(binary.Left),
        _ => false
    };

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj")
                continue;
            foreach (var file in EnumerateSourceFiles(child))
                yield return file;
        }
    }
}
