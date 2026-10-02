using System.Globalization;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars; // EncodedTokenAttributes: public, decodes a token's metadata
using TextMateSharp.Registry;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui.Graphics;

// forge chat TUI (Phase 56 G12, Task 5b): syntax colours for a reply's code block. The only file that
// references TextMateSharp. A fence's language is tokenized with VS Code's Light+ or Dark+ grammar
// theme; each token keeps only its foreground, on the code block's own fill. A token in the theme's
// default foreground, and any language the bundled grammars do not know, stays in CodeBlockText.
// The registry and each grammar load once, on first use.
internal sealed class CodeColours(bool light, Style text)
{
    // TextMate colour ids: 0 is "none", 1 the theme's default foreground (vscode-textmate's colour map).
    private const int DefaultForegroundId = 1;

    private readonly Lazy<(RegistryOptions Options, Registry Registry)> _registry = new(() => Load(light));
    private readonly Dictionary<string, IGrammar?> _grammars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Style> _styles = [];

    /// <summary>The coloured runs for <paramref name="code"/>, covering every character; null when
    /// the language is missing or unknown (the caller draws it plain).</summary>
    public StyledRun[]? Runs(string? language, string code)
    {
        if (string.IsNullOrWhiteSpace(language) || Grammar(language.Trim()) is not { } grammar) return null;
        var runs = new List<StyledRun>();
        IStateStack? state = null;
        var start = 0;
        foreach (var line in code.Split('\n'))
        {
            if (start > 0) runs.Add(new StyledRun(start - 1, 1, text)); // the '\n' before this line
            var result = grammar.TokenizeLine2(line, state, TimeSpan.MaxValue);
            AddLine(runs, start, line.Length, result.Tokens);
            state = result.RuleStack;
            start += line.Length + 1;
        }
        return [.. runs];
    }

    private IGrammar? Grammar(string language)
    {
        if (_grammars.TryGetValue(language, out var cached)) return cached;
        var (options, registry) = _registry.Value;
        var scope = options.GetScopeByLanguageId(language.ToLowerInvariant()) ?? options.GetScopeByExtension("." + language);
        var grammar = scope is null ? null : registry.LoadGrammar(scope);
        _grammars[language] = grammar;
        return grammar;
    }

    // Tokens come in pairs: start index, metadata.
    private void AddLine(List<StyledRun> runs, int lineStart, int length, int[] tokens)
    {
        for (var i = 0; i < tokens.Length; i += 2)
        {
            var from = tokens[i];
            var to = i + 2 < tokens.Length ? tokens[i + 2] : length;
            if (to > from) runs.Add(new StyledRun(lineStart + from, Math.Min(to, length) - from, StyleOf(tokens[i + 1])));
        }
    }

    private Style StyleOf(int metadata)
    {
        var id = EncodedTokenAttributes.GetForeground(metadata);
        if (id <= DefaultForegroundId) return text;
        if (_styles.TryGetValue(id, out var style)) return style;
        style = Parse(_registry.Value.Registry.GetTheme().GetColor(id)) is { } colour ? text.WithForeground(colour) : text;
        _styles[id] = style;
        return style;
    }

    /// <summary>A theme colour, "#rrggbb" or "#rrggbbaa" (alpha ignored).</summary>
    private static Color? Parse(string? hex)
    {
        if (hex is not { Length: >= 7 } || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        return Color.Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static (RegistryOptions, Registry) Load(bool light)
    {
        var options = new RegistryOptions(light ? ThemeName.LightPlus : ThemeName.DarkPlus);
        return (options, new Registry(options));
    }
}
