# Evaluation Tracing

When an expression returns an unexpected value it is often hard to answer questions such as:

- Which branch of an `if`/`&&`/`||` expression actually ran?
- Where did a parameter value come from: the static dictionary, a dynamic handler, or an event?
- Was a custom function called, or was its argument short-circuited away?
- Did evaluation reuse the parsed expression cache?
- Which node threw the exception?

<xref:NCalc.Tracing.EvaluationTrace> answers these questions by recording an **explanation trace** of one
evaluation, without changing the value returned by `Evaluate`.

## Enabling a trace

Tracing is opt-in. Create a fresh <xref:NCalc.Tracing.EvaluationTrace> and pass it to one of the `Evaluate`
overloads that accept a trace. When tracing is not requested, NCalc performs no tracking and allocates no
trace objects, so the default code path is unchanged.

```csharp
using NCalc.Tracing;

var expression = new Expression("if(enabled, secret, fallback())");
expression.Parameters["enabled"] = true;
expression.Parameters["secret"] = "password123";
expression.Functions["fallback"] = _ => "no-secret";

var trace = new EvaluationTrace();

var result = expression.Evaluate(trace);

foreach (var traceEvent in trace.Events)
{
    Console.WriteLine(
        $"#{traceEvent.Sequence} node={traceEvent.NodeId} parent={traceEvent.ParentId} " +
        $"{traceEvent.Kind} {traceEvent.NodeKind} {traceEvent.Name}");
}
```

The asynchronous API is identical:

```csharp
var trace = new EvaluationTrace();
var result = await expression.EvaluateAsync(trace);
```

The same event model is also used by the lambda compilation plugin through
`ToTracedLambda<TResult>()` and `ToTracedLambda<TContext, TResult>()`. The delegate takes a fresh
trace for each invocation:

```csharp
var compiled = new Expression("x > 0 && Positive(x)").ToTracedLambda<Order, bool>();
var trace = new EvaluationTrace();
var approved = compiled(new Order { X = 1 }, trace);
```

## Records, identifiers, and parent nodes

Each <xref:NCalc.Tracing.TraceEvent> contains:

- <xref:NCalc.Tracing.TraceEvent.Sequence>: the zero-based emission order within one evaluation.
- <xref:NCalc.Tracing.TraceEvent.NodeId>: a stable identifier shared by the enter, exit, fault and
  skipped records of one node. Child identifiers are always larger than their parent's.
- <xref:NCalc.Tracing.TraceEvent.ParentId>: the enclosing node, or `0` for the root evaluation boundary.

During asynchronous evaluation branches can finish in any order (especially with
`ConcurrentAsyncEvaluation`), but `ParentId` always describes the logical syntax tree. Identifiers and
events are never shared between evaluations: two evaluations that reuse the same parsed syntax tree
(through the expression cache) still get independent sequences and independent traces.

The recorded <xref:NCalc.Tracing.TraceEventKind> values are:

| Kind | Meaning |
| --- | --- |
| `Enter` | Evaluation of a node started. |
| `Exit` | Evaluation of a node finished; `Value` contains the result. |
| `Skipped` | The node was never evaluated (short circuit, coalesce, lazy built-in argument). |
| `Resolved` | A parameter or function was resolved; `ResolutionSource` says where the value came from. |
| `CacheHit` | The parsed syntax tree was reused from the expression cache. |
| `Fault` | The node terminated with an exception, available in `Exception`. |

## Short-circuiting and lazy branches

Tracing never evaluates a node a second time to collect data, and parameters or functions with side
effects are still invoked exactly once. Branches that ordinary evaluation skips only produce a `Skipped`
record:

```csharp
var calls = 0;
var expression = new Expression("if(true, 1, boom())");
expression.Functions["boom"] = _ => calls++;

var trace = new EvaluationTrace();
var result = expression.Evaluate(trace);

// result == 1, calls == 0, and the trace contains one skipped node with
// SkipReason == TraceSkipReason.LazyFunctionArgument.
```

The same applies to `&&` (`ShortCircuitAnd`), `||` (`ShortCircuitOr`), `?:` (`TernaryBranch`), and `??`
(`Coalesce`).

## Parameter and function sources

`Resolved` events identify how a value was supplied through
<xref:NCalc.Tracing.TraceResolutionSource>:

- `ParameterHandler` / `AsyncParameterHandler`: the `EvaluateParameter` / `EvaluateAsyncParameter` events.
- `StaticParameter`: the `Parameters` dictionary.
- `DynamicParameter` / `AsyncParameter`: callback dictionaries.
- `FunctionHandler` / `AsyncFunctionHandler`: the `EvaluateFunction` / `EvaluateAsyncFunction` events.
- `Function` / `AsyncFunction`: registered `Functions` / `AsyncFunctions`.
- `BuiltInFunction`: built-in functions such as `Max` or `if`.
- `NullKeyword`: the built-in `null` identifier.

## Redacting sensitive values

Captured exit and resolution values can contain secrets. Pass an
<xref:NCalc.Tracing.TraceValueRedactor> to the trace to rewrite values before they are stored. The
redactor runs as part of the traced evaluation only; the value returned by `Evaluate` is not affected.

```csharp
var trace = new EvaluationTrace((nodeKind, name, value) =>
    name == "secret" ? "***redacted***" : value);

var expression = new Expression("if(enabled, secret, fallback())");
expression.Parameters["enabled"] = true;
expression.Parameters["secret"] = "password123";
expression.Functions["fallback"] = _ => "no-secret";

var result = expression.Evaluate(trace);

// result is still "password123"; the trace stores "***redacted***" for the secret parameter,
// plus a skipped node for the fallback() branch.
```

This complete example is compiled and executed as the `TraceDocumentationExample` test in
`test/NCalc.Tests/TraceTests.cs`, so it cannot drift into an API that only exists in the documentation.

## Cache hits

When the parsed expression cache provides the syntax tree, a `CacheHit` record is written before the
tree is evaluated. A fresh <xref:NCalc.Tracing.EvaluationTrace> starts sequence numbering at zero
again, so cached reuse never leaks records or sequence numbers between evaluations.
