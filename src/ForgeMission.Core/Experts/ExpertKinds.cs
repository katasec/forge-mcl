namespace ForgeMission.Core.Experts;

/// <summary>
/// The one vocabulary of expert <c>kind</c> values. Matching is exact and lowercase;
/// an expert with no <c>kind</c> is an <see cref="Llm"/> step.
/// </summary>
public static class ExpertKinds
{
    public const string Llm         = "llm";
    public const string Rule        = "rule";
    public const string JsonExtract = "json_extract";
    public const string Exec        = "exec";
    public const string Onnx        = "onnx";
    public const string Http        = "http";
    public const string Search      = "search";

    public static readonly IReadOnlyList<string> All = [Llm, Rule, JsonExtract, Exec, Onnx, Http, Search];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
