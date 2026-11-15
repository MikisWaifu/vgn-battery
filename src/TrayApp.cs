using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace VgnBattery;

internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const int DefaultPollSeconds = 45;
    private static readonly int[] PollChoices = [5, 15, 45, 120];
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VgnBattery", "settings.json");
    private readonly BatteryReader reader = new();
    private readonly NotifyIcon tray;
    private readonly BatteryPopup popup;
    private readonly System.Windows.Forms.Timer timer;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem percentageItem;
    private readonly ToolStripMenuItem lowBatteryItem;
    private readonly ToolStripMenuItem exportItem;
    private readonly ToolStripMenuItem pollIntervalItem;
    private readonly LowBatteryPolicy lowBatteryPolicy = new();
    private Icon? currentIcon;
    private (int? Percent, bool Charging, bool Digits, bool Light, bool Stale, int Size)? renderedIcon;
    private BatteryView current = new(null, false, "正在查找鼠标…", DateTimeOffset.Now, "");
    private bool refreshing;
    private bool exiting;
    private bool exporting;
    private bool showPercentage = true;
    private bool lowBatteryEnabled = true;
    private int lowBatteryThreshold = 20;
    private int pollSeconds = DefaultPollSeconds;

    public TrayApp()
    {
        bool migrateSettings = false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (document.RootElement.TryGetProperty("ShowPercentage", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                showPercentage = value.GetBoolean();
            else if (document.RootElement.TryGetProperty("BatteryIcon", out var legacy) && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                showPercentage = !legacy.GetBoolean();
                migrateSettings = true;
            }
            if (document.RootElement.TryGetProperty("LowBatteryEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                lowBatteryEnabled = enabled.GetBoolean();
            if (document.RootElement.TryGetProperty("LowBatteryThreshold", out var threshold) && threshold.TryGetInt32(out int valueThreshold))
                lowBatteryThreshold = Math.Clamp(valueThreshold, 1, 99);
            if (document.RootElement.TryGetProperty("PollSeconds", out var poll) && poll.TryGetInt32(out int pollValue))
                pollSeconds = NormalizePollSeconds(pollValue);
        }
        catch { /* A missing or damaged preference file uses a battery with digits. */ }

        MigrateExistingStartupRegistration();
        popup = new BatteryPopup();
        popup.StartupRequested += (_, _) => SetStartup(popup.StartupChecked);
        popup.PercentageRequested += (_, _) => SetShowPercentage(popup.PercentageChecked);
        popup.LowBatteryOptionsRequested += (_, _) => SetLowBatteryOptions(popup.LowBatteryChecked, popup.NotificationThreshold);
        popup.TestNotificationRequested += OnTestNotificationRequested;
        popup.DevicesChanged += (_, _) => reader.InvalidateDevices();
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false, ShowCheckMargin = true, Renderer = new DarkMenuRenderer(),
            Font = new Font("Microsoft YaHei UI", 9.5F), BackColor = Color.FromArgb(27, 30, 36),
            ForeColor = Color.FromArgb(239, 243, 250), Padding = new Padding(4)
        };
        menu.Items.Add("查看电量", null, (_, _) => ShowPopup());
        refreshItem = new ToolStripMenuItem("立即刷新", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        percentageItem = new ToolStripMenuItem("显示电量数字") { CheckOnClick = true, Checked = showPercentage };
        percentageItem.CheckedChanged += (_, _) => SetShowPercentage(percentageItem.Checked);
        menu.Items.Add(percentageItem);
        lowBatteryItem = new ToolStripMenuItem($"低电通知（低于 {lowBatteryThreshold}%）") { CheckOnClick = true, Checked = lowBatteryEnabled };
        lowBatteryItem.CheckedChanged += (_, _) => SetLowBatteryOptions(lowBatteryItem.Checked, lowBatteryThreshold);
        menu.Items.Add(lowBatteryItem);
        pollIntervalItem = new ToolStripMenuItem("刷新间隔");
        foreach (int seconds in PollChoices)
        {
            var choice = new ToolStripMenuItem($"每 {seconds} 秒") { Tag = seconds };
            choice.Click += (_, _) => SetPollSeconds(seconds);
            pollIntervalItem.DropDownItems.Add(choice);
        }
        menu.Items.Add(pollIntervalItem);
        startupItem = new ToolStripMenuItem("开机自动运行") { CheckOnClick = true };
        startupItem.Click += (_, _) => SetStartup(startupItem.Checked);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        exportItem = new ToolStripMenuItem("导出设备信息…", null, async (_, _) => await ExportDeviceInformationAsync());
        menu.Items.Add(exportItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => SyncPopupSettings();

        tray = new NotifyIcon { ContextMenuStrip = menu, Text = "鼠标电量 · 正在读取", Visible = false };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        UpdateDisplay();
        tray.Visible = true;
        if (migrateSettings) SaveSettings();
        timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += async (_, _) => { timer.Interval = pollSeconds * 1000; await RefreshAsync(); };
        timer.Start();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void ShowPopup()
    {
        if (exiting) return;
        if (popup.InvokeRequired)
        {
            popup.BeginInvoke(ShowPopup);
            return;
        }
        SyncPopupSettings(forcePopup:true);
        popup.PositionNearTray();
        popup.TopMost = true;
        popup.Show();
        popup.Activate();
    }

    private void TogglePopup()
    {
        if (exiting) return;
        if (popup.Visible) { popup.Hide(); return; }
        ShowPopup();
    }

    private async Task RefreshAsync()
    {
        if (refreshing || exiting) return;
        refreshing = true;
        UpdateDisplay();
        try
        {
            var result = await Task.Run(() => reader.Read());
            if (exiting) return;
            var percent = result.Percent is >= 0 and <= 100 ? result.Percent : null;
            string model = string.IsNullOrWhiteSpace(result.Model) ? "鼠标电量" : result.Model;
            bool sameDeviceStillPresent = result.Transport is not ("未连接" or "未知")
                && string.Equals(model, current.Model, StringComparison.OrdinalIgnoreCase);
            if (percent is null && current.Percent.HasValue && sameDeviceStillPresent)
                current = current with { Stale = true, Message = result.Message };
            else
                current = new BatteryView(percent, result.Charging, result.Message, result.Timestamp, result.Transport, model);
            if (lowBatteryPolicy.ShouldNotify(result, lowBatteryEnabled, lowBatteryThreshold))
            {
                tray.ShowBalloonTip(8_000, "鼠标电量较低", $"{current.Model} 剩余 {percent}%，已低于设定的 {lowBatteryThreshold}%，可以安排充电了。", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            if (exiting) return;
            current = new BatteryView(null, false, "暂时无法读取电量：" + ex.Message, DateTimeOffset.Now, "", current.Model);
        }
        finally
        {
            refreshing = false;
            if (!exiting) UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        if (exiting) return;
        var key = (current.Percent, current.Charging, showPercentage, UsesLightTaskbar(), current.Stale,
            Math.Max(16, (int)Math.Round(16 * popup.DeviceDpi / 96d)));
        if (renderedIcon != key)
        {
            var next = TrayIconRenderer.Create(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5, iconSize:key.Item6);
            var previous = currentIcon;
            currentIcon = next;
            tray.Icon = next;
            renderedIcon = key;
            previous?.Dispose();
        }
        string state = current.Percent is int p ? $"{p}% · {(current.Stale ? "上次读数（鼠标暂未响应）" : ChargeLabel(current))}" : "未读取到电量";
        string tooltip = current.Model + " · " + state;
        if (!string.IsNullOrWhiteSpace(current.Transport)) tooltip += "\n" + current.Transport;
        if (current.Percent is null && !string.IsNullOrWhiteSpace(current.Message)) tooltip += "\n" + current.Message;
        tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (tray.Text != tooltip) tray.Text = tooltip;
        refreshItem.Enabled = !refreshing;
        refreshItem.Text = refreshing ? "正在刷新…" : "立即刷新";
        SyncPopupSettings();
    }

    private void SetShowPercentage(bool enabled)
    {
        if (showPercentage == enabled) return;
        showPercentage = enabled;
        SaveSettings();
        UpdateDisplay();
    }

    private void OnTestNotificationRequested(object? sender, EventArgs e) => tray.ShowBalloonTip(8_000, "低电提醒测试",
        $"这是一条测试通知。鼠标电量低于 {lowBatteryThreshold}% 时会提醒。", ToolTipIcon.Info);

    private async Task ExportDeviceInformationAsync()
    {
        if (exporting || exiting) return;
        exporting = true;
        exportItem.Enabled = false;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出设备接口信息",
                FileName = "Device-report.json",
                Filter = "JSON 文件 (*.json)|*.json",
                DefaultExt = "json",
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true
            };
            var choice = popup.Visible ? dialog.ShowDialog(popup) : dialog.ShowDialog();
            if (choice != DialogResult.OK) return;
            exportItem.Text = "正在导出设备信息…";
            string json = await Task.Run(() => JsonSerializer.Serialize(DeviceDiagnostics.Collect(), new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            if (exiting) return;
            await File.WriteAllTextAsync(dialog.FileName, json);
            if (!exiting)
                tray.ShowBalloonTip(6_000, "设备信息已导出", "已导出设备接口信息，未包含设备路径或序列号", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            if (!exiting) tray.ShowBalloonTip(6_000, "导出设备信息失败", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            exporting = false;
            if (!exiting)
            {
                exportItem.Enabled = true;
                exportItem.Text = "导出设备信息…";
            }
        }
    }

    private void SetLowBatteryOptions(bool enabled, int threshold)
    {
        threshold = Math.Clamp(threshold, 1, 99);
        if (lowBatteryEnabled == enabled && lowBatteryThreshold == threshold) return;
        lowBatteryEnabled = enabled;
        lowBatteryThreshold = threshold;
        lowBatteryPolicy.Reset();
        SaveSettings();
        UpdateDisplay();
    }

    private void SetPollSeconds(int seconds)
    {
        seconds = NormalizePollSeconds(seconds);
        if (pollSeconds == seconds)
        {
            SyncPopupSettings();
            return;
        }
        pollSeconds = seconds;
        SaveSettings();
        timer.Interval = pollSeconds * 1000;
        UpdateDisplay();
    }

    internal static int NormalizePollSeconds(int? seconds) => seconds is >= 5 and <= 120 ? seconds.Value : DefaultPollSeconds;

    private void SyncPopupSettings(bool forcePopup = false)
    {
        bool startup = IsStartupEnabled();
        startupItem.Checked = startup;
        if (percentageItem.Checked != showPercentage) percentageItem.Checked = showPercentage;
        if (lowBatteryItem.Checked != lowBatteryEnabled) lowBatteryItem.Checked = lowBatteryEnabled;
        lowBatteryItem.Text = $"低电通知（低于 {lowBatteryThreshold}%）";
        foreach (ToolStripMenuItem choice in pollIntervalItem.DropDownItems) choice.Checked = (int)choice.Tag! == pollSeconds;
        popup.PollSeconds = pollSeconds;
        if (forcePopup || popup.Visible)
            popup.SetState(current, refreshing, startup, showPercentage, lowBatteryEnabled, lowBatteryThreshold);
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue("VgnBattery", CurrentStartupCommand(), RegistryValueKind.String);
            }
            else key.DeleteValue("VgnBattery", false);
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(6_000, "未能更改开机启动", ex.Message, ToolTipIcon.Warning);
        }
        SyncPopupSettings();
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return StartupRegistration.MatchesCurrentCommand(key?.GetValue("VgnBattery") as string, CurrentStartupCommand());
        }
        catch { return false; }
    }

    private static string CurrentStartupCommand() => StartupRegistration.BuildCommand(Environment.ProcessPath, AppContext.BaseDirectory);

    private static void MigrateExistingStartupRegistration()
    {
        try
        {
            // Opening an existing key never opts a user into startup. Only an
            // existing nonempty application entry may follow this portable EXE.
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            string? registered = key?.GetValue("VgnBattery") as string;
            if (string.IsNullOrWhiteSpace(registered)) return;
            string command = CurrentStartupCommand();
            if (StartupRegistration.NeedsMigration(registered, command))
                key!.SetValue("VgnBattery", command, RegistryValueKind.String);
        }
        catch
        {
            // A stale registration remains unchecked, and the settings switch
            // can retry explicitly. Startup migration must not prevent launch.
        }
    }

    internal static string ChargeLabel(BatteryView view) => view.Percent is null ? "等待鼠标连接"
        : view.Charging ? "正在充电"
        : view.Message.Contains("已充满", StringComparison.Ordinal) ? "已充满"
        : "电池供电";

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AppSettings
            {
                ShowPercentage = showPercentage, LowBatteryEnabled = lowBatteryEnabled, LowBatteryThreshold = lowBatteryThreshold,
                PollSeconds = pollSeconds
            }));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception ex) { tray.ShowBalloonTip(6_000, "设置暂未保存", ex.Message, ToolTipIcon.Warning); }
    }

    private static bool UsesLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch { return false; }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!exiting && popup.IsHandleCreated && !popup.IsDisposed)
        {
            try { popup.BeginInvoke(UpdateDisplay); } catch (InvalidOperationException) { }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!exiting && popup.IsHandleCreated && !popup.IsDisposed)
        {
            try { popup.BeginInvoke(() => { if (popup.Visible) popup.PositionNearTray(); }); } catch (InvalidOperationException) { }
        }
    }

    protected override void ExitThreadCore()
    {
        if (exiting) return;
        exiting = true;
        timer.Stop();
        timer.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        tray.Visible = false;
        var menu = tray.ContextMenuStrip;
        tray.Dispose();
        menu?.Dispose();
        currentIcon?.Dispose();
        popup.Dispose();
        base.ExitThreadCore();
    }

    private sealed class AppSettings
    {
        public bool ShowPercentage { get; set; } = true;
        public bool LowBatteryEnabled { get; set; } = true;
        public int LowBatteryThreshold { get; set; } = 20;
        public int PollSeconds { get; set; } = DefaultPollSeconds;
    }
    internal sealed record BatteryView(int? Percent, bool Charging, string Message, DateTimeOffset Timestamp, string Transport, string Model = "鼠标电量", bool Stale = false);
}

internal sealed class BatteryPopup : Form
{
    private static readonly Color Background = Color.FromArgb(27, 30, 36);
    private static readonly Color Secondary = Color.FromArgb(158, 168, 183);
    private readonly Label title;
    private readonly Label connection;
    private readonly Label percentage;
    private readonly Label chargeState;
    private readonly Label message;
    private readonly Label updated;
    private readonly Label hint;
    private readonly CheckBox startup;
    private readonly CheckBox showPercentage;
    private readonly CheckBox lowBattery;
    private readonly ThresholdStepper notificationThreshold;
    private readonly BatteryCanvas battery;
    private bool settingState;
    public event EventHandler? StartupRequested;
    public event EventHandler? PercentageRequested;
    public event EventHandler? LowBatteryOptionsRequested;
    public event EventHandler? TestNotificationRequested;
    public event EventHandler? DevicesChanged;
    public bool StartupChecked => startup.Checked;
    public bool PercentageChecked => showPercentage.Checked;
    public bool LowBatteryChecked => lowBattery.Checked;
    public int NotificationThreshold => (int)notificationThreshold.Value;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int PollSeconds { get; set; } = TrayApp.DefaultPollSeconds;

    public BatteryPopup()
    {
        // AutoScaleDimensions causes an immediate layout unless suspended. Keep the
        // entire 96-DPI control tree intact until all bounds/fonts are established.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(350, 438);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Background;
        ForeColor = Color.FromArgb(239, 243, 250);
        Font = new Font("Microsoft YaHei UI", 9F);
        DoubleBuffered = true;
        Padding = new Padding(20);
        Text = "鼠标电量";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        layout.SuspendLayout();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 32, 26, 86, 36, 22, 178, 18 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        title = MakeLabel("鼠标电量", 13F, FontStyle.Bold);
        title.TextAlign = ContentAlignment.MiddleLeft;
        heading.Controls.Add(title, 0, 0);
        var close = new Button
        {
            Text = "×", Dock = DockStyle.Fill, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 15F), ForeColor = Secondary, BackColor = Background,
            Cursor = Cursors.Hand, AccessibleName = "关闭电量窗口", UseVisualStyleBackColor = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 60, 72);
        close.Click += (_, _) => Hide();
        heading.Controls.Add(close, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        connection = MakeLabel("正在查找鼠标…", 9F);
        connection.ForeColor = Secondary;
        connection.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(connection, 0, 1);

        var gauge = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        gauge.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        gauge.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gauge.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        battery = new BatteryCanvas
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 20, 18, 20),
            BackColor = Background, AccessibleName = "鼠标电池"
        };
        gauge.Controls.Add(battery, 0, 0);
        var amount = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        amount.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        amount.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        amount.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        percentage = MakeLabel("—", 32F, FontStyle.Bold);
        percentage.Font = new Font("Segoe UI", 32F, FontStyle.Bold);
        percentage.AutoEllipsis = false;
        percentage.TextAlign = ContentAlignment.MiddleLeft;
        amount.Controls.Add(percentage, 0, 0);
        chargeState = MakeLabel("等待电量数据", 9F);
        chargeState.ForeColor = Secondary;
        chargeState.TextAlign = ContentAlignment.MiddleLeft;
        amount.Controls.Add(chargeState, 0, 1);
        gauge.Controls.Add(amount, 1, 0);
        layout.Controls.Add(gauge, 0, 2);

        message = MakeLabel("", 9F);
        message.ForeColor = Secondary;
        message.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(message, 0, 3);
        updated = MakeLabel("", 8F);
        updated.ForeColor = Secondary;
        updated.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(updated, 0, 4);
        var settingsCard = new SettingsCard
        {
            Dock = DockStyle.Fill, Padding = new Padding(10), Margin = new Padding(0, 8, 0, 0)
        };
        settingsCard.SuspendLayout();
        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = SettingsPalette.Card
        };
        settings.SuspendLayout();
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 34, 34, 34, 8, 40 })
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        startup = MakeCheckBox("开机自动运行", divider:true);
        startup.AccessibleDescription = "登录 Windows 后自动在任务栏显示鼠标电量";
        startup.CheckedChanged += (_, _) => { if (!settingState) StartupRequested?.Invoke(this, EventArgs.Empty); };
        settings.Controls.Add(startup, 0, 0);
        showPercentage = MakeCheckBox("托盘显示电量数字", divider:true);
        showPercentage.CheckedChanged += (_, _) => { if (!settingState) PercentageRequested?.Invoke(this, EventArgs.Empty); };
        settings.Controls.Add(showPercentage, 0, 1);

        lowBattery = MakeCheckBox("低电通知");
        settings.Controls.Add(lowBattery, 0, 2);
        var lowBatteryRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = SettingsPalette.Card
        };
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        lowBatteryRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var below = MakeLabel("低于", 9F);
        below.TextAlign = ContentAlignment.MiddleLeft;
        below.ForeColor = Secondary;
        below.BackColor = SettingsPalette.Card;
        below.Margin = new Padding(4, 0, 0, 0);
        lowBatteryRow.Controls.Add(below, 0, 0);
        notificationThreshold = new ThresholdStepper
        {
            Value = 20, Anchor = AnchorStyles.Left | AnchorStyles.Right, Size = new Size(130, 30),
            Font = new Font("Microsoft YaHei UI", 9.5F), Margin = Padding.Empty
        };
        notificationThreshold.ValueChanged += (_, _) => { if (!settingState) LowBatteryOptionsRequested?.Invoke(this, EventArgs.Empty); };
        lowBattery.CheckedChanged += (_, _) =>
        {
            notificationThreshold.Enabled = lowBattery.Checked;
            if (!settingState) LowBatteryOptionsRequested?.Invoke(this, EventArgs.Empty);
        };
        lowBatteryRow.Controls.Add(notificationThreshold, 1, 0);

        var testNotification = new StyledButton
        {
            Text = "测试通知", Anchor = AnchorStyles.Left | AnchorStyles.Right, Size = new Size(76, 30),
            Font = new Font("Microsoft YaHei UI", 9F), Margin = Padding.Empty,
            AccessibleDescription = "发送一条 Windows 测试通知，不影响低电提醒状态"
        };
        testNotification.Click += (_, _) => TestNotificationRequested?.Invoke(this, EventArgs.Empty);
        lowBatteryRow.Controls.Add(testNotification, 3, 0);
        settings.Controls.Add(lowBatteryRow, 0, 4);
        settingsCard.Controls.Add(settings);
        layout.Controls.Add(settingsCard, 0, 5);
        hint = MakeLabel("自动更新 · 每 45 秒", 8F);
        hint.ForeColor = Color.FromArgb(113, 125, 143);
        hint.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(hint, 0, 6);
        Controls.Add(layout);

        EnableWindowDragging(this);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) => WriteLayoutDiagnostics();
        DpiChanged += (_, _) => BeginInvoke(WriteLayoutDiagnostics);

        // Establish a single scaling baseline only after every child has been added.
        AutoScaleDimensions = new SizeF(96, 96);
        _ = Handle;
        settings.ResumeLayout(false);
        settingsCard.ResumeLayout(false);
        layout.ResumeLayout(false);
        ResumeLayout(true);
        WriteLayoutDiagnostics();
    }

    public void SetState(TrayApp.BatteryView view, bool refreshing, bool startupEnabled, bool showTrayPercentage, bool lowBatteryEnabled, int lowBatteryThreshold)
    {
        if (IsDisposed) return;
        settingState = true;
        try
        {
            startup.Checked = startupEnabled;
            showPercentage.Checked = showTrayPercentage;
            lowBattery.Checked = lowBatteryEnabled;
            notificationThreshold.Value = Math.Clamp(lowBatteryThreshold, 1, 99);
            notificationThreshold.Enabled = lowBatteryEnabled;
        }
        finally { settingState = false; }
        title.Text = string.IsNullOrWhiteSpace(view.Model) ? "鼠标电量" : view.Model;
        Text = title.Text + " · 鼠标电量";
        percentage.Text = view.Percent is int p ? $"{p}%" : "—";
        percentage.ForeColor = view.Stale || view.Percent is null ? Secondary : Color.FromArgb(115, 227, 167);
        connection.Text = view.Stale ? "上次读数 · 鼠标暂未响应"
            : view.Percent is not null ? "已连接 · " + (string.IsNullOrWhiteSpace(view.Transport) ? "鼠标" : view.Transport)
            : refreshing ? "正在读取电量…" : "暂未读取到电量";
        connection.ForeColor = view.Percent is not null && !view.Stale ? Color.FromArgb(118, 215, 178) : Secondary;
        chargeState.Text = view.Stale ? "等待鼠标唤醒"
            : view.Percent is int remaining && lowBatteryEnabled && remaining < lowBatteryThreshold && !view.Charging ? "电量较低，建议充电"
            : TrayApp.ChargeLabel(view);
        message.Text = view.Stale ? "移动鼠标后会自动更新，当前显示最近一次读数。" : view.Message;
        hint.Text = $"自动更新 · 每 {PollSeconds} 秒";
        updated.Text = (view.Stale ? "上次读数 · " : view.Percent is null ? "最近尝试 · " : "已更新 · ") + view.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        battery.Percent = view.Percent;
        battery.Charging = !view.Stale && view.Charging;
        battery.Stale = view.Stale;
        battery.Invalidate();
        AccessibleDescription = view.Percent is int value ? $"{title.Text} 剩余电量 {value}%，{connection.Text}，{chargeState.Text}" : view.Message;
    }

    public void PositionNearTray()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        int margin = (int)Math.Round(12 * DeviceDpi / 96F);
        int x = Math.Clamp(cursor.X - Width + margin * 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        int y = cursor.Y < area.Top + margin * 2 ? area.Top + margin : area.Bottom - Height - margin;
        Location = new Point(x, Math.Max(area.Top + margin, y));
    }

    internal static bool CanDragFrom(Control control) => control is not ButtonBase and not TextBoxBase and not ComboBox and not LinkLabel and not ScrollBar and not UpDownBase and not ThresholdStepper;

    private void EnableWindowDragging(Control control)
    {
        if (!CanDragFrom(control)) return;
        control.MouseDown += DragWindow;
        foreach (Control child in control.Controls) EnableWindowDragging(child);
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
    }

    protected override void WndProc(ref Message m)
    {
        const int WmDeviceChange = 0x0219;
        if (m.Msg == WmDeviceChange && m.WParam.ToInt64() is 7 or 0x8000 or 0x8004)
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private void WriteLayoutDiagnostics()
    {
        try
        {
            var nodes = new List<object>();
            void Add(Control node)
            {
                nodes.Add(new { Type = node.GetType().Name, node.Text, Bounds = new { node.Left, node.Top, node.Width, node.Height }, Font = new { node.Font.Name, node.Font.SizeInPoints } });
                foreach (Control child in node.Controls) Add(child);
            }
            Add(this);
            var data = new { DeviceDpi, ClientSize = new { ClientSize.Width, ClientSize.Height }, AutoScaleDimensions, CurrentAutoScaleDimensions, Controls = nodes };
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VgnBattery");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "layout.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Diagnostics never interfere with the tray. */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Color.FromArgb(65, 74, 88));
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ClassStyle |= 0x00020000; return value; }
    }

    private static Label MakeLabel(string text, float fontSize, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", fontSize, style),
        AutoEllipsis = true, UseMnemonic = false, BackColor = Background
    };

    private static CheckBox MakeCheckBox(string text, bool divider = false) => new StyledCheckBox(divider)
    {
        Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty,
        Font = new Font("Microsoft YaHei UI", 9.5F)
    };
}
internal sealed class BatteryCanvas : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? Percent { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Charging { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Stale { get; set; }
    public BatteryCanvas() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var outline = Stale || Percent is null ? Color.FromArgb(146, 154, 166) : Color.FromArgb(115, 227, 167);
        TrayIconRenderer.DrawBattery(e.Graphics, new RectangleF(1, 2, Width - 3, Height - 5), Percent, Charging, outline, true, Stale);
    }
}
