using System.Text.Json.Serialization;

namespace ForgeMission.Cli.Contracts;

// --- Wire types (mirrors ForgeMission.Api/Messages.cs — a client, not a shared-lib reference,
// since ForgeAPI is a non-AOT server project the AOT `forge` CLI must not depend on) -------------

internal sealed class ExecuteMissionRequest
{
    public int Version { get; set; }
    public string ClientToken { get; set; } = "";
    public string Mission { get; set; } = "";
    public string? MissionVersion { get; set; }
    public string Input { get; set; } = "";
    public Dictionary<string, string>? Inputs { get; set; }
    public List<string>? InputArtifactIds { get; set; }
    public bool Stream { get; set; }
}

internal sealed class ExecuteMissionResponseDto
{
    public string RunId { get; set; } = "";
    public string Mission { get; set; } = "";
    public string MissionVersion { get; set; } = "";
    public string Answer { get; set; } = "";
    public bool Verified { get; set; }
    public List<MissionSourceDto>? Sources { get; set; }
    public List<MissionArtifactDto>? Artifacts { get; set; }
    public ResponseStatusDto? ResponseStatus { get; set; }
}

internal sealed class MissionSourceDto
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string Provider { get; set; } = "";
}

internal sealed class ResponseStatusDto
{
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

internal sealed class UploadArtifactResponseDto
{
    public MissionArtifactDto? Artifact { get; set; }
    public ResponseStatusDto? ResponseStatus { get; set; }
}

internal sealed class GetArtifactRequest
{
    public int Version { get; set; }
    public string ArtifactId { get; set; } = "";
}

internal sealed class MissionArtifactDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Role { get; set; } = "";
}

// M10: streaming form. Type is progress | heartbeat | result | error.
internal sealed class MissionRunEventDto
{
    public string Type { get; set; } = "";
    public string RunId { get; set; } = "";
    public MissionProgressDto? Progress { get; set; }
    public ExecuteMissionResponseDto? Result { get; set; }
}

internal sealed class MissionProgressDto
{
    public string ExpertName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Detail { get; set; }
    public int? ResultCount { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ExecuteMissionRequest))]
[JsonSerializable(typeof(ExecuteMissionResponseDto))]
[JsonSerializable(typeof(MissionRunEventDto))]
[JsonSerializable(typeof(UploadArtifactResponseDto))]
[JsonSerializable(typeof(GetArtifactRequest))]
internal partial class ForgeExecJsonContext : JsonSerializerContext { }
