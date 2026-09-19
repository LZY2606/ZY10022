using NCalc.LambdaCompilation;
using NCalc.Tracing;
#nullable disable

namespace NCalc.Tests;

[Property("Category", "Trace")]
public class TraceTests
{
    [Test]
    public async Task ShouldTraceSynchronousAndAsynchronousEvaluationWithSameRelationships()
    {
        var expressionText = "secret + 1";

        var syncExpression = CreateExpression(expressionText);
        var asyncExpression = CreateExpression(expressionText);
        syncExpression.Parameters["secret"] = 41;
        asyncExpression.Parameters["secret"] = 41;

        var syncTrace = new EvaluationTrace();
        var asyncTrace = new EvaluationTrace();

        await Assert.That(syncExpression.Evaluate(syncTrace)).IsEqualTo(42);
        await Assert.That(await asyncExpression.EvaluateAsync(asyncTrace)).IsEqualTo(42);

        var syncRelationships = Relationships(syncTrace);
        var asyncRelationships = Relationships(asyncTrace);

        await Assert.That(syncRelationships).IsEquivalentTo(asyncRelationships);
        var syncKinds = syncTrace.Events.Where(e => e.Kind != EvaluationTraceEventKind.CacheHit).Select(e => e.Kind);
        var asyncKinds = asyncTrace.Events.Where(e => e.Kind != EvaluationTraceEventKind.CacheHit).Select(e => e.Kind);
        await Assert.That(syncKinds).IsEquivalentTo(asyncKinds);
    }

    [Test]
    public async Task ShouldTraceShortCircuitWithoutEvaluatingBranch()
    {
        foreach (var (text, expected) in new (string, object)[]
                 {
                     ("false && skipped()", false),
                     ("true || skipped()", true),
                     ("'ready' ?? skipped()", "ready"),
                     ("false ? skipped() : 1", 1),
                     ("true ? 1 : skipped()", 1),
                     ("if(false, skipped(), 2)", 2)
                 })
        {
            var syncTrace = new EvaluationTrace();
            var syncExpression = CreateExpression(text);
            syncExpression.Functions["skipped"] = _ => throw new InvalidOperationException();
            await Assert.That(syncExpression.Evaluate(syncTrace)).IsEqualTo(expected);
            await Assert.That(syncTrace.Events.Any(e => e.Kind == EvaluationTraceEventKind.Skipped)).IsTrue();

            var asyncTrace = new EvaluationTrace();
            var asyncExpression = CreateExpression(text);
            asyncExpression.AsyncFunctions["skipped"] = _ =>
                Task.FromException<object>(new InvalidOperationException());
            await Assert.That(await asyncExpression.EvaluateAsync(asyncTrace)).IsEqualTo(expected);
            await Assert.That(asyncTrace.Events.Any(e => e.Kind == EvaluationTraceEventKind.Skipped)).IsTrue();
        }
    }

    [Test]
    public async Task ShouldInvokeSideEffectingHandlersOnceForBothEvaluationModes()
    {
        foreach (var asynchronous in new[] { false, true })
        {
            var count = 0;
            var trace = new EvaluationTrace();
            var expression = CreateExpression("sideEffect() + sideEffect()");
            if (asynchronous)
            {
                expression.AsyncFunctions["sideEffect"] = _ =>
                {
                    count++;
                    return Task.FromResult<object>(1);
                };
                await Assert.That(await expression.EvaluateAsync(trace)).IsEqualTo(2);
            }
            else
            {
                expression.Functions["sideEffect"] = _ =>
                {
                    count++;
                    return 1;
                };
                await Assert.That(expression.Evaluate(trace)).IsEqualTo(2);
            }

            await Assert.That(count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ShouldTraceParameterAndFunctionResolutionSources()
    {
        var expression = CreateExpression("callback(staticValue, dynamicValue)");
        expression.Parameters["staticValue"] = "static";
        expression.DynamicParameters["dynamicValue"] = _ => "dynamic";
        expression.Functions["callback"] = data => $"{data.Evaluate(0)}-{data.Evaluate(1)}";

        var trace = new EvaluationTrace();

        await Assert.That(expression.Evaluate(trace)).IsEqualTo("static-dynamic");
        await Assert.That(trace.Events.Any(e =>
            e.Kind == EvaluationTraceEventKind.ParameterResolved &&
            e.Name == "staticValue" &&
            e.Source == EvaluationTraceResolutionSource.StaticParameter)).IsTrue();
        await Assert.That(trace.Events.Any(e =>
            e.Kind == EvaluationTraceEventKind.ParameterResolved &&
            e.Name == "dynamicValue" &&
            e.Source == EvaluationTraceResolutionSource.DynamicParameter)).IsTrue();
        await Assert.That(trace.Events.Any(e =>
            e.Kind == EvaluationTraceEventKind.FunctionResolved &&
            e.Name == "callback" &&
            e.Source == EvaluationTraceResolutionSource.Function)).IsTrue();
    }

    [Test]
    public async Task ShouldTraceExceptionsWithoutChangingThem()
    {
        var expression = CreateExpression("fail()");
        expression.Functions["fail"] = _ => throw new InvalidOperationException("boom");
        var trace = new EvaluationTrace();

        Assert.Throws<InvalidOperationException>(() => expression.Evaluate(trace));
        await Assert.That(trace.Events.Any(e => e.Kind == EvaluationTraceEventKind.Exception)).IsTrue();
    }

    [Test]
    public async Task ShouldIsolateTracesWhenReusingCachedSyntaxTree()
    {
        var first = new Expression("cached + 1");
        var second = new Expression("cached + 1");
        var firstTrace = new EvaluationTrace();
        var secondTrace = new EvaluationTrace();

        first.Parameters["cached"] = 1;
        second.Parameters["cached"] = 2;

        await Assert.That(first.Evaluate(firstTrace)).IsEqualTo(2);
        await Assert.That(second.Evaluate(secondTrace)).IsEqualTo(3);
        await Assert.That(secondTrace.Events.Any(e => e.Kind == EvaluationTraceEventKind.CacheHit)).IsTrue();
        await Assert.That(firstTrace.Events.Min(e => e.Sequence)).IsEqualTo(0);
        await Assert.That(secondTrace.Events.Min(e => e.Sequence)).IsEqualTo(0);
        await Assert.That(firstTrace.Events).IsNotSameReferenceAs(secondTrace.Events);
    }

    [Test]
    public async Task ShouldApplyRedactorBeforeStoringValues()
    {
        var expression = CreateExpression("secret");
        expression.Parameters["secret"] = "classified";
        var trace = new EvaluationTrace(traceEvent =>
            traceEvent.Name == "secret"
                ? traceEvent with { Value = "redacted" }
                : traceEvent);

        await Assert.That(expression.Evaluate(trace)).IsEqualTo("classified");
        await Assert.That(trace.Events.Any(e => e.Value as string == "classified")).IsFalse();
        await Assert.That(trace.Events.Any(e => e.Value as string == "redacted")).IsTrue();
    }

    [Test]
    public async Task ShouldTraceCompiledLambdaExecution()
    {
        var expression = new Expression("FieldA > 5 && FieldB = 'go'");
        var trace = new EvaluationTrace();
        var lambda = expression.ToTracedLambda<LambdaContext, bool>();
        await Assert.That(lambda(trace, new LambdaContext { FieldA = 7, FieldB = "go" })).IsTrue();
        await Assert.That(trace.Events.Any(e => e.Kind == EvaluationTraceEventKind.Skipped)).IsFalse();

        var shortCircuitTrace = new EvaluationTrace();
        await Assert.That(lambda(shortCircuitTrace, new LambdaContext { FieldA = 1, FieldB = "no" })).IsFalse();
        await Assert.That(shortCircuitTrace.Events.Any(e => e.Kind == EvaluationTraceEventKind.Skipped)).IsTrue();

        var ifExpression = new Expression("if(FieldA > 5, FieldB, 'stopped')");
        var ifTrace = new EvaluationTrace();
        var ifLambda = ifExpression.ToTracedLambda<LambdaContext, string?>();
        await Assert.That(ifLambda(ifTrace, new LambdaContext { FieldA = 1, FieldB = "no" })).IsEqualTo("stopped");
        await Assert.That(ifTrace.Events.Count(e => e.Kind == EvaluationTraceEventKind.Skipped)).IsPositive();
    }

    [Test]
    public async Task ShouldTraceCultureDifferences()
    {
        var invariantExpression = new Expression("1.5", CultureInfo.InvariantCulture);
        var germanCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        germanCulture.NumberFormat.NumberDecimalSeparator = ",";
        germanCulture.NumberFormat.NumberGroupSeparator = " ";
        var germanExpression = new Expression("1 * [value]", germanCulture);
        germanExpression.Parameters["value"] = "1,5";
        var invariantTrace = new EvaluationTrace();
        var germanTrace = new EvaluationTrace();

        await Assert.That(invariantExpression.Evaluate(invariantTrace)).IsTypeOf<double>();
        await Assert.That(germanExpression.Evaluate(germanTrace)).IsTypeOf<double>();
        await Assert.That(invariantTrace.Events.Count(e => e.Kind == EvaluationTraceEventKind.Exit)).IsPositive();
        await Assert.That(germanTrace.Events.Count(e => e.Kind == EvaluationTraceEventKind.Exit)).IsPositive();
    }

    private static Expression CreateExpression(string text)
    {
        return new Expression(text, new ExpressionConfiguration
        {
            Evaluation = new ExpressionEvaluationOptions
            {
                ConcurrentAsyncEvaluation = true
            }
        });
    }

    private static IReadOnlyList<(int NodeId, int ParentId, EvaluationTraceNodeKind Kind, string Name)> Relationships(
        EvaluationTrace trace)
    {
        return trace.Events
            .Where(e => e.Kind is EvaluationTraceEventKind.Enter or EvaluationTraceEventKind.Skipped)
            .Select(e => (e.NodeId, e.ParentNodeId, e.NodeKind, e.Name))
            .ToArray();
    }

    public sealed class LambdaContext
    {
        public int FieldA { get; set; }
        public string? FieldB { get; set; }
    }
}
