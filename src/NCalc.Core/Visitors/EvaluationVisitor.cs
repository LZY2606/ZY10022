using NCalc.Factories;
using NCalc.Handlers;
using NCalc.Helpers;
using NCalc.Exceptions;
using NCalc.Tracing;
using static NCalc.Helpers.EvaluationHelper;

namespace NCalc.Visitors;

/// <summary>
/// Class responsible to synchronously evaluating <see cref="LogicalExpression"/>.
/// </summary>
public class EvaluationVisitor(
    ExpressionContext context,
    ExpressionEvaluationOptions options,
    CultureInfo cultureInfo,
    IEvaluationVisitorFactory? evaluationVisitorFactory = null,
    CancellationToken cancellationToken = default) : ILogicalExpressionVisitor<object?>
{
    protected CancellationToken CancellationToken { get; } = cancellationToken;
    protected IEvaluationVisitorFactory? EvaluationVisitorFactory { get; } = evaluationVisitorFactory;

    private EvaluationTrace? Trace => context.Tracer;

    protected AsyncEvaluationVisitor CreateAsyncEvaluationVisitor()
    {
        return EvaluationVisitorFactory?.CreateAsyncEvaluationVisitor(context, options, cultureInfo, CancellationToken)
               ?? new AsyncEvaluationVisitor(context, options, cultureInfo, cancellationToken: CancellationToken);
    }

    public virtual object? Visit(TernaryExpression expression)
    {
        var trace = Trace;
        if (trace is null)
            return EvaluateTernary(expression);

        using var scope = trace.EnterNode(TraceNodeKind.Ternary);
        try
        {
            var left = Convert.ToBoolean(expression.LeftExpression.Accept(this), cultureInfo);
            var result = left
                ? EvaluateTernaryTaken(expression)
                : EvaluateTernaryNotTaken(expression);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private object? EvaluateTernary(TernaryExpression expression)
    {
        var left = Convert.ToBoolean(expression.LeftExpression.Accept(this), cultureInfo);

        return left
            ? expression.MiddleExpression.Accept(this)
            : expression.RightExpression.Accept(this);
    }

    private object? EvaluateTernaryTaken(TernaryExpression expression)
    {
        Trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
            TraceSkipReason.TernaryBranch,
            EvaluationTrace.GetNodeName(expression.RightExpression),
            "Condition was true");

        return expression.MiddleExpression.Accept(this);
    }

    private object? EvaluateTernaryNotTaken(TernaryExpression expression)
    {
        Trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.MiddleExpression),
            TraceSkipReason.TernaryBranch,
            EvaluationTrace.GetNodeName(expression.MiddleExpression),
            "Condition was false");

        return expression.RightExpression.Accept(this);
    }

    public virtual object? Visit(BinaryExpression expression)
    {
        var trace = Trace;
        if (trace is null)
            return EvaluateBinary(expression);

        using var scope = trace.EnterNode(TraceNodeKind.Binary, expression.Type.ToString());
        try
        {
            var result = EvaluateBinary(expression);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private object? EvaluateBinary(BinaryExpression expression)
    {
        var binaryEventArgs = new BinaryEventArgs(expression, this, CreateAsyncEvaluationVisitor(), CancellationToken);
        OnEvaluateBinary(binaryEventArgs);

        if (binaryEventArgs.HasResult)
            return binaryEventArgs.Result;

        var trace = Trace;

        if (expression.Type == BinaryExpressionType.And)
        {
            var leftValue = Convert.ToBoolean(binaryEventArgs.LeftValue(), cultureInfo);
            if (leftValue)
                return Convert.ToBoolean(binaryEventArgs.RightValue(), cultureInfo);

            if (!binaryEventArgs.RightResolved)
                trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
                    TraceSkipReason.ShortCircuitAnd,
                    EvaluationTrace.GetNodeName(expression.RightExpression),
                    "Left side of 'and' was false");

            return false;
        }

        if (expression.Type == BinaryExpressionType.Or)
        {
            var leftValue = Convert.ToBoolean(binaryEventArgs.LeftValue(), cultureInfo);
            if (!leftValue)
                return Convert.ToBoolean(binaryEventArgs.RightValue(), cultureInfo);

            if (!binaryEventArgs.RightResolved)
                trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
                    TraceSkipReason.ShortCircuitOr,
                    EvaluationTrace.GetNodeName(expression.RightExpression),
                    "Left side of 'or' was true");

            return true;
        }

        if (expression.Type == BinaryExpressionType.Coalesce)
        {
            var leftValue = binaryEventArgs.LeftValue();
            if (leftValue is not null)
            {
                if (!binaryEventArgs.RightResolved)
                    trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
                        TraceSkipReason.Coalesce,
                        EvaluationTrace.GetNodeName(expression.RightExpression),
                        "Left side of '??' was not null");

                return leftValue;
            }

            return binaryEventArgs.RightValue();
        }

        var left = binaryEventArgs.LeftValue();
        var right = binaryEventArgs.RightValue();

        return EvaluationVisitorHelper.EvaluateBinary(expression.Type, left, right, options, cultureInfo);
    }

    public virtual object? Visit(UnaryExpression expression)
    {
        var trace = Trace;
        if (trace is null)
        {
            var untracedResult = expression.Expression.Accept(this);
            return Unary(expression, untracedResult, options, cultureInfo);
        }

        using var scope = trace.EnterNode(TraceNodeKind.Unary, expression.Type.ToString());
        try
        {
            var result = Unary(expression, expression.Expression.Accept(this), options, cultureInfo);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    public virtual object? Visit(Function function)
    {
        var trace = Trace;
        if (trace is null)
            return EvaluateFunction(function);

        using var scope = trace.EnterFunction(function.Identifier.Name, function.Parameters);
        try
        {
            var result = EvaluateFunction(function);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private object? EvaluateFunction(Function function)
    {
        var functionName = function.Identifier.Name;
        var functionData = new FunctionData(
            function.Identifier.Id,
            function.Parameters,
            context,
            options,
            cultureInfo,
            this,
            CreateAsyncEvaluationVisitor(),
            CancellationToken);
        var functionArgs = new FunctionEventArgs(functionData);

        OnEvaluateFunction(functionName, functionArgs);

        if (functionArgs.HasResult)
        {
            Trace?.ResolvedFunction(functionName, TraceResolutionSource.FunctionHandler, functionArgs.Result);
            return functionArgs.Result;
        }

        if (context.Functions.TryGetValue(functionName, out var expressionFunction))
        {
            var functionResult = expressionFunction(functionData);
            Trace?.ResolvedFunction(functionName, TraceResolutionSource.Function, functionResult);
            return functionResult;
        }

        var builtInResult = BuiltInFunctionHelper.Evaluate(functionName, functionData);
        Trace?.ResolvedFunction(functionName, TraceResolutionSource.BuiltInFunction, builtInResult,
            "Built-in function");
        return builtInResult;
    }

    public virtual object? Visit(Identifier identifier)
    {
        var trace = Trace;
        if (trace is null)
            return EvaluateIdentifier(identifier);

        using var scope = trace.EnterNode(TraceNodeKind.Identifier, identifier.Name);
        try
        {
            var result = EvaluateIdentifier(identifier, trace);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private object? EvaluateIdentifier(Identifier identifier) => EvaluateIdentifier(identifier, null);

    private object? EvaluateIdentifier(Identifier identifier, EvaluationTrace? trace)
    {
        var value = GetIdentifierValue(identifier, trace);

        if (value is Expression expression)
            return expression.Evaluate(trace, CancellationToken);

        return value;
    }

    public virtual object? Visit(ValueExpression expression)
    {
        var trace = Trace;
        if (trace is null)
            return expression.Value;

        using var scope = trace.EnterNode(TraceNodeKind.Value, EvaluationTrace.GetNodeName(expression));
        try
        {
            scope.Complete(expression.Value);
            return expression.Value;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    public virtual object? Visit(LogicalExpressionList list)
    {
        if (list.Count == 0)
            return Array.Empty<object?>();

        var trace = Trace;
        TraceNodeScope? scope = null;
        if (trace is not null)
            scope = trace.EnterNode(TraceNodeKind.List);

        try
        {
            var expressions = list.AsSpan();
            var listCount = expressions.Length;
            var result = new object?[listCount];

            for (var index = 0; index < listCount; index++)
            {
                result[index] = expressions[index].Accept(this);
            }

            scope?.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            if (scope is not null)
                scope.Fault(exception);
            throw;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    protected void OnEvaluateFunction(string name, FunctionEventArgs args)
    {
        context.EvaluateFunctionHandler?.Invoke(name, args);
    }

    protected void OnEvaluateBinary(BinaryEventArgs args)
    {
        context.EvaluateBinaryHandler?.Invoke(args);
    }

    private object? GetIdentifierValue(Identifier identifier, EvaluationTrace? trace)
    {
        var identifierName = identifier.Name;

        var parameterArgs = new ParameterEventArgs(identifier.Id, CancellationToken);

        context.EvaluateParameterHandler?.Invoke(identifierName, parameterArgs);

        if (parameterArgs.HasResult)
        {
            trace?.ResolvedParameter(identifierName, TraceResolutionSource.ParameterHandler, parameterArgs.Result);
            return parameterArgs.Result;
        }

        if (context.Parameters.TryGetValue(identifierName, out var parameter))
        {
            if (parameter is Expression expression)
            {
                trace?.ResolvedParameter(identifierName, TraceResolutionSource.StaticParameter, null,
                    "Nested expression");
                ShareParametersWithChildExpression(expression);
                return expression;
            }

            trace?.ResolvedParameter(identifierName, TraceResolutionSource.StaticParameter, parameter);
            return parameter;
        }

        if (context.DynamicParameters.TryGetValue(identifierName, out var dynamicParameter))
        {
            var dynamicResult = dynamicParameter(new ParameterData(identifier.Id, context, CancellationToken));
            trace?.ResolvedParameter(identifierName, TraceResolutionSource.DynamicParameter, dynamicResult);
            return dynamicResult;
        }

        if (identifierName.Equals("null", StringComparison.InvariantCultureIgnoreCase) &&
            options.AllowNullParameter)
        {
            trace?.ResolvedParameter(identifierName, TraceResolutionSource.NullKeyword, null);
            return null;
        }

        throw new NCalcParameterNotDefinedException(identifierName);
    }

    private void ShareParametersWithChildExpression(Expression expression)
    {
        foreach (var parameter in context.Parameters)
            expression.Parameters[parameter.Key] = parameter.Value;

        foreach (var parameter in context.DynamicParameters)
            expression.DynamicParameters[parameter.Key] = parameter.Value;

        foreach (var parameter in context.AsyncParameters)
            expression.AsyncParameters[parameter.Key] = parameter.Value;

        expression.SetEvaluationVisitorFactory(EvaluationVisitorFactory);

        expression.EvaluateFunction += context.EvaluateFunctionHandler;
        expression.EvaluateAsyncFunction += context.EvaluateAsyncFunctionHandler;
        expression.EvaluateParameter += context.EvaluateParameterHandler;
        expression.EvaluateAsyncParameter += context.EvaluateAsyncParameterHandler;
        expression.EvaluateBinary += context.EvaluateBinaryHandler;
        expression.EvaluateBinaryAsync += context.EvaluateBinaryAsyncHandler;
    }
}
