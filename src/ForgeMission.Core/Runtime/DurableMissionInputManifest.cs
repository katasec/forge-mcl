using System.Security.Cryptography;
using System.Text;

namespace ForgeMission.Core.Runtime;

/// <summary>The content class of a named run input after the CLI has staged its bytes.</summary>
public enum DurableMissionInputKind
{
    Text,
    Artifact,
}

/// <summary>An immutable named binding to one staged input body; no client path is representable.</summary>
public sealed record DurableMissionInputBinding(
    string Name,
    DurableMissionInputKind Kind,
    int ByteCount,
    string Sha256);

/// <summary>Canonical input bindings used to compare a retried durable launch.</summary>
public sealed record DurableMissionInputManifest(
    string ManifestHash,
    IReadOnlyList<DurableMissionInputBinding> Bindings)
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "output",
        "feedback",
        "max_loops",
    };

    /// <summary>Builds the one stable, name-ordered input manifest.</summary>
    public static bool TryCreate(
        IEnumerable<DurableMissionInputBinding> bindings,
        out DurableMissionInputManifest? manifest,
        out string? reason)
    {
        var ordered = bindings.OrderBy(binding => binding.Name, StringComparer.Ordinal).ToArray();
        if (!TryValidate(ordered, out reason))
        {
            manifest = null;
            return false;
        }

        manifest = new DurableMissionInputManifest(ComputeHash(ordered), ordered);
        return true;
    }

    /// <summary>Validates a persisted manifest before using it to bind staged bodies.</summary>
    public bool IsValid(out string? reason) =>
        TryValidate(Bindings, out reason) && string.Equals(ManifestHash, ComputeHash(Bindings), StringComparison.Ordinal);

    private static bool TryValidate(IReadOnlyList<DurableMissionInputBinding> bindings, out string? reason)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (!IsInputName(binding.Name))
            {
                reason = $"Input name '{binding.Name}' must use lowercase letters, digits, and underscores.";
                return false;
            }
            if (ReservedNames.Contains(binding.Name))
            {
                reason = $"Input name '{binding.Name}' is reserved by the runtime.";
                return false;
            }
            if (!names.Add(binding.Name) || binding.ByteCount < 0 || !Enum.IsDefined(binding.Kind) || !IsSha256(binding.Sha256))
            {
                reason = "The named input manifest is invalid.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static string ComputeHash(IEnumerable<DurableMissionInputBinding> bindings)
    {
        var canonical = new StringBuilder();
        foreach (var binding in bindings.OrderBy(binding => binding.Name, StringComparer.Ordinal))
        {
            Append(canonical, binding.Name);
            Append(canonical, binding.Kind.ToString());
            Append(canonical, binding.ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, binding.Sha256);
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static bool IsInputName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] is < 'a' or > 'z') return false;
        return name.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '_');
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;

        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiDigit(character) && (character < 'a' || character > 'f'))
                return false;
        }

        return true;
    }

    private static void Append(StringBuilder target, string value) =>
        target.Append(value.Length).Append(':').Append(value);
}
