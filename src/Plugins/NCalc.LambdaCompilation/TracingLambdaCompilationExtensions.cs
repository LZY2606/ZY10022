using FastExpressionCompiler;
using NCalc.Tracing;
using NCalc.LambdaCompilation.Visitors;
using LinqExpression = System.Linq.Expressions.Expression;
using LinqParameterExpression = System.Linq.Expressions.ParameterExpression;

namespace NCalc.LambdaCompilation;

public static class TracingLambdaCompilationExtensions
{
    /// <summary>
    /// Compiles the expression into a delegate that records an <see cref="EvaluationTrace"/> while
    /// it runs.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="expression">The expression to compile.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A delegate taking a fresh trace per invocation and returning the result.</returns>
    /// <remarks>
    /// Pass a new <see cref="EvaluationTrace"/> instance to every invocation so sequence numbers and
    /// records stay isolated. The traced delegate uses the same event model as
    /// <see cref="NCalc.Expression.Evaluate(NCalc.Tracing.EvaluationTrace?,System.Threading.CancellationToken)"/>.
    /// </remarks>
    public static Func<EvaluationTrace, TResult> ToTracedLambda<TResult>(
        this Expression expression,
        CancellationToken cancellationToken = default)
    {
        var linq = expression.ToTracedLinqExpressionInternal<TResult>(cancellationToken);
        var lambda = System.Linq.Expressions.Expression.Lambda<Func<EvaluationTrace, TResult>>(
            linq.Expression, linq.TraceParameter);

        if (LambdaCompilationExtensions.UseSystemLinqCompilerPublic)
            return lambda.Compile();

        return lambda.CompileFast();
    }

    /// <summary>
    /// Compiles the expression into a contextual delegate that records an <see cref="EvaluationTrace"/>.
    /// </summary>
    /// <typeparam name="TContext">The type exposing parameters and functions as public members.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    public static Func<TContext, EvaluationTrace, TResult> ToTracedLambda<TContext, TResult>(
        this Expression expression,
        CancellationToken cancellationToken = default)
    {
        var linq = expression.ToTracedLinqExpressionInternal<TContext, TResult>(cancellationToken);
        var lambda = System.Linq.Expressions.Expression.Lambda<Func<TContext, EvaluationTrace, TResult>>(
            linq.Expression, linq.ContextParameter!, linq.TraceParameter);

        if (LambdaCompilationExtensions.UseSystemLinqCompilerPublic)
            return lambda.Compile();

        return lambda.CompileFast();
    }

    private static TracedLinqExpression ToTracedLinqExpressionInternal<TResult>(
        this Expression expression,
        CancellationToken cancellationToken)
    {
        expression.LogicalExpression ??= expression.GetLogicalExpression(cancellationToken);

        if (expression.LogicalExpression is null)
            throw expression.Error!;

        var traceParameter = System.Linq.Expressions.Expression.Parameter(typeof(EvaluationTrace), "trace");
        var visitor = new TracingLambdaExpressionVisitor(traceParameter, expression.Parameters,
            expression.EvaluationOptions);

        cancellationToken.ThrowIfCancellationRequested();

        var body = expression.LogicalExpression.Accept(visitor);
        if (body.Type != typeof(TResult))
            body = System.Linq.Expressions.Expression.Convert(body, typeof(TResult));

        return new TracedLinqExpression { Expression = body, TraceParameter = traceParameter };
    }

    private static TracedLinqExpression ToTracedLinqExpressionInternal<TContext, TResult>(
        this Expression expression,
        CancellationToken cancellationToken)
    {
        expression.LogicalExpression ??= expression.GetLogicalExpression(cancellationToken);

        if (expression.LogicalExpression is null)
            throw expression.Error!;

        var contextParameter = System.Linq.Expressions.Expression.Parameter(typeof(TContext), "ctx");
        var traceParameter = System.Linq.Expressions.Expression.Parameter(typeof(EvaluationTrace), "trace");
        var visitor = new TracingLambdaExpressionVisitor(contextParameter, traceParameter,
            expression.EvaluationOptions);

        cancellationToken.ThrowIfCancellationRequested();

        var body = expression.LogicalExpression.Accept(visitor);
        if (body.Type != typeof(TResult))
            body = System.Linq.Expressions.Expression.Convert(body, typeof(TResult));

        return new TracedLinqExpression
        {
            Expression = body,
            ContextParameter = contextParameter,
            TraceParameter = traceParameter
        };
    }

    private sealed class TracedLinqExpression
    {
        public LinqExpression Expression { get; init; } = null!;
        public LinqParameterExpression? ContextParameter { get; init; }
        public LinqParameterExpression TraceParameter { get; init; } = null!;
    }
}
