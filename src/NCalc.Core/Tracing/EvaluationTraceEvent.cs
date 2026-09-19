namespace NCalc.Tracing;

/// <summary>
/// Contains one immutable event recorded during expression evaluation.
/// </summary>
/// <param name="Sequence">Stable zero-based sequence number within one evaluation.</param>
/// <param name="NodeId">Stable identifier of the node within one evaluation.</param>
/// <param name="ParentNodeId">Identifier of the logical parent node, or <c>0</c> for the root.</param>
/// <param name="Kind">The event kind.</param>
/// <param name="NodeKind">The kind of syntax node.</param>
/// <param name="Name">The node, parameter, or function name.</param>
/// <param name="Source">The parameter or function resolution source.</param>
/// <param name="Value">The redacted resolved value or node result, when available.</param>
/// <param name="ExceptionType">The exception type name for an exception event.</param>
public sealed record EvaluationTraceEvent(
    int Sequence,
    int NodeId,
    int ParentNodeId,
    EvaluationTraceEventKind Kind,
    EvaluationTraceNodeKind NodeKind,
    string Name,
    EvaluationTraceResolutionSource Source = EvaluationTraceResolutionSource.None,
    object? Value = null,
    string? ExceptionType = null);
