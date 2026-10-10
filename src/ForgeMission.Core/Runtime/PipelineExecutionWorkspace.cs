using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ForgeMission.Core.Runtime;

/// <summary>Runtime-only segment storage. The caller owns verification and registry lifetime.</summary>
public sealed record PipelineExecutionWorkspace
{
    public PipelineExecutionWorkspace(string RootDirectory, IReadOnlyDictionary<string, string> ArtifactPaths)
    {
        if (!Path.IsPathFullyQualified(RootDirectory))
            throw new ArgumentException("Execution workspace root must be absolute.", nameof(RootDirectory));
        this.RootDirectory = Path.GetFullPath(RootDirectory);
        this.ArtifactPaths = ArtifactPaths ?? throw new ArgumentNullException(nameof(ArtifactPaths));
    }

    public string RootDirectory { get; }
    public IReadOnlyDictionary<string, string> ArtifactPaths { get; }

    /// <summary>Calculates the shared allocation path without creating or reading a directory.</summary>
    public string GetStepOutputDirectory(string stepKey, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stepKey))).ToLowerInvariant();
        return Path.Combine(RootDirectory, "outputs", hash, attempt.ToString(CultureInfo.InvariantCulture));
    }
}
