using System.Text;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Cli.Commands;

/// <summary>
/// `forge run --input` values (Phase 76 decision 7): <c>name=value</c> is a text input and
/// <c>name=@path</c> is one file: a text file's contents become the input, a binary file is uploaded.
/// A folder is refused. The prompt fills the root mission's own
/// input, so that name cannot also be given here.
/// </summary>
internal static class RunInputs
{
    internal sealed record Input(string Name, string? Text, string? FilePath);

    /// <summary>Parses and checks every value before any network call; throws
    /// <see cref="ArgumentException"/> with a message naming the bad value.</summary>
    internal static IReadOnlyList<Input> Parse(IReadOnlyList<string> values, string invocationDirectory)
    {
        var inputs = new List<Input>();
        foreach (var value in values)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
                throw new ArgumentException($"--input '{value}' must be name=value or name=@file.");
            var name = value[..separator];
            var text = value[(separator + 1)..];
            if (!IsName(name))
                throw new ArgumentException($"--input name '{name}' must start with a letter and use only letters, digits and _.");
            if (inputs.Any(input => input.Name == name))
                throw new ArgumentException($"--input '{name}' is given more than once.");
            inputs.Add(text.StartsWith('@') ? new Input(name, null, FilePath(name, text[1..], invocationDirectory)) : new Input(name, text, null));
        }
        return inputs;
    }

    /// <summary>The prompt fills the root mission's own input, so <c>--input</c> cannot name it.</summary>
    internal static void RequireNotRoot(IReadOnlyList<Input> inputs, string rootInput)
    {
        if (inputs.Any(input => input.Name == rootInput))
            throw new ArgumentException($"--input '{rootInput}' is the mission's own input; it comes from the prompt.");
    }

    /// <summary>The turn's named inputs: the prompt under the root input and every text input. A text
    /// file's contents are its input's text, so <c>{{name}}</c> is the file. A binary file (not UTF-8,
    /// or containing a NUL byte) is uploaded instead, for an <c>exec</c> step to read.</summary>
    internal static async Task<MissionNamedInputs> BuildAsync(IReadOnlyList<Input> inputs, string rootInput, string prompt,
        Func<string, byte[], Task<MissionContentReference>> upload, CancellationToken ct)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal) { [rootInput] = prompt };
        var files = new Dictionary<string, MissionContentReference>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (input.Text is not null) { strings[input.Name] = input.Text; continue; }
            var bytes = await File.ReadAllBytesAsync(input.FilePath!, ct);
            if (Text(bytes) is { } text) strings[input.Name] = TextWithinLimit(input, text);
            else files[input.Name] = await upload(input.Name, bytes);
        }
        return new MissionNamedInputs(strings, files);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The file as text, or null when it is binary.</summary>
    private static string? Text(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        try { return StrictUtf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return null; }
    }

    // Text inputs travel in the turn's body, which ForgeAPI caps at 4 MiB.
    private static string TextWithinLimit(Input input, string text) =>
        Encoding.UTF8.GetByteCount(text) <= ConversationBodyLimits.MaxBodyBytes ? text
            : throw new ArgumentException($"--input {input.Name}: text file is larger than 4 MiB.");

    private static string FilePath(string name, string path, string invocationDirectory)
    {
        var full = Path.GetFullPath(path, invocationDirectory);
        if (Directory.Exists(full))
            throw new ArgumentException($"--input {name}=@{path} is a folder; pass one file.");
        if (!File.Exists(full))
            throw new ArgumentException($"--input {name}=@{path}: file not found.");
        if (new FileInfo(full).Length > MissionContentLimits.MaxContentBytes)
            throw new ArgumentException($"--input {name}=@{path} is larger than 100 MiB.");
        return full;
    }

    // The same rule ForgeAPI applies to an upload's input name.
    private static bool IsName(string name) => name.Length > 0 && char.IsAsciiLetter(name[0]) &&
        name.AsSpan(1).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_") < 0;
}
