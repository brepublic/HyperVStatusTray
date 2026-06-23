using HyperVStatusTray.Services;
using HyperVStatusTray.UI;

namespace HyperVStatusTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using Mutex mutex = new(initiallyOwned: true, name: @"Local\HyperVStatusTray.SingleInstance", createdNew: out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Hyper-V 状态指示器已经在运行。请检查系统托盘的隐藏图标区域。",
                "Hyper-V 状态指示器",
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
            Logger.Error("程序初始化失败。", ex);
            MessageBox.Show(
                $"程序初始化失败：\n\n{ex.Message}\n\n日志：{Logger.LogPath}",
                "Hyper-V 状态指示器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
