using ForgeMission.Cli.Tui.Graphics;
using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (Phase 56 Task 4): the Markdig pipelines replies are parsed with. They are
// XenoAtom's own configuration plus one step: a top-level h1–h3 whose text TextArt.Allows (plain
// text only: no code, emphasis, link or entity) becomes a fenced block marked forge-heading, which
// ForgeCodeBlockRenderer draws as an Inter image (XenoAtom's Markdown package has no heading hook).
// While a reply streams, a heading that is still the last block with no newline after it may not be
// complete: the Streaming pipeline marks it pending, drawn as bold text on the image's rows. The
// marker carries a per-process token, so a fence a model writes itself is never taken for one.
internal static class ForgeMarkdown
{
    // XenoAtom.Terminal.UI.Extensions.Markdown's default pipeline configuration (MarkdownDefaults).
    private const string XenoAtomConfiguration = "common+pipetables+alerts";
    private static readonly string Marker = $"forge-heading-{Guid.NewGuid():N}";

    /// <summary>For a reply that is complete, or streaming with a newline at its end.</summary>
    public static MarkdownPipeline Complete { get; } = Build(streaming: false);

    /// <summary>For a streaming reply whose text does not end in a newline.</summary>
    public static MarkdownPipeline Streaming { get; } = Build(streaming: true);

    /// <summary>The pipeline for a reply's current text.</summary>
    public static MarkdownPipeline For(bool streaming, string text) =>
        streaming && !text.EndsWith('\n') ? Streaming : Complete;

    /// <summary>Reads a forge-heading marker: the heading's kind and whether it is pending.</summary>
    public static bool TryReadHeading(string? fenceInfo, out TextKind kind, out bool pending)
    {
        (kind, pending) = (TextKind.Heading2, false);
        if (fenceInfo is null || !fenceInfo.StartsWith(Marker + ":", StringComparison.Ordinal)) return false;
        var parts = fenceInfo[(Marker.Length + 1)..].Split(':');
        TextKind? level = parts[0] switch { "1" => TextKind.Heading1, "2" => TextKind.Heading2, "3" => TextKind.Heading3, _ => null };
        if (level is null) return false;
        (kind, pending) = (level.Value, parts.Length > 1);
        return true;
    }

    private static MarkdownPipeline Build(bool streaming)
    {
        var builder = new MarkdownPipelineBuilder().Configure(XenoAtomConfiguration);
        var fences = builder.BlockParsers.Find<FencedCodeBlockParser>()
            ?? throw new InvalidOperationException("The Markdown configuration has no fenced code block parser.");
        builder.DocumentProcessed += document => RouteHeadings(document, streaming, fences);
        return builder.Build();
    }

    /// <summary>Replaces every qualifying top-level heading with its marked fenced block.</summary>
    private static void RouteHeadings(MarkdownDocument document, bool streaming, FencedCodeBlockParser fences)
    {
        for (var i = 0; i < document.Count; i++)
        {
            if (document[i] is not HeadingBlock { Level: <= 3 } heading || PlainText(heading) is not { } text || !TextArt.Allows(text))
                continue;
            var pending = streaming && i == document.Count - 1;
            document.RemoveAt(i);
            document.Insert(i, MarkedBlock(fences, heading.Level, pending, text));
        }
    }

    /// <summary>The heading's text when it is only literal text (soft breaks become spaces); else null.</summary>
    private static string? PlainText(HeadingBlock heading)
    {
        var text = new System.Text.StringBuilder();
        for (var inline = heading.Inline?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case LineBreakInline { IsHard: false }: text.Append(' '); break;
                default: return null;
            }
        }
        return text.ToString().Trim();
    }

    private static FencedCodeBlock MarkedBlock(FencedCodeBlockParser fences, int level, bool pending, string text) => new(fences)
    {
        Info = $"{Marker}:{level}{(pending ? ":pending" : "")}",
        FencedChar = '`',
        Lines = new StringLineGroup(text),
    };
}
