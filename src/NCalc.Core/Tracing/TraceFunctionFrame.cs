namespace NCalc.Tracing;

/// <summary>
/// Internal bookkeeping for tracking which arguments of a lazy function were actually evaluated.
/// </summary>
internal sealed class TraceFunctionFrame
{
    public TraceFunctionFrame(TraceNodeScope scope, IReadOnlyList<LogicalExpression> arguments)
    {
        Scope = scope;
        Arguments = arguments;
        ArgumentsEvaluated = new bool[arguments.Count];
    }

    public TraceNodeScope Scope { get; }

    public IReadOnlyList<LogicalExpression> Arguments { get; }

    public bool[] ArgumentsEvaluated { get; }
}
