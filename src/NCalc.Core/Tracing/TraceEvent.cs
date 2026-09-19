namespace NCalc.Tracing;

/// <summary>
/// A single immutable record of a traced expression evaluation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sequence"/> is the stable, zero-based position of the record within one evaluation.
/// <see cref="NodeId"/> identifies the node the record belongs to and is shared by the enter, exit,
/// fault and skipped records of that node; child nodes always receive larger identifiers than their
/// parents, so identifiers follow the logical pre-order of the syntax tree even when asynchronous
/// evaluation completes branches in a different order. <see cref="ParentId"/> is the identifier of
/// the enclosing node, or <c>0</c> for the root node and cache events.
/// </para>
/// </remarks>
public sealed record TraceEvent
{
    /// <summary>
    /// Zero-based order of this record within one evaluation.
    /// </summary>
    public long Sequence { get; internal init; }

    /// <summary>
    /// Stable identifier of the node within one evaluation. <c>0</c> marks the root evaluation boundary
    /// and events not attached to a syntax tree node, such as parsed expression cache hits.
    /// </summary>
    public required long NodeId { get; init; }

    /// <summary>
    /// Identifier of the enclosing node, or <c>0</c> when there is no parent.
    /// </summary>
    public required long ParentId { get; init; }

    /// <summary>
    /// What happened.
    /// </summary>
    public required TraceEventKind Kind { get; init; }

    /// <summary>
    /// The kind of node the record belongs to.
    /// </summary>
    public required TraceNodeKind NodeKind { get; init; }

    /// <summary>
    /// The parameter or function name when the node has one.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The evaluated value for <see cref="TraceEventKind.Exit"/> events, or a replacement/description
    /// value for <see cref="TraceEventKind.Resolved"/> events. Values pass through the configured
    /// <see cref="TraceValueRedactor"/> before being stored.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// The exception that terminated the node for <see cref="TraceEventKind.Fault"/> events.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// The reason a node was skipped.
    /// </summary>
    public TraceSkipReason SkipReason { get; init; }

    /// <summary>
    /// Where a parameter or function was resolved.
    /// </summary>
    public TraceResolutionSource ResolutionSource { get; init; }

    /// <summary>
    /// Additional human readable information, such as the built-in function name or the skip target.
    /// </summary>
    public string? Detail { get; init; }
}
