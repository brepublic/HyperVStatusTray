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
            AppConfig defaults = AppConfig.CreateDefault();
            defaults.Validate();
            Save(defaults);
            return defaults;
        }

        try
        {
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), BrokerProtocol.JsonOptions);
            if (config is null)
            {
                throw new InvalidDataException("配置文件内容为空。");
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
                backupPath = "（备份失败）";
            }

            AppConfig defaults = AppConfig.CreateDefault();
            defaults.Validate();
            Save(defaults);
            warning = $"配置文件无效，已恢复默认配置。\n\n原因：{ex.Message}\n备份：{backupPath}";
            return defaults;
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
                throw new InvalidDataException("配置文件内容为空。");
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
            throw new InvalidDataException("broker-security.json 内容为空。");
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
