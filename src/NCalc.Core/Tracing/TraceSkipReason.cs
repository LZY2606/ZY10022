namespace NCalc.Tracing;

/// <summary>
/// Why a node produced a <see cref="TraceEventKind.Skipped"/> event without being evaluated.
/// </summary>
public enum TraceSkipReason
{
    /// <summary>
    /// The node was not skipped.
    /// </summary>
    None,

    /// <summary>
    /// The right operand of a logical <c>and</c>/<c>&amp;&amp;</c> was skipped because the left side was false.
    /// </summary>
    ShortCircuitAnd,

    /// <summary>
    /// The right operand of a logical <c>or</c>/<c>||</c> was skipped because the left side was true.
    /// </summary>
    ShortCircuitOr,

    /// <summary>
    /// The right operand of a coalescing (<c>??</c>) expression was skipped because the left side was not null.
    /// </summary>
    Coalesce,

    /// <summary>
    /// The branch of a ternary expression that the condition did not select.
    /// </summary>
    TernaryBranch,

    /// <summary>
    /// A branch of a lazy built-in function such as <c>if</c>, <c>ifs</c> or <c>in</c> that was not needed.
    /// </summary>
    LazyFunctionArgument
}
