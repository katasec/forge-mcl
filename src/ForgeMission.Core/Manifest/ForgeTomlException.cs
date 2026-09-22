namespace ForgeMission.Core.Manifest;

public sealed class ForgeTomlException(string message, string filePath)
    : Exception(message)
{
    public string FilePath { get; } = filePath;
}
