namespace NCalc.Tracing;

/// <summary>
/// Where a parameter or function value came from.
/// </summary>
public enum TraceResolutionSource
{
    /// <summary>
    /// No resolution happened, or the event is not a <see cref="TraceEventKind.Resolved"/> event.
    /// </summary>
    None,

    /// <summary>
    /// A synchronous parameter event handler (<c>EvaluateParameter</c>) provided the value.
    /// </summary>
    ParameterHandler,

    /// <summary>
    /// An asynchronous parameter event handler (<c>EvaluateAsyncParameter</c>) provided the value.
    /// </summary>
    AsyncParameterHandler,

    /// <summary>
    /// The value came from the static <c>Parameters</c> dictionary.
    /// </summary>
    StaticParameter,

    /// <summary>
    /// The value came from a <c>DynamicParameters</c> callback.
    /// </summary>
    DynamicParameter,

    /// <summary>
    /// The value came from an <c>AsyncParameters</c> callback.
    /// </summary>
    AsyncParameter,

    /// <summary>
    /// The identifier was the built-in <c>null</c> keyword.
    /// </summary>
    NullKeyword,

    /// <summary>
    /// A synchronous function event handler (<c>EvaluateFunction</c>) provided the result.
    /// </summary>
    FunctionHandler,

    /// <summary>
    /// An asynchronous function event handler (<c>EvaluateAsyncFunction</c>) provided the result.
    /// </summary>
    AsyncFunctionHandler,

    /// <summary>
    /// The result came from a registered function in <c>Functions</c>.
    /// </summary>
    Function,

    /// <summary>
    /// The result came from a registered function in <c>AsyncFunctions</c>.
    /// </summary>
    AsyncFunction,

    /// <summary>
    /// The result came from a built-in function such as <c>Max</c> or <c>if</c>.
    /// </summary>
    BuiltInFunction
}
