using System.Text.RegularExpressions;

namespace ForgeMission.MissionRegistry;

/// <summary>A registry/name@reference OCI locator accepted by Forge pull operations.</summary>
public sealed record OciReference(string Registry, string Name, string Reference)
{
    internal OciReference Canonical() => Resolve($"{Registry}/{Name}{(Reference.StartsWith("sha256:", StringComparison.Ordinal) ? "@" : ":")}{Reference}", Registry);

    public static OciReference Resolve(string selector, string registryBase)
    {
        RejectSegments(selector);
        RejectSegments(registryBase);
        var slash = selector.IndexOf('/');
        var first = slash < 0 ? selector : selector[..slash];
        var explicitHost = slash > 0 && (first.Contains('.') || first.Contains(':') || first == "localhost");
        var full = explicitHost ? selector : registryBase.TrimEnd('/') + "/" + selector;
        var hostEnd = full.IndexOf('/');
        if (hostEnd < 1) throw new ArgumentException("OCI registry base must include a registry host.");
        var host = full[..hostEnd].ToLowerInvariant();
        if (host == "docker.io") host = "registry-1.docker.io";
        if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out var origin) || origin.Authority != host || origin.AbsolutePath != "/")
            throw new ArgumentException("Invalid OCI registry host.");
        var (repository, reference) = RepositoryReference(full[(hostEnd + 1)..]);
        return new(host, repository, reference);
    }

    public static OciReference Parse(string value)
    {
        var firstSlash = value.IndexOf('/');
        var atIndex = value.LastIndexOf('@');
        if (firstSlash < 1 || atIndex <= firstSlash + 1 || atIndex == value.Length - 1)
            throw new ArgumentException($"Invalid OCI reference '{value}': expected registry/name@reference", nameof(value));
        RejectSegments(value);
        return new(value[..firstSlash], value[(firstSlash + 1)..atIndex], value[(atIndex + 1)..]);
    }

    private static (string Repository, string Reference) RepositoryReference(string repository)
    {
        var at = repository.IndexOf('@');
        var reference = "latest";
        if (at >= 0)
        {
            reference = repository[(at + 1)..];
            repository = repository[..at];
            if (!Regex.IsMatch(reference, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("OCI digest must be sha256 followed by 64 lowercase hexadecimal digits.");
        }
        else if (repository.LastIndexOf(':') is var colon && colon >= 0)
        {
            reference = repository[(colon + 1)..];
            repository = repository[..colon];
            if (!Regex.IsMatch(reference, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Invalid OCI tag.");
        }
        if (repository.Split('/').Any(segment => !Regex.IsMatch(segment, "^[a-z0-9]+(?:(?:[._]|__|[-]+)[a-z0-9]+)*$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("OCI repository paths must be lowercase canonical names.");
        return (repository, reference);
    }

    private static void RejectSegments(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://", StringComparison.Ordinal) || value.IndexOfAny(['\\', '?', '#']) >= 0 ||
            value.StartsWith('@') || value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("OCI reference contains a URL, empty segment or traversal.");
    }
}
