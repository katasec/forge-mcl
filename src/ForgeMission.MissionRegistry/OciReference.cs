namespace ForgeMission.MissionRegistry;

/// <summary>A registry/name@reference OCI locator accepted by Forge pull operations.</summary>
public sealed record OciReference(string Registry, string Name, string Reference)
{
    public static OciReference Parse(string value)
    {
        var firstSlash = value.IndexOf('/');
        if (firstSlash < 1)
            throw new ArgumentException($"Invalid OCI reference '{value}': expected registry/name@reference", nameof(value));

        var atIndex = value.LastIndexOf('@');
        if (atIndex <= firstSlash + 1 || atIndex == value.Length - 1)
            throw new ArgumentException($"Invalid OCI reference '{value}': expected name@reference", nameof(value));

        return new OciReference(
            value[..firstSlash],
            value[(firstSlash + 1)..atIndex],
            value[(atIndex + 1)..]);
    }
}
