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
                using var path = TrayIconRenderer.RoundedRect(new RectangleF(5, top, e.ToolStrip.Width - 11, bottom - top), 8);
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
        var r = new RectangleF(12 * k - e.Item.Bounds.X, 2 * k, width - 25 * k, e.Item.Height - 4 * k);
        using var path = TrayIconRenderer.RoundedRect(r, 5 * k);
        using var fill = new SolidBrush(theme.CardHover);
        g.FillPath(fill, path);
    }

    // The check is drawn with the text instead, so the pill can be centred on the text line.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled || e.Item.Tag is "state" ? theme.Text : theme.SubText;
        base.OnRenderItemText(e);
        if (e.Item is not ToolStripMenuItem { Checked: true }) return;

        // A selection pill in front of the checked item, centred on its text and inside the hover area.
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = e.Item.Owner?.DeviceDpi / 96f ?? 1f;
        float w = 4 * k, h = 16 * k;
        float center = e.TextRectangle.Top + e.TextRectangle.Height / 2f;
        var pill = new RectangleF(17 * k - e.Item.Bounds.X, center - h / 2, w, h);
        using var path = TrayIconRenderer.RoundedRect(pill, w / 2);
        using var fill = new SolidBrush(theme.Accent);
        g.FillPath(fill, path);
    }

    // The gap between groups is the separator; no line is drawn.
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { }
}
