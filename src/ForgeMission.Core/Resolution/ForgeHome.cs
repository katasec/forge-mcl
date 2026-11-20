namespace ForgeMission.Core.Resolution;

/// <summary>The user's Forge home directory, <c>~/.forge</c>, and the files Forge keeps there.</summary>
public static class ForgeHome
{
    // Computed on each read, so a changed HOME is honoured.
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge");

    public static string CredentialsPath => Path.Combine(Root, "credentials.json");

    public static string ConfigPath => Path.Combine(Root, "config.json");
}
