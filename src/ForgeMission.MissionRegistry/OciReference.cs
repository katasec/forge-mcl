namespace ForgeMission.MissionRegistry;

/// <summary>A fully qualified registry/name@reference OCI locator accepted by Forge pull operations.</summary>
public sealed record OciReference(string Registry, string Name, string Reference)
{
    public const string DefaultTag = "latest";

    public bool IsDigest => Reference.StartsWith("sha256:", StringComparison.Ordinal);

    /// <summary>
    /// Resolves a bare mission name below a configured registry base. A qualified reference has a
    /// registry host (a dotted host, a host:port, or localhost) and is otherwise used as written.
    /// Names without an explicit tag or digest use the OCI default tag.
    /// </summary>
    public static OciReference Resolve(string value, string defaultBase)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(value, "a mission name or OCI reference");

        var fullReference = IsQualified(value) ? value : CombineBase(defaultBase, value);
        return Parse(fullReference);
    }

    public static OciReference Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://", StringComparison.Ordinal) || value.Any(char.IsWhiteSpace))
            throw Invalid(value, "registry/name with an optional tag or digest");

        var firstSlash = value.IndexOf('/');
        if (firstSlash < 1)
            throw Invalid(value, "registry/name with an optional tag or digest");

        var atIndex = value.LastIndexOf('@');
        var nameAndTag = atIndex < 0 ? value[(firstSlash + 1)..] : value[(firstSlash + 1)..atIndex];
        string name;
        string reference;
        if (atIndex < 0)
        {
            reference = Tag(nameAndTag, out name);
        }
        else
        {
            name = nameAndTag;
            reference = value[(atIndex + 1)..];
        }
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('/') < 1 || string.IsNullOrWhiteSpace(reference) || name.Contains('@'))
            throw Invalid(value, "registry/name with an optional tag or digest");

        return new OciReference(
            value[..firstSlash],
            name,
            reference);
    }

    private static bool IsQualified(string value)
    {
        var separator = value.IndexOf('/');
        if (separator < 1) return false;

        var host = value[..separator];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Contains('.') || host.Contains(':');
    }

    private static string CombineBase(string defaultBase, string name)
    {
        if (string.IsNullOrWhiteSpace(defaultBase) || defaultBase.Contains("://", StringComparison.Ordinal) ||
            defaultBase.EndsWith('/') || name.StartsWith('/') || name.Contains('@'))
            throw Invalid(defaultBase, "a registry/name base");
        return $"{defaultBase}/{name}";
    }

    private static string Tag(string nameAndTag, out string name)
    {
        var separator = nameAndTag.LastIndexOf(':');
        if (separator < 0)
        {
            name = nameAndTag;
            return DefaultTag;
        }

        name = nameAndTag[..separator];
        var tag = nameAndTag[(separator + 1)..];
        return string.IsNullOrWhiteSpace(tag) ? "" : tag;
    }

    private static ArgumentException Invalid(string? value, string expected) =>
        new($"Invalid OCI reference '{value}': expected {expected}.", nameof(value));
}
