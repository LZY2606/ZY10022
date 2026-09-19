using System.Reflection;
using NCalc.Exceptions;
using NCalc.Tracing;
using NCalc.Visitors;
using Linq = System.Linq.Expressions;
using LinqExpression = System.Linq.Expressions.Expression;
using LinqParameterExpression = System.Linq.Expressions.ParameterExpression;

namespace NCalc.LambdaCompilation.Visitors;

/// <summary>
/// Builds a compiled lambda that emits the same <see cref="EvaluationTrace"/> event model as the
/// interpreted synchronous and asynchronous visitors.
/// </summary>
/// <remarks>
/// <para>
/// Node identifiers are assigned when the compiled delegate runs, not at compile time, so every
/// invocation with its own trace receives fresh, isolated sequence numbers. The emitted code wraps
/// the ordinary compiled evaluation; nothing is evaluated a second time to produce the trace.
/// </para>
/// <para>
/// Only the expression itself is traced. In the contextual overload parameter reads become property
/// accessors on the context object, reported with
/// <see cref="TraceResolutionSource.StaticParameter"/>; registered callbacks, dynamic parameters and
/// the parsed expression cache are not part of compiled lambdas.
/// </para>
/// </remarks>
public sealed class TracingLambdaExpressionVisitor : ILogicalExpressionVisitor<LinqExpression>
{
    private static readonly MethodInfo EnterCompiledNodeMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.EnterCompiledNode))!;

    private static readonly MethodInfo SkipCompiledNodeMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.SkipCompiledNode))!;

    private static readonly MethodInfo ScopeCompleteMethod =
        typeof(TraceNodeScope).GetMethod(nameof(TraceNodeScope.Complete))!;

    private static readonly MethodInfo ScopeFaultMethod =
        typeof(TraceNodeScope).GetMethod(nameof(TraceNodeScope.Fault))!;

    private static readonly MethodInfo ResolveCompiledParameterMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.ResolveCompiledParameter))!;

    private static readonly MethodInfo ResolveCompiledFunctionMethod = typeof(EvaluationTrace)
        .GetMethod(nameof(EvaluationTrace.ResolveCompiledFunction))!;

    private readonly LinqParameterExpression _traceParameter;
    private readonly LambdaExpressionVisitor _inner;
    private readonly IDictionary<string, object?>? _parameters;
    private readonly LinqExpression? _context;

    /// <summary>
    /// Creates a tracing visitor for the no-context <c>Func&lt;EvaluationTrace, TResult&gt;</c> shape.
    /// </summary>
    public TracingLambdaExpressionVisitor(
        LinqParameterExpression traceParameter,
        IDictionary<string, object?>? parameters,
        ExpressionEvaluationOptions options)
    {
        _traceParameter = traceParameter;
        _parameters = parameters;
        _inner = new LambdaExpressionVisitor(parameters ?? new Dictionary<string, object?>(), options);
    }

    /// <summary>
    /// Creates a tracing visitor for the <c>Func&lt;TContext, EvaluationTrace, TResult&gt;</c> shape.
    /// </summary>
    public TracingLambdaExpressionVisitor(
        LinqParameterExpression contextParameter,
        LinqParameterExpression traceParameter,
        ExpressionEvaluationOptions options)
    {
        _traceParameter = traceParameter;
        _context = contextParameter;
        _inner = new LambdaExpressionVisitor(contextParameter, options);
    }

    /// <summary>
    /// The parameter expression representing the <see cref="EvaluationTrace"/> argument.
    /// </summary>
    public LinqParameterExpression TraceParameter => _traceParameter;

    public LinqExpression Visit(TernaryExpression expression)
    {
        return Wrap(TraceNodeKind.Ternary, null, () =>
        {
            var condition = expression.LeftExpression.Accept(this);
            var ifTrue = expression.MiddleExpression.Accept(this);
            var ifFalse = expression.RightExpression.Accept(this);

            var skipFalse = Skip(expression.RightExpression, TraceSkipReason.TernaryBranch,
                "Condition was true");
            var skipTrue = Skip(expression.MiddleExpression, TraceSkipReason.TernaryBranch,
                "Condition was false");

            return LinqExpression.Condition(condition,
                LinqExpression.Block(skipFalse, ifTrue),
                LinqExpression.Block(skipTrue, ifFalse));
        });
    }

    public LinqExpression Visit(BinaryExpression expression)
    {
        return Wrap(TraceNodeKind.Binary, expression.Type.ToString(), () =>
        {
            if (expression.Type == BinaryExpressionType.And)
                return BuildShortCircuit(expression, isAnd: true);

            if (expression.Type == BinaryExpressionType.Or)
                return BuildShortCircuit(expression, isAnd: false);

            if (expression.Type == BinaryExpressionType.Coalesce)
                return BuildCoalesce(expression);

            return CompileOrdinaryBinary(expression);
        });
    }

    public LinqExpression Visit(UnaryExpression expression)
    {
        return Wrap(TraceNodeKind.Unary, expression.Type.ToString(), () =>
        {
            var operand = expression.Expression.Accept(this);
            return _inner.Visit(new UnaryExpression(expression.Type, new PlaceholderExpression(operand)));
        });
    }

    public LinqExpression Visit(ValueExpression expression)
    {
        return Wrap(TraceNodeKind.Value, EvaluationTrace.GetNodeName(expression),
            () => LinqExpression.Constant(expression.Value));
    }

    public LinqExpression Visit(Identifier identifier)
    {
        return Wrap(TraceNodeKind.Identifier, identifier.Name, () =>
        {
            LinqExpression access;
            TraceResolutionSource source;
            if (_context is not null)
            {
                access = LinqExpression.PropertyOrField(_context, identifier.Name);
                source = TraceResolutionSource.StaticParameter;
            }
            else if (_parameters is not null && _parameters.TryGetValue(identifier.Name, out var value))
            {
                access = LinqExpression.Constant(value);
                source = TraceResolutionSource.StaticParameter;
            }
            else
            {
                throw new NCalcParameterNotDefinedException(identifier.Name);
            }

            var accessVariable = LinqExpression.Variable(access.Type, "parameterValue");
            var assignAccess = LinqExpression.Assign(accessVariable, access);
            var resolved = LinqExpression.Call(_traceParameter, ResolveCompiledParameterMethod,
                LinqExpression.Constant(identifier.Name),
                LinqExpression.Constant(source),
                LinqExpression.Convert(accessVariable, typeof(object)));

            return LinqExpression.Block(access.Type, new[] { accessVariable },
                assignAccess, resolved, accessVariable);
        });
    }

    public LinqExpression Visit(Function function)
    {
        return Wrap(TraceNodeKind.Function, function.Identifier.Name, () =>
        {
            var arguments = new LinqExpression[function.Parameters.Count];
            for (var index = 0; index < function.Parameters.Count; index++)
                arguments[index] = function.Parameters[index].Accept(this);

            var placeholders = new PlaceholderExpression[arguments.Length];
            for (var index = 0; index < arguments.Length; index++)
                placeholders[index] = new PlaceholderExpression(arguments[index]);

            var result = _inner.Visit(new Function(function.Identifier,
                new LogicalExpressionList(placeholders)));

            var resultVariable = LinqExpression.Variable(result.Type, "functionResult");
            var assignResult = LinqExpression.Assign(resultVariable, result);

            var resolved = LinqExpression.Call(_traceParameter, ResolveCompiledFunctionMethod,
                LinqExpression.Constant(function.Identifier.Name),
                LinqExpression.Constant(TraceResolutionSource.BuiltInFunction),
                LinqExpression.Convert(resultVariable, typeof(object)),
                LinqExpression.Constant("Built-in function"));

            return LinqExpression.Block(result.Type, new[] { resultVariable },
                assignResult, resolved, resultVariable);
        });
    }

    public LinqExpression Visit(LogicalExpressionList list)
    {
        return Wrap(TraceNodeKind.List, null, () =>
        {
            var items = list.Select(expressionNode => expressionNode.Accept(this)).ToArray();
            var newList = LinqExpression.New(typeof(List<object>));
            return (LinqExpression)LinqExpression.ListInit(newList,
                items.Select(item => LinqExpression.Convert(item, typeof(object))));
        });
    }

    private LinqExpression CompileOrdinaryBinary(BinaryExpression expression)
    {
        var left = expression.LeftExpression.Accept(this);
        var right = expression.RightExpression.Accept(this);
        return _inner.Visit(new BinaryExpression(expression.Type,
            new PlaceholderExpression(left),
            new PlaceholderExpression(right)));
    }

    private LinqExpression BuildShortCircuit(BinaryExpression expression, bool isAnd)
    {
        var left = expression.LeftExpression.Accept(this);
        var right = expression.RightExpression.Accept(this);

        if (left.Type != typeof(bool))
            left = LinqExpression.Convert(left, typeof(bool));
        if (right.Type != typeof(bool))
            right = LinqExpression.Convert(right, typeof(bool));

        var leftValue = LinqExpression.Variable(typeof(bool), "leftValue");
        var rightValue = LinqExpression.Variable(typeof(bool), "rightValue");

        var skip = Skip(expression.RightExpression,
            isAnd ? TraceSkipReason.ShortCircuitAnd : TraceSkipReason.ShortCircuitOr,
            isAnd ? "Left side of 'and' was false" : "Left side of 'or' was true");

        // When the short circuit fires the right side is never evaluated: store the default value
        // and only report the skipped event.
        var evaluateOrSkipRight = LinqExpression.Condition(
            isAnd ? leftValue : LinqExpression.Not(leftValue),
            right,
            LinqExpression.Block(skip, LinqExpression.Constant(isAnd ? false : true)));

        return LinqExpression.Block(new[] { leftValue, rightValue },
            LinqExpression.Assign(leftValue, left),
            LinqExpression.Assign(rightValue, evaluateOrSkipRight),
            isAnd
                ? LinqExpression.AndAlso(leftValue, rightValue)
                : LinqExpression.OrElse(leftValue, rightValue));
    }

    private LinqExpression BuildCoalesce(BinaryExpression expression)
    {
        var left = expression.LeftExpression.Accept(this);
        var right = expression.RightExpression.Accept(this);

        // Non-nullable value types cannot be null; defer to the ordinary compiler and emit no skip.
        if (left.Type.IsValueType && Nullable.GetUnderlyingType(left.Type) is null)
        {
            return _inner.Visit(new BinaryExpression(BinaryExpressionType.Coalesce,
                new PlaceholderExpression(left),
                new PlaceholderExpression(right)));
        }

        var leftValue = LinqExpression.Variable(left.Type, "coalesceLeft");
        var underlyingNullableType = Nullable.GetUnderlyingType(left.Type);

        LinqExpression leftIsNull = underlyingNullableType is not null
            ? LinqExpression.Not(LinqExpression.Property(
                LinqExpression.Convert(leftValue, underlyingNullableType), "HasValue"))
            : LinqExpression.ReferenceEqual(leftValue, LinqExpression.Constant(null));

        var skip = Skip(expression.RightExpression, TraceSkipReason.Coalesce,
            "Left side of coalesce was not null");

        // Keep both outcomes as object so string/nullable branches unify. The right subtree is only
        // reachable from the null branch, therefore it is never evaluated on a cache hit.
        var leftAsObject = underlyingNullableType is not null
            ? LinqExpression.Property(LinqExpression.Convert(leftValue, underlyingNullableType), "Value")
            : (LinqExpression)leftValue;

        var result = LinqExpression.Condition(leftIsNull,
            LinqExpression.Convert(right, typeof(object)),
            LinqExpression.Block(skip, LinqExpression.Convert(leftAsObject, typeof(object))));

        return LinqExpression.Block(typeof(object), new[] { leftValue },
            LinqExpression.Assign(leftValue, left),
            result);
    }

    private LinqExpression Skip(LogicalExpression node, TraceSkipReason reason, string detail)
    {
        return LinqExpression.Call(_traceParameter, SkipCompiledNodeMethod,
            LinqExpression.Constant(EvaluationTrace.GetNodeKind(node)),
            LinqExpression.Constant(reason),
            LinqExpression.Constant(EvaluationTrace.GetNodeName(node), typeof(string)),
            LinqExpression.Constant(detail, typeof(string)));
    }

    private LinqExpression Wrap(TraceNodeKind nodeKind, string? name, Func<LinqExpression> bodyFactory)
    {
        var body = bodyFactory();
        var scope = LinqExpression.Variable(typeof(TraceNodeScope), "scope");
        var result = LinqExpression.Variable(body.Type, "result");
        var exception = LinqExpression.Parameter(typeof(Exception), "ex");

        var enter = LinqExpression.Assign(scope,
            LinqExpression.Call(_traceParameter, EnterCompiledNodeMethod,
                LinqExpression.Constant(nodeKind),
                LinqExpression.Constant(name, typeof(string))));

        var evaluate = LinqExpression.Assign(result, body);

        var complete = LinqExpression.Call(scope, ScopeCompleteMethod,
            body.Type.IsValueType
                ? LinqExpression.Convert(result, typeof(object))
                : body.Type == typeof(object)
                    ? result
                    : LinqExpression.Convert(result, typeof(object)));

        var fault = LinqExpression.Call(scope, ScopeFaultMethod, exception);

        // Evaluate the body exactly once, then report the strongly typed result to the parent node.
        var work = LinqExpression.Block(evaluate, complete);
        var tried = LinqExpression.TryCatch(
            work,
            LinqExpression.Catch(exception,
                LinqExpression.Block(fault, LinqExpression.Rethrow())));

        return LinqExpression.Block(body.Type, new[] { scope, result }, enter, tried, result);
    }
}
