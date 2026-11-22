using System.Globalization;
using ForgeMission.Parser;

namespace ForgeMission.Core.Runtime;

/// <summary>
/// Evaluates a step's <c>when()</c> guard. A guarded step runs only when its guard matches;
/// <c>when(else)</c> runs only when no guard has.
/// </summary>
internal static class WhenGuards
{
    public static bool Admits(WhenClause? when, Dictionary<string, object> context, bool anyGuardedStepMatched) => when switch
    {
        StringEqualsWhen stringEquals => context.TryGetValue(stringEquals.Key, out var value)
                                         && value?.ToString() == stringEquals.Value,
        NumericCompareWhen numericCompare => context.TryGetValue(numericCompare.Key, out var raw)
                                             && TryParseDouble(raw, out var actual)
                                             && EvaluateNumericOp(actual, numericCompare.Op, numericCompare.Threshold),
        ElseWhen => !anyGuardedStepMatched,
        _ => true,
    };

    public static bool IsGuarded(WhenClause? when) => when is StringEqualsWhen or NumericCompareWhen;

    public static bool HasUnmatchedGuards(MissionDeclaration mission, bool anyGuardedStepMatched)
    {
        var steps = mission.Pipeline.Elements.OfType<StepElement>().ToList();
        return steps.Any(e => IsGuarded(e.Step.When))
            && !anyGuardedStepMatched
            && !steps.Any(e => e.Step.When is ElseWhen);
    }

    private static bool TryParseDouble(object? value, out double result)
    {
        result = 0;
        return value switch
        {
            double d   => (result = d)    == d,
            float f    => (result = f)    == f,
            int i      => (result = i)    == i,
            long l     => (result = l)    == l,
            string s   => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result),
            _          => false
        };
    }

    private static bool EvaluateNumericOp(double actual, CompOp op, double threshold) => op switch
    {
        CompOp.Gt  => actual >  threshold,
        CompOp.Lt  => actual <  threshold,
        CompOp.Gte => actual >= threshold,
        CompOp.Lte => actual <= threshold,
        CompOp.Eq  => Math.Abs(actual - threshold) < 1e-10,
        _          => false
    };
}
