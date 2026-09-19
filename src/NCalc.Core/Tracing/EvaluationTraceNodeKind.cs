namespace NCalc.Tracing;

/// <summary>
/// Identifies the kind of syntax node represented by a trace event.
/// </summary>
public enum EvaluationTraceNodeKind
{
    /// <summary>
    /// The root of one evaluation.
    /// </summary>
    Root,

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
    /// A ternary expression.
    /// </summary>
    Ternary,

    /// <summary>
    /// A function call.
    /// </summary>
    Function,

    /// <summary>
    /// An expression list.
    /// </summary>
    List
}
