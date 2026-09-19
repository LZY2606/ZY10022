using System.Runtime.CompilerServices;
using FastExpressionCompiler;
using NCalc.LambdaCompilation.Visitors;
using NCalc.Tracing;
using LinqExpression = System.Linq.Expressions.Expression;
using LinqParameterExpression = System.Linq.Expressions.ParameterExpression;

namespace NCalc.LambdaCompilation;

public static class LambdaCompilationExtensions
{
    private static readonly bool UseSystemLinqCompiler;

    static LambdaCompilationExtensions()
    {
        UseSystemLinqCompiler = AppContext.TryGetSwitch("NCalc.UseSystemLinqCompiler", out var enabled) && enabled;
    }

#if !DOCFX
    /// <summary>Compiles an expression to a parameterless delegate.</summary>
    public static Func<TResult> ToLambda<TResult>(this Expression expression,
        CancellationToken cancellationToken = default)
    {
        var linq = Build(expression, typeof(Void), typeof(TResult), null, cancellationToken);
        var lambda = LinqExpression.Lambda<Func<TResult>>(linq.Expression);
        return UseSystemLinqCompiler ? lambda.Compile() : lambda.CompileFast();
    }

    /// <summary>Compiles an expression to a delegate that records each invocation in the supplied trace.</summary>
    public static Func<EvaluationTrace, TResult> ToTracedLambda<TResult>(this Expression expression,
        CancellationToken cancellationToken = default)
    {
        var traceParameter = LinqExpression.Parameter(typeof(EvaluationTrace), "trace");
        var linq = Build(expression, typeof(Void), typeof(TResult), traceParameter, cancellationToken);
        var lambda = LinqExpression.Lambda<Func<EvaluationTrace, TResult>>(linq.Expression, traceParameter);
        return UseSystemLinqCompiler ? lambda.Compile() : lambda.CompileFast();
    }

    /// <summary>Compiles an expression to a delegate that receives a strongly typed context object.</summary>
    public static Func<TContext, TResult> ToLambda<TContext, TResult>(this Expression expression,
        CancellationToken cancellationToken = default)
    {
        var linq = Build(expression, typeof(TContext), typeof(TResult), null, cancellationToken);
        var lambda = LinqExpression.Lambda<Func<TContext, TResult>>(linq.Expression, linq.Parameter!);
        return UseSystemLinqCompiler ? lambda.Compile() : lambda.CompileFast();
    }

    /// <summary>Compiles a context-aware expression and records each invocation in the supplied trace.</summary>
    public static Func<EvaluationTrace, TContext, TResult> ToTracedLambda<TContext, TResult>(
        this Expression expression, CancellationToken cancellationToken = default)
    {
        var traceParameter = LinqExpression.Parameter(typeof(EvaluationTrace), "trace");
        var linq = Build(expression, typeof(TContext), typeof(TResult), traceParameter, cancellationToken);
        var lambda = LinqExpression.Lambda<Func<EvaluationTrace, TContext, TResult>>(
            linq.Expression, traceParameter, linq.Parameter!);
        return UseSystemLinqCompiler ? lambda.Compile() : lambda.CompileFast();
    }

    /// <summary>Builds a LINQ expression for a parameterless invocation.</summary>
    public static LinqExpression ToLinqExpression<TResult>(this Expression expression,
        CancellationToken cancellationToken = default)
    {
        return Build(expression, typeof(Void), typeof(TResult), null, cancellationToken).Expression;
    }

    /// <summary>Builds a LINQ expression that records invocation events in the supplied trace.</summary>
    public static LinqExpression ToTracedLinqExpression<TResult>(this Expression expression,
        EvaluationTrace trace, CancellationToken cancellationToken = default)
    {
        return Build(expression, typeof(Void), typeof(TResult), LinqExpression.Constant(trace), cancellationToken)
            .Expression;
    }

    /// <summary>Builds a LINQ expression and its context parameter.</summary>
    public static LinqExpressionWithParameter ToLinqExpression<TContext, TResult>(this Expression expression,
        CancellationToken cancellationToken = default)
    {
        return Build(expression, typeof(TContext), typeof(TResult), null, cancellationToken);
    }

    /// <summary>Builds a traced LINQ expression and its context parameter.</summary>
    public static LinqExpressionWithParameter ToTracedLinqExpression<TContext, TResult>(this Expression expression,
        EvaluationTrace trace, CancellationToken cancellationToken = default)
    {
        return Build(expression, typeof(TContext), typeof(TResult), LinqExpression.Constant(trace), cancellationToken);
    }
#endif

    private static LinqExpressionWithParameter Build(Expression expression, Type contextType, Type resultType,
        LinqExpression? trace, CancellationToken cancellationToken)
    {
        expression.LogicalExpression ??= expression.GetLogicalExpression(cancellationToken);

        if (expression.LogicalExpression is null)
            throw expression.Error!;

        LambdaExpressionVisitor visitor;
        LinqParameterExpression? parameter = null;
        LinqParameterExpression? rootId = null;
        LinqExpression rootParentId = LinqExpression.Constant(0);
        if (trace is not null)
        {
            rootId = LinqExpression.Parameter(typeof(int), "rootId");
            rootParentId = rootId;
        }

        if (contextType == typeof(Void))
        {
            visitor = trace is null
                ? new LambdaExpressionVisitor(expression.Parameters, expression.EvaluationOptions)
                : new LambdaExpressionVisitor(expression.Parameters, expression.EvaluationOptions, trace,
                    rootParentId);
        }
        else
        {
            parameter = LinqExpression.Parameter(contextType, "ctx");
            visitor = trace is null
                ? new LambdaExpressionVisitor(parameter, expression.EvaluationOptions)
                : new LambdaExpressionVisitor(parameter, expression.EvaluationOptions, trace,
                    rootParentId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var body = expression.LogicalExpression.Accept(visitor);

        if (trace is not null)
        {
            var runMethod = typeof(EvaluationTrace).GetMethods()
                .First(method => method.Name == nameof(EvaluationTrace.Run) &&
                                method.IsGenericMethodDefinition &&
                                method.GetParameters().Length == 4)!
                .MakeGenericMethod(body.Type);
            body = LinqExpression.Call(trace, runMethod,
                LinqExpression.Constant(0),
                LinqExpression.Constant(EvaluationTraceNodeKind.Root),
                LinqExpression.Constant(expression.ExpressionString ?? string.Empty),
                LinqExpression.Lambda(body, rootId!));
        }

        if (body.Type != resultType)
            body = LinqExpression.Convert(body, resultType);

        return new LinqExpressionWithParameter { Expression = body, Parameter = parameter };
    }
}
