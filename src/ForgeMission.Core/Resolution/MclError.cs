namespace ForgeMission.Core.Resolution;

public enum MclErrorCode
{
    SourceNotFound         = 5,
}

public class MclException(MclErrorCode code, string message, string? detail = null)
    : Exception($"MCL{(int)code:D3} {message}{(detail is null ? "" : $"\n\n{detail}")}")
{
    public MclErrorCode Code    { get; } = code;
    public string?      Detail  { get; } = detail;
}
