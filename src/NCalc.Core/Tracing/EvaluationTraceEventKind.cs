namespace NCalc.Tracing;

/// <summary>
/// Identifies the kind of event recorded by an <see cref="EvaluationTrace"/>.
/// </summary>
public enum EvaluationTraceEventKind
{
    /// <summary>
    /// A syntax node was entered.
    /// </summary>
    Enter,

    /// <summary>
    /// A syntax node completed successfully.
    /// </summary>
    Exit,

    /// <summary>
    /// A syntax node was skipped because of short-circuiting.
    /// </summary>
    Skipped,

    /// <summary>
    /// A parameter value was resolved.
    /// </summary>
    ParameterResolved,

    /// <summary>
    /// A function implementation was resolved.
    /// </summary>
    FunctionResolved,

    /// <summary>
    /// A parsed syntax tree was retrieved from the expression cache.
    /// </summary>
    CacheHit,

    /// <summary>
    /// A node terminated with an exception.
    /// </summary>
    Exception
}
