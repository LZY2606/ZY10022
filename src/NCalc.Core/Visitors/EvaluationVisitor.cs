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
    protected EvaluationTrace? Trace { get; private set; }
    protected int ParentNodeId { get; private set; }

    internal void AttachTrace(EvaluationTrace? trace, int parentNodeId)
    {
        Trace = trace;
        ParentNodeId = parentNodeId;
    }

    protected AsyncEvaluationVisitor CreateAsyncEvaluationVisitor(EvaluationTrace? trace = null, int parentNodeId = 0)
    {
        var visitor = EvaluationVisitorFactory?.CreateAsyncEvaluationVisitor(context, options, cultureInfo, CancellationToken)
                      ?? new AsyncEvaluationVisitor(context, options, cultureInfo, cancellationToken: CancellationToken);
        visitor.AttachTrace(trace ?? Trace, trace is null ? ParentNodeId : parentNodeId);
        return visitor;
    }

    private EvaluationVisitor CreateSelfForBinary()
    {
        var visitor = new EvaluationVisitor(context, options, cultureInfo, null, CancellationToken);
        visitor.AttachTrace(Trace, ParentNodeId);
        return visitor;
    }

    protected object? EvaluateChild(LogicalExpression expression, int parentNodeId, EvaluationTraceNodeKind nodeKind, string name)
    {
        if (Trace is null)
            return expression.Accept(this);

        var previousParent = ParentNodeId;
        ParentNodeId = parentNodeId;
        try
        {
            return Trace.Run(parentNodeId, nodeKind, name, _ => expression.Accept(this));
        }
        finally
        {
            ParentNodeId = parentNodeId;
        }
    }

    protected object? EvaluateChild(LogicalExpression expression, int parentNodeId) =>
        EvaluateChild(expression, parentNodeId, GetNodeKind(expression), GetNodeName(expression));

    internal object? EvaluateTracedChild(LogicalExpression expression, int parentNodeId) =>
        EvaluateChild(expression, parentNodeId);

    internal object? EvaluateBinaryChild(LogicalExpression expression, int parentNodeId)
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
        Trace?.Skip(parentNodeId, GetNodeKind(expression), GetNodeName(expression), reason);
    }

    public virtual object? Visit(TernaryExpression expression)
    {
        if (Trace is null)
        {
            var left = Convert.ToBoolean(expression.LeftExpression.Accept(this), cultureInfo);
            return left
                ? expression.MiddleExpression.Accept(this)
                : expression.RightExpression.Accept(this);
        }

        return Trace.Run(ParentNodeId, EvaluationTraceNodeKind.Ternary, "?:", nodeId =>
        {
            var left = Convert.ToBoolean(EvaluateChild(expression.LeftExpression, nodeId), cultureInfo);

            if (left)
            {
                var middle = EvaluateChild(expression.MiddleExpression, nodeId);
                SkipChild(expression.RightExpression, nodeId);
                return middle;
            }

            var right = EvaluateChild(expression.RightExpression, nodeId);
            SkipChild(expression.MiddleExpression, nodeId);
            return right;
        });
    }

    public virtual object? Visit(BinaryExpression expression)
    {
        if (Trace is null)
            return VisitWithoutTrace(expression);

        return Trace.Run(ParentNodeId, EvaluationTraceNodeKind.Binary, expression.Type.ToString(), nodeId =>
            VisitBinary(expression, nodeId));
    }

    private object? VisitWithoutTrace(BinaryExpression expression)
    {
        var binaryEventArgs = new BinaryEventArgs(expression, CreateSelfForBinary(),
            CreateAsyncEvaluationVisitor(), CancellationToken);
        OnEvaluateBinary(binaryEventArgs);

        if (binaryEventArgs.HasResult)
            return binaryEventArgs.Result;

        if (expression.Type == BinaryExpressionType.And)
        {
            return Convert.ToBoolean(binaryEventArgs.LeftValue(), cultureInfo) &&
                   Convert.ToBoolean(binaryEventArgs.RightValue(), cultureInfo);
        }

        if (expression.Type == BinaryExpressionType.Or)
        {
            return Convert.ToBoolean(binaryEventArgs.LeftValue(), cultureInfo) ||
                   Convert.ToBoolean(binaryEventArgs.RightValue(), cultureInfo);
        }

        if (expression.Type == BinaryExpressionType.Coalesce)
        {
            var leftValue = binaryEventArgs.LeftValue();
            return leftValue ?? binaryEventArgs.RightValue();
        }

        var left = binaryEventArgs.LeftValue();
        var right = binaryEventArgs.RightValue();

        return EvaluationVisitorHelper.EvaluateBinary(expression.Type, left, right, options, cultureInfo);
    }

    private object? VisitBinary(BinaryExpression expression, int nodeId)
    {
        ParentNodeId = nodeId;
        var binaryEventArgs = new BinaryEventArgs(expression, CreateSelfForBinary(),
            CreateAsyncEvaluationVisitor(Trace, nodeId),
            CancellationToken, Trace, nodeId);
        OnEvaluateBinary(binaryEventArgs);

        if (binaryEventArgs.HasResult)
            return binaryEventArgs.Result;

        if (expression.Type == BinaryExpressionType.And)
        {
            var left = Convert.ToBoolean(binaryEventArgs.LeftValue(), cultureInfo);
            if (!left)
            {
                SkipChild(expression.RightExpression, nodeId);
                return false;
            }

            var right = Convert.ToBoolean(binaryEventArgs.RightValue(), cultureInfo);
            return right;
        }

        if (expression.Type == BinaryExpressionType.Or)
        {
            var left = Convert.ToBoolean(binaryEventArgs.LeftValue(), cultureInfo);
            if (left)
            {
                SkipChild(expression.RightExpression, nodeId);
                return true;
            }

            var right = Convert.ToBoolean(binaryEventArgs.RightValue(), cultureInfo);
            return right;
        }

        if (expression.Type == BinaryExpressionType.Coalesce)
        {
            var leftValue = binaryEventArgs.LeftValue();
            if (leftValue is not null)
            {
                SkipChild(expression.RightExpression, nodeId);
                return leftValue;
            }

            return binaryEventArgs.RightValue();
        }

        var leftValue2 = binaryEventArgs.LeftValue();
        var rightValue = binaryEventArgs.RightValue();

        return EvaluationVisitorHelper.EvaluateBinary(expression.Type, leftValue2, rightValue, options, cultureInfo);
    }

    public virtual object? Visit(UnaryExpression expression)
    {
        if (Trace is null)
        {
            var untracedResult = expression.Expression.Accept(this);
            return Unary(expression, untracedResult, options, cultureInfo);
        }

        return Trace.Run(ParentNodeId, EvaluationTraceNodeKind.Unary, expression.Type.ToString(), nodeId =>
        {
            var result = EvaluateChild(expression.Expression, nodeId);
            return Unary(expression, result, options, cultureInfo);
        });
    }

    public virtual object? Visit(Function function)
    {
        if (Trace is null)
            return VisitFunctionWithoutTrace(function);

        return Trace.Run(ParentNodeId, EvaluationTraceNodeKind.Function, function.Identifier.Name,
            nodeId => VisitFunction(function, nodeId));
    }

    private object? VisitFunctionWithoutTrace(Function function)
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
            return functionArgs.Result;

        if (context.Functions.TryGetValue(functionName, out var expressionFunction))
            return expressionFunction(functionData);

        return BuiltInFunctionHelper.Evaluate(functionName, functionData);
    }

    private object? VisitFunction(Function function, int nodeId)
    {
        var functionName = function.Identifier.Name;
        var functionData = new FunctionData(
            function.Identifier.Id,
            function.Parameters,
            context,
            options,
            cultureInfo,
            this,
            CreateAsyncEvaluationVisitor(Trace, nodeId),
            CancellationToken,
            Trace,
            nodeId);
        var functionArgs = new FunctionEventArgs(functionData);

        OnEvaluateFunction(functionName, functionArgs);

        if (functionArgs.HasResult)
        {
            Trace!.ResolveFunction(nodeId, nodeId, functionName, EvaluationTraceResolutionSource.FunctionHandler, functionArgs.Result);
            functionData.ReportSkippedArguments();
            return functionArgs.Result;
        }

        if (context.Functions.TryGetValue(functionName, out var expressionFunction))
        {
            var result = expressionFunction(functionData);
            Trace!.ResolveFunction(nodeId, nodeId, functionName, EvaluationTraceResolutionSource.Function, result);
            functionData.ReportSkippedArguments();
            return result;
        }

        var builtInResult = BuiltInFunctionHelper.Evaluate(functionName, functionData);
        Trace!.ResolveFunction(nodeId, nodeId, functionName, EvaluationTraceResolutionSource.BuiltInFunction, builtInResult);
        functionData.ReportSkippedArguments();
        return builtInResult;
    }

    public virtual object? Visit(Identifier identifier)
    {
        var value = Trace is null
            ? GetIdentifierValue(identifier)
            : Trace.Run(ParentNodeId, EvaluationTraceNodeKind.Identifier, identifier.Name,
                nodeId => GetIdentifierValue(identifier, nodeId));

        return value is Expression expression ? EvaluateNestedExpression(expression) : value;
    }

    public virtual object? Visit(ValueExpression expression)
    {
        if (Trace is null)
            return expression.Value;

        return Trace.Run(ParentNodeId, EvaluationTraceNodeKind.Value, expression.Value?.ToString() ?? "null",
            _ => expression.Value);
    }

    public virtual object? Visit(LogicalExpressionList list)
    {
        if (Trace is null)
            return VisitListWithoutTrace(list);

        return Trace.Run(ParentNodeId, EvaluationTraceNodeKind.List, "()", nodeId =>
        {
            if (list.Count == 0)
                return Array.Empty<object?>();

            var expressions = list.AsSpan();
            var result = new object?[expressions.Length];

            for (var index = 0; index < expressions.Length; index++)
                result[index] = EvaluateChild(expressions[index], nodeId);

            return result;
        });
    }

    private object? VisitListWithoutTrace(LogicalExpressionList list)
    {
        if (list.Count == 0)
            return Array.Empty<object?>();

        var expressions = list.AsSpan();
        var listCount = expressions.Length;
        var result = new object?[listCount];

        for (var index = 0; index < listCount; index++)
            result[index] = expressions[index].Accept(this);

        return result;
    }

    private object? EvaluateNestedExpression(Expression expression)
    {
        ShareParametersWithChildExpression(expression);

        if (Trace is null)
            return expression.Evaluate(CancellationToken);

        return expression.EvaluateWithTrace(Trace, Trace.CurrentNodeId, CancellationToken);
    }

    protected void OnEvaluateFunction(string name, FunctionEventArgs args)
    {
        context.EvaluateFunctionHandler?.Invoke(name, args);
    }

    protected void OnEvaluateBinary(BinaryEventArgs args)
    {
        context.EvaluateBinaryHandler?.Invoke(args);
    }

    private object? GetIdentifierValue(Identifier identifier) => GetIdentifierValue(identifier, 0);

    private object? GetIdentifierValue(Identifier identifier, int nodeId)
    {
        var identifierName = identifier.Name;
        var parameterArgs = new ParameterEventArgs(identifier.Id, CancellationToken);

        context.EvaluateParameterHandler?.Invoke(identifierName, parameterArgs);

        if (parameterArgs.HasResult)
        {
            Trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.ParameterHandler, parameterArgs.Result);
            return parameterArgs.Result;
        }

        if (context.Parameters.TryGetValue(identifierName, out var parameter))
        {
            if (parameter is Expression expression)
            {
                ShareParametersWithChildExpression(expression);
                Trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.NestedExpression, null);
                return expression;
            }

            Trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.StaticParameter, parameter);
            return parameter;
        }

        if (context.DynamicParameters.TryGetValue(identifierName, out var dynamicParameter))
        {
            var dynamicValue = dynamicParameter(new ParameterData(identifier.Id, context, CancellationToken));
            Trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.DynamicParameter, dynamicValue);
            return dynamicValue;
        }

        if (identifierName.Equals("null", StringComparison.InvariantCultureIgnoreCase) &&
            options.AllowNullParameter)
        {
            Trace?.ResolveParameter(nodeId, nodeId, identifierName, EvaluationTraceResolutionSource.NullLiteral, null);
            return null;
        }

        throw new NCalcParameterNotDefinedException(identifierName);
    }

    internal static EvaluationTraceNodeKind GetNodeKind(LogicalExpression expression)
    {
        return expression switch
        {
            ValueExpression => EvaluationTraceNodeKind.Value,
            Identifier => EvaluationTraceNodeKind.Identifier,
            UnaryExpression => EvaluationTraceNodeKind.Unary,
            BinaryExpression => EvaluationTraceNodeKind.Binary,
            TernaryExpression => EvaluationTraceNodeKind.Ternary,
            Function => EvaluationTraceNodeKind.Function,
            LogicalExpressionList => EvaluationTraceNodeKind.List,
            _ => EvaluationTraceNodeKind.Root
        };
    }

    internal static string GetNodeName(LogicalExpression expression)
    {
        return expression switch
        {
            ValueExpression value => value.Value?.ToString() ?? "null",
            Identifier identifier => identifier.Name,
            UnaryExpression unary => unary.Type.ToString(),
            BinaryExpression binary => binary.Type.ToString(),
            TernaryExpression => "?:",
            Function function => function.Identifier.Name,
            LogicalExpressionList => "()",
            _ => string.Empty
        };
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
