using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Tray;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Profiller: the fan profile studio. Built-in profiles can be looked at and copied; your own can be drawn
/// (drag points on the curve), given a separate graphics-card curve, saved, deleted and put in or out of
/// the quick-access slots the panel and the tray menu show (docs/09 A4).
/// </summary>
internal sealed class ProfilesPage : UserControl
{
    private sealed class Draft
    {
        public string Name = "";
        public bool IsNew, BuiltIn, IsEcAuto;
        public List<CurvePoint> Cpu = [];
        public List<CurvePoint>? Gpu; // null = the graphics card follows the CPU curve
        public Draft Clone() => new() { Name = Name, IsNew = IsNew, BuiltIn = BuiltIn, IsEcAuto = IsEcAuto, Cpu = [.. Cpu], Gpu = Gpu is null ? null : [.. Gpu] };
    }

    private readonly FanConnection _connection;
    private readonly StackPanel _list = new();
    private readonly TextBox _name = new() { MaxLength = CustomProfile.MaxNameLength, MinWidth = 220, FontSize = 15 };
    private readonly TextBlock _title = InfoPage.Text("", "CardTitle");
    private readonly TextBlock _hint = InfoPage.Text("", "Caption", top: 4, bottom: 10);
    private readonly TextBlock _error = InfoPage.Text("", "Caption", top: 8);
    private readonly CurveEditor _editor = new() { Height = 330, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _separate = new() { Content = "Ekran kartı için ayrı eğri" };
    private readonly CheckBox _quick = new() { Content = "Hızlı erişimde göster" };
    private readonly RadioButton _cpuTab = new() { Content = "İşlemci fanı", GroupName = "curvetab", IsChecked = true };
    private readonly RadioButton _gpuTab = new() { Content = "Ekran kartı fanı", GroupName = "curvetab" };
    private readonly Button _save = new() { Content = "Kaydet" }, _revert = new() { Content = "Geri al" },
        _use = new() { Content = "Bu profili kullan" }, _delete = new() { Content = "Sil" },
        _copy = new() { Content = "Kopyala" }, _new = new() { Content = "Yeni profil" },
        _earlier = new() { Content = "‹ Öne al" }, _later = new() { Content = "Sona al ›" };
    private readonly StackPanel _editorBox = new(), _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

    private Draft? _draft, _saved;
    private bool _loading;

    public ProfilesPage(FanConnection connection)
    {
        _connection = connection;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        foreach (var c in new FrameworkElement[] { _name })
            c.SetResourceReference(Control.ForegroundProperty, "Text");
        _name.SetResourceReference(Control.BackgroundProperty, "Card");
        _name.SetResourceReference(Control.BorderBrushProperty, "Border");
        foreach (var b in new[] { _revert, _delete, _copy, _new, _earlier, _later }) b.SetResourceReference(StyleProperty, "PlainButton");
        foreach (var b in new[] { _save, _use }) b.SetResourceReference(StyleProperty, "AccentButton");
        foreach (var c in new Control[] { _separate, _quick }) c.SetResourceReference(Control.ForegroundProperty, "Text");
        foreach (var t in new[] { _cpuTab, _gpuTab }) { t.SetResourceReference(StyleProperty, "Segment"); t.MinWidth = 130; }
        _tabs.Children.Add(_cpuTab);
        _tabs.Children.Add(_gpuTab);

        var listCard = InfoPage.Card("Profiller", _list,
            Row(new Thickness(0, 12, 0, 0), _new, _copy));
        listCard.Width = 280;
        listCard.VerticalAlignment = VerticalAlignment.Top;

        var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        nameRow.Children.Add(_title);
        nameRow.Children.Add(_name);
        _editorBox.Children.Add(nameRow);
        _editorBox.Children.Add(_hint);
        _editorBox.Children.Add(Row(new Thickness(0, 0, 0, 4), _separate, new Border { Width = 18 }, _quick,
            new Border { Width = 12 }, _earlier, new Border { Width = 6 }, _later));
        _editorBox.Children.Add(_tabs);
        _editorBox.Children.Add(_editor);
        _editorBox.Children.Add(InfoPage.Text("Noktayı sürükleyerek ayarlayın. Boş yere tıklayınca nokta eklenir, sağ tık ya da Delete ile silinir. " +
            "Kırmızı noktalı çizgi, güvenlik için fanın asla inemeyeceği alt sınırdır.", "Caption", top: 8));
        _editorBox.Children.Add(Row(new Thickness(0, 14, 0, 0), _save, new Border { Width = 8 }, _revert, new Border { Width = 8 }, _use,
            new Border { Width = 8 }, _delete));
        _editorBox.Children.Add(_error);
        var editorCard = InfoPage.Card("", _editorBox);
        ((StackPanel)editorCard.Child).Children.RemoveAt(0); // no card title: the profile name is the title

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(editorCard, 2);
        layout.Children.Add(listCard);
        layout.Children.Add(editorCard);

        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Profiller", "PageTitle"),
                InfoPage.Text("Kendi fan profillerinizi çizin; hızlı panelde ve menüde hangilerinin görüneceğini seçin.", "Caption", top: 2, bottom: 18),
                layout,
            },
        };

        _name.TextChanged += (_, _) => { if (!_loading && _draft is { IsNew: true }) { _draft.Name = _name.Text; UpdateButtons(); } };
        _editor.Changed += OnCurveChanged;
        _separate.Checked += (_, _) => OnSeparate(true);
        _separate.Unchecked += (_, _) => OnSeparate(false);
        _cpuTab.Checked += (_, _) => ShowCurve();
        _gpuTab.Checked += (_, _) => ShowCurve();
        _save.Click += async (_, _) => await SaveAsync();
        _revert.Click += (_, _) => { if (_saved is not null) Edit(_saved.Clone()); };
        _use.Click += async (_, _) => await UseAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _copy.Click += (_, _) => StartNew(_draft is { IsEcAuto: false } d ? d.Name + " kopyası" : "Yeni profil", _draft);
        _new.Click += (_, _) => StartNew("Yeni profil", null);
        _quick.Checked += async (_, _) => { if (!_loading) await SetQuickAsync(true); };
        _quick.Unchecked += async (_, _) => { if (!_loading) await SetQuickAsync(false); };
        _earlier.Click += async (_, _) => await MoveSlotAsync(-1);
        _later.Click += async (_, _) => await MoveSlotAsync(+1);

        _connection.Catalog.Changed += OnCatalogChanged;
        _connection.Changed += UpdateLive;
        Loaded += async (_, _) =>
        {
            await _connection.RefreshProfilesAsync();
            RebuildList();
            if (_draft is null) SelectExisting(_connection.Status?.Profile ?? "Balanced");
        };
        RebuildList();
        SelectExisting(_connection.Status?.Profile ?? "Balanced");
    }

    private static StackPanel Row(Thickness margin, params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    // ---- list ----------------------------------------------------------------------------------

    private void RebuildList()
    {
        _list.Children.Clear();
        string group = "profilelist" + GetHashCode();
        // Built-ins first in their usual order (the original setting last of them), then the user's own.
        var order = ProfileNames.All.Select(p => p.Name).ToList();
        foreach (var info in _connection.Catalog.Profiles.OrderBy(p => p.BuiltIn ? order.IndexOf(p.Name) : order.Count))
        {
            bool quick = _connection.Catalog.Slots.Contains(info.Name, StringComparer.OrdinalIgnoreCase);
            var panel = new DockPanel();
            var tag = InfoPage.Text((info.BuiltIn ? "yerleşik" : "kendi profilim") + (quick ? " · ★" : ""), "Caption");
            panel.Children.Add(new TextBlock { Text = ProfileNames.Label(info.Name), FontSize = 13.5 });
            var wrap = new StackPanel { Children = { panel, tag } };
            var button = new RadioButton { GroupName = group, Content = wrap, Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 50 };
            button.SetResourceReference(StyleProperty, "Segment");
            button.IsChecked = _draft is { IsNew: false } d && string.Equals(d.Name, info.Name, StringComparison.OrdinalIgnoreCase);
            string name = info.Name;
            button.Checked += (_, _) => { if (!_loading) SelectExisting(name); };
            _list.Children.Add(button);
        }
    }

    private void OnCatalogChanged()
    {
        _loading = true;
        RebuildList();
        _loading = false;
        // The service's copy changed (saved here, or elsewhere): refresh what is shown unless the user is mid-edit.
        if (_draft is { IsNew: false } d && _saved is not null && SameCurves(d, _saved)) SelectExisting(d.Name);
        UpdateButtons();
    }

    private void SelectExisting(string name)
    {
        var info = _connection.Catalog.Get(name) ?? _connection.Catalog.Profiles.FirstOrDefault(p => p.Name == "Balanced");
        if (info is null) return;
        var d = new Draft
        {
            Name = info.Name, BuiltIn = info.BuiltIn, IsEcAuto = info.Cpu is null,
            Cpu = info.Cpu?.ToList() ?? [],
            Gpu = info.Gpu is { } g && info.Cpu is { } c && !g.SequenceEqual(c) ? g.ToList() : null,
        };
        _saved = d.Clone();
        Edit(d);
        _loading = true;
        RebuildList();
        _loading = false;
    }

    private void StartNew(string name, Draft? from)
    {
        var source = from is { IsEcAuto: false } ? from : null;
        var basis = _connection.Catalog.Get("Balanced");
        var d = new Draft
        {
            Name = name.Length > CustomProfile.MaxNameLength ? name[..CustomProfile.MaxNameLength] : name,
            IsNew = true,
            Cpu = source?.Cpu.ToList() ?? basis?.Cpu?.ToList() ?? [],
            Gpu = source?.Gpu?.ToList(),
        };
        _saved = null;
        Edit(d);
        _loading = true;
        RebuildList();
        _loading = false;
    }

    // ---- editing -------------------------------------------------------------------------------

    private void Edit(Draft d)
    {
        _loading = true;
        _draft = d;
        _error.Text = "";
        _name.Text = d.BuiltIn ? ProfileNames.Label(d.Name) : d.Name;
        _name.IsEnabled = d.IsNew;
        _title.Text = "";
        _title.Margin = new Thickness(0);
        _hint.Text = d.IsEcAuto ? "Orijinal ayar: fanları dizüstünün kendisi yönetir; bu profilin eğrisi yoktur."
            : d.BuiltIn ? "Yerleşik profil değiştirilemez. Kopyala ile kendi profilinizi oluşturup eğriyi düzenleyebilirsiniz."
            : d.IsNew ? "Yeni profil: bir ad verip eğriyi ayarlayın, sonra Kaydet'e basın."
            : "Kendi profiliniz: eğriyi değiştirip Kaydet'e basın.";
        bool editable = !d.BuiltIn;
        _separate.IsEnabled = editable;
        _separate.IsChecked = d.Gpu is not null;
        _tabs.Visibility = d.Gpu is not null ? Visibility.Visible : Visibility.Collapsed;
        _cpuTab.IsChecked = true;
        _editor.Visibility = d.IsEcAuto ? Visibility.Collapsed : Visibility.Visible;
        _separate.Visibility = d.IsEcAuto ? Visibility.Collapsed : Visibility.Visible;
        ShowCurve();
        _loading = false;
        UpdateButtons();
    }

    private bool OnGpuTab => _gpuTab.IsChecked == true && _draft?.Gpu is not null;

    private void ShowCurve()
    {
        if (_draft is null) return;
        var points = OnGpuTab ? _draft.Gpu! : _draft.Cpu;
        _editor.Load(points, editable: !_draft.BuiltIn);
        UpdateLive();
    }

    private void UpdateLive()
    {
        var s = _connection.Status;
        int? temp = s is null ? null : OnGpuTab ? s.GpuTempC : s.CpuTempC;
        _editor.LiveTempC = temp;
        _editor.LiveLabel = temp is { } t ? $"{(OnGpuTab ? "ekran kartı" : "işlemci")} {t}°" : null;
        _editor.InvalidateVisual();
    }

    private void OnCurveChanged()
    {
        if (_draft is null) return;
        var edited = _editor.Points.ToList();
        if (OnGpuTab) _draft.Gpu = edited; else _draft.Cpu = edited;
        UpdateButtons();
    }

    private void OnSeparate(bool on)
    {
        if (_loading || _draft is null || _draft.BuiltIn) return;
        _draft.Gpu = on ? [.. _draft.Cpu] : null;
        _tabs.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _loading = true;
        _cpuTab.IsChecked = true;
        _loading = false;
        ShowCurve();
        UpdateButtons();
    }

    private static bool SameCurves(Draft a, Draft b) =>
        a.Cpu.SequenceEqual(b.Cpu) && (a.Gpu is null ? b.Gpu is null : b.Gpu is not null && a.Gpu.SequenceEqual(b.Gpu));

    private bool Dirty => _draft is { BuiltIn: false } d && (d.IsNew || _saved is null || !SameCurves(d, _saved));

    private void UpdateButtons()
    {
        if (_draft is not { } d) return;
        bool connected = _connection.Connected;
        _save.IsEnabled = connected && !d.BuiltIn && Dirty;
        _revert.IsEnabled = !d.IsNew && Dirty;
        _revert.Visibility = d.BuiltIn || d.IsNew ? Visibility.Collapsed : Visibility.Visible;
        _delete.Visibility = d.BuiltIn || d.IsNew ? Visibility.Collapsed : Visibility.Visible;
        _delete.IsEnabled = connected;
        _use.Visibility = d.IsNew ? Visibility.Collapsed : Visibility.Visible;
        _use.IsEnabled = connected && !Dirty && !string.Equals(_connection.Status?.Profile, d.Name, StringComparison.OrdinalIgnoreCase);
        _use.Content = string.Equals(_connection.Status?.Profile, d.Name, StringComparison.OrdinalIgnoreCase) ? "Şu an kullanılıyor" : "Bu profili kullan";
        _copy.IsEnabled = _new.IsEnabled = connected;
        _copy.Visibility = d.IsEcAuto ? Visibility.Collapsed : Visibility.Visible;

        bool inSlots = !d.IsNew && IndexInSlots(d.Name) >= 0;
        _quick.Visibility = d.IsNew ? Visibility.Collapsed : Visibility.Visible;
        _loading = true;
        _quick.IsChecked = inSlots;
        _loading = false;
        _earlier.Visibility = _later.Visibility = inSlots ? Visibility.Visible : Visibility.Collapsed;
        _earlier.IsEnabled = inSlots && IndexInSlots(d.Name) > 0;
        _later.IsEnabled = inSlots && IndexInSlots(d.Name) < _connection.Catalog.Slots.Count - 1;
    }

    private int IndexInSlots(string name)
    {
        var slots = _connection.Catalog.Slots;
        for (int i = 0; i < slots.Count; i++)
            if (string.Equals(slots[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ---- commands ------------------------------------------------------------------------------

    private void Fail(string text) => _error.Text = text;

    private string? CheckName(Draft d)
    {
        string name = d.Name.Trim();
        if (name.Length == 0) return "Profile bir ad verin.";
        if (name.Length > CustomProfile.MaxNameLength) return $"Ad en fazla {CustomProfile.MaxNameLength} karakter olabilir.";
        if (CustomProfile.IsReserved(name)) return "Bu ad yerleşik bir profile ait; başka bir ad seçin.";
        if (d.IsNew && _connection.Catalog.Get(name) is not null) return "Bu adda bir profil zaten var; başka bir ad seçin.";
        return null;
    }

    private async Task SaveAsync()
    {
        if (_draft is not { BuiltIn: false } d) return;
        if (CheckName(d) is { } problem) { Fail(problem); return; }
        var profile = new CustomProfile(d.Name.Trim(), d.Cpu, d.Gpu);
        var r = await _connection.RequestAsync(new FanRequest("saveProfile", Definition: profile));
        if (r is null) { Fail("Fan servisine ulaşılamadı."); return; }
        if (!r.Ok) { Fail("Profil kaydedilemedi. Eğrinin sıcaklık arttıkça düşmediğinden ve en az 2, en çok 12 nokta olduğundan emin olun."); return; }
        string saved = profile.Name.Trim();
        // A new profile goes into the quick-access slots when there is room, so it shows up in the panel.
        SelectExisting(saved);
        _error.Text = "";
    }

    private async Task UseAsync()
    {
        if (_draft is not { } d) return;
        var r = await _connection.RequestAsync(new FanRequest("setProfile", Profile: d.Name));
        if (r is null) Fail("Fan servisine ulaşılamadı.");
        else if (!r.Ok) Fail("Profil değiştirilemedi.");
        else _error.Text = "";
        UpdateButtons();
    }

    private async Task DeleteAsync()
    {
        if (_draft is not { BuiltIn: false, IsNew: false } d) return;
        if (!ConfirmDialog.Ask(this, "Profili sil", $"\"{d.Name}\" profili silinsin mi?" +
            (string.Equals(_connection.Status?.Profile, d.Name, StringComparison.OrdinalIgnoreCase) ? "\nŞu an bu profil kullanılıyor; silince Dengeli'ye geçilir." : ""),
            "Sil", danger: true)) return;
        var r = await _connection.RequestAsync(new FanRequest("deleteProfile", Profile: d.Name));
        if (r is null) { Fail("Fan servisine ulaşılamadı."); return; }
        if (!r.Ok) { Fail("Profil silinemedi."); return; }
        SelectExisting(_connection.Status?.Profile ?? "Balanced");
    }

    private async Task SetQuickAsync(bool on)
    {
        if (_draft is not { IsNew: false } d) return;
        var slots = _connection.Catalog.Slots.ToList();
        int at = IndexInSlots(d.Name);
        if (on && at < 0)
        {
            if (slots.Count >= ServiceSettings.MaxSlots) { Fail($"Hızlı erişimde en fazla {ServiceSettings.MaxSlots} profil olabilir; önce birini çıkarın."); UpdateButtons(); return; }
            slots.Add(d.Name);
        }
        else if (!on && at >= 0)
        {
            if (slots.Count == 1) { Fail("Hızlı erişimde en az bir profil kalmalı."); UpdateButtons(); return; }
            slots.RemoveAt(at);
        }
        else return;
        await SendSlotsAsync(slots);
    }

    private async Task MoveSlotAsync(int delta)
    {
        if (_draft is not { } d) return;
        var slots = _connection.Catalog.Slots.ToList();
        int at = IndexInSlots(d.Name), to = at + delta;
        if (at < 0 || to < 0 || to >= slots.Count) return;
        (slots[at], slots[to]) = (slots[to], slots[at]);
        await SendSlotsAsync(slots);
    }

    private async Task SendSlotsAsync(List<string> slots)
    {
        var r = await _connection.RequestAsync(new FanRequest("setSlots", Names: slots));
        if (r is null) Fail("Fan servisine ulaşılamadı.");
        else if (!r.Ok) Fail("Hızlı erişim kaydedilemedi.");
        else _error.Text = "";
        UpdateButtons();
        _loading = true;
        RebuildList();
        _loading = false;
    }
}
