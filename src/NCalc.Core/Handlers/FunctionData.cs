using NCalc.Exceptions;
using NCalc.Tracing;
using NCalc.Visitors;

namespace NCalc.Handlers;

public class FunctionData(
    Guid id,
    LogicalExpressionList arguments,
    ExpressionContext context,
    ExpressionEvaluationOptions evaluationOptions,
    CultureInfo cultureInfo,
    ILogicalExpressionVisitor<object?> syncVisitor,
    ILogicalExpressionVisitor<Task<object?>>? asyncVisitor,
    CancellationToken cancellationToken,
    EvaluationTrace? trace = null,
    int parentNodeId = 0)
    : IReadOnlyList<LogicalExpression>
{
    private LogicalExpressionList Arguments { get; } = arguments;
    private ILogicalExpressionVisitor<object?> SyncVisitor { get; } = syncVisitor;
    private ILogicalExpressionVisitor<Task<object?>>? AsyncVisitor { get; } = asyncVisitor;
    private EvaluationTrace? Trace { get; } = trace;
    private int ParentNodeId { get; } = parentNodeId;
    private readonly bool[] _evaluatedArguments = new bool[arguments.Count];

    public Guid Id { get; } = id;

    public ExpressionContext Context { get; } = context;
    public ExpressionEvaluationOptions EvaluationOptions { get; } = evaluationOptions;
    public CultureInfo CultureInfo { get; } = cultureInfo;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public LogicalExpression this[int index] => Arguments[index];

    public Task<object?> EvaluateAsync(int index)
    {
        if (AsyncVisitor is null)
            throw new NCalcEvaluationException(
                "Asynchronous binary value evaluation is not available in this context.");

        _evaluatedArguments[index] = true;
        return AsyncVisitor is AsyncEvaluationVisitor asyncVisitor && Trace is not null
            ? asyncVisitor.EvaluateTracedChildAsync(Arguments[index], ParentNodeId)
            : Arguments[index].Accept(AsyncVisitor);
    }

    public object? Evaluate(int index)
    {
        _evaluatedArguments[index] = true;
        return SyncVisitor is EvaluationVisitor syncVisitor && Trace is not null
            ? syncVisitor.EvaluateTracedChild(Arguments[index], ParentNodeId)
            : Arguments[index].Accept(SyncVisitor);
    }

    internal void ReportSkippedArguments()
    {
        if (Trace is null)
            return;

        for (var index = 0; index < _evaluatedArguments.Length; index++)
        {
            if (_evaluatedArguments[index])
                continue;

            var argument = Arguments[index];
            Trace.Skip(ParentNodeId, EvaluationVisitor.GetNodeKind(argument),
                EvaluationVisitor.GetNodeName(argument), "Function argument was not evaluated");
        }
    }
    public int Count => Arguments.Count;

    public IEnumerator<LogicalExpression> GetEnumerator()
    {
        return Arguments.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return Arguments.GetEnumerator();
    }
}
