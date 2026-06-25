using System.Text.Json;
using HyperVStatusTray.Protocol;

namespace HyperVStatusTray.Services;

public static class ConfigService
{
    public static string DataDirectory => AppPaths.MachineDataDirectory;

    public static string ConfigPath => AppPaths.ConfigPath;

    public static AppConfig LoadOrCreate(out string? warning)
    {
        Directory.CreateDirectory(DataDirectory);
        warning = null;

        if (!File.Exists(ConfigPath))
        {
            throw new InvalidDataException(AppText.Format(AppText.DefaultLanguage, TextId.ConfigMissing, ConfigPath));
        }

        try
        {
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), BrokerProtocol.JsonOptions);
            if (config is null)
            {
                throw new InvalidDataException(AppText.Get(AppText.DefaultLanguage, TextId.ConfigEmpty));
            }

            config.Validate();
            return config;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            string backupPath = Path.Combine(
                DataDirectory,
                $"config.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            try
            {
                File.Copy(ConfigPath, backupPath, overwrite: false);
            }
            catch
            {
                backupPath = AppText.Get(AppText.DefaultLanguage, TextId.ConfigBackupFailed);
            }

            throw new InvalidDataException(
                AppText.Format(AppText.DefaultLanguage, TextId.ConfigInvalid, ex.Message, backupPath),
                ex);
        }
    }

    public static bool TryReload(out AppConfig? config, out string? error)
    {
        config = null;
        error = null;

        try
        {
            AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), BrokerProtocol.JsonOptions);
            if (loaded is null)
            {
                throw new InvalidDataException(AppText.Get(AppText.DefaultLanguage, TextId.ConfigEmpty));
            }

            loaded.Validate();
            config = loaded;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static BrokerSecurityOptions LoadSecurityOptions()
    {
        BrokerSecurityOptions? options = JsonSerializer.Deserialize<BrokerSecurityOptions>(
            File.ReadAllText(AppPaths.BrokerSecurityPath),
            BrokerProtocol.JsonOptions);
        if (options is null)
        {
            throw new InvalidDataException(AppText.Get(AppText.DefaultLanguage, TextId.BrokerSecurityEmpty));
        }

        options.Validate();
        return options;
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(DataDirectory);
        string temporaryPath = ConfigPath + ".tmp";
        JsonSerializerOptions options = new(BrokerProtocol.JsonOptions)
        {
            WriteIndented = true
        };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, options));
        File.Move(temporaryPath, ConfigPath, overwrite: true);
    }
}
