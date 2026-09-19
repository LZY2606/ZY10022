#if NET8_0_OR_GREATER
#nullable disable
using System.Globalization;
using NCalc.Exceptions;
using NCalc.LambdaCompilation;
using NCalc.Tests.Attributes;
using NCalc.Tracing;

namespace NCalc.Tests;

[Property("Category", "Trace")]
public class TraceTests
{
    private static IReadOnlyList<TraceEvent> RunTraced(string expression, out object? result,
        Action<Expression>? configure = null, CultureInfo? cultureInfo = null)
    {
        var exp = new Expression(expression, cultureInfo: cultureInfo);
        configure?.Invoke(exp);
        var trace = new EvaluationTrace();
        result = exp.Evaluate(trace);
        return trace.Events;
    }

    private static List<TraceEvent> Kind(IEnumerable<TraceEvent> events, TraceEventKind kind)
        => events.Where(e => e.Kind == kind).ToList();

    [Test]
    public async Task ShouldTraceSyncAndAsyncWithSameLogicalHierarchy()
    {
        const string formula = "1 + Abs(-3)";

        var syncExpression = new Expression(formula);
        var syncTrace = new EvaluationTrace();
        var syncResult = syncExpression.Evaluate(syncTrace);

        var asyncExpression = new Expression(formula);
        var asyncTrace = new EvaluationTrace();
        var asyncResult = await asyncExpression.EvaluateAsync(asyncTrace);

        await Assert.That(asyncResult).IsEqualTo(syncResult);

        var syncPairs = syncTrace.Events
            .Where(e => e.Kind is TraceEventKind.Enter or TraceEventKind.Exit)
            .Select(e => (e.NodeId, e.ParentId, e.NodeKind, e.Name))
            .ToList();

        var asyncPairs = asyncTrace.Events
            .Where(e => e.Kind is TraceEventKind.Enter or TraceEventKind.Exit)
            .Select(e => (e.NodeId, e.ParentId, e.NodeKind, e.Name))
            .ToList();

        await Assert.That(asyncPairs).IsEquivalentTo(syncPairs);

        var root = syncTrace.Events[0];
        await Assert.That(root.Kind).IsEqualTo(TraceEventKind.Enter);
        await Assert.That(root.NodeKind).IsEqualTo(TraceNodeKind.Evaluation);
        await Assert.That(root.ParentId).IsEqualTo(0);
    }

    [Test]
    public async Task ShouldTraceShortCircuitBranchesAsSkippedWithoutEvaluatingThem()
    {
        var rightCalls = 0;
        var expression = new Expression("true or boom()");
        expression.Functions["boom"] = _ =>
        {
            rightCalls++;
            return true;
        };

        var trace = new EvaluationTrace();
        var result = expression.Evaluate(trace);

        await Assert.That(result).IsEqualTo(true);
        await Assert.That(rightCalls).IsEqualTo(0);

        var skipped = Kind(trace.Events, TraceEventKind.Skipped);
        await Assert.That(skipped).HasCount().EqualTo(1);
        await Assert.That(skipped[0].SkipReason).IsEqualTo(TraceSkipReason.ShortCircuitOr);
        await Assert.That(skipped[0].NodeKind).IsEqualTo(TraceNodeKind.Function);
        await Assert.That(skipped[0].Name).IsEqualTo("boom");

        var andCalls = 0;
        var andExpression = new Expression("false && sideEffect()");
        andExpression.Functions["sideEffect"] = _ => andCalls++;
        var andTrace = new EvaluationTrace();
        andExpression.Evaluate(andTrace);
        await Assert.That(andCalls).IsEqualTo(0);
        await Assert.That(Kind(andTrace.Events, TraceEventKind.Skipped)[0].SkipReason)
            .IsEqualTo(TraceSkipReason.ShortCircuitAnd);
    }

    [Test]
    public async Task ShouldTraceTernarySkippedBranchAndCoalesce()
    {
        var expression = new Expression("true ? 1 : never()");
        expression.Functions["never"] = _ => throw new InvalidOperationException("must not run");
        var trace = new EvaluationTrace();
        var result = expression.Evaluate(trace);
        await Assert.That(result).IsEqualTo(1);
        var skipped = Kind(trace.Events, TraceEventKind.Skipped);
        await Assert.That(skipped).HasCount().EqualTo(1);
        await Assert.That(skipped[0].SkipReason).IsEqualTo(TraceSkipReason.TernaryBranch);

        var coalesceExpression = new Expression("a ?? b");
        coalesceExpression.Parameters["a"] = "set";
        var coalesceCalls = 0;
        coalesceExpression.Functions["b"] = _ => coalesceCalls++;
        var coalesceTrace = new EvaluationTrace();
        await Assert.That(coalesceExpression.Evaluate(coalesceTrace)).IsEqualTo("set");
        await Assert.That(Kind(coalesceTrace.Events, TraceEventKind.Skipped)[0].SkipReason)
            .IsEqualTo(TraceSkipReason.Coalesce);
    }

    [Test]
    public async Task ShouldTraceLazyBuiltInIfArgumentsAsSkipped()
    {
        var calls = 0;
        var expression = new Expression("if(true, 1, boom())");
        expression.Functions["boom"] = _ =>
        {
            calls++;
            return 0;
        };

        var trace = new EvaluationTrace();
        var result = expression.Evaluate(trace);

        await Assert.That(result).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(0);

        var skipped = Kind(trace.Events, TraceEventKind.Skipped);
        await Assert.That(skipped).HasCount().EqualTo(1);
        await Assert.That(skipped[0].SkipReason).IsEqualTo(TraceSkipReason.LazyFunctionArgument);
        await Assert.That(skipped[0].NodeKind).IsEqualTo(TraceNodeKind.Function);
        await Assert.That(skipped[0].Name).IsEqualTo("boom");

        var skippedParent = trace.Events.Last(e =>
            e.Kind == TraceEventKind.Enter && e.NodeKind == TraceNodeKind.Function &&
            e.Name == "if" && e.Sequence < skipped[0].Sequence);
        await Assert.That(skipped[0].ParentId).IsEqualTo(skippedParent.NodeId);
    }

    [Test]
    public async Task ShouldInvokeSideEffectingParameterAndFunctionHandlersExactlyOnce()
    {
        var parameterCalls = 0;
        var functionCalls = 0;

        var expression = new Expression("secret + getValue()");
        expression.EvaluateParameter += (name, args) =>
        {
            if (name == "secret")
            {
                parameterCalls++;
                args.Result = 10;
            }
        };
        expression.Functions["getValue"] = _ =>
        {
            functionCalls++;
            return 32;
        };

        var untraced = new Expression("secret + getValue()");
        untraced.EvaluateParameter += (name, args) =>
        {
            if (name == "secret")
                args.Result = 10;
        };
        untraced.Functions["getValue"] = _ => 32;

        var trace = new EvaluationTrace();
        var tracedResult = expression.Evaluate(trace);

        await Assert.That(tracedResult).IsEqualTo(untraced.Evaluate());
        await Assert.That(parameterCalls).IsEqualTo(1);
        await Assert.That(functionCalls).IsEqualTo(1);

        var parameterResolution = trace.Events.Single(e =>
            e.Kind == TraceEventKind.Resolved && e.Name == "secret");
        await Assert.That(parameterResolution.ResolutionSource)
            .IsEqualTo(TraceResolutionSource.ParameterHandler);

        var functionResolution = trace.Events.Single(e =>
            e.Kind == TraceEventKind.Resolved && e.Name == "getValue");
        await Assert.That(functionResolution.ResolutionSource)
            .IsEqualTo(TraceResolutionSource.Function);
    }

    [Test]
    public async Task ShouldTraceParameterResolutionSources()
    {
        var expression = new Expression("a + b + c");
        expression.Parameters["a"] = 1;
        expression.DynamicParameters["b"] = _ => 2;
        expression.EvaluateParameter += (name, args) =>
        {
            if (name == "c")
                args.Result = 3;
        };

        var trace = new EvaluationTrace();
        await Assert.That(expression.Evaluate(trace)).IsEqualTo(6);

        var sources = trace.Events
            .Where(e => e.Kind == TraceEventKind.Resolved)
            .ToDictionary(e => e.Name!, e => e.ResolutionSource);

        await Assert.That(sources["a"]).IsEqualTo(TraceResolutionSource.StaticParameter);
        await Assert.That(sources["b"]).IsEqualTo(TraceResolutionSource.DynamicParameter);
        await Assert.That(sources["c"]).IsEqualTo(TraceResolutionSource.ParameterHandler);
    }

    [Test]
    public async Task ShouldTraceAsyncResolutionSources()
    {
        var expression = new Expression("a + b + c");
        expression.Parameters["a"] = 1;
        expression.AsyncParameters["b"] = _ => Task.FromResult<object?>(2);
        expression.EvaluateAsyncParameter += (name, args) =>
        {
            if (name == "c")
                args.Result = 3;
            return Task.CompletedTask;
        };

        var trace = new EvaluationTrace();
        await Assert.That(await expression.EvaluateAsync(trace)).IsEqualTo(6);

        var sources = trace.Events
            .Where(e => e.Kind == TraceEventKind.Resolved)
            .ToDictionary(e => e.Name!, e => e.ResolutionSource);

        await Assert.That(sources["a"]).IsEqualTo(TraceResolutionSource.StaticParameter);
        await Assert.That(sources["b"]).IsEqualTo(TraceResolutionSource.AsyncParameter);
        await Assert.That(sources["c"]).IsEqualTo(TraceResolutionSource.AsyncParameterHandler);
    }

    [Test]
    public async Task ShouldRecordFaultAndRethrowWithoutChangingBehavior()
    {
        var expression = new Expression("doesNotExist()");
        var trace = new EvaluationTrace();

        Exception? tracedException = null;
        try
        {
            expression.Evaluate(trace);
        }
        catch (Exception exception)
        {
            tracedException = exception;
        }

        await Assert.That(tracedException is not null).IsTrue();
        var fault = Kind(trace.Events, TraceEventKind.Fault);
        await Assert.That(fault).HasCount().GreaterThanOrEqualTo(1);
        await Assert.That(fault.Last().Exception is not null).IsTrue();

        var untraced = new Expression("doesNotExist()");
        Exception? untracedException = null;
        try
        {
            untraced.Evaluate();
        }
        catch (Exception exception)
        {
            untracedException = exception;
        }

        await Assert.That(untracedException?.GetType()).IsEqualTo(tracedException!.GetType());
    }

    [Test]
    public async Task ShouldRecordMissingParameterFaultSource()
    {
        var expression = new Expression("missing");
        var trace = new EvaluationTrace();

        try
        {
            expression.Evaluate(trace);
        }
        catch (NCalcParameterNotDefinedException)
        {
            // expected
        }

        var fault = Kind(trace.Events, TraceEventKind.Fault);
        await Assert.That(fault).HasCount().GreaterThanOrEqualTo(1);
        await Assert.That(fault[0].NodeKind).IsEqualTo(TraceNodeKind.Identifier);
    }

    [Test]
    public async Task ShouldRecordCacheHitOnlyForSecondEvaluationAndKeepTracesIsolated()
    {
        const string formula = "42 + 1337 + 9001";

        var first = new Expression(formula);
        var firstTrace = new EvaluationTrace();
        await Assert.That(first.Evaluate(firstTrace)).IsEqualTo(42 + 1337 + 9001);
        await Assert.That(Kind(firstTrace.Events, TraceEventKind.CacheHit)).IsEmpty();

        var second = new Expression(formula);
        var secondTrace = new EvaluationTrace();
        await Assert.That(second.Evaluate(secondTrace)).IsEqualTo(42 + 1337 + 9001);
        var cacheHits = Kind(secondTrace.Events, TraceEventKind.CacheHit);
        await Assert.That(cacheHits).HasCount().EqualTo(1);
        await Assert.That(cacheHits[0].Detail).IsEqualTo(formula);

        await Assert.That(firstTrace.Events.First().Sequence).IsEqualTo(0);
        await Assert.That(secondTrace.Events.First().Sequence).IsEqualTo(0);
        // Both evaluations trace the same reused syntax tree, so they emit the same records
        // except for the extra cache hit record on the second evaluation.
        await Assert.That(secondTrace.Events.Count).IsEqualTo(firstTrace.Events.Count + 1);
    }

    [Test]
    public async Task ShouldRedactSensitiveValuesBeforeTheyAreStored()
    {
        var expression = new Expression("password == 'secret'");
        expression.Parameters["password"] = "secret";

        var trace = new EvaluationTrace((nodeKind, name, value) =>
            name == "password" ? "***" : value);

        expression.Evaluate(trace);

        var identifierExit = trace.Events.Last(e =>
            e.NodeKind == TraceNodeKind.Identifier && e.Kind == TraceEventKind.Exit);
        await Assert.That(identifierExit.Value).IsEqualTo("***");

        var resolved = trace.Events.Single(e =>
            e.Kind == TraceEventKind.Resolved && e.Name == "password");
        await Assert.That(resolved.Value).IsEqualTo("***");

        var untracedValue = new Expression("password");
        untracedValue.Parameters["password"] = "secret";
        await Assert.That(untracedValue.Evaluate()).IsEqualTo("secret");
    }

    [Test]
    public async Task ShouldKeepEventsStableWhenAsyncBranchesCompleteOutOfOrder()
    {
        var started = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var expression = new Expression("slow(0) + slow(1)",
            new ExpressionConfiguration
            {
                Evaluation = new ExpressionEvaluationOptions { ConcurrentAsyncEvaluation = true }
            });

        expression.AsyncFunctions["slow"] = async data =>
        {
            var index = (int)await data.EvaluateAsync(0);
            started[index].SetResult();
            await release.Task;
            return index == 0 ? 10 : 20;
        };

        var evaluation = expression.EvaluateAsync();
        await Task.WhenAll(started.Select(s => s.Task));
        release.SetResult();
        await Assert.That(await evaluation).IsEqualTo(30);

        // Build the trace after running the same shape with tracing enabled.
        var tracedStarted = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var tracedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tracedExpression = new Expression("slow(0) + slow(1)",
            new ExpressionConfiguration
            {
                Evaluation = new ExpressionEvaluationOptions { ConcurrentAsyncEvaluation = true }
            });
        tracedExpression.AsyncFunctions["slow"] = async data =>
        {
            var index = (int)await data.EvaluateAsync(0);
            tracedStarted[index].SetResult();
            await tracedRelease.Task;
            return index == 0 ? 10 : 20;
        };

        var trace = new EvaluationTrace();
        var tracedEvaluation = tracedExpression.EvaluateAsync(trace);
        await Task.WhenAll(tracedStarted.Select(s => s.Task));
        tracedRelease.SetResult();
        await Assert.That(await tracedEvaluation).IsEqualTo(30);

        var binary = trace.Events.First(e => e.NodeKind == TraceNodeKind.Binary);
        var functionEnters = trace.Events
            .Where(e => e.Kind == TraceEventKind.Enter && e.NodeKind == TraceNodeKind.Function)
            .ToList();
        await Assert.That(functionEnters).HasCount().EqualTo(2);
        foreach (var functionEnter in functionEnters)
            await Assert.That(functionEnter.ParentId).IsEqualTo(binary.NodeId);

        var sequences = trace.Events.Select(e => e.Sequence).ToList();
        for (var i = 0; i < sequences.Count; i++)
            await Assert.That(sequences[i]).IsEqualTo(i);
    }

    [Test]
    public async Task ShouldTraceWithDifferentCultures()
    {
        var caseSensitive = new ExpressionConfiguration
        {
            Evaluation = new ExpressionEvaluationOptions { StringComparer = StringComparer.Ordinal }
        };
        var caseInsensitive = new ExpressionConfiguration
        {
            Evaluation = new ExpressionEvaluationOptions { StringComparer = StringComparer.OrdinalIgnoreCase }
        };

        var sensitiveExpression = new Expression("a == b", caseSensitive);
        sensitiveExpression.Parameters["a"] = "AbC";
        sensitiveExpression.Parameters["b"] = "abc";

        var insensitiveExpression = new Expression("a == b", caseInsensitive);
        insensitiveExpression.Parameters["a"] = "AbC";
        insensitiveExpression.Parameters["b"] = "abc";

        var sensitiveTrace = new EvaluationTrace();
        var insensitiveTrace = new EvaluationTrace();

        var sensitiveResult = sensitiveExpression.Evaluate(sensitiveTrace);
        var insensitiveResult = insensitiveExpression.Evaluate(insensitiveTrace);

        await Assert.That(sensitiveResult).IsEqualTo(false);
        await Assert.That(insensitiveResult).IsEqualTo(true);

        var sensitiveRootExit = sensitiveTrace.Events.Last(e => e.NodeKind == TraceNodeKind.Evaluation);
        var insensitiveRootExit = insensitiveTrace.Events.Last(e => e.NodeKind == TraceNodeKind.Evaluation);
        await Assert.That(sensitiveRootExit.Value).IsEqualTo(false);
        await Assert.That(insensitiveRootExit.Value).IsEqualTo(true);
    }

    [Test]
    [SkipInNativeAot]
    public async Task ShouldTraceCompiledLambdaWithSameEventModel()
    {
        var expression = new Expression("x > 0 && positive(x)");
        var compiled = expression.ToTracedLambda<LambdaContext, bool>();

        var trace = new EvaluationTrace();
        var result = compiled(new LambdaContext { X = 1 }, trace);
        await Assert.That(result).IsTrue();

        var enters = Kind(trace.Events, TraceEventKind.Enter);
        await Assert.That(enters.Select(e => e.NodeKind)).Contains(TraceNodeKind.Identifier);
        await Assert.That(enters.Select(e => e.NodeKind)).Contains(TraceNodeKind.Function);

        var xResolution = trace.Events.First(e =>
            e.Kind == TraceEventKind.Resolved && e.Name == "x");
        await Assert.That(xResolution.ResolutionSource).IsEqualTo(TraceResolutionSource.StaticParameter);

        var skippedTrace = new EvaluationTrace();
        var skippedResult = compiled(new LambdaContext { X = -1 }, skippedTrace);
        await Assert.That(skippedResult).IsFalse();
        var skipped = Kind(skippedTrace.Events, TraceEventKind.Skipped);
        await Assert.That(skipped).HasCount().EqualTo(1);
        await Assert.That(skipped[0].SkipReason).IsEqualTo(TraceSkipReason.ShortCircuitAnd);
    }

    /// <summary>
    /// Full example embedded in docs/articles/runtime/tracing.md. It is compiled and executed as
    /// a test, so the documentation can never drift into a pseudo API.
    /// </summary>
    #region tracing-doc-example
    [Test]
    public async Task TraceDocumentationExample()
    {
        var expression = new Expression("if(enabled, secret, fallback())");
        expression.Parameters["enabled"] = true;
        expression.Parameters["secret"] = "password123";
        expression.Functions["fallback"] = _ => "no-secret";

        var trace = new EvaluationTrace((nodeKind, name, value) =>
            name == "secret" ? "***redacted***" : value);

        var result = expression.Evaluate(trace);

        await Assert.That(result).IsEqualTo("password123");
        await Assert.That(trace.Events.Count).IsGreaterThan(0);

        var skippedBranch = trace.Events.First(e => e.Kind == TraceEventKind.Skipped);
        await Assert.That(skippedBranch.SkipReason)
            .IsEqualTo(TraceSkipReason.LazyFunctionArgument);
        await Assert.That(skippedBranch.Name).IsEqualTo("fallback");

        var secretEvent = trace.Events.First(e =>
            e.Kind == TraceEventKind.Resolved && e.Name == "secret");
        await Assert.That(secretEvent.Value).IsEqualTo("***redacted***");
    }
    #endregion

    [Test]
    [SkipInNativeAot]
    public async Task ShouldTraceCompiledLambdaCoalesceWithoutEnteringSkippedRight()
    {
        var compiled = new Expression("a ?? b").ToTracedLambda<CoalesceContext, object>();

        var hitTrace = new EvaluationTrace();
        var hit = compiled(new CoalesceContext { A = "x", B = "y" }, hitTrace);
        await Assert.That(hit).IsEqualTo("x");
        var hitSkipped = hitTrace.Events.Where(e => e.Kind == TraceEventKind.Skipped).ToList();
        await Assert.That(hitSkipped).HasCount().EqualTo(1);
        await Assert.That(hitSkipped[0].Name).IsEqualTo("b");
        await Assert.That(hitTrace.Events.Count(e =>
            e.Kind == TraceEventKind.Enter && e.Name == "b")).IsEqualTo(0);

        var missTrace = new EvaluationTrace();
        var miss = compiled(new CoalesceContext { A = null, B = "y" }, missTrace);
        await Assert.That(miss).IsEqualTo("y");
        await Assert.That(missTrace.Events.Count(e => e.Kind == TraceEventKind.Skipped)).IsEqualTo(0);
    }

    private sealed class CoalesceContext
    {
        public string? A { get; set; }
        public string? B { get; set; }
    }

    private sealed class LambdaContext
    {
        public int X { get; set; }

        public bool Positive(int value) => value > 0;
    }
}
#endif
