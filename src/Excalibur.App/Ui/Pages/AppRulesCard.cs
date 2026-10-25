using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Excalibur.Core.Automation;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// The program rules on the Otomasyon page (A6): add a program from the ones with a window open or by browsing for its .exe,
/// then pick what changes while it runs. Edits <see cref="UserSettings.AppRules"/>; the tray applies them. Every change is saved
/// at once, like the rest of the app.
/// </summary>
internal sealed class AppRulesCard : StackPanel
{
    private const string Keep = "Değiştirme";
    private const string NoLimit = "Sınır yok";
    private const string OwnLimit = "Kendi sınırım";
    private const string PickRunning = "Açık bir programdan ekle…";
    private static readonly (PowerMode Mode, string Label)[] Modes =
        [(PowerMode.Office, "Ofis"), (PowerMode.Gaming, "Oyun"), (PowerMode.HighPerformance, "Yüksek performans")];

    private readonly FanConnection _connection;
    private readonly UserSettingsStore _store = new();
    private readonly IReadOnlyList<int> _rates = DisplayRefresh.FindInternal()?.SupportedHz.OrderBy(h => h).ToList() ?? [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty = InfoPage.Text("Henüz kural yok.", "Caption", top: 10);
    private readonly ComboBox _running = new() { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DispatcherTimer _saveSoon = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Action? _pendingSave;
    private bool _picking;

    public AppRulesCard(FanConnection connection)
    {
        _connection = connection;
        _running.SetResourceReference(StyleProperty, "ThemedCombo");
        _running.ItemsSource = new[] { PickRunning };
        _running.SelectedIndex = 0;
        _running.DropDownOpened += (_, _) => FillRunning();
        _running.SelectionChanged += (_, _) =>
        {
            if (_picking || _running.SelectedItem is not string name || name == PickRunning) return;
            Add(name);
            _picking = true;
            _running.SelectedIndex = 0;
            _picking = false;
        };
        var browse = new Button { Content = "Gözat…", MinWidth = 90 };
        browse.SetResourceReference(StyleProperty, "PlainButton");
        browse.Click += (_, _) => Browse();

        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); _pendingSave?.Invoke(); _pendingSave = null; };

        Children.Add(InfoPage.Text("Bir program açıkken fan profilini, güç modunu, ekran yenileme hızını ve işlemci sınırını değiştirir; " +
                                   "program kapanınca önceki ayarlara döner. Birkaç kurallı program açıksa son açılan geçerlidir.", "Caption", bottom: 12));
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _running, browse } });
        Children.Add(_list);
        Children.Add(_empty);
        Children.Add(InfoPage.Text("Pilde güç modu değişmez (pilde yalnız Ofis açılır). Program açıkken elle değiştirdiğiniz bir ayar, program kapanınca " +
                                   "olduğu gibi kalır. Kurallar simge alanındaki uygulama açıkken çalışır.", "Caption", top: 12));

        _connection.Catalog.Changed += Render;
        // Another process (the tray never writes them, a second window might) changed the rules.
        _store.Changed += _ => Dispatcher.BeginInvoke(Render);
        Unloaded += (_, _) => { _saveSoon.Stop(); _pendingSave?.Invoke(); _pendingSave = null; };
        Render();
    }

    private IReadOnlyList<AppRule> Rules => _store.Current.AppRules;

    private void Save(IReadOnlyList<AppRule> rules) => _store.Save(_store.Current with { AppRules = rules });

    private void Add(string program)
    {
        if (!AppRule.IsProgramName(program)) return;
        if (Rules.Any(r => string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase)) || Rules.Count >= AppRule.MaxRules) return;
        Save([.. Rules, new AppRule(program)]);
        Render();
    }

    private void Replace(string program, AppRule? next)
    {
        var rules = Rules.Select(r => string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase) ? next : r)
            .OfType<AppRule>().ToList();
        Save(rules);
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programlar (*.exe)|*.exe", Title = "Program seçin" };
        if (dialog.ShowDialog() == true) Add(Path.GetFileName(dialog.FileName));
    }

    /// <summary>Programs with a window on screen, by file name (the rest are found with Gözat).</summary>
    private void FillRunning()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id != me && p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0) names.Add(p.ProcessName + ".exe");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // exited meanwhile
            }
        }
        names.RemoveWhere(n => Rules.Any(r => string.Equals(r.Program, n, StringComparison.OrdinalIgnoreCase)));
        _picking = true;
        _running.ItemsSource = new[] { PickRunning }.Concat(names).ToList();
        _running.SelectedIndex = 0;
        _picking = false;
    }

    private void Render()
    {
        _list.Children.Clear();
        foreach (var rule in Rules) _list.Children.Add(RuleBox(rule));
        _empty.Visibility = Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border RuleBox(AppRule rule)
    {
        var profiles = new[] { Keep }.Concat(_connection.Catalog.Profiles.Select(p => Tray.ProfileNames.Label(p.Name))).ToList();
        // Before the profile list arrives, keep the rule's own so an edit of another field does not drop it.
        if (rule.FanProfile is { } saved && !profiles.Contains(Tray.ProfileNames.Label(saved))) profiles.Add(Tray.ProfileNames.Label(saved));
        var profile = Combo(profiles, rule.FanProfile is { } fp ? Tray.ProfileNames.Label(fp) : Keep);
        var mode = Combo([Keep, .. Modes.Select(m => m.Label)], rule.PowerMode is { } pm ? Modes.First(m => m.Mode == pm).Label : Keep);
        // A rate the screen does not list now (another screen in use) stays in the list, so editing another field keeps it.
        var rates = _rates.Union(rule.RefreshHz is { } kept ? [kept] : []).Order().Select(h => $"{h} Hz");
        var rate = Combo([Keep, .. rates], rule.RefreshHz is { } hz ? $"{hz} Hz" : Keep);
        rate.IsEnabled = _rates.Count > 0 || rule.RefreshHz is not null;
        var limit = Combo([Keep, NoLimit, OwnLimit], rule.CpuLimit is null ? Keep : rule.CpuLimit.Limit is null ? NoLimit : OwnLimit);

        var own = rule.CpuLimit?.Limit ?? new CpuLimit(35, 65);
        var pl1 = LimitSlider(CpuLimit.MinPl1, CpuLimit.MaxPl1, own.Pl1Watts);
        var pl2 = LimitSlider(CpuLimit.MinPl2, CpuLimit.MaxPl2, own.Pl2Watts);
        var pl1Text = InfoPage.Text("", "Body");
        var pl2Text = InfoPage.Text("", "Body");
        var limitPanel = new StackPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children = { SliderRow("Uzun süreli sınır", pl1, pl1Text), SliderRow("Kısa süreli sınır", pl2, pl2Text) },
        };

        AppRule Current() => new(rule.Program,
            (string)profile.SelectedItem == Keep ? null
                : Tray.ProfileNames.All.FirstOrDefault(p => p.Label == (string)profile.SelectedItem).Name ?? (string)profile.SelectedItem,
            Modes.FirstOrDefault(m => m.Label == (string)mode.SelectedItem) is { Label: not null } found ? found.Mode : null,
            int.TryParse(((string)rate.SelectedItem).Replace(" Hz", ""), out int hzValue) ? hzValue : null,
            (string)limit.SelectedItem switch
            {
                NoLimit => new CpuLimitOverride(null),
                OwnLimit => new CpuLimitOverride(new CpuLimit((int)pl1.Value, (int)pl2.Value)),
                _ => null,
            });
        void Texts()
        {
            pl1Text.Text = $"{(int)pl1.Value} W";
            pl2Text.Text = $"{(int)pl2.Value} W";
            limitPanel.Visibility = (string)limit.SelectedItem == OwnLimit ? Visibility.Visible : Visibility.Collapsed;
        }
        Texts();

        foreach (var c in new[] { profile, mode, rate, limit })
            c.SelectionChanged += (_, _) => { Texts(); Replace(rule.Program, Current()); };
        // The short-term limit can never be below the long-term one; a drag saves once it rests.
        pl1.ValueChanged += (_, _) => { if (pl2.Value < pl1.Value) pl2.Value = Math.Max(pl1.Value, CpuLimit.MinPl2); Texts(); SaveSoon(() => Replace(rule.Program, Current())); };
        pl2.ValueChanged += (_, _) => { if (pl1.Value > pl2.Value) pl1.Value = pl2.Value; Texts(); SaveSoon(() => Replace(rule.Program, Current())); };

        var remove = new Button { Content = "Kaldır", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        remove.SetResourceReference(StyleProperty, "PlainButton");
        remove.Click += (_, _) => { Replace(rule.Program, null); Render(); };
        var name = InfoPage.Text(rule.Program, "Body");
        name.FontWeight = FontWeights.SemiBold;
        name.VerticalAlignment = VerticalAlignment.Center;
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        header.Children.Add(name);

        var fields = new WrapPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { Field("Fan profili", profile), Field("Güç modu", mode), Field("Yenileme hızı", rate), Field("İşlemci sınırı", limit) },
        };
        var box = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new StackPanel { Children = { header, fields, limitPanel } },
        };
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        return box;
    }

    private void SaveSoon(Action save)
    {
        _pendingSave = save;
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    private static ComboBox Combo(IReadOnlyList<string> items, string selected)
    {
        var c = new ComboBox { MinWidth = 150, ItemsSource = items, SelectedItem = items.Contains(selected) ? selected : items[0] };
        c.SetResourceReference(StyleProperty, "ThemedCombo");
        return c;
    }

    private static StackPanel Field(string label, ComboBox combo) =>
        new() { Margin = new Thickness(0, 0, 14, 6), Children = { InfoPage.Text(label, "Kicker", bottom: 4), combo } };

    private static Slider LimitSlider(int min, int max, int value)
    {
        var s = new Slider
        {
            Minimum = min, Maximum = max, Value = value, TickFrequency = CpuLimit.Step, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
        };
        s.SetResourceReference(StyleProperty, "ThinSlider");
        return s;
    }

    private static Grid SliderRow(string label, Slider slider, TextBlock value)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 6, 0, 0),
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(150) }, new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(60) } },
        };
        var name = InfoPage.Text(label, "Body");
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        value.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }
}
