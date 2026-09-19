# Evaluation Tracing

`EvaluationTrace` records an optional diagnostic trail for one evaluation. Create a new trace for each call; traces do not use global state and can be reused safely with a compiled lambda. Tracing remains off unless a trace object is supplied.

## Synchronous and asynchronous evaluation

The following example is compiled as part of `TraceTests`.

```csharp
using NCalc;
using NCalc.Tracing;

var expression = new Expression("secret + 1");
expression.Parameters["secret"] = 41;

var trace = new EvaluationTrace(traceEvent =>
    traceEvent.Name == "secret"
        ? traceEvent with { Value = "<redacted>" }
        : traceEvent);

var result = expression.Evaluate(trace);

foreach (var traceEvent in trace.Events)
{
    Console.WriteLine($"{traceEvent.Sequence}: {traceEvent.NodeId} -> {traceEvent.ParentNodeId} {traceEvent.Kind}");
}

// result is 42, and trace events show enter, resolution, and exit events.
```

Use `EvaluateAsync(trace)` for asynchronous handlers. Both paths use the same event types and logical parent identifiers. Short-circuited branches produce `Skipped` events and are never evaluated.

## Compiled lambdas

The Lambda Compilation plugin can compile tracing into the delegate itself.

```csharp
using NCalc;
using NCalc.LambdaCompilation;
using NCalc.Tracing;

var expression = new Expression("FieldA > 5 && FieldB = 'go'");
var evaluate = expression.ToTracedLambda<Order, bool>();

var trace = new EvaluationTrace();
var allowed = evaluate(trace, new Order { FieldA = 7, FieldB = "go" });

public sealed class Order
{
    public int FieldA { get; set; }
    public string? FieldB { get; set; }
}
```

## Event meaning

- `Enter` and `Exit` bracket a node execution.
- `Skipped` represents a branch that short-circuiting did not evaluate.
- `ParameterResolved` and `FunctionResolved` identify the handler, dictionary, nested expression, or built-in implementation used.
- `CacheHit` identifies reuse of a parsed syntax tree.
- `Exception` marks the node where evaluation terminated; the original exception still propagates.
