using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Tray;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Ayarlar > Windows güç ayarları: for each performance mode (and the battery) the Windows power plan and power mode to
/// switch to. The tray applies them (<see cref="WindowsPowerMode"/>); this card only edits <see cref="UserSettings.WindowsPower"/>.
/// </summary>
internal static class WindowsPowerCard
{
    private const string Leave = "Değiştirme";

    private static readonly (WindowsPowerOverlay Mode, string Label)[] Modes =
    [
        (WindowsPowerOverlay.BestEfficiency, "En iyi güç verimliliği"),
        (WindowsPowerOverlay.Balanced, "Dengeli"),
        (WindowsPowerOverlay.BestPerformance, "En iyi performans"),
    ];

    public static Border Create()
    {
        var store = new UserSettingsStore();
        var plans = WindowsPowerMode.Plans();
        var rules = store.Current.WindowsPower;
        bool loading = true;

        var enabled = new CheckBox
        {
            Content = "Performans modu değişince Windows güç ayarlarını da değiştir", FontSize = 14, Margin = new Thickness(0, 10, 0, 0),
            IsChecked = rules.Enabled,
        };
        enabled.SetResourceReference(Control.ForegroundProperty, "Text");

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Add(grid, 0, 1, InfoPage.Text("Güç planı", "Caption", bottom: 4));
        Add(grid, 0, 2, InfoPage.Text("Windows güç modu", "Caption", bottom: 4));

        var rows = new (string Label, Func<WindowsPowerRules, WindowsPowerRule> Get, Func<WindowsPowerRules, WindowsPowerRule, WindowsPowerRules> Set)[]
        {
            ("Ofis modu", r => r.Office, (r, x) => r with { Office = x }),
            ("Oyun modu", r => r.Gaming, (r, x) => r with { Gaming = x }),
            ("Yüksek performans", r => r.HighPerformance, (r, x) => r with { HighPerformance = x }),
            ("Pilde", r => r.Battery, (r, x) => r with { Battery = x }),
        };
        var boxes = new List<(ComboBox Plan, ComboBox Mode)>();
        for (int i = 0; i < rows.Length; i++)
        {
            var rule = rows[i].Get(rules);
            // Two plans may share a name (this machine has two "Dengeli"); the start of the id tells them apart.
            var plan = Combo([Leave, .. plans.Select(p => plans.Count(q => q.Name == p.Name) > 1 ? $"{p.Name} ({p.Id.ToString()[..4]})" : p.Name)]);
            int planIndex = rule.Plan is { } id ? plans.ToList().FindIndex(p => p.Id == id) : -1;
            // A plan deleted since it was chosen shows as "leave it": it could not be applied anyway.
            plan.SelectedIndex = planIndex + 1;
            var mode = Combo([Leave, .. Modes.Select(m => m.Label)]);
            mode.SelectedIndex = rule.Mode is { } m ? Array.FindIndex(Modes, x => x.Mode == m) + 1 : 0;
            var label = InfoPage.Text(rows[i].Label, "Body");
            label.VerticalAlignment = VerticalAlignment.Center;
            Add(grid, i + 1, 0, label);
            Add(grid, i + 1, 1, plan);
            Add(grid, i + 1, 2, mode);
            boxes.Add((plan, mode));
        }

        void Save()
        {
            if (loading) return;
            var next = store.Current.WindowsPower with { Enabled = enabled.IsChecked == true };
            for (int i = 0; i < rows.Length; i++)
            {
                var (plan, mode) = boxes[i];
                next = rows[i].Set(next, new WindowsPowerRule(
                    plan.SelectedIndex > 0 ? plans[plan.SelectedIndex - 1].Id : null,
                    mode.SelectedIndex > 0 ? Modes[mode.SelectedIndex - 1].Mode : null));
            }
            store.Save(store.Current with { WindowsPower = next });
        }
        enabled.Checked += (_, _) => Save();
        enabled.Unchecked += (_, _) => Save();
        foreach (var (plan, mode) in boxes)
        {
            plan.SelectionChanged += (_, _) => Save();
            mode.SelectionChanged += (_, _) => Save();
        }
        enabled.Unloaded += (_, _) => store.Dispose();
        loading = false;

        return InfoPage.Card("Windows güç ayarları",
            InfoPage.Text("Performans modunu değiştirince Windows'un güç planı ve güç modu da seçtiğin gibi değişir. " +
                          "Güç planını kendin ya da başka bir uygulamayla ayarlıyorsan kapalı bırak.", "Caption"),
            enabled, grid,
            InfoPage.Text("Pilde yalnızca Ofis modu çalıştığı için pilin ayrı satırı var. Windows güç modu yalnızca Dengeli güç planındayken " +
                          "işe yarar; başka bir plan seçtiysen onu \"Değiştirme\" bırakabilirsin.", "Caption", top: 6));
    }

    private static ComboBox Combo(IReadOnlyList<string> items)
    {
        var c = new ComboBox { Margin = new Thickness(0, 0, 12, 8), ItemsSource = items };
        c.SetResourceReference(FrameworkElement.StyleProperty, "ThemedCombo");
        return c;
    }

    private static void Add(Grid grid, int row, int column, UIElement element)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
