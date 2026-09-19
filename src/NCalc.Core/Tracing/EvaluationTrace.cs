namespace NCalc.Tracing;

/// <summary>
/// Collects isolated events for one optional evaluation trace.
/// </summary>
public sealed class EvaluationTrace
{
    private readonly object _gate = new();
    private readonly List<EvaluationTraceEvent> _events = [];
    private readonly EvaluationTraceRedactor? _redactor;
    private readonly AsyncLocal<int> _currentNodeId = new();
    private int _nextNodeId = 1;

    /// <summary>
    /// Creates a trace that optionally redacts each event before storing it.
    /// </summary>
    /// <param name="redactor">An optional callback used to replace sensitive event data.</param>
    public EvaluationTrace(EvaluationTraceRedactor? redactor = null)
    {
        _redactor = redactor;
    }

    /// <summary>
    /// Gets a snapshot of the events recorded so far.
    /// </summary>
    public IReadOnlyList<EvaluationTraceEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToArray();
        }
    }

    internal int CurrentNodeId
    {
        get => _currentNodeId.Value;
        set => _currentNodeId.Value = value;
    }

    internal Scope Begin(EvaluationTraceNodeKind nodeKind, string name, int parentNodeId)
    {
        var nodeId = AllocateNodeId();
        var scope = new Scope(this, nodeId, parentNodeId, nodeKind, name, _currentNodeId.Value);
        _currentNodeId.Value = nodeId;
        Record(nodeId, parentNodeId, EvaluationTraceEventKind.Enter, nodeKind, name);
        return scope;
    }

    /// <summary>
    /// Runs a node and records its enter, exit, and exception events.
    /// </summary>
    public T Run<T>(
        int parentNodeId,
        EvaluationTraceNodeKind nodeKind,
        string name,
        Func<int, T> evaluate)
    {
        var scope = Begin(nodeKind, name, parentNodeId);

        try
        {
            var result = evaluate(scope.NodeId);
            scope.Dispose(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fail(exception);
            throw;
        }
    }

    internal async Task<T> RunAsync<T>(
        int parentNodeId,
        EvaluationTraceNodeKind nodeKind,
        string name,
        Func<int, Task<T>> evaluate)
    {
        var scope = Begin(nodeKind, name, parentNodeId);
        Task<T> task;

        try
        {
            task = evaluate(scope.NodeId);
        }
        catch (Exception exception)
        {
            scope.Fail(exception);
            throw;
        }
        finally
        {
            _currentNodeId.Value = scope.ParentNodeId;
        }

        try
        {
            var result = await task.ConfigureAwait(false);
            scope.Dispose(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fail(exception);
            throw;
        }
    }

    /// <summary>
    /// Records a skipped node without evaluating it.
    /// </summary>
    public void Skip(
        int parentNodeId,
        EvaluationTraceNodeKind nodeKind,
        string name,
        string reason)
    {
        var nodeId = AllocateNodeId();
        Record(nodeId, parentNodeId, EvaluationTraceEventKind.Skipped, nodeKind, name, value: reason);
    }

    /// <summary>
    /// Records where a parameter value was resolved from.
    /// </summary>
    public void ResolveParameter(
        int nodeId,
        int parentNodeId,
        string name,
        EvaluationTraceResolutionSource source,
        object? value)
    {
        Record(nodeId, parentNodeId, EvaluationTraceEventKind.ParameterResolved,
            EvaluationTraceNodeKind.Identifier, name, source, value);
    }

    /// <summary>
    /// Records where a function result was resolved from.
    /// </summary>
    public void ResolveFunction(
        int nodeId,
        int parentNodeId,
        string name,
        EvaluationTraceResolutionSource source,
        object? value)
    {
        Record(nodeId, parentNodeId, EvaluationTraceEventKind.FunctionResolved,
            EvaluationTraceNodeKind.Function, name, source, value);
    }

    internal void CacheHit(string expression)
    {
        Record(0, 0, EvaluationTraceEventKind.CacheHit, EvaluationTraceNodeKind.Root, expression);
    }

    /// <summary>
    /// Runs a traced ternary branch and records the unselected branch as skipped.
    /// </summary>
    public T RunTernary<T>(
        int parentNodeId,
        EvaluationTraceNodeKind selectedKind,
        string selectedName,
        EvaluationTraceNodeKind unselectedKind,
        string unselectedName,
        Func<int, bool> condition,
        Func<int, T> whenTrue,
        Func<int, T> whenFalse)
    {
        return Run(parentNodeId, EvaluationTraceNodeKind.Ternary, "?:", nodeId =>
        {
            if (condition(nodeId))
            {
                var result = whenTrue(nodeId);
                Skip(nodeId, unselectedKind, unselectedName, "Short-circuit");
                return result;
            }

            var falseResult = whenFalse(nodeId);
            Skip(nodeId, selectedKind, selectedName, "Short-circuit");
            return falseResult;
        });
    }

    /// <summary>
    /// Runs a traced short-circuiting AND expression.
    /// </summary>
    public bool RunAndAlso(
        int parentNodeId,
        EvaluationTraceNodeKind rightKind,
        string rightName,
        Func<int, bool> left,
        Func<int, bool> right)
    {
        return Run(parentNodeId, EvaluationTraceNodeKind.Binary, "And", nodeId =>
        {
            if (!left(nodeId))
            {
                Skip(nodeId, rightKind, rightName, "Short-circuit");
                return false;
            }

            return right(nodeId);
        });
    }

    /// <summary>
    /// Runs a traced short-circuiting OR expression.
    /// </summary>
    public bool RunOrElse(
        int parentNodeId,
        EvaluationTraceNodeKind rightKind,
        string rightName,
        Func<int, bool> left,
        Func<int, bool> right)
    {
        return Run(parentNodeId, EvaluationTraceNodeKind.Binary, "Or", nodeId =>
        {
            if (left(nodeId))
            {
                Skip(nodeId, rightKind, rightName, "Short-circuit");
                return true;
            }

            return right(nodeId);
        });
    }

    /// <summary>
    /// Runs a traced coalescing expression.
    /// </summary>
    public T? RunCoalesce<T>(
        int parentNodeId,
        EvaluationTraceNodeKind rightKind,
        string rightName,
        Func<int, T?> left,
        Func<int, T?> right)
    {
        return Run(parentNodeId, EvaluationTraceNodeKind.Binary, "Coalesce", nodeId =>
        {
            var value = left(nodeId);
            if (value is not null)
            {
                Skip(nodeId, rightKind, rightName, "Short-circuit");
                return value;
            }

            return right(nodeId);
        });
    }

    /// <summary>
    /// Runs the lazy built-in <c>if</c> function with traced branch selection.
    /// </summary>
    public T RunFunctionIf<T>(
        int parentNodeId,
        EvaluationTraceNodeKind trueKind,
        string trueName,
        EvaluationTraceNodeKind falseKind,
        string falseName,
        Func<int, bool> condition,
        Func<int, T> whenTrue,
        Func<int, T> whenFalse)
    {
        var result = RunTernary(parentNodeId, trueKind, trueName, falseKind, falseName,
            condition, whenTrue, whenFalse);
        ResolveFunction(parentNodeId, parentNodeId, "if", EvaluationTraceResolutionSource.BuiltInFunction, result);
        return result;
    }

    internal int AllocateNodeId()
    {
        lock (_gate)
            return _nextNodeId++;
    }

    internal void Record(
        int nodeId,
        int parentNodeId,
        EvaluationTraceEventKind kind,
        EvaluationTraceNodeKind nodeKind,
        string name,
        EvaluationTraceResolutionSource source = EvaluationTraceResolutionSource.None,
        object? value = null,
        string? exceptionType = null)
    {
        var traceEvent = new EvaluationTraceEvent(0, nodeId, parentNodeId, kind, nodeKind, name, source, value, exceptionType);
        traceEvent = _redactor?.Invoke(traceEvent) ?? traceEvent;

        lock (_gate)
            _events.Add(traceEvent with { Sequence = _events.Count });
    }

    internal readonly struct Scope
    {
        private readonly EvaluationTrace _trace;

        internal Scope(EvaluationTrace trace, int nodeId, int parentNodeId,
            EvaluationTraceNodeKind nodeKind, string name, int previousNodeId)
        {
            _trace = trace;
            NodeId = nodeId;
            ParentNodeId = parentNodeId;
            NodeKind = nodeKind;
            Name = name;
            PreviousNodeId = previousNodeId;
        }

        internal int NodeId { get; }
        internal int ParentNodeId { get; }
        private EvaluationTraceNodeKind NodeKind { get; }
        private string Name { get; }
        private int PreviousNodeId { get; }

        public void Dispose(object? result)
        {
            _trace.CurrentNodeId = PreviousNodeId;
            _trace.Record(NodeId, ParentNodeId, EvaluationTraceEventKind.Exit, NodeKind, Name, value: result);
        }

        public void Fail(Exception exception)
        {
            _trace.CurrentNodeId = PreviousNodeId;
            _trace.Record(NodeId, ParentNodeId, EvaluationTraceEventKind.Exception, NodeKind, Name,
                exceptionType: exception.GetType().FullName);
        }
    }
}
