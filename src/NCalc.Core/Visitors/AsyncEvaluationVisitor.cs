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
    protected EvaluationTrace? Trace { get; private set; }
    protected int ParentNodeId { get; private set; }

    internal void AttachTrace(EvaluationTrace? trace, int parentNodeId)
    {
        Trace = trace;
        ParentNodeId = parentNodeId;
    }

    protected EvaluationVisitor CreateEvaluationVisitor(EvaluationTrace? trace = null, int parentNodeId = 0)
    {
        var visitor = EvaluationVisitorFactory?.CreateEvaluationVisitor(context, options, cultureInfo, CancellationToken)
                      ?? new EvaluationVisitor(context, options, cultureInfo, cancellationToken: CancellationToken);
        visitor.AttachTrace(trace ?? Trace, trace is null ? ParentNodeId : parentNodeId);
        return visitor;
    }

    private AsyncEvaluationVisitor CreateSelfForBinary()
    {
        var visitor = new AsyncEvaluationVisitor(context, options, cultureInfo, null, CancellationToken);
        visitor.AttachTrace(Trace, ParentNodeId);
        return visitor;
    }

    internal Task<object?> EvaluateTracedChildAsync(LogicalExpression expression, int parentNodeId)
    {
        if (Trace is null)
            return expression.Accept(this);

        var previousParent = ParentNodeId;
        ParentNodeId = parentNodeId;
        try
        {
            return Trace.RunAsync(parentNodeId, EvaluationVisitor.GetNodeKind(expression),
                EvaluationVisitor.GetNodeName(expression), _ => expression.Accept(this));
        }
        finally
        {
            ParentNodeId = parentNodeId;
        }
    }

    internal Task<object?> EvaluateBinaryChildAsync(LogicalExpression expression, int parentNodeId)
    {
        var previousParent = ParentNodeId;
        ParentNodeId = parentNodeId;
        try
        {
            return expression.Accept(this);
        }
        finally
        {
            ParentNodeId = parentNodeId;
        }
    }

    protected void SkipChild(LogicalExpression expression, int parentNodeId, string reason = "Short-circuit")
    {
        Trace?.Skip(parentNodeId, EvaluationVisitor.GetNodeKind(expression),
            EvaluationVisitor.GetNodeName(expression), reason);
    }

    public virtual Task<object?> Visit(TernaryExpression expression)
    {
        if (Trace is null)
            return VisitTernaryWithoutTrace(expression);

        return Trace.RunAsync(ParentNodeId, EvaluationTraceNodeKind.Ternary, "?:",
            nodeId => VisitTernary(expression, nodeId));
    }

    private async Task<object?> VisitTernaryWithoutTrace(TernaryExpression expression)
    {
        var left = Convert.ToBoolean(await expression.LeftExpression.Accept(this), cultureInfo);

        if (left)
            return await expression.MiddleExpression.Accept(this);

        return await expression.RightExpression.Accept(this);
    }

    private async Task<object?> VisitTernary(TernaryExpression expression, int nodeId)
    {
        var left = Convert.ToBoolean(await EvaluateTracedChildAsync(expression.LeftExpression, nodeId), cultureInfo);

        if (left)
        {
            var result = await EvaluateTracedChildAsync(expression.MiddleExpression, nodeId);
            SkipChild(expression.RightExpression, nodeId);
            return result;
        }

        var falseResult = await EvaluateTracedChildAsync(expression.RightExpression, nodeId);
        SkipChild(expression.MiddleExpression, nodeId);
        return falseResult;
    }

    public virtual Task<object?> Visit(BinaryExpression expression)
    {
        if (Trace is null)
            return VisitBinaryWithoutTrace(expression);

        return Trace.RunAsync(ParentNodeId, EvaluationTraceNodeKind.Binary, expression.Type.ToString(),
            nodeId => VisitBinary(expression, nodeId));
    }

    private Task<object?> VisitBinaryWithoutTrace(BinaryExpression expression)
    {
        var binaryEventArgs = new BinaryEventArgs(expression, CreateEvaluationVisitor(), CreateSelfForBinary(),
            CancellationToken);
        return VisitBinary(expression, binaryEventArgs, null, 0);
    }

    private Task<object?> VisitBinary(BinaryExpression expression, int nodeId)
    {
        var binaryEventArgs = new BinaryEventArgs(expression, CreateEvaluationVisitor(Trace, nodeId), CreateSelfForBinary(),
            CancellationToken, Trace, nodeId);
        return VisitBinary(expression, binaryEventArgs, Trace, nodeId);
    }

    private async Task<object?> VisitBinary(BinaryExpression expression, BinaryEventArgs binaryEventArgs,
        EvaluationTrace? trace, int nodeId)
    {
        await OnEvaluateBinaryAsync(binaryEventArgs);

        if (binaryEventArgs.HasResult)
            return binaryEventArgs.Result;

        if (expression.Type == BinaryExpressionType.And)
        {
            var left = Convert.ToBoolean(await binaryEventArgs.LeftValueAsync(), cultureInfo);
            if (!left)
            {
                trace?.Skip(nodeId, EvaluationTraceNodeKind.Binary,
                    EvaluationVisitor.GetNodeName(expression.RightExpression), "Short-circuit");
                return false;
            }

            return Convert.ToBoolean(await binaryEventArgs.RightValueAsync(), cultureInfo);
        }

        if (expression.Type == BinaryExpressionType.Or)
        {
            var left = Convert.ToBoolean(await binaryEventArgs.LeftValueAsync(), cultureInfo);
            if (left)
            {
                trace?.Skip(nodeId, EvaluationTraceNodeKind.Binary,
                    EvaluationVisitor.GetNodeName(expression.RightExpression), "Short-circuit");
                return true;
            }

            return Convert.ToBoolean(await binaryEventArgs.RightValueAsync(), cultureInfo);
        }

        if (expression.Type == BinaryExpressionType.Coalesce)
        {
            var leftValue = await binaryEventArgs.LeftValueAsync();
            if (leftValue is not null)
            {
                trace?.Skip(nodeId, EvaluationTraceNodeKind.Binary,
                    EvaluationVisitor.GetNodeName(expression.RightExpression), "Short-circuit");
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

        var left2 = await binaryEventArgs.LeftValueAsync();
        var right = await binaryEventArgs.RightValueAsync();

        return EvaluationVisitorHelper.EvaluateBinary(expression.Type, left2, right, options, cultureInfo);
    }

    public virtual Task<object?> Visit(UnaryExpression expression)
    {
        if (Trace is null)
            return VisitUnaryWithoutTrace(expression);

        return Trace.RunAsync(ParentNodeId, EvaluationTraceNodeKind.Unary, expression.Type.ToString(),
            nodeId => VisitUnary(expression, nodeId));
    }

    private async Task<object?> VisitUnaryWithoutTrace(UnaryExpression expression)
    {
        var result = await expression.Expression.Accept(this);
        return Unary(expression, result, options, cultureInfo);
    }

    private async Task<object?> VisitUnary(UnaryExpression expression, int nodeId)
    {
        var result = await EvaluateTracedChildAsync(expression.Expression, nodeId);
        return Unary(expression, result, options, cultureInfo);
    }

    public virtual Task<object?> Visit(Function function)
    {
        if (Trace is null)
            return VisitFunctionWithoutTrace(function);

        return Trace.RunAsync(ParentNodeId, EvaluationTraceNodeKind.Function, function.Identifier.Name,
            nodeId => VisitFunction(function, nodeId));
    }

    private Task<object?> VisitFunctionWithoutTrace(Function function)
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
        return InvokeFunctionAsync(functionName, functionData, null, 0);
    }

    private Task<object?> VisitFunction(Function function, int nodeId)
    {
        var functionName = function.Identifier.Name;
        var syncEvaluationVisitor = CreateEvaluationVisitor(Trace, nodeId);
        var functionData = new FunctionData(
            function.Identifier.Id,
            function.Parameters,
            context,
            options,
            cultureInfo,
            syncEvaluationVisitor,
            this,
            CancellationToken,
            Trace,
            nodeId);
        return InvokeFunctionAsync(functionName, functionData, Trace, nodeId);
    }

    private async Task<object?> InvokeFunctionAsync(string functionName, FunctionData functionData,
        EvaluationTrace? trace, int nodeId)
    {
        var functionArgs = new FunctionEventArgs(functionData);
        await OnEvaluateFunctionAsync(functionName, functionArgs);

        if (functionArgs.HasResult)
        {
            trace?.ResolveFunction(nodeId, nodeId, functionName, EvaluationTraceResolutionSource.FunctionHandler, functionArgs.Result);
            functionData.ReportSkippedArguments();
            return functionArgs.Result;
        }

        if (context.Functions.TryGetValue(functionName, out var expressionFunction))
        {
            var syncResult = expressionFunction(functionData);
            trace?.ResolveFunction(nodeId, nodeId, functionName, EvaluationTraceResolutionSource.Function, syncResult);
            functionData.ReportSkippedArguments();
            return syncResult;
        }

        if (context.AsyncFunctions.TryGetValue(functionName, out var asyncExpressionFunction))
        {
            var asyncResult = await asyncExpressionFunction(functionData);
            trace?.ResolveFunction(nodeId, nodeId, functionName, EvaluationTraceResolutionSource.AsyncFunction, asyncResult);
            functionData.ReportSkippedArguments();
            return asyncResult;
        }

        var result = await BuiltInFunctionHelper.EvaluateAsync(functionName, functionData);
        trace?.ResolveFunction(nodeId, nodeId, functionName, EvaluationTraceResolutionSource.BuiltInFunction, result);
        functionData.ReportSkippedArguments();
        return result;
    }

    public virtual Task<object?> Visit(Identifier identifier)
    {
        if (Trace is null)
            return VisitIdentifierWithoutTrace(identifier);

        return Trace.RunAsync(ParentNodeId, EvaluationTraceNodeKind.Identifier, identifier.Name,
            nodeId => VisitIdentifier(identifier, nodeId));
    }

    private async Task<object?> VisitIdentifierWithoutTrace(Identifier identifier)
    {
        var value = await GetIdentifierValueAsync(identifier, 0, null);
        return value is Expression expression
            ? await EvaluateNestedExpressionAsync(expression, null)
            : value;
    }

    private async Task<object?> VisitIdentifier(Identifier identifier, int nodeId)
    {
        var value = await GetIdentifierValueAsync(identifier, nodeId, Trace);

        return value is Expression expression
            ? await EvaluateNestedExpressionAsync(expression, Trace)
            : value;
    }

    public virtual Task<object?> Visit(ValueExpression expression)
    {
        if (Trace is null)
            return Task.FromResult(expression.Value);

        return Trace.RunAsync(ParentNodeId, EvaluationTraceNodeKind.Value, expression.Value?.ToString() ?? "null",
            _ => Task.FromResult(expression.Value));
    }

    public virtual Task<object?> Visit(LogicalExpressionList list)
    {
        if (Trace is null)
            return VisitListWithoutTrace(list);

        return Trace.RunAsync(ParentNodeId, EvaluationTraceNodeKind.List, "()",
            nodeId => VisitList(list, nodeId));
    }

    private async Task<object?> VisitListWithoutTrace(LogicalExpressionList list)
    {
        if (list.Count == 0)
            return Array.Empty<object?>();

        if (options.ConcurrentAsyncEvaluation)
            return await Task.WhenAll(list.Select(EvaluateAsyncWithoutTrace));

        var listCount = list.Count;
        var result = new object?[listCount];

        for (var index = 0; index < listCount; index++)
            result[index] = await EvaluateAsyncWithoutTrace(list[index]);

        return result;
    }

    private async Task<object?> VisitList(LogicalExpressionList list, int nodeId)
    {
        if (list.Count == 0)
            return Array.Empty<object?>();

        if (options.ConcurrentAsyncEvaluation)
            return await Task.WhenAll(list.Select(expression => EvaluateTracedChildAsync(expression, nodeId)));

        var result = new object?[list.Count];

        for (var index = 0; index < list.Count; index++)
            result[index] = await EvaluateTracedChildAsync(list[index], nodeId);

        return result;
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

    protected Task<object?> EvaluateAsyncWithoutTrace(LogicalExpression expression)
    {
        return expression.Accept(this);
    }

    private async Task<object?> EvaluateNestedExpressionAsync(Expression expression, EvaluationTrace? trace)
    {
        ShareParametersWithChildExpression(expression);

        if (trace is null)
            return await expression.EvaluateAsync(CancellationToken);

        return await expression.EvaluateWithTraceAsync(trace, trace.CurrentNodeId, CancellationToken);
    }

    private async Task<object?> GetIdentifierValueAsync(Identifier identifier, int nodeId, EvaluationTrace? trace)
    {
        var identifierName = identifier.Name;
        var parameterArgs = new ParameterEventArgs(identifier.Id, CancellationToken);

        context.EvaluateParameterHandler?.Invoke(identifierName, parameterArgs);
        if (parameterArgs.HasResult)
        {
            trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.ParameterHandler, parameterArgs.Result);
            return parameterArgs.Result;
        }

        if (!parameterArgs.HasResult)
            await (context.EvaluateAsyncParameterHandler?.Invoke(identifierName, parameterArgs) ?? Task.CompletedTask);

        if (parameterArgs.HasResult)
        {
            trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.AsyncParameterHandler, parameterArgs.Result);
            return parameterArgs.Result;
        }

        if (context.Parameters.TryGetValue(identifierName, out var parameter))
        {
            if (parameter is Expression expression)
            {
                ShareParametersWithChildExpression(expression);
                trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.NestedExpression, null);
                return expression;
            }

            trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.StaticParameter, parameter);
            return parameter;
        }

        if (context.DynamicParameters.TryGetValue(identifierName, out var dynamicParameter))
        {
            var dynamicValue = dynamicParameter(new ParameterData(identifier.Id, context, CancellationToken));
            trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.DynamicParameter, dynamicValue);
            return dynamicValue;
        }

        if (context.AsyncParameters.TryGetValue(identifierName, out var asyncParameter))
        {
            var asyncValue = await asyncParameter(new ParameterData(identifier.Id, context, CancellationToken));
            trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.AsyncParameter, asyncValue);
            return asyncValue;
        }

        if (identifierName.Equals("null", StringComparison.InvariantCultureIgnoreCase) &&
            options.AllowNullParameter)
        {
            trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.NullLiteral, null);
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
