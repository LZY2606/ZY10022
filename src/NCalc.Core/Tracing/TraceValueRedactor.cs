namespace NCalc.Tracing;

/// <summary>
/// Rewrites a captured value before it is written to an <see cref="EvaluationTrace"/>.
/// </summary>
/// <param name="nodeKind">The kind of node that produced the value.</param>
/// <param name="name">The parameter or function name when applicable; otherwise <see langword="null"/>.</param>
/// <param name="value">The value about to be captured, possibly sensitive.</param>
/// <returns>The replacement value stored in the trace. Return the input unchanged to keep the value as-is.</returns>
/// <remarks>
/// The redactor is only invoked for traced evaluations and can be used to hide secrets, for example by
/// replacing a password parameter with <c>"***"</c>.
/// </remarks>
public delegate object? TraceValueRedactor(TraceNodeKind nodeKind, string? name, object? value);
