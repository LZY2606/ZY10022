using System.Numerics;
using System.Reflection;
using ExtendedNumerics;
using NCalc.Exceptions;
using NCalc.Helpers;
using NCalc.LambdaCompilation.Reflection;
using NCalc.Tracing;
using NCalc.Visitors;
using Linq = System.Linq.Expressions;
using LinqExpression = System.Linq.Expressions.Expression;
using LinqParameterExpression = System.Linq.Expressions.ParameterExpression;

namespace NCalc.LambdaCompilation.Visitors;

public sealed class LambdaExpressionVisitor : ILogicalExpressionVisitor<LinqExpression>
{
    private readonly IDictionary<string, object?>? _parameters;
    private readonly LinqExpression? _context;
    private readonly ExpressionEvaluationOptions _options;
    private readonly StringComparer _stringComparer;
    private readonly bool _ignoreCaseAtBuiltInFunctions;
    private readonly bool _checked;
    private readonly LinqExpression? _trace;
    private readonly LinqExpression _parentId;

    private static readonly MethodInfo StringComparerEqualsMethod =
        typeof(StringComparer).GetMethod("Equals", [typeof(string), typeof(string)])!;

    private static readonly MethodInfo StringComparerCompareMethod =
        typeof(StringComparer).GetMethod("Compare", [typeof(string), typeof(string)])!;

    private static readonly MethodInfo TraceRunMethod = typeof(EvaluationTrace).GetMethods()
        .First(method => method.Name == nameof(EvaluationTrace.Run) && method.IsGenericMethodDefinition)!;

    private static readonly MethodInfo TraceResolveParameterMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.ResolveParameter))!;

    private static readonly MethodInfo TraceResolveFunctionMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.ResolveFunction))!;

    private static readonly MethodInfo TraceRunTernaryMethod = typeof(EvaluationTrace).GetMethods()
        .First(method => method.Name == nameof(EvaluationTrace.RunTernary) && method.IsGenericMethodDefinition)!;

    private static readonly MethodInfo TraceRunAndAlsoMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.RunAndAlso))!;

    private static readonly MethodInfo TraceRunOrElseMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.RunOrElse))!;

    private static readonly MethodInfo TraceRunCoalesceMethod = typeof(EvaluationTrace).GetMethods()
        .First(method => method.Name == nameof(EvaluationTrace.RunCoalesce) && method.IsGenericMethodDefinition)!;

    private static readonly MethodInfo TraceRunFunctionIfMethod = typeof(EvaluationTrace).GetMethods()
        .First(method => method.Name == nameof(EvaluationTrace.RunFunctionIf) && method.IsGenericMethodDefinition)!;

    private LambdaExpressionVisitor(ExpressionEvaluationOptions options)
    {
        _options = options;
        _stringComparer = _options.StringComparer;
        _checked = _options.Math.OverflowProtection;
        _ignoreCaseAtBuiltInFunctions = _options.IgnoreCaseAtBuiltInFunctions;
        _parentId = LinqExpression.Constant(0);
    }

    public LambdaExpressionVisitor(IDictionary<string, object?> parameters, ExpressionEvaluationOptions options,
        LinqExpression? trace = null, LinqExpression? parentId = null) : this(options)
    {
        _parameters = parameters;
        _trace = trace;
        if (parentId is not null)
            _parentId = parentId;
    }

    public LambdaExpressionVisitor(LinqParameterExpression context, ExpressionEvaluationOptions options,
        LinqExpression? trace = null, LinqExpression? parentId = null) : this(options)
    {
        _context = context;
        _trace = trace;
        if (parentId is not null)
            _parentId = parentId;
    }

    private LinqExpression Emit(LogicalExpression expression)
    {
        var visitor = new LambdaExpressionVisitor(this, _parentId);
        return expression.Accept(visitor);
    }

    private LinqExpression Emit(LogicalExpression expression, LinqExpression parentId)
    {
        var visitor = new LambdaExpressionVisitor(this, parentId);
        return expression.Accept(visitor);
    }

    private LambdaExpressionVisitor(LambdaExpressionVisitor source, LinqExpression parentId)
    {
        _parameters = source._parameters;
        _context = source._context;
        _options = source._options;
        _stringComparer = source._stringComparer;
        _ignoreCaseAtBuiltInFunctions = source._ignoreCaseAtBuiltInFunctions;
        _checked = source._checked;
        _trace = source._trace;
        _parentId = parentId;
    }

    private LinqExpression EmitChild(LogicalExpression expression, LinqExpression parentId)
    {
        if (_trace is null)
            return Emit(expression, parentId);

        var child = Emit(expression, parentId);
        return WrapAsObject(child);
    }

    private LinqExpression TraceNode(
        LogicalExpression node,
        EvaluationTraceNodeKind kind,
        string name,
        Func<LambdaExpressionVisitor, LinqExpression> build)
    {
        if (_trace is null)
            return build(this);

        var nodeId = LinqExpression.Parameter(typeof(int), "nodeId");
        var tracedVisitor = new LambdaExpressionVisitor(this, nodeId);
        var body = build(tracedVisitor);
        var method = TraceRunMethod.MakeGenericMethod(body.Type);
        var callback = LinqExpression.Lambda(body, nodeId);

        return LinqExpression.Call(_trace, method, _parentId, LinqExpression.Constant(kind),
            LinqExpression.Constant(name), callback);
    }

    private LinqExpression TraceNode(
        LogicalExpression node,
        EvaluationTraceNodeKind kind,
        string name,
        Func<LinqExpression> build) =>
        TraceNode(node, kind, name, _ => build());

    private static LinqExpression WrapAsObject(LinqExpression expression)
    {
        return expression.Type.IsValueType ? LinqExpression.Convert(expression, typeof(object)) : expression;
    }

    private static LinqExpression UnwrapObject(LinqExpression expression, Type targetType)
    {
        if (expression.Type != typeof(object))
            return expression;

        return targetType.IsValueType ? LinqExpression.Unbox(expression, targetType) :
            targetType != typeof(object) ? LinqExpression.Convert(expression, targetType) : expression;
    }

    private static EvaluationTraceNodeKind GetNodeKind(LogicalExpression expression)
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

    private static string GetNodeName(LogicalExpression expression)
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

    public LinqExpression Visit(TernaryExpression expression)
    {
        if (_trace is not null)
            return TraceNode(expression, EvaluationTraceNodeKind.Ternary, "?:",
                visitor => BuildTernaryWithTrace(expression, visitor));

        var conditionalUntraced = expression.LeftExpression.Accept(this);
        var ifTrueUntraced = expression.MiddleExpression.Accept(this);
        var ifFalseUntraced = expression.RightExpression.Accept(this);

        return LinqExpression.Condition(conditionalUntraced, ifTrueUntraced, ifFalseUntraced);
    }

    private static LinqExpression BuildTernaryWithTrace(TernaryExpression expression, LambdaExpressionVisitor visitor)
    {
        var genericMethod = TraceRunTernaryMethod.MakeGenericMethod(typeof(object));
        var nodeId = LinqExpression.Parameter(typeof(int), "nodeId");

        return LinqExpression.Call(visitor._trace!, genericMethod, visitor._parentId,
            LinqExpression.Constant(GetNodeKind(expression.MiddleExpression)),
            LinqExpression.Constant(GetNodeName(expression.MiddleExpression)),
            LinqExpression.Constant(GetNodeKind(expression.RightExpression)),
            LinqExpression.Constant(GetNodeName(expression.RightExpression)),
            LinqExpression.Lambda(UnwrapObject(visitor.EmitChild(expression.LeftExpression, nodeId), typeof(bool)), nodeId),
            LinqExpression.Lambda(visitor.EmitChild(expression.MiddleExpression, nodeId), nodeId),
            LinqExpression.Lambda(visitor.EmitChild(expression.RightExpression, nodeId), nodeId));
    }

    public LinqExpression Visit(BinaryExpression expression)
    {
        if (_trace is not null &&
            expression.Type is BinaryExpressionType.And or BinaryExpressionType.Or or BinaryExpressionType.Coalesce)
        {
            return TraceNode(expression, EvaluationTraceNodeKind.Binary, expression.Type.ToString(),
                visitor => BuildTracedLazyBinary(expression, visitor));
        }

        return TraceNode(expression, EvaluationTraceNodeKind.Binary, expression.Type.ToString(),
            () => BuildBinary(expression));
    }

    private LinqExpression BuildBinary(BinaryExpression expression)
    {
        var left = expression.LeftExpression.Accept(this);
        var right = expression.RightExpression.Accept(this);

        return expression.Type switch
        {
            BinaryExpressionType.And => LinqExpression.AndAlso(left, right),
            BinaryExpressionType.Or => LinqExpression.OrElse(left, right),
            BinaryExpressionType.NotEqual => WithCommonNumericType(left, right, LinqExpression.NotEqual, expression.Type),
            BinaryExpressionType.LesserOrEqual => WithCommonNumericType(left, right, LinqExpression.LessThanOrEqual, expression.Type),
            BinaryExpressionType.GreaterOrEqual => WithCommonNumericType(left, right, LinqExpression.GreaterThanOrEqual, expression.Type),
            BinaryExpressionType.Lesser => WithCommonNumericType(left, right, LinqExpression.LessThan, expression.Type),
            BinaryExpressionType.Greater => WithCommonNumericType(left, right, LinqExpression.GreaterThan, expression.Type),
            BinaryExpressionType.Equal => WithCommonNumericType(left, right, LinqExpression.Equal, expression.Type),
            BinaryExpressionType.Minus => _checked ? WithCommonNumericType(left, right, LinqExpression.SubtractChecked) : WithCommonNumericType(left, right, LinqExpression.Subtract),
            BinaryExpressionType.Plus => _checked ? WithCommonNumericType(left, right, LinqExpression.AddChecked) : WithCommonNumericType(left, right, LinqExpression.Add),
            BinaryExpressionType.Modulo => WithCommonNumericType(left, right, LinqExpression.Modulo),
            BinaryExpressionType.Div => WithCommonNumericType(left, right, LinqExpression.Divide),
            BinaryExpressionType.Times => _checked ? WithCommonNumericType(left, right, LinqExpression.MultiplyChecked) : WithCommonNumericType(left, right, LinqExpression.Multiply),
            BinaryExpressionType.BitwiseOr => LinqExpression.Or(left, right),
            BinaryExpressionType.BitwiseAnd => LinqExpression.And(left, right),
            BinaryExpressionType.BitwiseXOr => LinqExpression.ExclusiveOr(left, right),
            BinaryExpressionType.LeftShift => LinqExpression.LeftShift(left, right),
            BinaryExpressionType.RightShift => LinqExpression.RightShift(left, right),
            BinaryExpressionType.Exponentiation => ExponentiationOperator(left, right),
            BinaryExpressionType.Like => LikeOperator(left, right),
            BinaryExpressionType.NotLike => LinqExpression.Not(LikeOperator(left, right)),
            BinaryExpressionType.In => InOperator(left, right),
            BinaryExpressionType.NotIn => LinqExpression.Not(InOperator(left, right)),
            BinaryExpressionType.Coalesce => Coalesce(left, right),
            BinaryExpressionType.Unknown => throw new ArgumentOutOfRangeException(),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static LinqExpression BuildTracedLazyBinary(BinaryExpression expression, LambdaExpressionVisitor visitor)
    {
        var nodeId = LinqExpression.Parameter(typeof(int), "nodeId");
        var leftObject = visitor.EmitChild(expression.LeftExpression, nodeId);
        var rightObject = visitor.EmitChild(expression.RightExpression, nodeId);

        if (expression.Type == BinaryExpressionType.Coalesce)
        {
            var coalesceType = expression.LeftExpression.Accept(visitor).Type;
            var method = TraceRunCoalesceMethod.MakeGenericMethod(coalesceType);

            return LinqExpression.Call(visitor._trace!, method, visitor._parentId,
                LinqExpression.Constant(GetNodeKind(expression.RightExpression)),
                LinqExpression.Constant(GetNodeName(expression.RightExpression)),
                LinqExpression.Lambda(UnwrapObject(leftObject, coalesceType), nodeId),
                LinqExpression.Lambda(UnwrapObject(rightObject, coalesceType), nodeId));
        }

        var helperMethod = expression.Type == BinaryExpressionType.And
            ? TraceRunAndAlsoMethod
            : TraceRunOrElseMethod;

        return LinqExpression.Call(visitor._trace!, helperMethod, visitor._parentId,
            LinqExpression.Constant(GetNodeKind(expression.RightExpression)),
            LinqExpression.Constant(GetNodeName(expression.RightExpression)),
            LinqExpression.Lambda(UnwrapObject(leftObject, typeof(bool)), nodeId),
            LinqExpression.Lambda(UnwrapObject(rightObject, typeof(bool)), nodeId));
    }

    private static System.Linq.Expressions.BinaryExpression Coalesce(LinqExpression left, LinqExpression right)
    {
        if (Nullable.GetUnderlyingType(left.Type) is { } underlyingType)
        {
            if (right.Type != underlyingType && right.Type != left.Type)
                right = LinqExpression.Convert(right, underlyingType);

            return LinqExpression.Coalesce(left, right);
        }

        if (!left.Type.IsValueType)
        {
            if (right.Type != left.Type)
                right = LinqExpression.Convert(right, left.Type);

            return LinqExpression.Coalesce(left, right);
        }

        throw new InvalidOperationException(
            $"The coalesce operator cannot be applied to a non-nullable value of type '{left.Type}'.");
    }

    public LinqExpression Visit(UnaryExpression expression)
    {
        return TraceNode(expression, EvaluationTraceNodeKind.Unary, expression.Type.ToString(), () => BuildUnary(expression, this));
    }

    private static LinqExpression BuildUnary(UnaryExpression expression, LambdaExpressionVisitor visitor)
    {
        var operand = visitor.Emit(expression.Expression, visitor._parentId);

        return expression.Type switch
        {
            UnaryExpressionType.Not => LinqExpression.Not(operand),
            UnaryExpressionType.Negate => LinqExpression.Negate(operand),
            UnaryExpressionType.BitwiseNot => LinqExpression.Not(operand),
            UnaryExpressionType.Positive => operand,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public LinqExpression Visit(ValueExpression expression)
    {
        return TraceNode(expression, EvaluationTraceNodeKind.Value, expression.Value?.ToString() ?? "null",
            () => LinqExpression.Constant(expression.Value));
    }

    public LinqExpression Visit(Function function)
        => TraceNode(function, EvaluationTraceNodeKind.Function, function.Identifier.Name,
            visitor => BuildFunction(function, visitor, visitor._trace is not null));

    private LinqExpression BuildFunction(Function function)
        => BuildFunction(function, this, _trace is not null);

    private LinqExpression BuildFunction(Function function, LambdaExpressionVisitor visitor, bool traced)
    {
        var args = new LinqExpression[function.Parameters.Count];
        for (var index = 0; index < function.Parameters.Count; index++)
        {
            if (traced)
            {
                if (IsLazyIf(function, index))
                    continue;

                var argumentVisitor = new LambdaExpressionVisitor(visitor, visitor._parentId);
                args[index] = function.Parameters[index].Accept(argumentVisitor);
            }
            else
            {
                args[index] = function.Parameters[index].Accept(visitor);
            }
        }

        // Context methods take precedence over built-in functions because they're user-customizable.
        var mi = visitor._context is null ? null : FindMethod(function.Identifier.Name, args);
        if (mi != null)
            return TraceFunctionResult(function.Identifier.Name,
                EvaluationTraceResolutionSource.ContextMethod,
                LinqExpression.Call(visitor._context, mi.MethodInfo, mi.PreparedArguments),
                traced);

        if (traced && string.Equals(function.Identifier.Name, "if", StringComparison.OrdinalIgnoreCase))
            return BuildTracedIf(function, visitor);

        Linq.UnaryExpression arg0;
        Linq.UnaryExpression arg1;

        var comparisonType = _ignoreCaseAtBuiltInFunctions ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var functionName = function.Identifier.Name;

        switch (functionName)
        {
            // Exceptional handling
            case var s when string.Equals(s, "Max", comparisonType):
                CheckArgumentsLengthForFunction(functionName, function.Parameters.Count, 2);
                arg0 = LinqExpression.Convert(args[0], typeof(double));
                arg1 = LinqExpression.Convert(args[1], typeof(double));
                return TraceBuiltInFunction(functionName,
                    LinqExpression.Condition(LinqExpression.GreaterThan(arg0, arg1), arg0, arg1), traced);
            case var s when string.Equals(s, "Min", comparisonType):
                CheckArgumentsLengthForFunction(functionName, function.Parameters.Count, 2);
                arg0 = LinqExpression.Convert(args[0], typeof(double));
                arg1 = LinqExpression.Convert(args[1], typeof(double));
                return TraceBuiltInFunction(functionName,
                    LinqExpression.Condition(LinqExpression.LessThan(arg0, arg1), arg0, arg1), traced);
            case var s when string.Equals(s, "Pow", comparisonType):
                CheckArgumentsLengthForFunction(functionName, function.Parameters.Count, 2);
                return TraceBuiltInFunction(functionName, ExponentiationOperator(args[0], args[1]), traced);

            case var s when string.Equals(s, "Round", comparisonType):
                CheckArgumentsLengthForFunction(functionName, function.Parameters.Count, 2);

                if (args[0].Type == typeof(decimal))
                    arg0 = LinqExpression.Convert(args[0], typeof(decimal));
                else
                    arg0 = LinqExpression.Convert(args[0], typeof(double));

                arg1 = LinqExpression.Convert(args[1], typeof(int));

                var rounding = _options.Math.MidpointRounding;
                return TraceBuiltInFunction(functionName,
                    LinqExpression.Call(MathFunctionHelper.Functions["Round"].First().MethodInfo, arg0, arg1,
                        LinqExpression.Constant(rounding)), traced);
            case var s when string.Equals(s, "if", comparisonType):
                var numberTypePriority = new[] { typeof(double), typeof(float), typeof(long), typeof(int), typeof(short) };
                var index1 = Array.IndexOf(numberTypePriority, args[1].Type);
                var index2 = Array.IndexOf(numberTypePriority, args[2].Type);
                if (index1 >= 0 && index2 >= 0 && index1 != index2)
                {
                    args[1] = LinqExpression.Convert(args[1], numberTypePriority[Math.Min(index1, index2)]);
                    args[2] = LinqExpression.Convert(args[2], numberTypePriority[Math.Min(index1, index2)]);
                }

                return TraceBuiltInFunction(functionName, LinqExpression.Condition(args[0], args[1], args[2]), traced);

            case var s when string.Equals(s, "in", comparisonType):
                var items = LinqExpression.NewArrayInit(args[0].Type,
                    new ArraySegment<LinqExpression>(args, 1, args.Length - 1));
                var smi = typeof(Array).GetMethod("IndexOf", [typeof(Array), typeof(object)]);
                var r = LinqExpression.Call(smi!, LinqExpression.Convert(items, typeof(Array)),
                    LinqExpression.Convert(args[0], typeof(object)));
                return TraceBuiltInFunction(functionName,
                    LinqExpression.GreaterThanOrEqual(r, LinqExpression.Constant(0)), traced);

            case var s when string.Equals(s, "isNull", comparisonType):
                CheckArgumentsLengthForFunction(functionName, function.Parameters.Count, 1);
                return TraceBuiltInFunction(functionName, IsNull(args[0]), traced);

            case var s when string.Equals(s, "isNullOrEmpty", comparisonType):
                CheckArgumentsLengthForFunction(functionName, function.Parameters.Count, 1);
                var isNull = IsNull(args[0]);
                var isEmpty = IsEmptyString(args[0]);
                return TraceBuiltInFunction(functionName, LinqExpression.OrElse(isNull, isEmpty), traced);

            case var s when string.Equals(s, "EscapeLike", comparisonType):
                CheckArgumentsLengthForFunction(functionName, function.Parameters.Count, 1);
                var escapeLikeMethod = typeof(LikeOperatorHelper).GetMethod(
                    nameof(LikeOperatorHelper.EscapeLike),
                    [typeof(string)])!;
                return TraceBuiltInFunction(functionName,
                    LinqExpression.Call(escapeLikeMethod, LinqExpression.Convert(args[0], typeof(string))), traced);

            default:
                // Regular handling
                var kvp = MathFunctionHelper.Functions
                    .FirstOrDefault(_ => string.Equals(functionName, _.Key, comparisonType));

                if (kvp.Key != null)
                {
                    MathMethodInfo func;
                    var f = kvp.Value;

                    if (args.Any(_ => _.Type == typeof(decimal)) && f.Any(_ => _.DecimalSupport))
                        func = f.First(_ => _.DecimalSupport);
                    else
                        func = f.First(_ => !_.DecimalSupport);

                    CheckArgumentsLengthForFunction(functionName, args.Length, func.ArgumentCount);

                    var arguments = new List<LinqExpression>();

                    var parameters = func.MethodInfo.GetParameters();
                    for (int i = 0; i < parameters.Length; i++)
                        arguments.Add(LinqExpression.Convert(args[i], parameters[i].ParameterType));

                    return TraceBuiltInFunction(functionName, LinqExpression.Call(func.MethodInfo, arguments), traced);
                }

                throw new MissingMethodException($"method not found: {functionName}");
        }

        static void CheckArgumentsLengthForFunction(string funcStr, int argsNum, int argsNeed)
        {
            if (argsNum != argsNeed)
                throw new ArgumentException($"{funcStr} takes exactly {argsNeed} argument");
        }

        static LinqExpression IsNull(LinqExpression argument)
        {
            return argument.Type.IsValueType && Nullable.GetUnderlyingType(argument.Type) == null
                ? LinqExpression.Constant(false)
                : LinqExpression.Equal(argument, LinqExpression.Constant(null, argument.Type));
        }

        static LinqExpression IsEmptyString(LinqExpression argument)
        {
            if (argument.Type == typeof(string))
                return LinqExpression.Equal(argument, LinqExpression.Constant(string.Empty));

            if (!argument.Type.IsValueType && argument.Type.IsAssignableFrom(typeof(string)))
            {
                return LinqExpression.AndAlso(
                    LinqExpression.TypeIs(argument, typeof(string)),
                    LinqExpression.Equal(
                        LinqExpression.Convert(argument, typeof(string)),
                        LinqExpression.Constant(string.Empty)));
            }

            return LinqExpression.Constant(false);
        }
    }

    private static bool IsLazyIf(Function function, int index)
    {
        return string.Equals(function.Identifier.Name, "if", StringComparison.OrdinalIgnoreCase) &&
               function.Parameters.Count == 3 && index > 0;
    }

    private LinqExpression BuildTracedIf(Function function, LambdaExpressionVisitor visitor)
    {
        var nodeId = LinqExpression.Parameter(typeof(int), "nodeId");
        var condition = UnwrapObject(visitor.EmitChild(function.Parameters[0], nodeId), typeof(bool));
        var whenTrue = visitor.EmitChild(function.Parameters[1], nodeId);
        var whenFalse = visitor.EmitChild(function.Parameters[2], nodeId);
        var method = TraceRunFunctionIfMethod.MakeGenericMethod(typeof(object));

        return LinqExpression.Call(visitor._trace!, method, visitor._parentId,
            LinqExpression.Constant(GetNodeKind(function.Parameters[1])),
            LinqExpression.Constant(GetNodeName(function.Parameters[1])),
            LinqExpression.Constant(GetNodeKind(function.Parameters[2])),
            LinqExpression.Constant(GetNodeName(function.Parameters[2])),
            LinqExpression.Lambda(condition, nodeId),
            LinqExpression.Lambda(whenTrue, nodeId),
            LinqExpression.Lambda(whenFalse, nodeId));
    }

    private LinqExpression TraceBuiltInFunction(string name, LinqExpression body, bool traced)
    {
        return TraceFunctionResult(name, EvaluationTraceResolutionSource.BuiltInFunction, body, traced);
    }

    private LinqExpression TraceFunctionResult(string name, EvaluationTraceResolutionSource source,
        LinqExpression body, bool traced)
    {
        if (!traced || _trace is null)
            return body;

        var result = LinqExpression.Parameter(typeof(object), "result");
        var assign = LinqExpression.Assign(result, WrapAsObject(body));
        var resolve = LinqExpression.Call(_trace, TraceResolveFunctionMethod,
            _parentId, _parentId, LinqExpression.Constant(name), LinqExpression.Constant(source), result);

        return LinqExpression.Block(
            new[] { result },
            assign,
            resolve,
            UnwrapObject(result, body.Type));
    }

    public LinqExpression Visit(Identifier identifier)
    {
        var identifierName = identifier.Name;

        if (_context == null)
        {
            if (_parameters != null && _parameters.TryGetValue(identifierName, out var param))
            {
                var value = LinqExpression.Constant(param);
                return WrapIdentifier(identifierName, EvaluationTraceResolutionSource.StaticParameter,
                    LinqExpression.Constant(param?.GetType() ?? typeof(object)), value);
            }

            throw new NCalcParameterNotDefinedException(identifierName);
        }

        var member = LinqExpression.PropertyOrField(_context, identifierName);
        return WrapIdentifier(identifierName, EvaluationTraceResolutionSource.ContextMember,
            LinqExpression.Constant(member.Type), member);
    }

    private LinqExpression WrapIdentifier(string name, EvaluationTraceResolutionSource source,
        LinqExpression valueType, LinqExpression value)
    {
        if (_trace is null)
            return value;

        return TraceNode(new Identifier(name), EvaluationTraceNodeKind.Identifier, name,
            () => TraceParameterResolution(name, source, value));
    }

    private LinqExpression TraceParameterResolution(string name, EvaluationTraceResolutionSource source,
        LinqExpression value)
    {
        if (_trace is null)
            return value;

        var resolvedValue = LinqExpression.Parameter(typeof(object), "resolvedValue");
        var assign = LinqExpression.Assign(resolvedValue, WrapAsObject(value));
        var resolve = LinqExpression.Call(_trace, TraceResolveParameterMethod,
            _parentId, _parentId, LinqExpression.Constant(name), LinqExpression.Constant(source), resolvedValue);

        return LinqExpression.Block(
            new[] { resolvedValue },
            assign,
            resolve,
            UnwrapObject(resolvedValue, value.Type));
    }

    public LinqExpression Visit(LogicalExpressionList list)
    {
        return TraceNode(list, EvaluationTraceNodeKind.List, "()", () => BuildList(list));
    }

    private LinqExpression BuildList(LogicalExpressionList list)
    {
        var newList = LinqExpression.New(typeof(List<object>));
        return LinqExpression.ListInit(newList,
            list.Select(e => LinqExpression.Convert(Emit(e, _parentId), typeof(object)))
        );
    }

    private ExtendedMethodInfo? FindMethod(string methodName, LinqExpression[] methodArgs)
    {
        if (_context == null)
            return null;

        var contextType = _context.Type;
        var objectType = typeof(object);

        do
        {
            var methods = contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

            var candidates = new List<ExtendedMethodInfo>();

            foreach (var potentialMethod in methods)
            {
                var methodParams = potentialMethod.GetParameters();
                var preparedArguments = LinqUtils.PrepareMethodArgumentsIfValid(methodParams, methodArgs);

                if (preparedArguments != null)
                {
                    var candidate = new ExtendedMethodInfo
                    {
                        MethodInfo = potentialMethod,
                        PreparedArguments = preparedArguments.Item2,
                        Score = preparedArguments.Item1
                    };

                    if (candidate.Score == 0)
                        return candidate;

                    candidates.Add(candidate);
                }
            }

            if (candidates.Count != 0)
                return candidates.OrderBy(method => method.Score).First();

            contextType = contextType.BaseType;
        }
        while (contextType != null && contextType != objectType);

        return null;
    }

    private LinqExpression WithCommonNumericType(LinqExpression left, LinqExpression right,
        Func<LinqExpression, LinqExpression, LinqExpression> action,
        BinaryExpressionType expressionType = BinaryExpressionType.Unknown)
    {
        left = LinqUtils.UnwrapNullable(left);
        right = LinqUtils.UnwrapNullable(right);

        if (_options.Math.AllowBooleanCalculation)
        {
            if (left.Type == typeof(bool))
            {
                left = LinqExpression.Condition(left, LinqExpression.Constant(1.0), LinqExpression.Constant(0.0));
            }

            if (right.Type == typeof(bool))
            {
                right = LinqExpression.Condition(right, LinqExpression.Constant(1.0), LinqExpression.Constant(0.0));
            }
        }

        var type = TypeHelper.GetMostPreciseNumberType(left.Type, right.Type);
        if (type != null)
        {
            if (left.Type != type)
            {
                left = LinqExpression.Convert(left, type);
            }

            if (right.Type != type)
            {
                right = LinqExpression.Convert(right, type);
            }
        }

        if (typeof(string) != left.Type && typeof(string) != right.Type)
            return action(left, right);

        LinqExpression comparer = LinqExpression.Constant(_stringComparer);

        switch (expressionType)
        {
            case BinaryExpressionType.Equal:
                return LinqExpression.Call(comparer, StringComparerEqualsMethod, [left, right]);
            case BinaryExpressionType.NotEqual:
                return LinqExpression.Not(
                    LinqExpression.Call(comparer, StringComparerEqualsMethod, [left, right]));
            case BinaryExpressionType.GreaterOrEqual:
                return LinqExpression.GreaterThanOrEqual(
                    LinqExpression.Call(comparer, StringComparerCompareMethod, [left, right]),
                    LinqExpression.Constant(0));
            case BinaryExpressionType.LesserOrEqual:
                return LinqExpression.LessThanOrEqual(
                    LinqExpression.Call(comparer, StringComparerCompareMethod, [left, right]),
                    LinqExpression.Constant(0));
            case BinaryExpressionType.Greater:
                return LinqExpression.GreaterThan(
                    LinqExpression.Call(comparer, StringComparerCompareMethod, [left, right]),
                    LinqExpression.Constant(0));
            case BinaryExpressionType.Lesser:
                return LinqExpression.LessThan(
                    LinqExpression.Call(comparer, StringComparerCompareMethod, [left, right]),
                    LinqExpression.Constant(0));
        }

        return action(left, right);
    }

    private LinqExpression InOperator(LinqExpression left, LinqExpression arr)
    {
        if (arr == null) return LinqExpression.Constant(false);

        if (!typeof(IEnumerable).IsAssignableFrom(arr.Type))
            return LinqExpression.Constant(false);

        var isString = left.Type == typeof(string);

        var castMi = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Enumerable.Cast) && m.GetParameters().Length == 1)
            .MakeGenericMethod(left.Type);
        var containsMi = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == (isString ? 3 : 2))
            .MakeGenericMethod(left.Type);

        var source = LinqExpression.Call(castMi, LinqExpression.Convert(arr, typeof(IEnumerable)));

        if (isString)
        {
            LinqExpression comparer = LinqExpression.Constant(_stringComparer);

            return LinqExpression.Call(
                null, containsMi, source, LinqExpression.Convert(left, typeof(string)), comparer);
        }

        return LinqExpression.Call(null, containsMi, source, left);
    }

    public LinqExpression LikeOperator(LinqExpression leftValue, LinqExpression? rightValue)
    {
        if (leftValue is null || rightValue is null)
            return LinqExpression.Constant(false);

        var leftObj = LinqExpression.Convert(leftValue, typeof(string));
        var rightObj = LinqExpression.Convert(rightValue, typeof(string));

        var likeMethod = typeof(LikeOperatorHelper).GetMethod(
            nameof(LikeOperatorHelper.Like),
            [typeof(string), typeof(string), typeof(StringComparer)])!;
        var callIsMatch = LinqExpression.Call(
            likeMethod,
            leftObj,
            rightObj,
            LinqExpression.Constant(_options.StringComparer));

        // if either side is null should be false
        var leftNull = LinqExpression.Equal(leftObj, LinqExpression.Constant(null));
        var rightNull = LinqExpression.Equal(rightObj, LinqExpression.Constant(null));
        var anyNull = LinqExpression.OrElse(leftNull, rightNull);

        return LinqExpression.Condition(anyNull, LinqExpression.Constant(false), callIsMatch);
    }

    public static LinqExpression ExponentiationOperator(LinqExpression left, LinqExpression right)
    {
        Linq.UnaryExpression arg0;
        Linq.UnaryExpression arg1;

        if (left.Type == typeof(decimal))
        {
            arg0 = LinqExpression.Convert(left, typeof(decimal));
            arg1 = LinqExpression.Convert(right, typeof(decimal));

            var @base = LinqExpression.Convert(arg0, typeof(BigDecimal));
            var exponent = LinqExpression.Convert(arg1, typeof(BigInteger));

            var methodInfo = typeof(BigDecimal).GetMethod("Pow", [typeof(BigDecimal), typeof(BigInteger)]);
            if (methodInfo != null)
            {
                var result = LinqExpression.Call(methodInfo, @base, exponent);
                return LinqExpression.Convert(result, typeof(decimal));
            }
        }

        arg0 = LinqExpression.Convert(left, typeof(double));
        arg1 = LinqExpression.Convert(right, typeof(double));

        return LinqExpression.Power(arg0, arg1);
    }
}
