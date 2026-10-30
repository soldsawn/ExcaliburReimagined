using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Excalibur.App.Tray;

/// <summary>
/// Paints the tray's right-click menu like the panel: theme background, rounded hover
/// highlight, an accent pill in front of the checked profile, thin separators.
/// </summary>
internal sealed class ThemedMenuRenderer(Theme theme) : ToolStripRenderer
{
    /// <summary>How far a group's card extends above its first and below its last item (px at 96 dpi); the gaps between groups must exceed twice this.</summary>
    public const int CardPadding = 5;
    /// <summary>The cards' and the hover highlight's distance from the menu's sides (px at 96 dpi), the same on both sides.</summary>
    private const int CardInset = 6, HoverInset = 10;
    /// <summary>Where every text starts (the items' left padding): past the hover edge and the pill.</summary>
    public const int TextInset = 22;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(theme.Back))
            g.FillRectangle(back, e.AffectedBounds);

        // Each group of items (between the gaps) sits on a soft rounded card; the first group is the
        // status line and stays plain.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var card = new SolidBrush(theme.Card);
        var group = new List<ToolStripItem>();
        int index = 0;
        void Flush()
        {
            if (group.Count > 0 && index++ > 0)
            {
                // The card reaches a little past its first and last item, so text never touches the edge.
                int top = group.Min(i => i.Bounds.Top) - CardPadding, bottom = group.Max(i => i.Bounds.Bottom) + CardPadding;
                float k = e.ToolStrip.DeviceDpi / 96f;
                using var path = TrayIconRenderer.RoundedRect(new RectangleF(CardInset * k, top, e.ToolStrip.Width - 2 * CardInset * k, bottom - top), 8 * k);
                g.FillPath(card, path);
            }
            group.Clear();
        }
        foreach (ToolStripItem item in e.ToolStrip.Items)
        {
            if (item is ToolStripSeparator) Flush();
            else group.Add(item);
        }
        Flush();
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { } // no grey gutter

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled || e.Item.Tag is "state") return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = e.Item.Owner?.DeviceDpi / 96f ?? 1f;
        // Measured from the menu, not the item (an item can be wider than what is visible), so it sits
        // the same distance inside the card on the left and on the right.
        float width = e.Item.Owner?.Width ?? e.Item.Width;
        var r = new RectangleF(HoverInset * k - e.Item.Bounds.X, 2 * k, width - 2 * HoverInset * k, e.Item.Height - 4 * k);
        using var path = TrayIconRenderer.RoundedRect(r, 5 * k);
        using var fill = new SolidBrush(theme.CardHover);
        g.FillPath(fill, path);
    }

    // The check is drawn with the text instead, so the pill can be centred on the text line.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled || e.Item.Tag is "state" ? theme.Text : theme.SubText;
        // Without the image/check column the item lays its text out from its very edge; start every one at TextInset.
        float scale = e.Item.Owner?.DeviceDpi / 96f ?? 1f;
        var text = e.TextRectangle;
        // The status line is a heading over the cards: centred on the menu, at the bottom of its (taller) row, near the first card.
        bool heading = e.Item.Tag is "state";
        int left = heading
            ? ((e.Item.Owner?.Width ?? e.Item.Width) - TextRenderer.MeasureText(e.Text, e.TextFont).Width) / 2 - e.Item.Bounds.X
            : (int)(TextInset * scale) - e.Item.Bounds.X;
        int top = heading ? e.Item.Height - text.Height - (int)(5 * scale) : text.Y;
        e.TextRectangle = new Rectangle(left, top, text.Width, text.Height);
        base.OnRenderItemText(e);
        if (e.Item is not ToolStripMenuItem { Checked: true }) return;

        // A selection pill in front of the checked item, centred on its text and inside the hover area.
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = e.Item.Owner?.DeviceDpi / 96f ?? 1f;
        float w = 4 * k, h = 16 * k;
        float center = e.TextRectangle.Top + e.TextRectangle.Height / 2f;
        var pill = new RectangleF((HoverInset + 4) * k - e.Item.Bounds.X, center - h / 2, w, h);
        using var path = TrayIconRenderer.RoundedRect(pill, w / 2);
        using var fill = new SolidBrush(theme.Accent);
        g.FillPath(fill, path);
    }

    // The gap between groups is the separator; no line is drawn.
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { }
}
