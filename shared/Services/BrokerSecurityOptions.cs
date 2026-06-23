namespace HyperVStatusTray.Services;

public sealed class BrokerSecurityOptions
{
    public string AllowedUserSid { get; set; } = string.Empty;

    public string AllowedClientPath { get; set; } = Path.Combine(AppPaths.InstallDirectory, "HyperVStatusTray.exe");

    public void Validate()
    {
        AllowedUserSid = AllowedUserSid.Trim();
        AllowedClientPath = AllowedClientPath.Trim();

        if (string.IsNullOrWhiteSpace(AllowedUserSid))
        {
            throw new InvalidDataException("AllowedUserSid is required.");
        }

        if (string.IsNullOrWhiteSpace(AllowedClientPath))
        {
            throw new InvalidDataException("AllowedClientPath is required.");
        }
    }
}
