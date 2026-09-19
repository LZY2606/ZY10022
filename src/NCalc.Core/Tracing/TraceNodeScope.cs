namespace NCalc.Tracing;

/// <summary>
/// Represents the lifetime of one entered node in an <see cref="EvaluationTrace"/>.
/// </summary>
/// <remarks>
/// Dispose the scope when the node finishes. Successful disposal records an exit event;
/// call <see cref="Fault"/> with the thrown exception before disposing to record a fault event
/// instead. Instances follow the lifetime of a single node evaluation and must not be shared
/// between concurrent sibling branches.
/// </remarks>
public sealed class TraceNodeScope : IDisposable
{
    private readonly EvaluationTrace _trace;
    private readonly TraceNodeScope? _previousNode;
    private readonly TraceFunctionFrame? _previousFunction;
    private TraceFunctionFrame? _ownedFunctionFrame;
    private bool _completed;

    internal TraceNodeScope(
        EvaluationTrace trace,
        long nodeId,
        long parentId,
        TraceNodeKind nodeKind,
        string? name,
        TraceNodeScope? previousNode,
        TraceFunctionFrame? previousFunction)
    {
        _trace = trace;
        _previousNode = previousNode;
        _previousFunction = previousFunction;
        NodeId = nodeId;
        ParentId = parentId;
        NodeKind = nodeKind;
        Name = name;
    }

    /// <summary>
    /// The stable identifier of the entered node.
    /// </summary>
    public long NodeId { get; }

    /// <summary>
    /// The identifier of the enclosing node.
    /// </summary>
    public long ParentId { get; }

    /// <summary>
    /// The kind of the entered node.
    /// </summary>
    public TraceNodeKind NodeKind { get; }

    /// <summary>
    /// The node name when it has one.
    /// </summary>
    public string? Name { get; }

    internal void SetOwnedFunctionFrame(TraceFunctionFrame frame) => _ownedFunctionFrame = frame;

    public void Complete(object? value)
    {
        if (_completed)
            return;

        _completed = true;

        if (_ownedFunctionFrame is not null)
            _trace.CompleteFunctionFrame(_ownedFunctionFrame);

        _trace.RecordExit(NodeId, ParentId, NodeKind, Name, value);
    }

    public void Fault(Exception exception)
    {
        if (_completed)
            return;

        _completed = true;
        _trace.RecordFault(NodeId, ParentId, NodeKind, Name, exception);
    }

    /// <summary>
    /// Closes the scope, recording an exit event when <see cref="Fault"/> was not called.
    /// </summary>
    public void Dispose()
    {
        if (!_completed)
            Complete(null);

        _trace.RestoreFrames(_previousNode, _previousFunction);
    }
}
