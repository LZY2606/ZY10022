using NCalc.Factories;
using NCalc.Handlers;
using NCalc.Helpers;
using NCalc.Exceptions;
using NCalc.Tracing;
using static NCalc.Helpers.EvaluationHelper;

namespace NCalc.Visitors;

/// <summary>
/// Class responsible to asynchronous evaluating <see cref="LogicalExpression"/>.
/// </summary>
public class AsyncEvaluationVisitor(
    ExpressionContext context,
    ExpressionEvaluationOptions options,
    CultureInfo cultureInfo,
    IEvaluationVisitorFactory? evaluationVisitorFactory = null,
    CancellationToken cancellationToken = default) : ILogicalExpressionVisitor<Task<object?>>
{
    protected CancellationToken CancellationToken { get; } = cancellationToken;
    protected IEvaluationVisitorFactory? EvaluationVisitorFactory { get; } = evaluationVisitorFactory;

    private EvaluationTrace? Trace => context.Tracer;

    protected EvaluationVisitor CreateEvaluationVisitor()
    {
        return EvaluationVisitorFactory?.CreateEvaluationVisitor(context, options, cultureInfo, CancellationToken)
               ?? new EvaluationVisitor(context, options, cultureInfo, cancellationToken: CancellationToken);
    }

    public virtual Task<object?> Visit(TernaryExpression expression)
    {
        return Trace is null ? EvaluateTernaryAsync(expression) : EvaluateTernaryTracedAsync(expression);
    }

    private async Task<object?> EvaluateTernaryTracedAsync(TernaryExpression expression)
    {
        var trace = Trace!;
        using var scope = trace.EnterNode(TraceNodeKind.Ternary);
        try
        {
            var left = Convert.ToBoolean(await expression.LeftExpression.Accept(this), cultureInfo);
            object? result;
            if (left)
            {
                trace.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
                    TraceSkipReason.TernaryBranch,
                    EvaluationTrace.GetNodeName(expression.RightExpression),
                    "Condition was true");
                result = await expression.MiddleExpression.Accept(this);
            }
            else
            {
                trace.SkipNode(EvaluationTrace.GetNodeKind(expression.MiddleExpression),
                    TraceSkipReason.TernaryBranch,
                    EvaluationTrace.GetNodeName(expression.MiddleExpression),
                    "Condition was false");
                result = await expression.RightExpression.Accept(this);
            }

            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private async Task<object?> EvaluateTernaryAsync(TernaryExpression expression)
    {
        var left = Convert.ToBoolean(await expression.LeftExpression.Accept(this), cultureInfo);

        if (left)
        {
            return await expression.MiddleExpression.Accept(this);
        }

        return await expression.RightExpression.Accept(this);
    }

    public virtual Task<object?> Visit(BinaryExpression expression)
    {
        return Trace is null ? EvaluateBinaryAsync(expression) : EvaluateBinaryTracedAsync(expression);
    }

    private async Task<object?> EvaluateBinaryTracedAsync(BinaryExpression expression)
    {
        var trace = Trace!;
        using var scope = trace.EnterNode(TraceNodeKind.Binary, expression.Type.ToString());
        try
        {
            var result = await EvaluateBinaryAsync(expression);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private async Task<object?> EvaluateBinaryAsync(BinaryExpression expression)
    {
        var binaryEventArgs = new BinaryEventArgs(expression, CreateEvaluationVisitor(), this, CancellationToken);
        await OnEvaluateBinaryAsync(binaryEventArgs);

        if (binaryEventArgs.HasResult)
            return binaryEventArgs.Result;

        var trace = Trace;

        if (expression.Type == BinaryExpressionType.And)
        {
            var leftValue = Convert.ToBoolean(await binaryEventArgs.LeftValueAsync(), cultureInfo);
            if (leftValue)
                return Convert.ToBoolean(await binaryEventArgs.RightValueAsync(), cultureInfo);

            if (!binaryEventArgs.RightResolved)
                trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
                    TraceSkipReason.ShortCircuitAnd,
                    EvaluationTrace.GetNodeName(expression.RightExpression),
                    "Left side of 'and' was false");

            return false;
        }

        if (expression.Type == BinaryExpressionType.Or)
        {
            var leftValue = Convert.ToBoolean(await binaryEventArgs.LeftValueAsync(), cultureInfo);
            if (!leftValue)
                return Convert.ToBoolean(await binaryEventArgs.RightValueAsync(), cultureInfo);

            if (!binaryEventArgs.RightResolved)
                trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
                    TraceSkipReason.ShortCircuitOr,
                    EvaluationTrace.GetNodeName(expression.RightExpression),
                    "Left side of 'or' was true");

            return true;
        }

        if (expression.Type == BinaryExpressionType.Coalesce)
        {
            var leftValue = await binaryEventArgs.LeftValueAsync();
            if (leftValue is not null)
            {
                if (!binaryEventArgs.RightResolved)
                    trace?.SkipNode(EvaluationTrace.GetNodeKind(expression.RightExpression),
                        TraceSkipReason.Coalesce,
                        EvaluationTrace.GetNodeName(expression.RightExpression),
                        "Left side of coalesce was not null");

                return leftValue;
            }

            return await binaryEventArgs.RightValueAsync();
        }

        if (options.ConcurrentAsyncEvaluation)
        {
            var values = await Task.WhenAll(
                binaryEventArgs.LeftValueAsync(),
                binaryEventArgs.RightValueAsync());

            return EvaluationVisitorHelper.EvaluateBinary(
                expression.Type,
                values[0],
                values[1],
                options,
                cultureInfo);
        }

        var left = await binaryEventArgs.LeftValueAsync();
        var right = await binaryEventArgs.RightValueAsync();

        return EvaluationVisitorHelper.EvaluateBinary(expression.Type, left, right, options, cultureInfo);
    }

    public virtual Task<object?> Visit(UnaryExpression expression)
    {
        return Trace is null ? EvaluateUnaryAsync(expression) : EvaluateUnaryTracedAsync(expression);
    }

    private async Task<object?> EvaluateUnaryAsync(UnaryExpression expression)
    {
        var result = await expression.Expression.Accept(this);
        return Unary(expression, result, options, cultureInfo);
    }

    private async Task<object?> EvaluateUnaryTracedAsync(UnaryExpression expression)
    {
        var trace = Trace!;
        using var scope = trace.EnterNode(TraceNodeKind.Unary, expression.Type.ToString());
        try
        {
            var result = Unary(expression, await expression.Expression.Accept(this), options, cultureInfo);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    public virtual Task<object?> Visit(Function function)
    {
        return Trace is null ? EvaluateFunctionAsync(function) : EvaluateFunctionTracedAsync(function);
    }

    private async Task<object?> EvaluateFunctionTracedAsync(Function function)
    {
        var trace = Trace!;
        using var scope = trace.EnterFunction(function.Identifier.Name, function.Parameters);
        try
        {
            var result = await EvaluateFunctionAsync(function);
            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private async Task<object?> EvaluateFunctionAsync(Function function)
    {
        var functionName = function.Identifier.Name;
        var syncEvaluationVisitor = CreateEvaluationVisitor();
        var functionData = new FunctionData(
            function.Identifier.Id,
            function.Parameters,
            context,
            options,
            cultureInfo,
            syncEvaluationVisitor,
            this,
            CancellationToken);
        var functionArgs = new FunctionEventArgs(functionData);

        var resolvedBySyncHandler = false;
        var syncHandler = context.EvaluateFunctionHandler;
        if (syncHandler is not null)
        {
            syncHandler.Invoke(functionName, functionArgs);
            resolvedBySyncHandler = functionArgs.HasResult;
        }

        if (!functionArgs.HasResult && context.EvaluateAsyncFunctionHandler is not null)
            await context.EvaluateAsyncFunctionHandler.Invoke(functionName, functionArgs);

        var trace = Trace;

        if (functionArgs.HasResult)
        {
            trace?.ResolvedFunction(functionName,
                resolvedBySyncHandler
                    ? TraceResolutionSource.FunctionHandler
                    : TraceResolutionSource.AsyncFunctionHandler,
                functionArgs.Result);
            return functionArgs.Result;
        }

        if (context.Functions.TryGetValue(functionName, out var expressionFunction))
        {
            var syncFunctionResult = expressionFunction(functionData);
            trace?.ResolvedFunction(functionName, TraceResolutionSource.Function, syncFunctionResult);
            return syncFunctionResult;
        }

        if (context.AsyncFunctions.TryGetValue(functionName, out var asyncExpressionFunction))
        {
            var asyncFunctionResult = await asyncExpressionFunction(functionData);
            trace?.ResolvedFunction(functionName, TraceResolutionSource.AsyncFunction, asyncFunctionResult);
            return asyncFunctionResult;
        }

        var builtInResult = await BuiltInFunctionHelper.EvaluateAsync(functionName, functionData);
        trace?.ResolvedFunction(functionName, TraceResolutionSource.BuiltInFunction, builtInResult,
            "Built-in function");
        return builtInResult;
    }

    public virtual Task<object?> Visit(Identifier identifier)
    {
        return Trace is null ? EvaluateIdentifierAsync(identifier) : EvaluateIdentifierTracedAsync(identifier);
    }

    private async Task<object?> EvaluateIdentifierTracedAsync(Identifier identifier)
    {
        var trace = Trace!;
        using var scope = trace.EnterNode(TraceNodeKind.Identifier, identifier.Name);
        try
        {
            var result = await GetIdentifierValueAsync(identifier);
            if (result is Expression expression)
                result = await expression.EvaluateAsync(trace, CancellationToken);

            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    private async Task<object?> EvaluateIdentifierAsync(Identifier identifier)
    {
        var value = await GetIdentifierValueAsync(identifier);

        return value is Expression expression
            ? await expression.EvaluateAsync(CancellationToken)
            : value;
    }

    public virtual Task<object?> Visit(ValueExpression expression)
    {
        var trace = Trace;
        if (trace is null)
            return Task.FromResult(expression.Value);

        return EvaluateValueTracedAsync(expression, trace);
    }

    private Task<object?> EvaluateValueTracedAsync(ValueExpression expression, EvaluationTrace trace)
    {
        using var scope = trace.EnterNode(TraceNodeKind.Value, EvaluationTrace.GetNodeName(expression));
        try
        {
            scope.Complete(expression.Value);
            return Task.FromResult(expression.Value);
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            return Task.FromException<object?>(exception);
        }
    }

    public virtual Task<object?> Visit(LogicalExpressionList list)
    {
        return Trace is null ? EvaluateListAsync(list) : EvaluateListTracedAsync(list);
    }

    private async Task<object?> EvaluateListAsync(LogicalExpressionList list)
    {
        if (list.Count == 0) return Array.Empty<object?>();

        if (options.ConcurrentAsyncEvaluation)
            return await Task.WhenAll(list.Select(EvaluateAsync));

        var listCount = list.Count;
        var result = new object?[listCount];

        for (var index = 0; index < listCount; index++)
        {
            result[index] = await EvaluateAsync(list[index]);
        }

        return result;
    }

    private async Task<object?> EvaluateListTracedAsync(LogicalExpressionList list)
    {
        var trace = Trace!;
        using var scope = trace.EnterNode(TraceNodeKind.List);
        try
        {
            if (list.Count == 0)
            {
                var empty = Array.Empty<object?>();
                scope.Complete(empty);
                return empty;
            }

            object?[] result;
            if (options.ConcurrentAsyncEvaluation)
            {
                result = await Task.WhenAll(list.Select(EvaluateAsync));
            }
            else
            {
                result = new object?[list.Count];
                for (var index = 0; index < list.Count; index++)
                {
                    result[index] = await EvaluateAsync(list[index]);
                }
            }

            scope.Complete(result);
            return result;
        }
        catch (Exception exception)
        {
            scope.Fault(exception);
            throw;
        }
    }

    protected Task OnEvaluateFunctionAsync(string name, FunctionEventArgs args)
    {
        context.EvaluateFunctionHandler?.Invoke(name, args);
        if (args.HasResult)
            return Task.CompletedTask;

        return context.EvaluateAsyncFunctionHandler?.Invoke(name, args) ?? Task.CompletedTask;
    }

    protected Task OnEvaluateBinaryAsync(BinaryEventArgs args)
    {
        context.EvaluateBinaryHandler?.Invoke(args);
        if (args.HasResult)
            return Task.CompletedTask;

        return context.EvaluateBinaryAsyncHandler?.Invoke(args) ?? Task.CompletedTask;
    }

    protected Task<object?> EvaluateAsync(LogicalExpression expression)
    {
        return expression.Accept(this);
    }

    private async Task<object?> GetIdentifierValueAsync(Identifier identifier)
    {
        var identifierName = identifier.Name;

        var parameterArgs = new ParameterEventArgs(identifier.Id, CancellationToken);

        var resolvedBySyncHandler = false;
        var syncHandler = context.EvaluateParameterHandler;
        if (syncHandler is not null)
        {
            syncHandler.Invoke(identifierName, parameterArgs);
            resolvedBySyncHandler = parameterArgs.HasResult;
        }

        if (!parameterArgs.HasResult && context.EvaluateAsyncParameterHandler is not null)
            await context.EvaluateAsyncParameterHandler.Invoke(identifierName, parameterArgs);

        var trace = Trace;

        if (parameterArgs.HasResult)
        {
            trace?.ResolvedParameter(identifierName,
                resolvedBySyncHandler
                    ? TraceResolutionSource.ParameterHandler
                    : TraceResolutionSource.AsyncParameterHandler,
                parameterArgs.Result);
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

        if (context.AsyncParameters.TryGetValue(identifierName, out var asyncParameter))
        {
            var asyncResult = await asyncParameter(new ParameterData(identifier.Id, context, CancellationToken));
            trace?.ResolvedParameter(identifierName, TraceResolutionSource.AsyncParameter, asyncResult);
            return asyncResult;
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
