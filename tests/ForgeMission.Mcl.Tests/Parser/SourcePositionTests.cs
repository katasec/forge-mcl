using ForgeMission.Parser;

namespace ForgeMission.Tests.Parser;

public class SourcePositionTests
{
    // ANTLR coordinates: line is 1-based, column is 0-based.

    [Fact]
    public void MissionDeclaration_SpanStartsAtKeyword()
    {
        var ast     = MclParser.Parse("mission BuildOperator = { KubernetesArchitect }");
        var mission = ast.Declarations.OfType<MissionDeclaration>().Single();

        Assert.NotNull(mission.Span);
        Assert.Equal(1, mission.Span!.StartLine);
        Assert.Equal(0, mission.Span.StartCol);
    }

    [Fact]
    public void Step_SpanStartsAtExpertName()
    {
        var source = """
            mission M = {
                KubernetesArchitect
            }
            """;
        var ast  = MclParser.Parse(source);
        var step = ast.Declarations.OfType<MissionDeclaration>().Single()
                      .Pipeline.Elements.OfType<StepElement>().Single().Step;

        Assert.NotNull(step.Span);
        Assert.Equal(2, step.Span!.StartLine);
        Assert.Equal(4, step.Span.StartCol);     // 4-space indent
    }

    [Fact]
    public void MultiStep_EachStepHasDistinctLine()
    {
        var source = """
            mission M = {
                ExpertA
                -> ExpertB
                -> ExpertC
            }
            """;
        var ast   = MclParser.Parse(source);
        var steps = ast.Declarations.OfType<MissionDeclaration>().Single()
                       .Pipeline.Elements.OfType<StepElement>()
                       .Select(e => e.Step)
                       .ToList();

        Assert.Equal(3, steps.Count);
        Assert.Equal(2, steps[0].Span!.StartLine);
        Assert.Equal(3, steps[1].Span!.StartLine);
        Assert.Equal(4, steps[2].Span!.StartLine);
    }

    [Fact]
    public void LetBinding_SpanStartsAtKeyword()
    {
        var source = """
            let goal = "Build a K8s operator"
            mission Demo = { Worker }
            """;
        var ast     = MclParser.Parse(source);
        var binding = ast.Bindings.Single();

        Assert.NotNull(binding.Span);
        Assert.Equal(1, binding.Span!.StartLine);
        Assert.Equal(0, binding.Span.StartCol);
    }

    [Fact]
    public void ParallelBlock_SpanStartsAtParallelKeyword()
    {
        var source = """
            mission M = {
                parallel {
                    ExpertA
                    ExpertB
                }
            }
            """;
        var ast      = MclParser.Parse(source);
        var parallel = ast.Declarations.OfType<MissionDeclaration>().Single()
                          .Pipeline.Elements.OfType<ParallelElement>().Single();

        Assert.NotNull(parallel.Span);
        Assert.Equal(2, parallel.Span!.StartLine);
        Assert.Equal(4, parallel.Span.StartCol);
    }

    [Fact]
    public void ParallelSteps_EachHaveDistinctLines()
    {
        var source = """
            mission M = {
                parallel {
                    ExpertA
                    ExpertB
                }
            }
            """;
        var ast      = MclParser.Parse(source);
        var parallel = ast.Declarations.OfType<MissionDeclaration>().Single()
                          .Pipeline.Elements.OfType<ParallelElement>().Single();

        Assert.Equal(3, parallel.Steps[0].Span!.StartLine);
        Assert.Equal(4, parallel.Steps[1].Span!.StartLine);
    }
}
