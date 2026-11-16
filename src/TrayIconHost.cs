namespace VgnBattery;

// Owns one shell icon, but never owns the shared context menu.
internal sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon icon;
    private Icon? image;
    private (int? Percent, bool Charging, bool Digits, bool Light, bool Stale, int Size)? lastImage;
    internal string DeviceId { get; private set; } = "";
    internal event EventHandler? LeftClicked;
    internal event EventHandler? ContextRequested;

    internal TrayIconHost(ContextMenuStrip menu)
    {
        icon = new NotifyIcon { ContextMenuStrip = menu, Visible = false, Text = "鼠标电量" };
        icon.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) ContextRequested?.Invoke(this, EventArgs.Empty); };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) LeftClicked?.Invoke(this, EventArgs.Empty); };
    }

    internal void Update(MouseBattery device, bool digits, bool light, int size)
    {
        DeviceId = device.DeviceId;
        var reading = device.Reading;
        var key = (reading.Percent, reading.Charging, digits, light, device.Stale, size);
        if (key != lastImage)
        {
            var next = TrayIconRenderer.Create(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5, iconSize:key.Item6);
            var previous = image;
            image = next;
            icon.Icon = next;
            lastImage = key;
            previous?.Dispose();
        }
        string state = reading.Percent is int p ? $"{p}% · {(device.Stale ? reading.Transport == "未连接" ? "未连接（上次读数）" : "上次读数（鼠标暂未响应）" : reading.Charging ? "正在充电" : "电池供电")}" : "未读取到电量";
        string tooltip = reading.Model + " · " + state;
        if (!string.IsNullOrWhiteSpace(reading.Transport)) tooltip += "\n" + reading.Transport;
        if (reading.Percent is null && !string.IsNullOrWhiteSpace(reading.Message)) tooltip += "\n" + reading.Message;
        tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (icon.Text != tooltip) icon.Text = tooltip;
        if (!icon.Visible) icon.Visible = true;
    }

    internal void Notify(string title, string text, ToolTipIcon kind = ToolTipIcon.Info) => icon.ShowBalloonTip(8_000, title, text, kind);

    public void Dispose()
    {
        icon.Visible = false;
        icon.ContextMenuStrip = null;
        icon.Dispose();
        image?.Dispose();
        image = null;
    }
}
