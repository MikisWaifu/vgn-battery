namespace VgnBattery;

internal sealed record OtherDeviceRow(string DeviceId, string Model, string Transport, string StatusText, bool Stale);

/// <summary>A read-only summary of the other currently enumerated battery interfaces.</summary>
internal sealed class OtherDevicesPanel : Control
{
    private const int TopGap = 8;
    private const int HeadingHeight = 18;
    private const int DeviceRowHeight = 36;
    private const int BottomGap = 4;
    private static readonly Color Muted = Color.FromArgb(158, 168, 183);
    private static readonly Color Connected = Color.FromArgb(115, 227, 167);
    private IReadOnlyList<OtherDeviceRow> rows = Array.Empty<OtherDeviceRow>();
    private Font? secondaryFont;

    internal IReadOnlyList<OtherDeviceRow> Rows => rows;

    internal OtherDevicesPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "其它设备电量";
        TabStop = false;
    }

    internal static IReadOnlyList<OtherDeviceRow> CreateRows(string currentDeviceId, IReadOnlyList<MouseBattery> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<OtherDeviceRow>();
        foreach (var device in devices)
        {
            if (device is null || string.IsNullOrWhiteSpace(device.DeviceId)
                || string.Equals(device.DeviceId, currentDeviceId, StringComparison.Ordinal)
                || !seen.Add(device.DeviceId)) continue;
            var reading = device.Reading;
            bool hasValue = reading.Percent is >= 0 and <= 100;
            bool stale = device.Stale || !hasValue;
            string status = !hasValue ? "未响应" : device.Stale ? $"{reading.Percent}% · 上次读数" : $"{reading.Percent}%";
            result.Add(new(device.DeviceId,
                string.IsNullOrWhiteSpace(reading.Model) ? "鼠标" : reading.Model,
                string.IsNullOrWhiteSpace(reading.Transport) ? "连接方式未知" : reading.Transport,
                status, stale));
        }
        return result.AsReadOnly();
    }

    internal static int LogicalHeightForRows(int count) => count <= 0 ? 0
        : TopGap + HeadingHeight + DeviceRowHeight * count + BottomGap;

    internal bool SetDevices(string currentDeviceId, IReadOnlyList<MouseBattery> devices)
    {
        var next = CreateRows(currentDeviceId, devices);
        if (rows.SequenceEqual(next)) return false;
        rows = next;
        AccessibleDescription = string.Join("；", rows.Select(r => $"{r.Model}，{r.Transport}，{r.StatusText}"));
        Invalidate();
        return true;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        secondaryFont?.Dispose();
        secondaryFont = new Font(Font.FontFamily, Math.Max(7F, Font.SizeInPoints - 1F), FontStyle.Regular);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (rows.Count == 0) return;
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96F);
        var smallFont = secondaryFont ?? Font;
        const TextFormatFlags nameFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        const TextFormatFlags valueFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var headingBounds = new Rectangle(0, Scale(TopGap), Width, Scale(HeadingHeight));
        TextRenderer.DrawText(e.Graphics, "其它设备", smallFont, headingBounds, Muted, nameFlags);
        int statusWidth = Math.Min(Width, Scale(102));
        int nameWidth = Math.Max(1, Width - statusWidth - Scale(10));
        using var line = new Pen(Color.FromArgb(47, 56, 68));
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int top = Scale(TopGap + HeadingHeight + i * DeviceRowHeight);
            var nameBounds = new Rectangle(0, top, nameWidth, Scale(18));
            var transportBounds = new Rectangle(0, top + Scale(18), nameWidth, Scale(16));
            var statusBounds = new Rectangle(Width - statusWidth, top, statusWidth, Scale(DeviceRowHeight - 4));
            TextRenderer.DrawText(e.Graphics, row.Model, Font, nameBounds, row.Stale ? Muted : ForeColor, nameFlags);
            TextRenderer.DrawText(e.Graphics, row.Transport, smallFont, transportBounds, Muted, nameFlags);
            TextRenderer.DrawText(e.Graphics, row.StatusText, Font, statusBounds, row.Stale ? Muted : Connected, valueFlags);
            if (i < rows.Count - 1)
                e.Graphics.DrawLine(line, 0, top + Scale(DeviceRowHeight) - 1, Width, top + Scale(DeviceRowHeight) - 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { secondaryFont?.Dispose(); secondaryFont = null; }
        base.Dispose(disposing);
    }
}
