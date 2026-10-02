using Markdig;
using Markdig.Syntax;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 4): forge's Markdig step routes a qualifying heading — top-level
// h1–h3 whose text is plain and in the allowed set — to a marked fenced block, which the code-block
// renderer draws as an Inter image. Everything else stays XenoAtom's text heading. While a reply
// streams, its last heading with no newline after it is pending.
public sealed class HeadingRouteTests
{
    private static readonly Type Markdown = Type("ForgeMission.Cli.Tui.ForgeMarkdown");

    [Theory]
    [InlineData("# Title\n\nText", "Heading1", "Title")]
    [InlineData("## Hello World in Pascal\n\nText", "Heading2", "Hello World in Pascal")]
    [InlineData("### Notes — “quoted” → next\n", "Heading3", "Notes — “quoted” → next")]
    [InlineData("Setext title\n===\n", "Heading1", "Setext title")]
    public void A_plain_top_level_heading_becomes_a_marked_block(string markdown, string kind, string text)
    {
        var block = Assert.IsType<FencedCodeBlock>(Parse(markdown, "Complete")[0]);

        Assert.True(ReadHeading(block.Info, out var read, out var pending));
        Assert.Equal(Kind(kind), read);
        Assert.False(pending);
        Assert.Equal(text, block.Lines.ToString());
    }

    [Theory]
    [InlineData("## Use `code` here")]
    [InlineData("## An *emphasised* word")]
    [InlineData("## A [link](https://example.com)")]
    [InlineData("## Fish &amp; chips")]
    [InlineData("## Привет мир")]
    [InlineData("#### Fourth level")]
    [InlineData("- ## In a list")]
    [InlineData("> ## In a quote")]
    public void Any_other_heading_stays_text(string markdown)
    {
        var document = Parse(markdown, "Complete");

        Assert.DoesNotContain(document.Descendants<FencedCodeBlock>(), block => ReadHeading(block.Info, out _, out _));
    }

    [Theory]
    [InlineData("forge-heading:2")]
    [InlineData("forge-heading-0123456789abcdef0123456789abcdef:2")]
    public void A_fence_a_model_writes_is_never_taken_for_a_heading(string info)
    {
        var block = Assert.IsType<FencedCodeBlock>(Parse($"```{info}\nHello\n```", "Complete")[0]);

        Assert.False(ReadHeading(block.Info, out _, out _));
    }

    [Fact]
    public void A_marker_with_a_level_other_than_1_to_3_is_not_a_heading()
    {
        var marker = ((FencedCodeBlock)Parse("## Hello", "Complete")[0]).Info!;
        var token = marker[..marker.LastIndexOf(':')];

        Assert.True(ReadHeading($"{token}:3", out var kind, out _));
        Assert.Equal(Kind("Heading3"), kind);
        Assert.False(ReadHeading($"{token}:4", out _, out _));
    }

    [Fact]
    public void While_streaming_only_a_last_heading_without_a_newline_is_pending()
    {
        var streaming = Parse("## First\n\nSome text\n\n## Hello Wor", "Streaming");

        Assert.True(ReadHeading(((FencedCodeBlock)streaming[0]).Info, out _, out var firstPending));
        Assert.True(ReadHeading(((FencedCodeBlock)streaming[^1]).Info, out _, out var lastPending));
        Assert.False(firstPending);
        Assert.True(lastPending);
    }

    [Theory]
    [InlineData(true, "## Hello Wor", "Streaming")]
    [InlineData(true, "## Hello World\n", "Complete")]
    [InlineData(false, "## Hello World", "Complete")]
    public void The_pipeline_follows_the_reply_and_its_last_newline(bool streaming, string text, string expected)
    {
        var chosen = Markdown.GetMethod("For")!.Invoke(null, [streaming, text]);

        Assert.Same(Pipeline(expected), chosen);
    }

    private static MarkdownDocument Parse(string markdown, string pipeline) => Markdig.Markdown.Parse(markdown, Pipeline(pipeline));

    private static MarkdownPipeline Pipeline(string name) => (MarkdownPipeline)Markdown.GetProperty(name)!.GetValue(null)!;

    private static bool ReadHeading(string? info, out object kind, out bool pending)
    {
        object?[] args = [info, null, null];
        var read = (bool)Markdown.GetMethod("TryReadHeading")!.Invoke(null, args)!;
        (kind, pending) = (args[1]!, (bool)args[2]!);
        return read;
    }
}
