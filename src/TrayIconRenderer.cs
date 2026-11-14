using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VgnBattery;

internal static class TrayIconRenderer
{
    public static Icon Create(int? percent, bool charging, bool showPercentage, bool lightTaskbar, bool stale = false, int iconSize = 0)
    {
        int size = ResolveSize(iconSize);
        using var bitmap = CreateBitmap(percent, charging, showPercentage, lightTaskbar, stale, size);
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        byte[] frame = png.ToArray();
        // One exact-size frame avoids shrinking a 32px HICON onto a 24px tray.
        // The caller creates a new icon when its monitor DPI changes.
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(frame.Length); writer.Write(22); writer.Write(frame);
        }
        ico.Position = 0;
        using var source = new Icon(ico, new Size(size, size));
        return (Icon)source.Clone();
    }

    internal static Bitmap CreateBitmap(int? percent, bool charging, bool showPercentage, bool lightTaskbar, bool stale = false, int iconSize = 0)
    {
        int size = ResolveSize(iconSize);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        bool offline = stale || percent is null;
        var foreground = offline ? (lightTaskbar ? Color.FromArgb(116, 121, 129) : Color.FromArgb(146, 154, 166))
            : lightTaskbar ? Color.FromArgb(12, 125, 61) : Color.FromArgb(102, 235, 155);
        if (showPercentage)
        {
            // Numeric mode gives the digits their own clear interior, without a
            // fill boundary or charging glyph crossing through the characters.
            DrawNumericBattery(graphics, size, percent, foreground);
        }
        else
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(size * .01F, size * .14F, size * .98F, size * .72F);
            DrawBattery(graphics, bounds, percent, charging && !offline, foreground, false, offline);
        }
        return bitmap;
    }

    private static int ResolveSize(int iconSize) => Math.Clamp(iconSize > 0 ? iconSize : SystemInformation.SmallIconSize.Width, 16, 256);
    internal static void DrawBattery(Graphics graphics, RectangleF bounds, int? percent, bool charging, Color outline, bool large, bool stale = false)
    {
        float scale = bounds.Height;
        float stroke = Math.Max(1F, scale * .065F);
        float capWidth = Math.Max(1F, bounds.Width * .06F);
        var body = new RectangleF(bounds.X + stroke, bounds.Y + stroke, bounds.Width - capWidth - stroke * 3F, bounds.Height - stroke * 2F);
        using var path = Rounded(body, Math.Max(1F, scale * .12F));
        using var pen = new Pen(outline, stroke);
        graphics.DrawPath(pen, path);
        using var cap = new SolidBrush(outline);
        graphics.FillRectangle(cap, body.Right + stroke, bounds.Y + bounds.Height * .34F, capWidth, bounds.Height * .32F);
        var inner = RectangleF.Inflate(body, -stroke * 1.65F, -stroke * 1.65F);
        Color fill = stale ? Color.FromArgb(125, 135, 150) : large ? Color.FromArgb(115, 227, 167) : outline;
        if (percent is int value && value > 0 && inner.Width > 0 && inner.Height > 0)
        {
            using var brush = new SolidBrush(fill);
            graphics.FillRectangle(brush, inner.X, inner.Y, inner.Width * Math.Clamp(value, 0, 100) / 100F, inner.Height);
        }
        if (percent is null)
        {
            var previousTextRendering = graphics.TextRenderingHint;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", scale * .65F, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(outline);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("?", font, brush, body, format);
            graphics.TextRenderingHint = previousTextRendering;
        }
        else if (charging)
        {
            float center = body.X + body.Width / 2;
            PointF[] bolt =
            [
                new(center + scale * .08F, body.Y + body.Height * .10F),
                new(center - scale * .12F, body.Y + body.Height * .57F),
                new(center + scale * .015F, body.Y + body.Height * .57F),
                new(center - scale * .065F, body.Y + body.Height * .90F),
                new(center + scale * .15F, body.Y + body.Height * .40F),
                new(center + scale * .015F, body.Y + body.Height * .40F)
            ];
            using var brush = new SolidBrush(Color.FromArgb(26, 55, 43));
            graphics.FillPolygon(brush, bolt);
        }
    }

    // Nine rows keep the numerals tall enough to read at 16px. Two-digit values
    // use wider glyphs; 100 uses a compact 11-column word with genuine gaps.
    private static readonly string[][] WideDigits =
    [
        ["01110", "11011", "10001", "10001", "10001", "10001", "10001", "11011", "01110"],
        ["00100", "01100", "10100", "00100", "00100", "00100", "00100", "00100", "11111"],
        ["01110", "10001", "00001", "00001", "00110", "01000", "10000", "10000", "11111"],
        ["11110", "00001", "00001", "00001", "01110", "00001", "00001", "00001", "11110"],
        ["00010", "00110", "01010", "10010", "10010", "11111", "00010", "00010", "00010"],
        ["11111", "10000", "10000", "10000", "11110", "00001", "00001", "10001", "01110"],
        ["00110", "01000", "10000", "10000", "11110", "10001", "10001", "10001", "01110"],
        ["11111", "00001", "00001", "00010", "00100", "00100", "01000", "01000", "01000"],
        ["01110", "10001", "10001", "10001", "01110", "10001", "10001", "10001", "01110"],
        ["01110", "10001", "10001", "10001", "01111", "00001", "00001", "00010", "11100"]
    ];
    private static readonly string[] NarrowOne = ["010", "110", "010", "010", "010", "010", "010", "010", "111"];
    private static readonly string[] NarrowZero = ["111", "101", "101", "101", "101", "101", "101", "101", "111"];
    private static readonly string[] Question = ["01110", "10001", "00001", "00001", "00110", "00100", "00100", "00000", "00100"];

    private static void DrawNumericBattery(Graphics graphics, int size, int? percent, Color foreground)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        int stroke = Math.Max(1, (int)Math.Round(size / 24F));
        int cap = Math.Max(1, (int)Math.Round(size / 16F));
        int top = Math.Max(1, (int)Math.Round(size / 16F));
        int bodyWidth = size - cap;
        int bodyHeight = size - top * 2;
        using var ink = new SolidBrush(foreground);
        // Crisp rectangular segments with open corner pixels form a minimal
        // battery frame; every glyph has breathing room inside these edges.
        graphics.FillRectangle(ink, stroke, top, bodyWidth - stroke * 2, stroke);
        graphics.FillRectangle(ink, stroke, top + bodyHeight - stroke, bodyWidth - stroke * 2, stroke);
        graphics.FillRectangle(ink, 0, top + stroke, stroke, bodyHeight - stroke * 2);
        graphics.FillRectangle(ink, bodyWidth - stroke, top + stroke, stroke, bodyHeight - stroke * 2);
        int capHeight = Math.Max(2, (int)Math.Round(size * .3F));
        graphics.FillRectangle(ink, bodyWidth, (size - capHeight) / 2, cap, capHeight);

        string text = percent.HasValue ? Math.Clamp(percent.Value, 0, 100).ToString() : "?";
        string[][] glyphs = text == "100" ? [NarrowOne, NarrowZero, NarrowZero]
            : text == "?" ? [Question] : text.Select(c => WideDigits[c - '0']).ToArray();
        int columns = glyphs.Sum(g => g[0].Length) + glyphs.Length - 1;
        int desiredWidth = (int)Math.Round((glyphs.Length == 1 ? 7 : 11) * size / 16F, MidpointRounding.AwayFromZero);
        int desiredHeight = (int)Math.Round(10 * size / 16F);
        int width = Math.Min(desiredWidth, bodyWidth - stroke * 2 - 2);
        int height = Math.Min(desiredHeight, bodyHeight - stroke * 2 - 2);
        int x = (bodyWidth - width) / 2;
        int y = top + (bodyHeight - height) / 2;
        int offset = 0;
        foreach (var glyph in glyphs)
        {
            for (int row = 0; row < 9; row++)
            {
                int y0 = y + (int)Math.Round(row * height / 9F);
                int y1 = y + (int)Math.Round((row + 1) * height / 9F);
                for (int column = 0; column < glyph[0].Length; column++)
                {
                    if (glyph[row][column] != '1') continue;
                    int x0 = x + (int)Math.Round((offset + column) * width / (float)columns);
                    int x1 = x + (int)Math.Round((offset + column + 1) * width / (float)columns);
                    graphics.FillRectangle(ink, x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
                }
            }
            offset += glyph[0].Length + 1;
        }
    }
    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
