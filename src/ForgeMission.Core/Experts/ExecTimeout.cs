using System.Globalization;

namespace ForgeMission.Core.Experts;

/// <summary>
/// The one parser for exec timeouts: expert frontmatter <c>timeout</c> and forge.toml
/// <c>defaultTimeout</c>. Accepts a positive whole number followed by s or m (no sign or whitespace), or blank (the 30s default).
/// Loaders reject values <see cref="TryParse"/> refuses; the runner reads only validated values.
/// </summary>
internal static class ExecTimeout
{
    internal const string Default = "30s";

    internal static TimeSpan Parse(string timeout) => TryParse(timeout, out var value)
        ? value
        : throw new InvalidOperationException($"Exec timeout '{timeout}' was not validated at load.");

    internal static bool TryParse(string timeout, out TimeSpan value)
    {
        value = TimeSpan.FromSeconds(30);
        if (string.IsNullOrWhiteSpace(timeout)) return true;
        if (!int.TryParse(timeout[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return false;

        if (timeout.EndsWith('s')) value = TimeSpan.FromSeconds(amount);
        else if (timeout.EndsWith('m')) value = TimeSpan.FromMinutes(amount);
        else return false;
        return true;
    }
}
