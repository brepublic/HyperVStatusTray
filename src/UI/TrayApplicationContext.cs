using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using HyperVStatusTray.Protocol;
using HyperVStatusTray.Services;
using HyperVStatusTray.State;

namespace HyperVStatusTray.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ConfigureVmsScriptName = "configure-vms.ps1";

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
    private ToolStripMenuItem? _languageMenuItem;
    private bool _suppressStartupToggle;
    private Icon? _currentIcon;
    private string? _lastTrayIndicatorSignature;
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
            Text = T(TextId.TrayTitle)
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
        Logger.Info(T(TextId.ExitLog));
        base.ExitThreadCore();
    }

    private static List<VmStateTracker> CreateTrackers(AppConfig config) =>
        config.VirtualMachines.Select(vm => new VmStateTracker(config, vm)).ToList();

    private AppLanguage Language => _config.Language;

    private string T(TextId id) => AppText.Get(Language, id);

    private string F(TextId id, params object?[] args) => AppText.Format(Language, id, args);

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

            Logger.Error(T(TextId.PollUnhandledLog), ex);
            ApplyBrokerFailure(F(TextId.BrokerConnectionFailure, ex.Message));
            if (!ShouldShowPollFailureNotification(ex))
            {
                return;
            }

            try
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    T(TextId.TrayTitle),
                    F(TextId.StatusRefreshFailed, ex.Message),
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
            ToolStripMenuItem status = new(T(TextId.WaitingStatus)) { Enabled = false };
            ToolStripMenuItem startupPolicy = new(T(TextId.CurrentStartupPolicyUnknown)) { Enabled = false };
            ToolStripMenuItem configureStartupPolicy = new(T(TextId.ConfigureStartupPolicy), null, async (_, _) => await ConfigureVmStartupPolicyAsync(capturedIndex));
            ToolStripMenuItem start = new(T(TextId.ActionStart), null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.Start));
            ToolStripMenuItem connect = new(T(TextId.ActionConnect), null, (_, _) => ConnectToVm(capturedIndex));
            ToolStripMenuItem shutdown = new(T(TextId.ActionShutdown), null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.ShutDownGuest));
            ToolStripMenuItem turnOff = new(T(TextId.ActionTurnOff), null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.TurnOff));
            ToolStripMenuItem restart = new(T(TextId.ActionRestart), null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.RestartGuest));
            ToolStripMenuItem reset = new(T(TextId.ActionReset), null, async (_, _) => await ExecuteActionAsync(capturedIndex, VmAction.Reset));
            ToolStripMenuItem clearFault = new(T(TextId.ClearFault), null, (_, _) =>
            {
                _trackers[capturedIndex].ClearFault();
                UpdateVisuals();
                _ = PollAsync();
            });

            root.DropDownItems.Add(status);
            root.DropDownItems.Add(startupPolicy);
            root.DropDownItems.Add(configureStartupPolicy);
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

            _vmMenus.Add(new VmMenuBinding(root, status, startupPolicy, configureStartupPolicy, start, connect, shutdown, turnOff, restart, reset, clearFault));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T(TextId.ShowDetails), null, (_, _) => ShowDetails());
        menu.Items.Add(T(TextId.RefreshNow), null, async (_, _) => await PollAsync());
        menu.Items.Add(T(TextId.OpenHyperVManager), null, (_, _) => StartShell("virtmgmt.msc"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T(TextId.EditConfigAsAdmin), null, (_, _) => StartShellElevated("notepad.exe", QuoteArgument(ConfigService.ConfigPath)));
        menu.Items.Add(T(TextId.ReconfigureVmMonitoring), null, async (_, _) => await ReconfigureMonitoredVmsAsync());
        menu.Items.Add(T(TextId.ReloadConfig), null, async (_, _) => await ReloadConfigurationAsync());
        menu.Items.Add(T(TextId.OpenConfigDirectory), null, (_, _) => StartShell("explorer.exe", ConfigService.DataDirectory));
        menu.Items.Add(T(TextId.OpenBrokerLogDirectory), null, (_, _) => StartShell("explorer.exe", AppPaths.MachineDataDirectory));
        _languageMenuItem = CreateLanguageMenuItem();
        menu.Items.Add(_languageMenuItem);

        _startupMenuItem = new ToolStripMenuItem(T(TextId.StartWithWindows))
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        _startupMenuItem.CheckedChanged += StartupMenuItemCheckedChanged;
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T(TextId.Exit), null, (_, _) => ExitThread());

        menu.Opening += (_, _) => UpdateMenuState();
        _contextMenu = menu;
        _notifyIcon.ContextMenuStrip = menu;
    }

    private ToolStripMenuItem CreateLanguageMenuItem()
    {
        ToolStripMenuItem languageMenu = new(T(TextId.LanguageMenu));
        foreach (AppLanguage language in AppText.SupportedLanguages)
        {
            AppLanguage capturedLanguage = language;
            ToolStripMenuItem item = new(
                AppText.LanguageName(language),
                null,
                async (_, _) => await SetLanguageAsync(capturedLanguage))
            {
                Checked = language == Language,
                Tag = language
            };
            languageMenu.DropDownItems.Add(item);
        }

        return languageMenu;
    }

    private async Task SetLanguageAsync(AppLanguage language)
    {
        language = AppText.Normalize(language);
        if (language == Language)
        {
            return;
        }

        try
        {
            BrokerSnapshot snapshot = await _brokerClient.SetLanguageAsync(language, _cancellation.Token);
            ApplyBrokerSnapshot(snapshot);
            _notifyIcon.ShowBalloonTip(2500, T(TextId.TrayTitle), T(TextId.LanguageUpdated), ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Normal during application shutdown.
        }
        catch (Exception ex)
        {
            ShowError(T(TextId.LanguageUpdateFailed), ex);
        }
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

            ShowError(T(TextId.StartupSettingChangeFailed), ex);
        }
    }

    private async Task ReloadConfigurationAsync()
    {
        await _pollGate.WaitAsync();
        try
        {
            BrokerSnapshot snapshot = await _brokerClient.ReloadConfigAsync(_cancellation.Token);
            ApplyBrokerSnapshot(snapshot);
            Logger.Info(T(TextId.ConfigReloadedLog));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                F(TextId.ConfigReloadFailedMessage, ex.Message),
                T(TextId.ConfigReloadFailedTitle),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _pollGate.Release();
        }

        await PollAsync();
    }

    private async Task ReconfigureMonitoredVmsAsync()
    {
        string? scriptPath = FindConfigureVmsScriptPath();
        if (scriptPath is null)
        {
            ShowError(
                T(TextId.CannotReconfigureVmMonitoring),
                new FileNotFoundException(
                    F(TextId.ConfigureScriptMissing, ConfigureVmsScriptName)));
            return;
        }

        try
        {
            using Process? process = StartShellElevated(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArgument(scriptPath)} -ConfigPath {QuoteArgument(ConfigService.ConfigPath)}");

            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync(_cancellation.Token);

            if (process.ExitCode != 0)
            {
                MessageBox.Show(
                    F(TextId.ConfigureScriptExitMessage, ConfigureVmsScriptName, process.ExitCode),
                    T(TextId.ReconfigureVmMonitoringFailed),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            await ReloadConfigurationAsync();
            _notifyIcon.ShowBalloonTip(2500, T(TextId.TrayTitle), T(TextId.VmMonitoringConfigUpdated), ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Normal during application shutdown.
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Logger.Info(T(TextId.ReconfigureCancelledLog));
        }
        catch (Exception ex)
        {
            ShowError(T(TextId.CannotReconfigureVmMonitoring), ex);
        }
    }

    private async Task ExecuteActionAsync(int index, VmAction action)
    {
        VmStateTracker tracker = _trackers[index];
        string vmName = tracker.Config.Name;

        if (action is VmAction.TurnOff or VmAction.Reset)
        {
            DialogResult answer = MessageBox.Show(
                action == VmAction.TurnOff
                    ? F(TextId.ConfirmTurnOff, vmName)
                    : F(TextId.ConfirmReset, vmName),
                T(TextId.ConfirmDestructiveTitle),
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

                Logger.Info($"{vmName}: {T(TextId.OperationAccepted)} ({action})");
                ApplyBrokerSnapshot(snapshot);
                _notifyIcon.ShowBalloonTip(2500, tracker.Config.Label, T(TextId.OperationAccepted), ToolTipIcon.Info);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Error(F(TextId.OperationFailedTitle, $"{vmName}: {action}"), ex);
                if (action is VmAction.Start or VmAction.RestartGuest or VmAction.Reset)
                {
                    string summary = action == VmAction.Start ? T(TextId.StartFailed) : T(TextId.RestartFailed);
                    tracker.MarkOperationFailure(summary, ex.Message);
                    UpdateVisuals();
                }

                ShowError(F(TextId.OperationFailedTitle, tracker.Config.Label), ex);
            }
        }
        finally
        {
            _pollGate.Release();
        }

        await PollAsync();
    }

    private async Task ConfigureVmStartupPolicyAsync(int index)
    {
        VmStateTracker tracker = _trackers[index];
        VmObservation? observation = tracker.Current.Observation;
        using VmStartupPolicyDialog dialog = new(
            Language,
            tracker.Config.Label,
            observation?.StartupPolicy ?? VmStartupPolicy.Unknown,
            observation?.AutomaticStartDelaySeconds);

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        await _pollGate.WaitAsync();
        try
        {
            BrokerSnapshot snapshot = await _brokerClient.SetVmStartupPolicyAsync(
                index,
                dialog.SelectedPolicy,
                dialog.AutomaticStartDelaySeconds,
                _cancellation.Token);

            if (_cancellation.IsCancellationRequested)
            {
                return;
            }

            ApplyBrokerSnapshot(snapshot);
            _notifyIcon.ShowBalloonTip(2500, tracker.Config.Label, T(TextId.StartupPolicyUpdated), ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Normal during application shutdown.
        }
        catch (Exception ex)
        {
            Logger.Error(F(TextId.StartupPolicyUpdateFailedTitle, tracker.Config.Name), ex);
            ShowError(F(TextId.StartupPolicyUpdateFailedTitle, tracker.Config.Label), ex);
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
            ShowError(T(TextId.CannotOpenVmConnect), ex);
        }
    }

    private void UpdateVisuals()
    {
        IndicatorState[] indicators = _trackers.Count == 0
            ? [IndicatorState.Unknown]
            : _trackers.Select(tracker => tracker.Current.Indicator).ToArray();
        string indicatorSignature = string.Join(",", indicators);
        if (_currentIcon is null ||
            !string.Equals(_lastTrayIndicatorSignature, indicatorSignature, StringComparison.Ordinal))
        {
            Icon newIcon = IconFactory.CreateTrayIcon(indicators);
            Icon? oldIcon = _currentIcon;
            _currentIcon = newIcon;
            _notifyIcon.Icon = newIcon;
            _lastTrayIndicatorSignature = indicatorSignature;
            oldIcon?.Dispose();
        }

        string tooltip = _trackers.Count == 0
            ? T(TextId.TrayTitle)
            : string.Join(
                "\n",
                _trackers.Select(tracker => $"{tracker.Config.Label}: {tracker.Current.FormatIndicatorText(Language)}"));
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
        int vmCount = snapshot.Config.VirtualMachines.Count;
        if (vmCount is < 1 or > 2 || snapshot.Observations.Length != vmCount)
        {
            throw new InvalidDataException(T(TextId.BrokerSnapshotVmCountInvalid));
        }

        string signature = CreateConfigSignature(snapshot.Config);
        if (!string.Equals(_configSignature, signature, StringComparison.Ordinal))
        {
            _config = snapshot.Config;
            _brokerClient.Language = _config.Language;
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
            binding.Status.Text = $"{snapshot.FormatIndicatorText(Language)} - {snapshot.Summary}";

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

            binding.StartupPolicy.Text = F(TextId.CurrentStartupPolicyFormat, FormatStartupPolicy(observation));
            binding.ConfigureStartupPolicy.Enabled = exists;
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

        if (_languageMenuItem is not null)
        {
            foreach (ToolStripItem item in _languageMenuItem.DropDownItems)
            {
                if (item is ToolStripMenuItem languageItem && languageItem.Tag is AppLanguage language)
                {
                    languageItem.Checked = language == Language;
                }
            }
        }
    }

    private string FormatStartupPolicy(VmObservation? observation)
    {
        if (observation is null || !observation.Exists)
        {
            return T(TextId.StartupPolicyUnknown);
        }

        string policyText = observation.StartupPolicy switch
        {
            VmStartupPolicy.Disabled => T(TextId.StartupPolicyDisabled),
            VmStartupPolicy.StartIfRunning => T(TextId.StartupPolicyStartIfRunning),
            VmStartupPolicy.AlwaysStart => T(TextId.StartupPolicyAlwaysStart),
            _ => T(TextId.StartupPolicyUnknown)
        };

        if (observation.StartupPolicy is VmStartupPolicy.StartIfRunning or VmStartupPolicy.AlwaysStart &&
            observation.AutomaticStartDelaySeconds is not null)
        {
            return F(TextId.StartupPolicyWithDelay, policyText, observation.AutomaticStartDelaySeconds.Value);
        }

        return policyText;
    }

    private void ShowDetails()
    {
        string text = string.Join(
            "\n\n",
            _trackers.Select(tracker =>
                $"{tracker.Config.Label}\n" +
                $"{T(TextId.DetailsStatusLabel)}: {tracker.Current.FormatIndicatorText(Language)} - {tracker.Current.Summary}\n" +
                $"{tracker.Current.Detail}\n" +
                $"{T(TextId.DetailsUpdatedAtLabel)}: {tracker.Current.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));

        MessageBox.Show(
            text,
            T(TextId.DetailsTitle),
            MessageBoxButtons.OK,
            _trackers.Any(tracker => tracker.Current.Indicator == IndicatorState.Fault)
                ? MessageBoxIcon.Warning
                : MessageBoxIcon.Information);
    }

    private void StartShell(string fileName, string? arguments = null)
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
            ShowError(F(TextId.CannotOpenFile, fileName), ex);
        }
    }

    private Process? StartShellElevated(string fileName, string? arguments = null)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Logger.Info(F(TextId.AdminOpenCancelled, fileName));
            return null;
        }
        catch (Exception ex)
        {
            ShowError(F(TextId.CannotOpenAsAdmin, fileName), ex);
            return null;
        }
    }

    private static string? FindConfigureVmsScriptPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ConfigureVmsScriptName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        string installedCandidate = Path.Combine(AppPaths.InstallDirectory, ConfigureVmsScriptName);
        return File.Exists(installedCandidate) ? installedCandidate : null;
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string CreateConfigSignature(AppConfig config)
    {
        return string.Join(
            "|",
            config.PollIntervalSeconds,
            config.Language,
            config.StartupTimeoutSeconds,
            config.SignalLossGraceSeconds,
            config.MonitorFailureThreshold,
            string.Join(
                ";",
                config.VirtualMachines.Select(vm =>
                    string.Join(",", vm.Name, vm.Label, vm.UseHeartbeat, vm.PingAddress, vm.PingTimeoutMilliseconds))));
    }

    private void ShowError(string title, Exception exception)
    {
        MessageBox.Show(
            F(TextId.ErrorDetailsWritten, exception.Message, Logger.LogPath),
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
        ToolStripMenuItem StartupPolicy,
        ToolStripMenuItem ConfigureStartupPolicy,
        ToolStripMenuItem Start,
        ToolStripMenuItem Connect,
        ToolStripMenuItem Shutdown,
        ToolStripMenuItem TurnOff,
        ToolStripMenuItem Restart,
        ToolStripMenuItem Reset,
        ToolStripMenuItem ClearFault);

}
