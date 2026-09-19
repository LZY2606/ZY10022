namespace NCalc.Tracing;

/// <summary>
/// Describes what happened during a single step of a traced evaluation.
/// </summary>
public enum TraceEventKind
{
    /// <summary>
    /// Evaluation of a node started.
    /// </summary>
    Enter,

    /// <summary>
    /// Evaluation of a node finished successfully.
    /// </summary>
    Exit,

    /// <summary>
    /// The node was never evaluated, for example because of short-circuiting or a lazy built-in function.
    /// </summary>
    Skipped,

    /// <summary>
    /// A parameter or function was resolved. See <see cref="TraceEvent.ResolutionSource"/> and <see cref="TraceEvent.Detail"/>.
    /// </summary>
    Resolved,

    /// <summary>
    /// The parsed syntax tree was reused from the expression cache.
    /// </summary>
    CacheHit,

    /// <summary>
    /// Evaluation of a node terminated with an exception.
    /// </summary>
    Fault
}
