using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Excalibur.App.Tray;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>
/// Profiller, as on Windows: the fan profile studio. Built-in profiles can be looked at and copied; your own can be drawn
/// (drag points on the curve), given a separate graphics-card curve, saved, deleted and put in or out of the quick-access
/// slots the panel and the tray menu show.
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
    private readonly TextBlock _hint = Kit.Text("", "Caption", top: 4, bottom: 10);
    private readonly TextBlock _error = Kit.Text("", "Caption", top: 8);
    private readonly CurveEditor _editor = new() { Height = 330, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _separate = new() { Content = "Ekran kartı için ayrı eğri" };
    private readonly CheckBox _quick = new() { Content = "Hızlı erişimde göster" };
    private readonly RadioButton _cpuTab, _gpuTab;
    private readonly Button _save = Kit.Button("Kaydet", "AccentButton"), _revert = Kit.Button("Geri al"),
        _use = Kit.Button("Bu profili kullan", "AccentButton"), _delete = Kit.Button("Sil"),
        _copy = Kit.Button("Kopyala"), _new = Kit.Button("Yeni profil"),
        _earlier = Kit.Button("‹ Öne al"), _later = Kit.Button("Sona al ›");
    private readonly StackPanel _editorBox = new(), _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

    private Draft? _draft, _saved;
    private bool _loading;

    public ProfilesPage(FanConnection connection)
    {
        _connection = connection;
        _error.Res(TextBlock.ForegroundProperty, "Bad");
        _cpuTab = Kit.Segment("İşlemci fanı", "curvetab" + GetHashCode());
        _gpuTab = Kit.Segment("Ekran kartı fanı", "curvetab" + GetHashCode());
        _cpuTab.IsChecked = true;
        foreach (var t in new[] { _cpuTab, _gpuTab }) t.MinWidth = 130;
        _tabs.Children.Add(_cpuTab);
        _tabs.Children.Add(_gpuTab);

        var listCard = Kit.Card("Profiller", _list, Row(new Thickness(0, 12, 0, 0), _new, Gap(8), _copy));
        listCard.Width = 280;
        listCard.VerticalAlignment = VerticalAlignment.Top;

        _editorBox.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 4), Children = { _name } });
        _editorBox.Children.Add(_hint);
        _editorBox.Children.Add(Row(new Thickness(0, 0, 0, 4), _separate, Gap(18), _quick, Gap(12), _earlier, Gap(6), _later));
        _editorBox.Children.Add(_tabs);
        _editorBox.Children.Add(_editor);
        _editorBox.Children.Add(Kit.Text("Noktayı sürükleyerek ayarlayın. Boş yere tıklayınca nokta eklenir, sağ tık ya da Delete ile silinir. " +
            "Kırmızı noktalı çizgi, güvenlik için fanın asla inemeyeceği alt sınırdır.", "Caption", top: 8));
        _editorBox.Children.Add(Row(new Thickness(0, 14, 0, 0), _save, Gap(8), _revert, Gap(8), _use, Gap(8), _delete));
        _editorBox.Children.Add(_error);
        // no card title: the profile name is the title
        var editorCard = new BracketCard { Child = _editorBox, Margin = new Thickness(0, 0, 0, 14) }.Classed("CardBox");
        Grid.SetColumn(editorCard, 2);

        Content = new StackPanel
        {
            Children =
            {
                Kit.Text("Profiller", "PageTitle"),
                Kit.Text("Kendi fan profillerinizi çizin; hızlı panelde ve menüde hangilerinin görüneceğini seçin.", "Caption", top: 2, bottom: 18),
                new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,14,*"), Children = { listCard, editorCard } },
            },
        };

        _name.TextChanged += (_, _) => { if (!_loading && _draft is { IsNew: true }) { _draft.Name = _name.Text ?? ""; UpdateButtons(); } };
        _editor.Changed += OnCurveChanged;
        _separate.IsCheckedChanged += (_, _) => OnSeparate(_separate.IsChecked == true);
        _cpuTab.IsCheckedChanged += (_, _) => { if (_cpuTab.IsChecked == true) ShowCurve(); };
        _gpuTab.IsCheckedChanged += (_, _) => { if (_gpuTab.IsChecked == true) ShowCurve(); };
        _save.Click += async (_, _) => await SaveAsync();
        _revert.Click += (_, _) => { if (_saved is not null) Edit(_saved.Clone()); };
        _use.Click += async (_, _) => await UseAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _copy.Click += (_, _) => StartNew(_draft is { IsEcAuto: false } d ? d.Name + " kopyası" : "Yeni profil", _draft);
        _new.Click += (_, _) => StartNew("Yeni profil", null);
        _quick.IsCheckedChanged += async (_, _) => { if (!_loading) await SetQuickAsync(_quick.IsChecked == true); };
        _earlier.Click += async (_, _) => await MoveSlotAsync(-1);
        _later.Click += async (_, _) => await MoveSlotAsync(+1);

        _connection.Catalog.Changed += OnCatalogChanged;
        _connection.Changed += UpdateLive;
        AttachedToVisualTree += async (_, _) =>
        {
            await _connection.RefreshProfilesAsync();
            RebuildList();
            if (_draft is null) SelectExisting(_connection.Status?.Profile ?? "Balanced");
        };
        RebuildList();
        SelectExisting(_connection.Status?.Profile ?? "Balanced");
    }

    private static Control Gap(double width) => new Border { Width = width };

    private static StackPanel Row(Thickness margin, params Control[] items)
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
            var tag = Kit.Text((info.BuiltIn ? "yerleşik" : "kendi profilim") + (quick ? " · ★" : ""), "Caption");
            tag.HorizontalAlignment = HorizontalAlignment.Center;
            var label = new TextBlock { Text = ProfileNames.Label(info.Name), FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Center };
            var button = Kit.Segment(new StackPanel { Children = { label, tag } }, group);
            button.Margin = new Thickness(0, 0, 0, 6);
            button.MinHeight = 50;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.IsChecked = _draft is { IsNew: false } d && string.Equals(d.Name, info.Name, StringComparison.OrdinalIgnoreCase);
            string name = info.Name;
            button.IsCheckedChanged += (_, _) => { if (!_loading && button.IsChecked == true) SelectExisting(name); };
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
        _hint.Text = d.IsEcAuto ? "Orijinal ayar: fanları dizüstünün kendisi yönetir; bu profilin eğrisi yoktur."
            : d.BuiltIn ? "Yerleşik profil değiştirilemez. Kopyala ile kendi profilinizi oluşturup eğriyi düzenleyebilirsiniz."
            : d.IsNew ? "Yeni profil: bir ad verip eğriyi ayarlayın, sonra Kaydet'e basın."
            : "Kendi profiliniz: eğriyi değiştirip Kaydet'e basın.";
        _separate.IsEnabled = !d.BuiltIn;
        _separate.IsChecked = d.Gpu is not null;
        _tabs.IsVisible = d.Gpu is not null;
        _cpuTab.IsChecked = true;
        _editor.IsVisible = !d.IsEcAuto;
        _separate.IsVisible = !d.IsEcAuto;
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
        _tabs.IsVisible = on;
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
        bool inUse = string.Equals(_connection.Status?.Profile, d.Name, StringComparison.OrdinalIgnoreCase);
        _save.IsEnabled = connected && !d.BuiltIn && Dirty;
        _revert.IsEnabled = !d.IsNew && Dirty;
        _revert.IsVisible = !(d.BuiltIn || d.IsNew);
        _delete.IsVisible = !(d.BuiltIn || d.IsNew);
        _delete.IsEnabled = connected;
        _use.IsVisible = !d.IsNew;
        _use.IsEnabled = connected && !Dirty && !inUse;
        _use.Content = inUse ? "Şu an kullanılıyor" : "Bu profili kullan";
        _copy.IsEnabled = _new.IsEnabled = connected;
        _copy.IsVisible = !d.IsEcAuto;

        bool inSlots = !d.IsNew && IndexInSlots(d.Name) >= 0;
        _quick.IsVisible = !d.IsNew;
        _loading = true;
        _quick.IsChecked = inSlots;
        _loading = false;
        _earlier.IsVisible = _later.IsVisible = inSlots;
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
        SelectExisting(profile.Name.Trim());
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
        if (!await ConfirmDialog.Ask(this, "Profili sil", $"\"{d.Name}\" profili silinsin mi?" +
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
