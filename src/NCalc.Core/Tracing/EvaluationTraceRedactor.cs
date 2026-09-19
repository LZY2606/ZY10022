namespace NCalc.Tracing;

/// <summary>
/// Rewrites an event before it is added to an <see cref="EvaluationTrace"/>.
/// </summary>
/// <param name="traceEvent">The event that is about to be recorded.</param>
/// <returns>The event to record.</returns>
public delegate EvaluationTraceEvent EvaluationTraceRedactor(EvaluationTraceEvent traceEvent);
