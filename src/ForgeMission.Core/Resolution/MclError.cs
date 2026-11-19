namespace ForgeMission.Core.Resolution;

// Every MCL diagnostic code, one meaning each. Messages build their "MCL0NN" prefix with Code().
public enum MclErrorCode
{
    SourceNotFound          = 5,
    LockFileMissing         = 6,   // warning
    MissionNotInitialised   = 7,
    ExpertNotFound          = 8,
    ExpertChanged           = 9,
    LocalExpertShadowsLock  = 10,  // warning
    InputKeyMissingUpstream = 11,  // warning from validate
    InputKeyTypeMismatch    = 12,  // warning from validate
    OciPullFailed           = 13,
}

public static class MclErrorCodeExtensions
{
    // The code as it appears in messages, e.g. "MCL008".
    public static string Code(this MclErrorCode code) => $"MCL{(int)code:D3}";
}

public class MclException(MclErrorCode code, string message, string? detail = null)
    : Exception($"{code.Code()} {message}{(detail is null ? "" : $"\n\n{detail}")}")
{
    public MclErrorCode Code    { get; } = code;
    public string?      Detail  { get; } = detail;
}
