using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ForgeMission.Core.Experts;
using ForgeMission.Parser;

namespace ForgeMission.Core.Runtime;

// Explicit semantic encoding avoids collection ToString(), delimiter ambiguity and scratch paths.
internal static class PipelineDefinitionFingerprint
{
    internal static string Compute(Program ast, IReadOnlyDictionary<string, ExpertDefinition> experts, string root)
    {
        var text = new StringBuilder();
        Append(text, root);
        AppendMany(text, DurableMissionInputPolicy.AdmittedNames(ast, experts, root));
        Append(text, ast.Bindings.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var binding in ast.Bindings) { Append(text, binding.Name); AppendLet(text, binding.Value); }
        var missions = ast.Declarations.OfType<MissionDeclaration>().OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
        Append(text, missions.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var mission in missions)
            AppendMission(text, mission);
        Append(text, ast.Outputs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var output in ast.Outputs) { Append(text, output.MissionName); Append(text, output.FilePath); }
        Append(text, experts.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in experts.OrderBy(e => e.Key, StringComparer.Ordinal))
        { Append(text, pair.Key); AppendExpert(text, pair.Value); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void AppendMission(StringBuilder text, MissionDeclaration mission)
    {
        Append(text, "mission"); Append(text, mission.Name); AppendMany(text, mission.Params);
        Append(text, mission.MaxLoops.ToString(CultureInfo.InvariantCulture));
        Append(text, mission.Pipeline.Elements.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var element in mission.Pipeline.Elements)
        {
            Append(text, element is ParallelElement ? "parallel" : "step");
            var steps = element is ParallelElement parallel ? parallel.Steps : [((StepElement)element).Step];
            Append(text, steps.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var step in steps) AppendStep(text, step);
        }
    }

    private static void AppendStep(StringBuilder text, Step step)
    {
        Append(text, step.ExpertName); Append(text, step.Using);
        Append(text, step.Context.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var binding in step.Context) { Append(text, binding.Key); AppendBinding(text, binding.Value); }
        Append(text, step.When switch
        {
            StringEqualsWhen condition => "string",
            NumericCompareWhen condition => "numeric",
            ElseWhen => "else",
            _ => "none",
        });
        if (step.When is StringEqualsWhen equals) { Append(text, equals.Key); Append(text, equals.Value); }
        if (step.When is NumericCompareWhen numeric)
        { Append(text, numeric.Key); Append(text, numeric.Op.ToString()); Append(text, numeric.Threshold.ToString("R", CultureInfo.InvariantCulture)); }
    }

    private static void AppendExpert(StringBuilder text, ExpertDefinition expert)
    {
        AppendMany(text, [expert.Name, expert.Input, expert.Output, expert.SystemPrompt, expert.Role, expert.Kind,
            expert.Endpoint, expert.Check, expert.OnFail, expert.Model, expert.OutputKey, expert.Threshold,
            expert.Command, expert.Timeout]);
        AppendMany(text, expert.Inputs ?? []); AppendMany(text, expert.Args ?? []);
        AppendDictionary(text, expert.InputKeys); AppendDictionary(text, expert.OutputKeys);
    }

    private static void AppendLet(StringBuilder text, LetValue value)
    {
        Append(text, value is EnvLetValue ? "env" : "string");
        if (value is StringLetValue literal) Append(text, literal.Text);
        if (value is EnvLetValue environment) { Append(text, environment.VarName); Append(text, environment.DefaultValue); }
    }

    private static void AppendBinding(StringBuilder text, BindingValue value)
    {
        switch (value)
        {
            case StringBindingValue literal: Append(text, "string"); Append(text, literal.Text); break;
            case VarRefBindingValue variable: Append(text, "var"); Append(text, variable.Name); break;
            case NumberBindingValue number: Append(text, "number"); Append(text, number.Number.ToString(CultureInfo.InvariantCulture)); break;
            case EnvBindingValue environment: Append(text, "env"); Append(text, environment.VarName); Append(text, environment.DefaultValue); break;
        }
    }

    private static void AppendDictionary(StringBuilder text, IReadOnlyDictionary<string, string>? values)
    {
        Append(text, (values?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        foreach (var pair in (values ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
        { Append(text, pair.Key); Append(text, pair.Value); }
    }

    private static void AppendMany(StringBuilder text, IReadOnlyList<string> values)
    {
        Append(text, values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in values) Append(text, value);
    }

    private static void Append(StringBuilder text, string? value)
    {
        if (value is null) { text.Append("-1:"); return; }
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}
