using NCalc.Visitors;
using LinqExpression = System.Linq.Expressions.Expression;

namespace NCalc.LambdaCompilation.Visitors;

/// <summary>
/// A synthetic syntax node carrying an already built LINQ expression. Used internally while weaving
/// trace instrumentation around the ordinary lambda compilation.
/// </summary>
internal sealed class PlaceholderExpression : LogicalExpression
{
    public PlaceholderExpression(LinqExpression expression)
    {
        Expression = expression;
    }

    public LinqExpression Expression { get; }

    public override T Accept<T>(ILogicalExpressionVisitor<T> visitor)
    {
        if (visitor is LambdaExpressionVisitor lambdaVisitor)
            return (T)(object)lambdaVisitor.VisitPlaceholder(this);

        throw new InvalidOperationException(
            "A placeholder expression can only be used during lambda compilation.");
    }
}
