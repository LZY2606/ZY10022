namespace NCalc.Tracing;

/// <summary>
/// Identifies where a parameter or function value came from.
/// </summary>
public enum EvaluationTraceResolutionSource
{
    /// <summary>
    /// No resolution source applies to this event.
    /// </summary>
    None,

    /// <summary>
    /// A synchronous parameter event handler supplied the value.
    /// </summary>
    ParameterHandler,

    /// <summary>
    /// An asynchronous parameter event handler supplied the value.
    /// </summary>
    AsyncParameterHandler,

    /// <summary>
    /// The value came from <see cref="ExpressionContext.Parameters"/>.
    /// </summary>
    StaticParameter,

    /// <summary>
    /// The value came from <see cref="ExpressionContext.DynamicParameters"/>.
    /// </summary>
    DynamicParameter,

    /// <summary>
    /// The value came from <see cref="ExpressionContext.AsyncParameters"/>.
    /// </summary>
    AsyncParameter,

    /// <summary>
    /// The identifier was the configured nullable literal.
    /// </summary>
    NullLiteral,

    /// <summary>
    /// A nested <see cref="Expression"/> supplied the value.
    /// </summary>
    NestedExpression,

    /// <summary>
    /// A compiled lambda read a parameter from a context member.
    /// </summary>
    ContextMember,

    /// <summary>
    /// A synchronous function event handler supplied the result.
    /// </summary>
    FunctionHandler,

    /// <summary>
    /// An asynchronous function event handler supplied the result.
    /// </summary>
    AsyncFunctionHandler,

    /// <summary>
    /// The function came from <see cref="ExpressionContext.Functions"/>.
    /// </summary>
    Function,

    /// <summary>
    /// The function came from <see cref="ExpressionContext.AsyncFunctions"/>.
    /// </summary>
    AsyncFunction,

    /// <summary>
    /// The function was a built-in function.
    /// </summary>
    BuiltInFunction,

    /// <summary>
    /// A compiled lambda invoked a method from the supplied context.
    /// </summary>
    ContextMethod
}
