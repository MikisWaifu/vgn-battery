using System.Drawing.Drawing2D;

namespace VgnBattery;

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Background = Color.FromArgb(27, 30, 36);
    private static readonly Color Hover = Color.FromArgb(47, 63, 80);
    private static readonly Color Text = Color.FromArgb(239, 243, 250);
    private static readonly Color Secondary = Color.FromArgb(158, 168, 183);
    private static readonly Color Green = Color.FromArgb(115, 227, 167);
    private static readonly Color Border = Color.FromArgb(65, 74, 88);

    public DarkMenuRenderer() : base(new DarkColors()) => RoundedEdges = false;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width < 2 || bounds.Height < 2) return;
        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        using (var brush = new SolidBrush(Background))
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));

        if (e.Item.Enabled && (e.Item.Selected || e.Item.Pressed))
        {
            int inset = Scale(e.ToolStrip, 2);
            var highlight = new Rectangle(inset, 0, Math.Max(0, e.Item.Width - inset * 2), e.Item.Height);
            using var brush = new SolidBrush(Hover);
            e.Graphics.FillRectangle(brush, highlight);
        }

        if (e.Item is ToolStripMenuItem { CheckOnClick: true } item)
            DrawCheckBox(e.Graphics, CheckBounds(e.ToolStrip, item), item.Checked, item.Enabled, e.ToolStrip);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Both checked and unchecked boxes are drawn together with the item background.
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Secondary;
        if (e.Item is ToolStripMenuItem item && e.ToolStrip is ToolStripDropDownMenu { ShowCheckMargin:true })
        {
            var box = CheckBounds(e.ToolStrip, item);
            var bounds = e.TextRectangle;
            int gap = Scale(e.ToolStrip, 7);
            if (e.ToolStrip?.RightToLeft == RightToLeft.Yes)
                bounds.Width = Math.Max(0, Math.Min(bounds.Right, box.Left - gap) - bounds.Left);
            else
            {
                int left = Math.Max(bounds.Left, box.Right + gap);
                bounds = new Rectangle(left, bounds.Top, Math.Max(0, bounds.Right - left), bounds.Height);
            }
            e.TextRectangle = bounds;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled is not false ? Text : Secondary;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int inset = Scale(e.ToolStrip, 7);
        using var pen = new Pen(Border);
        if (e.Vertical)
        {
            int x = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, x, inset, x, Math.Max(inset, e.Item.Height - inset));
        }
        else
        {
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, inset, y, Math.Max(inset, e.Item.Width - inset), y);
        }
    }

    private static Rectangle CheckBounds(ToolStrip? strip, ToolStripMenuItem item)
    {
        int size = Math.Max(1, Math.Min(Scale(strip, 15), item.Height - Scale(strip, 4)));
        int margin = Scale(strip, 7);
        int x = strip?.RightToLeft == RightToLeft.Yes ? item.Width - margin - size : margin;
        return new Rectangle(x, (item.Height - size) / 2, size, size);
    }

    private static void DrawCheckBox(Graphics graphics, Rectangle bounds, bool isChecked, bool enabled, ToolStrip? strip)
    {
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (isChecked)
            {
                using var brush = new SolidBrush(enabled ? Green : Secondary);
                graphics.FillRectangle(brush, bounds);
                using var tick = new Pen(Background, Math.Max(1.5F, (strip?.DeviceDpi ?? 96) / 96F * 1.8F))
                {
                    StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
                };
                graphics.DrawLines(tick,
                [
                    new PointF(bounds.Left + bounds.Width * .23F, bounds.Top + bounds.Height * .51F),
                    new PointF(bounds.Left + bounds.Width * .43F, bounds.Top + bounds.Height * .72F),
                    new PointF(bounds.Left + bounds.Width * .78F, bounds.Top + bounds.Height * .28F)
                ]);
            }
            else
            {
                using var pen = new Pen(enabled ? Secondary : Border, Math.Max(1F, (strip?.DeviceDpi ?? 96) / 96F));
                graphics.DrawRectangle(pen, bounds.X + .5F, bounds.Y + .5F, Math.Max(0, bounds.Width - 1F), Math.Max(0, bounds.Height - 1F));
            }
        }
        finally { graphics.Restore(state); }
    }

    private static int Scale(ToolStrip? strip, int pixels) => Math.Max(1, (int)Math.Round(pixels * (strip?.DeviceDpi ?? 96) / 96F));

    private sealed class DarkColors : ProfessionalColorTable
    {
        public DarkColors() => UseSystemColors = false;
        public override Color ToolStripDropDownBackground => Background;
        public override Color ToolStripGradientBegin => Background;
        public override Color ToolStripGradientMiddle => Background;
        public override Color ToolStripGradientEnd => Background;
        public override Color ToolStripBorder => Border;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color ImageMarginRevealedGradientBegin => Background;
        public override Color ImageMarginRevealedGradientMiddle => Background;
        public override Color ImageMarginRevealedGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color CheckBackground => Background;
        public override Color CheckSelectedBackground => Green;
        public override Color CheckPressedBackground => Green;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
    }
}
