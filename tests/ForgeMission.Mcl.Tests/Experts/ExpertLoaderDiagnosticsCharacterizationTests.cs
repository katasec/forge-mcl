using ForgeMission.Core.Experts;
using ForgeMission.Parser;

namespace ForgeMission.Tests.Experts;

// Pins the exact text, order and position of ExpertLoader diagnostics.
public class ExpertLoaderDiagnosticsCharacterizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ExpertLoaderDiagnosticsCharacterizationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ParseContent_reports_missing_fields_then_onnx_fields_in_order()
    {
        var errors = ParseErrors("dir/X/expert.md", "---\nkind: onnx\n---\nbody");

        Assert.Equal(
        [
            ("Missing required frontmatter field 'name' in expert.md", 1, 0, 3),
            ("Missing required frontmatter field 'input' in expert.md", 1, 0, 3),
            ("Missing required frontmatter field 'output' in expert.md", 1, 0, 3),
            ("Expert 'expert' has kind:onnx but is missing required field 'model'.", 2, 0, 4),
            ("Expert 'expert' has kind:onnx but is missing required field 'inputs'.", 2, 0, 4),
            ("Expert 'expert' has kind:onnx but is missing required field 'outputKey'.", 2, 0, 4),
            ("Expert 'expert' has kind:onnx but is missing required field 'threshold'.", 2, 0, 4),
        ], errors);
    }

    [Fact]
    public void ParseContent_rejects_wrong_case_kind_and_lists_allowed_kinds()
    {
        var errors = ParseErrors("dir/Runner/expert.md", "---\nname: Runner\ninput: a\noutput: b\nkind: EXEC\n---\n");

        Assert.Equal(
            [("Expert 'Runner' has unknown kind 'EXEC'. Allowed kinds: llm, rule, json_extract, exec, onnx, http, search.", 5, 0, 4)],
            errors);
    }

    [Fact]
    public void ParseContent_rejects_unknown_kind()
    {
        var errors = ParseErrors("dir/Runner/expert.md", "---\nname: Runner\ninput: a\noutput: b\nkind: shell\n---\n");

        Assert.Equal(
            [("Expert 'Runner' has unknown kind 'shell'. Allowed kinds: llm, rule, json_extract, exec, onnx, http, search.", 5, 0, 4)],
            errors);
    }

    [Fact]
    public void ParseContent_without_kind_loads_as_llm()
    {
        var expert = ExpertLoader.ParseContent("dir/Writer/expert.md", "---\nname: Writer\ninput: a\noutput: b\n---\nWrite.");

        Assert.Equal(ExpertKinds.Llm, expert.Kind);
    }

    [Fact]
    public void ParseContent_reports_http_and_rule_fields()
    {
        Assert.Equal(
            [("Expert 'H' has kind:http but is missing required field 'endpoint'.", 5, 0, 4)],
            ParseErrors("H.md", "---\nname: H\ninput: a\noutput: b\nkind: http\n---\n"));
        Assert.Equal(
            [("Expert 'R' has kind:rule but is missing required field 'check'.", 5, 0, 4)],
            ParseErrors("R.md", "---\nname: R\ninput: a\noutput: b\nkind: rule\n---\n"));
    }

    [Theory]
    // Unclosed '[' followed by another key: the YAML parser has no message of its own here.
    [InlineData("---\nname: X\ninput: a\noutput: b\ninputs: [a, b\nrole: r\n---\n",
        "syntax error", 5, 9)]
    // Unclosed '[' where a string is expected: the parser's own complaint, at the '['.
    [InlineData("---\nname: [X\n---\n",
        "While parsing a flow sequence, did not find expected ',' or ']'.", 2, 6)]
    // Well-formed YAML with a mapping or a list where a string is expected.
    [InlineData("---\nname: X\ninput:\n  nested: 1\noutput: b\n---\n",
        "value has the wrong type for its field", 4, 2)]
    [InlineData("---\nname: [X]\n---\n",
        "value has the wrong type for its field", 2, 6)]
    public void ParseContent_reports_malformed_yaml_in_plain_words_at_its_file_position(
        string markdown, string reason, int line, int column)
    {
        var error = Assert.Throws<ExpertLoadException>(() => ExpertLoader.ParseContent("dir/Writer.md", markdown));

        Assert.Equal(($"Invalid YAML frontmatter in expert 'Writer': {reason}", "dir/Writer.md", line, column),
            (error.Message, error.FilePath, error.Line, error.Column));
    }

    [Fact]
    public void Validate_reports_MCL011_and_MCL012_in_key_order_at_key_positions()
    {
        WriteExpert("StepA", "---\nname: StepA\ninput: x\noutput: y\noutputKeys:\n  score: float\n---\nA.");
        WriteExpert("StepB", "---\nname: StepB\ninput: x\noutput: y\ninputKeys:\n  missing: string\n  score: string\n---\nB.");

        var errors = ValidateErrors("mission M = { StepA -> StepB }");

        Assert.Equal(
        [
            ("MCL011: expert 'StepB' in mission 'M' declares inputKey 'missing: string' but no upstream expert declares this outputKey.", 6, 2, 9),
            ("MCL012: expert 'StepB' in mission 'M' expects 'score: string' but upstream declares 'score: float'.", 7, 2, 7),
        ], errors);
    }

    [Fact]
    public void Validate_finds_key_after_blank_crlf_line_in_on_disk_frontmatter()
    {
        WriteExpert("StepB", "---\r\nname: StepB\r\ninput: x\r\noutput: y\r\ninputKeys:\r\n\r\n  missing: string\r\n---\r\nB.");

        var errors = ValidateErrors("mission M = { StepB }");

        Assert.Equal(
            [("MCL011: expert 'StepB' in mission 'M' declares inputKey 'missing: string' but no upstream expert declares this outputKey.", 7, 2, 9)],
            errors);
    }

    private static List<(string, int, int, int)> ParseErrors(string sourceName, string markdown)
    {
        var aggregate = Assert.Throws<AggregateExpertLoadException>(() => ExpertLoader.ParseContent(sourceName, markdown));
        return aggregate.Errors.Select(e => (e.Message, e.Line, e.Column, e.EndColumn)).ToList();
    }

    private List<(string, int, int, int)> ValidateErrors(string mission)
    {
        var experts = new ExpertLoader(_dir).LoadAll();
        var aggregate = Assert.Throws<AggregateExpertLoadException>(() =>
            ExpertLoader.Validate(MclParser.Parse(mission), experts, contractErrorsAreFatal: true));
        return aggregate.Errors.Select(e => (e.Message, e.Line, e.Column, e.EndColumn)).ToList();
    }

    private void WriteExpert(string name, string content)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "expert.md"), content);
    }
}
