namespace NCalc.Tracing;

/// <summary>
/// The kind of syntax tree node or evaluation boundary a <see cref="TraceEvent"/> belongs to.
/// </summary>
public enum TraceNodeKind
{
    /// <summary>
    /// The event is not associated with a syntax tree node, for example a parsed expression cache hit.
    /// </summary>
    None,

    /// <summary>
    /// The root boundary of one evaluation call.
    /// </summary>
    Evaluation,

    /// <summary>
    /// A constant value.
    /// </summary>
    Value,

    /// <summary>
    /// A parameter identifier.
    /// </summary>
    Identifier,

    /// <summary>
    /// A unary expression.
    /// </summary>
    Unary,

    /// <summary>
    /// A binary expression.
    /// </summary>
    Binary,

    /// <summary>
    /// A ternary (<c>condition ? whenTrue : whenFalse</c>) expression.
    /// </summary>
    Ternary,

    /// <summary>
    /// A function call.
    /// </summary>
    Function,

    /// <summary>
    /// A list of expressions, such as function arguments.
    /// </summary>
    List
}
