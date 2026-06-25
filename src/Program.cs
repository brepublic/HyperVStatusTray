using HyperVStatusTray.Services;
using HyperVStatusTray.UI;

namespace HyperVStatusTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        AppLanguage language = GetConfiguredLanguage();

        using Mutex mutex = new(initiallyOwned: true, name: @"Local\HyperVStatusTray.SingleInstance", createdNew: out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                AppText.Get(language, TextId.ProgramAlreadyRunning),
                AppText.Get(language, TextId.TrayTitle),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Logger.Initialize(AppPaths.UserDataDirectory);
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            Logger.Error(AppText.Get(language, TextId.ProgramInitFailedLog), ex);
            MessageBox.Show(
                AppText.Format(language, TextId.ProgramInitFailedMessage, ex.Message, Logger.LogPath),
                AppText.Get(language, TextId.TrayTitle),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static AppLanguage GetConfiguredLanguage()
    {
        return ConfigService.TryReload(out AppConfig? config, out _) && config is not null
            ? config.Language
            : AppText.DefaultLanguage;
    }
}
