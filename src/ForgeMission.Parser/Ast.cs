namespace ForgeMission.Parser;

// Line is 1-based; Col is 0-based.
public record SourceSpan(int StartLine, int StartCol, int EndLine, int EndCol);

public record Program(
    IReadOnlyList<LetBinding> Bindings,
    IReadOnlyList<Declaration> Declarations,
    IReadOnlyList<OutputDeclaration> Outputs,
    SourceSpan? Span = null)
{
    /// <summary>The mission declarations, in source order.</summary>
    public IEnumerable<MissionDeclaration> Missions => Declarations.OfType<MissionDeclaration>();

    /// <summary>The mission named <paramref name="name"/> (ordinal match), or null.</summary>
    public MissionDeclaration? FindMission(string name) => Missions.FirstOrDefault(m => m.Name == name);
}

public record OutputDeclaration(string MissionName, string? FilePath, SourceSpan? Span = null);

public record LetBinding(string Name, LetValue Value, SourceSpan? Span = null);

public abstract record LetValue;
public record StringLetValue(string Text) : LetValue;
public record EnvLetValue(string VarName, string? DefaultValue) : LetValue;

public abstract record Declaration(string Name);

public record MissionDeclaration(string Name, IReadOnlyList<string> Params, Pipeline Pipeline, int MaxLoops = 1, SourceSpan? Span = null)
    : Declaration(Name);

// Pipeline is a sequence of elements — each element is a step or a parallel block.
public record Pipeline(IReadOnlyList<PipelineElement> Elements, SourceSpan? Span = null)
{
    /// <summary>Every step in pipeline order, with each parallel block's steps in place.</summary>
    public IEnumerable<Step> Steps() => Elements.SelectMany(element => element switch
    {
        StepElement step => (IEnumerable<Step>)[step.Step],
        ParallelElement parallel => parallel.Steps,
        _ => [],
    });
}

public abstract record PipelineElement;
public record StepElement(Step Step) : PipelineElement;
public record ParallelElement(IReadOnlyList<Step> Steps, SourceSpan? Span = null) : PipelineElement;

// A step is a named expert/mission call with optional domain context, provider profile, and guard.
public record Step(
    string ExpertName,
    IReadOnlyList<Binding> Context,   // (key: value) named parameters
    string? Using,                    // using <profile> — provider profile selector
    WhenClause? When,                 // when() guard — null means unconditional
    SourceSpan? Span = null);

// When guard — abstract so new expression types are additive
public abstract record WhenClause;
public record StringEqualsWhen(string Key, string Value) : WhenClause;
public record NumericCompareWhen(string Key, CompOp Op, double Threshold) : WhenClause;
public record ElseWhen() : WhenClause;

public enum CompOp { Gt, Lt, Gte, Lte, Eq }

// Binding value types
public abstract record BindingValue;
public record StringBindingValue(string Text) : BindingValue;
public record VarRefBindingValue(string Name) : BindingValue;
public record EnvBindingValue(string VarName, string? DefaultValue) : BindingValue;
public record NumberBindingValue(int Number) : BindingValue;

public record Binding(string Key, BindingValue Value);
