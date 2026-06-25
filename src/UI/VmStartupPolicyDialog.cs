namespace HyperVStatusTray.UI;

internal sealed class VmStartupPolicyDialog : Form
{
    private readonly RadioButton _disabledOption = new() { Text = "不自动启动", AutoSize = true };
    private readonly RadioButton _startIfRunningOption = new() { Text = "如果之前正在运行则自动启动", AutoSize = true };
    private readonly RadioButton _alwaysStartOption = new() { Text = "始终自动启动", AutoSize = true };
    private readonly Label _delayLabel = new() { Text = "AutomaticStartDelay（秒）", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown _delayInput = new()
    {
        Minimum = 0,
        Maximum = int.MaxValue,
        Width = 120,
        Anchor = AnchorStyles.Left
    };
    private readonly Button _okButton = new() { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };

    public VmStartupPolicyDialog(string vmLabel, VmStartupPolicy currentPolicy, int? currentDelaySeconds)
    {
        Text = $"配置自动启动策略 - {vmLabel}";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        Label title = new()
        {
            Text = vmLabel,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };

        TableLayoutPanel layout = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 7,
            Dock = DockStyle.Fill
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(_disabledOption, 0, 1);
        layout.SetColumnSpan(_disabledOption, 2);
        layout.Controls.Add(_startIfRunningOption, 0, 2);
        layout.SetColumnSpan(_startIfRunningOption, 2);
        layout.Controls.Add(_alwaysStartOption, 0, 3);
        layout.SetColumnSpan(_alwaysStartOption, 2);
        layout.Controls.Add(_delayLabel, 0, 4);
        layout.Controls.Add(_delayInput, 1, 4);

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0)
        };
        Button cancelButton = new() { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_okButton);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = _okButton;
        CancelButton = cancelButton;

        _disabledOption.CheckedChanged += (_, _) => UpdateState();
        _startIfRunningOption.CheckedChanged += (_, _) => UpdateState();
        _alwaysStartOption.CheckedChanged += (_, _) => UpdateState();

        _delayInput.Value = Math.Clamp(currentDelaySeconds ?? 0, 0, int.MaxValue);
        switch (currentPolicy)
        {
            case VmStartupPolicy.Disabled:
                _disabledOption.Checked = true;
                break;
            case VmStartupPolicy.StartIfRunning:
                _startIfRunningOption.Checked = true;
                break;
            case VmStartupPolicy.AlwaysStart:
                _alwaysStartOption.Checked = true;
                break;
        }

        UpdateState();
    }

    public VmStartupPolicy SelectedPolicy
    {
        get
        {
            if (_disabledOption.Checked)
            {
                return VmStartupPolicy.Disabled;
            }

            if (_startIfRunningOption.Checked)
            {
                return VmStartupPolicy.StartIfRunning;
            }

            return _alwaysStartOption.Checked ? VmStartupPolicy.AlwaysStart : VmStartupPolicy.Unknown;
        }
    }

    public int? AutomaticStartDelaySeconds =>
        SelectedPolicy == VmStartupPolicy.Disabled || SelectedPolicy == VmStartupPolicy.Unknown
            ? null
            : (int)_delayInput.Value;

    private void UpdateState()
    {
        bool delayVisible = SelectedPolicy is VmStartupPolicy.StartIfRunning or VmStartupPolicy.AlwaysStart;
        _delayLabel.Visible = delayVisible;
        _delayInput.Visible = delayVisible;
        _okButton.Enabled = SelectedPolicy != VmStartupPolicy.Unknown;
    }
}
