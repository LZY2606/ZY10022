using NCalc.Extensions;

namespace NCalc.Tracing;

/// <summary>
/// Collects an optional explanation trace of one expression evaluation.
/// </summary>
/// <remarks>
/// <para>
/// Create one instance per evaluation and pass it to an <c>Evaluate</c> overload that accepts a trace.
/// When tracing is not requested, NCalc performs no tracking and allocates no trace objects, so the
/// default code path is unchanged.
/// </para>
/// <para>
/// Each record has a stable <see cref="TraceEvent.NodeId"/> shared by its enter/exit/fault/skipped
/// records and a <see cref="TraceEvent.ParentId"/> describing the logical syntax tree hierarchy.
/// During asynchronous evaluation branches may complete in any order, but parent identifiers always
/// describe logical nesting. Identifiers and events are never shared between evaluations: reuse of the
/// parsed syntax tree (including the expression cache) does not share sequence numbers or redactors.
/// </para>
/// <para>
/// Tracing observes the ordinary evaluation only. Nodes are never evaluated a second time to build
/// the trace, short-circuited branches only produce <see cref="TraceEventKind.Skipped"/> records, and
/// parameter or function callbacks with side effects are still invoked exactly once.
/// </para>
/// </remarks>
public sealed class EvaluationTrace
{
    private readonly object _gate = new();
    private readonly List<TraceEvent> _events = [];
    private readonly TraceValueRedactor? _redactor;

    private long _nextNodeId = 1;

    // Logical nesting of the node currently executing on this async flow.
    private readonly AsyncLocal<TraceNodeScope?> _currentNode = new();

    // Nearest enclosing lazy function call, used to mark arguments that were never evaluated.
    private readonly AsyncLocal<TraceFunctionFrame?> _currentFunction = new();

    /// <summary>
    /// Creates an empty trace.
    /// </summary>
    /// <param name="redactor">
    /// Optional callback used to rewrite captured values before they are stored. Use it to hide
    /// sensitive parameters or results.
    /// </param>
    public EvaluationTrace(TraceValueRedactor? redactor = null)
    {
        _redactor = redactor;
    }

    /// <summary>
    /// The records collected so far, in emission order. Returns a snapshot safe to enumerate.
    /// </summary>
    public IReadOnlyList<TraceEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToArray();
        }
    }

    /// <summary>
    /// Opens the root boundary of one evaluation. A root has no parent node.
    /// </summary>
    public TraceNodeScope EnterRoot()
    {
        return Enter(TraceNodeKind.Evaluation, null);
    }

    /// <summary>
    /// Opens a syntax tree node. Node identifiers follow logical pre-order, so a child's identifier
    /// is always larger than its parent's.
    /// </summary>
    public TraceNodeScope EnterNode(TraceNodeKind nodeKind, string? name = null)
    {
        return Enter(nodeKind, name);
    }

    /// <summary>
    /// Records that a node was never evaluated. The skipped node receives a new identifier whose
    /// parent is the enclosing node; sibling identifiers therefore still follow logical order.
    /// </summary>
    public void SkipNode(TraceNodeKind nodeKind, TraceSkipReason reason, string? name = null, string? detail = null)
    {
        var parent = _currentNode.Value;
        AddEvent(new TraceEvent
        {
            NodeId = AllocateNodeId(),
            ParentId = parent?.NodeId ?? 0,
            Kind = TraceEventKind.Skipped,
            NodeKind = nodeKind,
            Name = name,
            SkipReason = reason,
            Detail = detail
        });
    }

    /// <summary>
    /// Records where a parameter was resolved and the (redacted) value it provided.
    /// </summary>
    public void ResolvedParameter(string name, TraceResolutionSource source, object? value, string? detail = null)
    {
        var current = _currentNode.Value;
        AddEvent(new TraceEvent
        {
            NodeId = current?.NodeId ?? 0,
            NodeKind = TraceNodeKind.Identifier,
            ParentId = current?.ParentId ?? 0,
            Kind = TraceEventKind.Resolved,
            Name = name,
            Value = Redact(TraceNodeKind.Identifier, name, value),
            ResolutionSource = source,
            Detail = detail
        });
    }

    /// <summary>
    /// Records where a function was resolved and its (redacted) result.
    /// </summary>
    public void ResolvedFunction(string name, TraceResolutionSource source, object? value, string? detail = null)
    {
        var current = _currentNode.Value;
        AddEvent(new TraceEvent
        {
            NodeId = current?.NodeId ?? 0,
            NodeKind = TraceNodeKind.Function,
            ParentId = current?.ParentId ?? 0,
            Kind = TraceEventKind.Resolved,
            Name = name,
            Value = Redact(TraceNodeKind.Function, name, value),
            ResolutionSource = source,
            Detail = detail
        });
    }

    /// <summary>
    /// Records that the parsed expression was reused from the parsed expression cache.
    /// </summary>
    public void RecordCacheHit(string expression)
    {
        var parent = _currentNode.Value;
        AddEvent(new TraceEvent
        {
            NodeId = 0,
            ParentId = parent?.NodeId ?? 0,
            Kind = TraceEventKind.CacheHit,
            NodeKind = TraceNodeKind.None,
            Detail = expression
        });
    }

    /// <summary>
    /// Begins tracking the arguments of a function call so arguments that are never evaluated
    /// (for example the untaken branches of the built-in <c>if</c>) are reported as skipped when
    /// the function exits.
    /// </summary>
    /// <summary>
    /// Opens a function node and starts tracking which of its arguments are actually evaluated.
    /// </summary>
    internal TraceNodeScope EnterFunction(string name, IReadOnlyList<LogicalExpression> arguments)
    {
        var scope = Enter(TraceNodeKind.Function, name, out var previousFunction);
        var frame = new TraceFunctionFrame(scope, arguments);
        scope.SetOwnedFunctionFrame(frame);
        _currentFunction.Value = frame;
        return scope;
    }

    /// <summary>
    /// Marks <paramref name="argument"/> of the innermost tracked function frame as evaluated.
    /// Called immediately before a function argument is accepted so side-effecting callbacks are
    /// never invoked twice and never executed just to complete the trace.
    /// </summary>
    internal void MarkFunctionArgument(LogicalExpression argument)
    {
        var frame = _currentFunction.Value;
        if (frame is null)
            return;

        var arguments = frame.Arguments;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (ReferenceEquals(arguments[index], argument))
            {
                frame.ArgumentsEvaluated[index] = true;
                return;
            }
        }
    }

    /// <summary>
    /// Emits skipped records for arguments that were never evaluated, then leaves the function frame.
    /// </summary>
    internal void CompleteFunctionFrame(TraceFunctionFrame frame)
    {
        var evaluated = frame.ArgumentsEvaluated;
        var arguments = frame.Arguments;
        for (var index = 0; index < evaluated.Length; index++)
        {
            if (evaluated[index])
                continue;

            var argument = arguments[index];
            SkipNode(GetNodeKind(argument), TraceSkipReason.LazyFunctionArgument,
                GetNodeName(argument), $"Argument {index} of '{frame.Scope.Name}'");
        }
    }

    /// <summary>
    /// Enters a node from code generated by lambda compilation.
    /// </summary>
    public TraceNodeScope EnterCompiledNode(TraceNodeKind nodeKind, string? name) => EnterNode(nodeKind, name);

    /// <summary>
    /// Reports a skipped node from code generated by lambda compilation.
    /// </summary>
    public void SkipCompiledNode(TraceNodeKind nodeKind, TraceSkipReason reason, string? name, string? detail)
        => SkipNode(nodeKind, reason, name, detail);

    /// <summary>
    /// Reports a resolved parameter from code generated by lambda compilation.
    /// </summary>
    public void ResolveCompiledParameter(string name, TraceResolutionSource source, object? value)
        => ResolvedParameter(name, source, value);

    /// <summary>
    /// Reports a resolved function from code generated by lambda compilation.
    /// </summary>
    public void ResolveCompiledFunction(string name, TraceResolutionSource source, object? value, string? detail)
        => ResolvedFunction(name, source, value, detail);

    public static TraceNodeKind GetNodeKind(LogicalExpression expression) => expression switch
    {
        BinaryExpression => TraceNodeKind.Binary,
        TernaryExpression => TraceNodeKind.Ternary,
        UnaryExpression => TraceNodeKind.Unary,
        Function => TraceNodeKind.Function,
        Identifier => TraceNodeKind.Identifier,
        ValueExpression => TraceNodeKind.Value,
        LogicalExpressionList => TraceNodeKind.List,
        _ => TraceNodeKind.None
    };

    public static string? GetNodeName(LogicalExpression expression) => expression switch
    {
        Function function => function.Identifier.Name,
        Identifier identifier => identifier.Name,
        ValueExpression value => value.ToExpressionString(),
        _ => null
    };

    internal void RecordExit(long nodeId, long parentId, TraceNodeKind nodeKind, string? name, object? value)
    {
        AddEvent(new TraceEvent
        {
            NodeId = nodeId,
            ParentId = parentId,
            Kind = TraceEventKind.Exit,
            NodeKind = nodeKind,
            Name = name,
            Value = Redact(nodeKind, name, value)
        });
    }

    internal void RecordFault(long nodeId, long parentId, TraceNodeKind nodeKind, string? name, Exception exception)
    {
        AddEvent(new TraceEvent
        {
            NodeId = nodeId,
            ParentId = parentId,
            Kind = TraceEventKind.Fault,
            NodeKind = nodeKind,
            Name = name,
            Exception = exception
        });
    }

    internal void RestoreFrames(TraceNodeScope? nodeFrame, TraceFunctionFrame? functionFrame)
    {
        _currentNode.Value = nodeFrame;
        _currentFunction.Value = functionFrame;
    }

    private TraceNodeScope Enter(TraceNodeKind nodeKind, string? name)
        => Enter(nodeKind, name, out _);

    private TraceNodeScope Enter(TraceNodeKind nodeKind, string? name,
        out TraceFunctionFrame? previousFunction)
    {
        var parent = _currentNode.Value;
        previousFunction = _currentFunction.Value;
        var parentId = parent?.NodeId ?? 0;
        var nodeId = AllocateNodeId();

        AddEvent(new TraceEvent
        {
            NodeId = nodeId,
            ParentId = parentId,
            Kind = TraceEventKind.Enter,
            NodeKind = nodeKind,
            Name = name
        });

        var scope = new TraceNodeScope(this, nodeId, parentId, nodeKind, name, parent, previousFunction);
        _currentNode.Value = scope;
        return scope;
    }

    private long AllocateNodeId() => Interlocked.Increment(ref _nextNodeId) - 1;

    private void AddEvent(TraceEvent traceEvent)
    {
        lock (_gate)
        {
            traceEvent = traceEvent with { Sequence = _events.Count };
            _events.Add(traceEvent);
        }
    }

    private object? Redact(TraceNodeKind nodeKind, string? name, object? value)
        => _redactor is null ? value : _redactor(nodeKind, name, value);
}
