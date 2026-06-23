using System.Diagnostics;
using System.Drawing;
using HyperVStatusTray.Protocol;
using HyperVStatusTray.Services;
using HyperVStatusTray.State;

namespace HyperVStatusTray.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly BrokerClient _brokerClient = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private AppConfig _config;
    private List<VmStateTracker> _trackers;
    private List<VmMenuBinding> _vmMenus = [];
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _startupMenuItem;
    private bool _suppressStartupToggle;
    private Icon? _currentIcon;
    private IndicatorState? _lastFirstTrayIndicator;
    private IndicatorState? _lastSecondTrayIndicator;
    private string? _lastTooltipText;
    private string _configSignature;
    private static readonly TimeSpan PollFailureNotificationInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset? _lastPollFailureNotificationUtc;
    private string? _lastPollFailureMessage;

    public TrayApplicationContext()
    {
        AppConfig config = AppConfig.CreateDefault();
        _config = config;
        _configSignature = CreateConfigSignature(config);
        _trackers = CreateTrackers(config);

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "Hyper-V 状态指示器"
        };
        _notifyIcon.DoubleClick += (_, _) => ShowDetails();

        RebuildContextMenu();
        UpdateVisuals();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = checked(_config.PollIntervalSeconds * 1000),
            Enabled = true
        };
        _timer.Tick += async (_, _) => await PollAsync();

        _ = PollAsync();
    }

    protected override void ExitThreadCore()
    {
        _cancellation.Cancel();
        _timer.Stop();
        _timer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        DisposeMenuImages();
        _contextMenu?.Dispose();
        Logger.Info("程序退出。");
        base.ExitThreadCore();
    }

    private static List<VmStateTracker> CreateTrackers(AppConfig config) =>
        config.VirtualMachines.Select(vm => new VmStateTracker(config, vm)).ToList();

    private async Task PollAsync()
    {
        if (!await _pollGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            CancellationToken token = _cancellation.Token;
            BrokerSnapshot snapshot = await _brokerClient.GetSnapshotAsync(token);
            token.ThrowIfCancellationRequested();
            ApplyBrokerSnapshot(snapshot);
            ResetPollFailureNotification();
        }
        catch (OperationCanceledException)
        {
            // Normal during application shutdown.
        }
        catch (Exception ex)
        {
            if (_cancellation.IsCancellationRequested)
            {
                return;
            }

            Logger.Error("轮询状态时发生未处理异常。", ex);
            ApplyBrokerFailure($"无法连接或访问 HyperVStatusTrayBroker 服务：{ex.Message}");
            if (!ShouldShowPollFailureNotification(ex))
            {
                return;
            }

            try
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "Hyper-V 状态指示器",
                    $"状态刷新失败：{ex.Message}",
                    ToolTipIcon.Error);
            }
            catch (ObjectDisposedException)
            {
                // The application is already shutting down.
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private bool ShouldShowPollFailureNotification(Exception exception)
    {
        string message = exception.Message;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool messageChanged = !string.Equals(_lastPollFailureMessage, message, StringComparison.Ordinal);
        bool intervalElapsed = _lastPollFailureNotificationUtc is null ||
            now - _lastPollFailureNotificationUtc.Value >= PollFailureNotificationInterval;

        if (!messageChanged && !intervalElapsed)
        {
            return false;
        }

        _lastPollFailureMessage = message;
        _lastPollFailureNotificationUtc = now;
        return true;
    }

    private void ResetPollFailureNotification()
    {
        _lastPollFailureMessage = null;
        _lastPollFailureNotificationUtc = null;
    }

    private void RebuildContextMenu()
    {
        DisposeMenuImages();
        _contextMenu?.Dispose();
        _vmMenus = [];

        ContextMenuStrip menu = new();

        for (int index = 0; index < _trackers.Count; index++)
        {
            int capturedIndex = index;
            VmStateTracker tracker = _trackers[index];
            ToolStripMenuItem root = new(tracker.Config.Label);
            ToolStripMenuItem status = new("等待状态") { Enabled = false };
            ToolStripMenuItem start = new("启动", null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.Start));
            ToolStripMenuItem connect = new("连接控制台", null, (_, _) => ConnectToVm(capturedIndex));
            ToolStripMenuItem shutdown = new("正常关机", null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.ShutDownGuest));
            ToolStripMenuItem turnOff = new("强制关闭电源…", null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.TurnOff));
            ToolStripMenuItem restart = new("正常重启", null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.RestartGuest));
            ToolStripMenuItem reset = new("强制重置…", null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.Reset));
            ToolStripMenuItem clearFault = new("清除故障锁存", null, (_, _) =>
            {
                _trackers[capturedIndex].ClearFault();
                UpdateVisuals();
                _ = PollAsync();
            });

            root.DropDownItems.Add(status);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(start);
            root.DropDownItems.Add(connect);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(shutdown);
            root.DropDownItems.Add(restart);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(turnOff);
            root.DropDownItems.Add(reset);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(clearFault);
            menu.Items.Add(root);

            _vmMenus.Add(new VmMenuBinding(root, status, start, connect, shutdown, turnOff, restart, reset, clearFault));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("查看详细状态", null, (_, _) => ShowDetails());
        menu.Items.Add("立即刷新", null, async (_, _) => await PollAsync());
        menu.Items.Add("打开 Hyper-V 管理器", null, (_, _) => StartShell("virtmgmt.msc"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("以管理员身份编辑配置文件", null, (_, _) => StartShellElevated("notepad.exe", ConfigService.ConfigPath));
        menu.Items.Add("重新加载配置", null, async (_, _) => await ReloadConfigurationAsync());
        menu.Items.Add("打开日志目录", null, (_, _) => StartShell("explorer.exe", ConfigService.DataDirectory));

        _startupMenuItem = new ToolStripMenuItem("随 Windows 登录启动")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        _startupMenuItem.CheckedChanged += StartupMenuItemCheckedChanged;
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());

        menu.Opening += (_, _) => UpdateMenuState();
        _contextMenu = menu;
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void StartupMenuItemCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressStartupToggle || _startupMenuItem is null)
        {
            return;
        }

        bool requested = _startupMenuItem.Checked;
        try
        {
            StartupManager.SetEnabled(requested);
        }
        catch (Exception ex)
        {
            _suppressStartupToggle = true;
            try
            {
                _startupMenuItem.Checked = !requested;
            }
            finally
            {
                _suppressStartupToggle = false;
            }

            ShowError("无法修改开机启动设置", ex);
        }
    }

    private async Task ReloadConfigurationAsync()
    {
        await _pollGate.WaitAsync();
        try
        {
            BrokerSnapshot snapshot = await _brokerClient.ReloadConfigAsync(_cancellation.Token);
            ApplyBrokerSnapshot(snapshot);
            Logger.Info("配置已重新加载。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"配置文件重新加载失败，当前配置保持不变。\n\n{ex.Message}",
                "重新加载配置失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _pollGate.Release();
        }

        await PollAsync();
    }

    private async Task ExecuteActionAsync(int index, VmAction action)
    {
        VmStateTracker tracker = _trackers[index];
        string vmName = tracker.Config.Name;

        if (action is VmAction.TurnOff or VmAction.Reset)
        {
            DialogResult answer = MessageBox.Show(
                action == VmAction.TurnOff
                    ? $"确定立即切断 {vmName} 的虚拟电源吗？客户机中未保存的数据会丢失。"
                    : $"确定强制重置 {vmName} 吗？效果类似按下实体计算机的 Reset 键，未保存的数据会丢失。",
                "确认破坏性操作",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        await _pollGate.WaitAsync();
        try
        {
            if (action == VmAction.Start)
            {
                tracker.MarkStartRequested();
            }
            else if (action is VmAction.RestartGuest or VmAction.Reset)
            {
                tracker.MarkRestartRequested();
            }
            else if (action is VmAction.ShutDownGuest or VmAction.TurnOff)
            {
                tracker.MarkExpectedStop();
            }

            UpdateVisuals();

            try
            {
                BrokerSnapshot snapshot = await _brokerClient.ExecuteVmActionAsync(index, action, _cancellation.Token);

                if (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                Logger.Info($"{vmName}: 操作 {action} 已被 Hyper-V 接受。");
                ApplyBrokerSnapshot(snapshot);
                _notifyIcon.ShowBalloonTip(2500, tracker.Config.Label, "操作已被 Hyper-V 接受。", ToolTipIcon.Info);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"{vmName}: 操作 {action} 失败。", ex);
                if (action is VmAction.Start or VmAction.RestartGuest or VmAction.Reset)
                {
                    string summary = action == VmAction.Start ? "启动失败" : "重启失败";
                    tracker.MarkOperationFailure(summary, ex.Message);
                    UpdateVisuals();
                }

                ShowError($"{tracker.Config.Label} 操作失败", ex);
            }
        }
        finally
        {
            _pollGate.Release();
        }

        await PollAsync();
    }

    private void ConnectToVm(int index)
    {
        try
        {
            string vmName = _trackers[index].Config.Name;
            ProcessStartInfo startInfo = new()
            {
                FileName = "vmconnect.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("localhost");
            startInfo.ArgumentList.Add(vmName);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            ShowError("无法打开虚拟机连接", ex);
        }
    }

    private void UpdateVisuals()
    {
        if (_trackers.Count != 2)
        {
            return;
        }

        IndicatorState firstIndicator = _trackers[0].Current.Indicator;
        IndicatorState secondIndicator = _trackers[1].Current.Indicator;
        if (_currentIcon is null ||
            _lastFirstTrayIndicator != firstIndicator ||
            _lastSecondTrayIndicator != secondIndicator)
        {
            Icon newIcon = IconFactory.CreateTrayIcon(firstIndicator, secondIndicator);
            Icon? oldIcon = _currentIcon;
            _currentIcon = newIcon;
            _notifyIcon.Icon = newIcon;
            _lastFirstTrayIndicator = firstIndicator;
            _lastSecondTrayIndicator = secondIndicator;
            oldIcon?.Dispose();
        }

        string tooltip = string.Join(
            "\n",
            _trackers.Select(tracker => $"{tracker.Config.Label}: {tracker.Current.IndicatorText}"));
        string displayTooltip = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        if (!string.Equals(_lastTooltipText, displayTooltip, StringComparison.Ordinal))
        {
            _notifyIcon.Text = displayTooltip;
            _lastTooltipText = displayTooltip;
        }

        UpdateMenuState();
    }

    private void ApplyBrokerSnapshot(BrokerSnapshot snapshot)
    {
        if (snapshot.Observations.Length != 2)
        {
            throw new InvalidDataException("Broker 返回的虚拟机状态数量不正确。");
        }

        string signature = CreateConfigSignature(snapshot.Config);
        if (!string.Equals(_configSignature, signature, StringComparison.Ordinal))
        {
            _config = snapshot.Config;
            _configSignature = signature;
            _trackers = CreateTrackers(_config);
            _timer.Interval = checked(_config.PollIntervalSeconds * 1000);
            RebuildContextMenu();
        }

        for (int index = 0; index < snapshot.Observations.Length; index++)
        {
            VmStatusSnapshot before = _trackers[index].Current;
            VmStatusSnapshot after = _trackers[index].Update(snapshot.Observations[index]);

            if (before.Indicator != after.Indicator || !string.Equals(before.Summary, after.Summary, StringComparison.Ordinal))
            {
                Logger.Info($"{after.Config.Name}: {before.Indicator}/{before.Summary} -> {after.Indicator}/{after.Summary}");
            }
        }

        UpdateVisuals();
    }

    private void ApplyBrokerFailure(string message)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (VmStateTracker tracker in _trackers)
        {
            tracker.Update(new VmObservation
            {
                ObservedAtUtc = now,
                MonitoringSucceeded = false,
                Exists = false,
                MonitoringError = message
            });
        }

        UpdateVisuals();
    }

    private void UpdateMenuState()
    {
        for (int index = 0; index < _trackers.Count; index++)
        {
            VmStateTracker tracker = _trackers[index];
            VmStatusSnapshot snapshot = tracker.Current;
            VmObservation? observation = snapshot.Observation;
            VmMenuBinding binding = _vmMenus[index];

            binding.Root.Text = tracker.Config.Label;
            binding.Status.Text = $"{snapshot.IndicatorText} — {snapshot.Summary}";

            if (binding.Root.Tag is not IndicatorState menuIndicator || menuIndicator != snapshot.Indicator)
            {
                Image? previousImage = binding.Root.Image;
                binding.Root.Image = IconFactory.CreateMenuImage(snapshot.Indicator);
                binding.Root.Tag = snapshot.Indicator;
                previousImage?.Dispose();
            }

            bool exists = observation?.Exists == true;
            bool offLike = observation?.IsOffLike == true;
            bool running = observation?.IsRunning == true;
            bool powered = observation?.IsPowered == true;

            binding.Start.Enabled = exists && offLike;
            binding.Connect.Enabled = exists && powered;
            binding.Shutdown.Enabled = exists && running;
            binding.Restart.Enabled = exists && running;
            binding.TurnOff.Enabled = exists && powered;
            binding.Reset.Enabled = exists && running;
            binding.ClearFault.Enabled = snapshot.Indicator == IndicatorState.Fault;
        }

        if (_startupMenuItem is not null)
        {
            bool enabled = StartupManager.IsEnabled();
            if (_startupMenuItem.Checked != enabled)
            {
                _suppressStartupToggle = true;
                try
                {
                    _startupMenuItem.Checked = enabled;
                }
                finally
                {
                    _suppressStartupToggle = false;
                }
            }
        }
    }

    private void ShowDetails()
    {
        string text = string.Join(
            "\n\n",
            _trackers.Select(tracker =>
                $"{tracker.Config.Label}\n" +
                $"状态：{tracker.Current.IndicatorText} — {tracker.Current.Summary}\n" +
                $"{tracker.Current.Detail}\n" +
                $"更新时间：{tracker.Current.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));

        MessageBox.Show(
            text,
            "Hyper-V 虚拟机状态",
            MessageBoxButtons.OK,
            _trackers.Any(tracker => tracker.Current.Indicator == IndicatorState.Fault)
                ? MessageBoxIcon.Warning
                : MessageBoxIcon.Information);
    }

    private static void StartShell(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments is null ? string.Empty : $"\"{arguments}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError($"无法打开 {fileName}", ex);
        }
    }

    private static void StartShellElevated(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments is null ? string.Empty : $"\"{arguments}\"",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            ShowError($"无法以管理员身份打开 {fileName}", ex);
        }
    }

    private static string CreateConfigSignature(AppConfig config)
    {
        return string.Join(
            "|",
            config.PollIntervalSeconds,
            config.StartupTimeoutSeconds,
            config.SignalLossGraceSeconds,
            config.MonitorFailureThreshold,
            string.Join(
                ";",
                config.VirtualMachines.Select(vm =>
                    string.Join(",", vm.Name, vm.Label, vm.UseHeartbeat, vm.PingAddress, vm.PingTimeoutMilliseconds))));
    }

    private static void ShowError(string title, Exception exception)
    {
        MessageBox.Show(
            $"{exception.Message}\n\n详细信息已写入：{Logger.LogPath}",
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void DisposeMenuImages()
    {
        foreach (VmMenuBinding binding in _vmMenus)
        {
            binding.Root.Image?.Dispose();
            binding.Root.Image = null;
            binding.Root.Tag = null;
        }
    }

    private sealed record VmMenuBinding(
        ToolStripMenuItem Root,
        ToolStripMenuItem Status,
        ToolStripMenuItem Start,
        ToolStripMenuItem Connect,
        ToolStripMenuItem Shutdown,
        ToolStripMenuItem TurnOff,
        ToolStripMenuItem Restart,
        ToolStripMenuItem Reset,
        ToolStripMenuItem ClearFault);

}
