using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.History;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Linux.Ui;

/// <summary>
/// The main desktop window for Linux (L3): sidebar navigation, 6 feature tabs matching the Windows app, live status bar,
/// and PNG snapshot export for automated visual verification.
/// </summary>
internal sealed class LinuxMainWindow : Window
{
    private static readonly (string Key, string Title, bool Bottom)[] Tabs =
    [
        ("overview", "Panel", false),
        ("cooling", "Soğutma", false),
        ("power", "Performans", false),
        ("lighting", "Aydınlatma", false),
        ("battery", "Pil & Geçmiş", false),
        ("about", "Hakkında", true),
    ];

    private static readonly (string Name, string Hex)[] ColorSwatches =
    [
        ("Camgöbeği", "#00FFFF"), ("Kızıl", "#E84855"), ("Turuncu", "#FF6A00"),
        ("Sarı", "#FFD000"), ("Yeşil", "#00E676"), ("Mavi", "#0070FF"),
        ("Mor", "#9D4EDD"), ("Pembe", "#FF0080"), ("Beyaz", "#FFFFFF"),
    ];

    private readonly LinuxGuiClient _client;
    private readonly bool _hideToTrayOnClose;
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly ScrollViewer _scroll;
    private readonly ContentControl _pageHost;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly TextBlock _updatedText;
    private readonly TextBlock _bannerError;
    private readonly DispatcherTimer _timer;

    private string _activeTab = "overview";
    private int _selectedLightZone = -1; // -1 = all, 0 = left, 1 = middle, 2 = right
    private string _customProfileName = "Gece";
    private string _customProfileCurve = "45:25, 60:40, 70:70, 80:100";
    private int _manualPercent = 60;
    private int _manualMinutes = 10;
    private int _cpuPl1Input = 35;
    private int _cpuPl2Input = 60;
    private int _gpuWattsInput = 65;
    private string? _infoBanner;
    private bool _allowExit;
    private bool _limitInputsSeeded;

    public LinuxMainWindow(LinuxGuiClient client, bool hideToTrayOnClose, Stream? iconStream)
    {
        _client = client;
        _hideToTrayOnClose = hideToTrayOnClose;

        Title = "Excalibur Reimagined";
        Width = 1180;
        Height = 780;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = LinuxTheme.Bg;
        Foreground = LinuxTheme.Text;
        if (iconStream is not null)
        {
            iconStream.Position = 0;
            Icon = new WindowIcon(iconStream);
        }

        // Sidebar
        var navTop = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0) };
        var navBottom = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 12) };
        DockPanel.SetDock(navBottom, Dock.Bottom);

        var brand = new StackPanel
        {
            Margin = new Thickness(22, 20, 16, 22),
            Children =
            {
                new TextBlock
                {
                    Text = "Excalibur",
                    FontSize = 26,
                    FontWeight = FontWeight.Bold,
                    Foreground = LinuxTheme.Accent,
                },
                new TextBlock
                {
                    Text = "R E I M A G I N E D   ·   L I N U X",
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = LinuxTheme.SubText,
                    Margin = new Thickness(2, 2, 0, 8),
                },
                new Border
                {
                    Height = 2,
                    Width = 160,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = LinuxTheme.Accent,
                    CornerRadius = new CornerRadius(1),
                },
            },
        };
        DockPanel.SetDock(brand, Dock.Top);

        foreach (var (key, title, bottom) in Tabs)
        {
            string tabKey = key;
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold },
            };
            btn.Click += (_, _) => SelectTab(tabKey);
            _navButtons[tabKey] = btn;
            (bottom ? navBottom : navTop).Children.Add(btn);
        }

        var sidebar = new Border
        {
            Background = LinuxTheme.Sidebar,
            BorderBrush = LinuxTheme.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { brand, navBottom, navTop },
            },
        };

        // Main content area
        _bannerError = new TextBlock
        {
            Foreground = LinuxTheme.Bad,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(30, 14, 30, 0),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(_bannerError, Dock.Top);

        _pageHost = new ContentControl { Margin = new Thickness(30, 22, 30, 26) };
        _scroll = new ScrollViewer { Content = _pageHost };

        var rightPane = new DockPanel
        {
            LastChildFill = true,
            Children = { _bannerError, _scroll },
        };

        // Status bar
        _statusDot = new Ellipse { Width = 8, Height = 8, Fill = LinuxTheme.Off, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _statusText = new TextBlock { FontSize = 12, Foreground = LinuxTheme.SubText, VerticalAlignment = VerticalAlignment.Center };
        _updatedText = new TextBlock { FontSize = 12, Foreground = LinuxTheme.SubText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(_updatedText, Dock.Right);

        var statusBar = new Border
        {
            Background = LinuxTheme.Sidebar,
            BorderBrush = LinuxTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 0),
            Child = new DockPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _statusDot, _updatedText, _statusText },
            },
        };

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("228,*"),
            RowDefinitions = new RowDefinitions("*,32"),
        };
        Grid.SetColumn(sidebar, 0);
        Grid.SetRow(sidebar, 0);
        Grid.SetColumn(rightPane, 1);
        Grid.SetRow(rightPane, 0);
        Grid.SetColumn(statusBar, 0);
        Grid.SetColumnSpan(statusBar, 2);
        Grid.SetRow(statusBar, 1);
        root.Children.Add(sidebar);
        root.Children.Add(rightPane);
        root.Children.Add(statusBar);
        Content = root;

        _client.Changed += () => Dispatcher.UIThread.Post(RefreshUi);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _client.Poll();

        Opened += (_, _) =>
        {
            _client.Poll();
            SelectTab(_activeTab);
            _timer.Start();
        };

        Closing += (_, e) =>
        {
            if (_hideToTrayOnClose && !_allowExit)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    public void ExitApplication()
    {
        _allowExit = true;
        _timer.Stop();
        Close();
    }

    public void SelectTab(string tabKey)
    {
        _activeTab = tabKey;
        foreach (var (k, btn) in _navButtons)
        {
            bool active = k == tabKey;
            btn.Background = active ? LinuxTheme.AccentSoft : Brushes.Transparent;
            btn.BorderBrush = active ? LinuxTheme.Accent : Brushes.Transparent;
            if (btn.Content is TextBlock tb)
                tb.Foreground = active ? Brushes.White : LinuxTheme.Text;
        }
        RefreshUi();
    }

    public async Task SnapshotAndCloseAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        _timer.Stop();
        await Task.Run(() => _client.SendNow(new LinuxRequest("guiStatus", Minutes: _client.HistoryMinutes)));
        foreach (var (key, _, _) in Tabs)
        {
            SelectTab(key);
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Measure(new Size(Width, Height));
            Arrange(new Rect(0, 0, Width, Height));
            UpdateLayout();

            var pixelSize = new PixelSize((int)Width, (int)Height);
            using var rtb = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            rtb.Render(this);
            rtb.Save(System.IO.Path.Combine(outputDir, $"{key}.png"));
        }
        ExitApplication();
    }

    private void RefreshUi()
    {
        RenderStatusBar();
        var s = _client.Status;
        if (!_limitInputsSeeded && s?.Power is { } seed)
        {
            // The sliders start from the limits in force, once the first answer has arrived.
            _limitInputsSeeded = true;
            if (seed.CpuLimit is { } cl)
            {
                _cpuPl1Input = cl.Pl1Watts;
                _cpuPl2Input = cl.Pl2Watts;
            }
            if (seed.GpuLimitWatts is { } gw) _gpuWattsInput = gw;
        }
        if (s?.Error is { Length: > 0 } err)
        {
            _bannerError.Text = $"Hata: {err}";
            _bannerError.Foreground = LinuxTheme.Bad;
            _bannerError.IsVisible = true;
        }
        else if (_infoBanner is { Length: > 0 } info)
        {
            _bannerError.Text = info;
            _bannerError.Foreground = LinuxTheme.Ok;
            _bannerError.IsVisible = true;
        }
        else
        {
            _bannerError.IsVisible = false;
        }

        _pageHost.Content = _activeTab switch
        {
            "overview" => BuildOverviewPage(s),
            "cooling" => BuildCoolingPage(s),
            "power" => BuildPowerPage(s),
            "lighting" => BuildLightingPage(s),
            "battery" => BuildBatteryAndHistoryPage(s),
            "about" => BuildAboutPage(s),
            _ => BuildOverviewPage(s),
        };
    }

    private void RenderStatusBar()
    {
        var s = _client.Status;
        if (!_client.Connected || s is null)
        {
            _statusDot.Fill = LinuxTheme.Bad;
            _statusText.Text = "Fan servisine ulaşılamıyor. Çalışıyor mu? sudo systemctl status excalibur-reimagined";
            _updatedText.Text = "";
            return;
        }

        (string state, IBrush dot) = s.Mode switch
        {
            "Running" => ("Fan kontrolü açık", LinuxTheme.Ok),
            "MonitorOnly" => ("Fanlar yalnız izleniyor (kısa deneme gerekiyor)", LinuxTheme.Warn),
            "Faulted" => ("Sorun var: fanlar dizüstünün kendi ayarına bırakıldı", LinuxTheme.Bad),
            _ => ($"Durum: {s.Mode}", LinuxTheme.Off),
        };
        _statusDot.Fill = dot;
        _statusText.Text = $"{state}{(s.Simulated ? " (deneme modu)" : "")}   ·   Profil: {ProfileLabel(s.Profile)}   ·   EC: {s.EcVersion ?? "bilinmiyor"}";
        _updatedText.Text = _client.LastUpdated is { } at ? $"güncellendi {at:HH:mm:ss}" : "";
    }

    // =========================================================================
    // 1. OVERVIEW PAGE
    // =========================================================================
    private Control BuildOverviewPage(LinuxStatus? s)
    {
        var page = new StackPanel { Spacing = 4 };
        page.Children.Add(LinuxTheme.SectionTitle("Panel", "Canlı sıcaklıklar, fanlar ve hızlı kontroller"));

        if (s is null)
        {
            page.Children.Add(LinuxTheme.MakeCard("Bağlantı Yok",
                LinuxTheme.Caption("Arka plan servisine ulaşılamadı. Servisi başlatmak için terminalde `sudo systemctl start excalibur-reimagined` çalıştırın.")));
            return page;
        }

        // Sensor row (CPU & GPU cards side by side)
        var sensorGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
        string cpuPower = s.Power?.CpuWatts is { } cw ? $"{cw} W" : "-";
        string cpuLim = s.Power?.CpuLimit is { } cl ? $"özel sınır: {cl.Pl1Watts}/{cl.Pl2Watts} W"
            : (s.Power?.CpuPl1Watts is { } pl1 ? $"sınır: {pl1}/{s.Power.CpuPl2Watts} W" : "");
        var cpuCard = BuildSensorCard("İşlemci (CPU)", s.CpuTempC, s.CpuPercent, s.CpuRpm, $"{cpuPower}  {cpuLim}".Trim());

        string gpuPower = s.Power?.GpuPowerWatts is { } gw ? $"{gw} W" : "-";
        string gpuLim = s.Power?.GpuLimitWatts is { } gl ? $"özel hedef: {gl} W"
            : (s.Power?.GpuTgpWatts is { } tgp ? $"tavan: {tgp} W" : "");
        var gpuCard = BuildSensorCard("Ekran Kartı (GPU)", s.GpuTempC, s.GpuPercent, s.GpuRpm, $"{gpuPower}  {gpuLim}".Trim());

        Grid.SetColumn(cpuCard, 0);
        Grid.SetColumn(gpuCard, 2);
        sensorGrid.Children.Add(cpuCard);
        sensorGrid.Children.Add(gpuCard);
        page.Children.Add(sensorGrid);

        // Quick Fan Profile selector
        var profileWrap = new WrapPanel();
        var profiles = s.Profiles ??
        [
            new("Quiet", true, null, null),
            new("Balanced", true, null, null),
            new("Performance", true, null, null),
            new("EcAuto", true, null, null),
        ];
        foreach (var p in profiles)
        {
            string name = p.Name;
            bool active = string.Equals(s.Profile, name, StringComparison.OrdinalIgnoreCase);
            profileWrap.Children.Add(LinuxTheme.MakeChoiceButton(
                ProfileLabel(name), active,
                () => { _infoBanner = null; _client.Send(new LinuxRequest("setProfile", Profile: name)); }));
        }
        page.Children.Add(LinuxTheme.MakeCard("Fan Profili",
            LinuxTheme.Caption(s.HoldsFans ? "Fanlar seçtiğiniz eğri ve güvenlik tabanlarıyla yönetiliyor." : "Fanlar şu an dizüstünün kendi tablosunda."),
            profileWrap));

        // Quick Power Mode selector
        if (s.Power is { } pwr)
        {
            var modeWrap = new WrapPanel();
            foreach (var (mode, label, desc) in new[]
            {
                (PowerMode.Office, "Ofis", "Sessiz ve serin (~35 W CPU / ~75 W GPU)"),
                (PowerMode.Gaming, "Oyun", "Dengeli oyun gücü (~45 W CPU / ~85 W GPU)"),
                (PowerMode.HighPerformance, "Yüksek performans", "En yüksek güç (~55 W CPU / ~100 W GPU)"),
            })
            {
                PowerMode target = mode;
                bool active = pwr.Mode == target;
                modeWrap.Children.Add(LinuxTheme.MakeChoiceButton(label, active,
                    () => { _infoBanner = null; _client.Send(new LinuxRequest("setPowerMode", PowerMode: target)); }, desc));
            }
            string lockNote = pwr.OnBattery
                ? $"Pilde çalışıyor (%{pwr.BatteryPercent ?? 0}) — pil koruması için Ofis modunda kilitli (priz tercihi: {PowerModeLabel(pwr.Preferred)})."
                : $"Prizde çalışıyor{(pwr.BatteryPercent is { } bp ? $" (pil %{bp})" : "")}.";
            page.Children.Add(LinuxTheme.MakeCard("Güç Modu", LinuxTheme.Caption(lockNote), modeWrap));
        }

        // Recent events card
        var eventsStack = new StackPanel { Spacing = 4 };
        if (s.Events.Count == 0)
            eventsStack.Children.Add(LinuxTheme.Caption("Henüz kayıtlı olay yok."));
        else
            foreach (var ev in s.Events)
                eventsStack.Children.Add(new TextBlock { Text = "• " + ev, FontSize = 12, Foreground = LinuxTheme.SubText });

        page.Children.Add(LinuxTheme.MakeCard("Son Olaylar", eventsStack));
        return page;
    }

    private static Border BuildSensorCard(string title, int? tempC, int? fanPct, int? rpm, string powerLine)
    {
        var tempText = new TextBlock
        {
            Text = tempC is { } t ? $"{t} °C" : "— °C",
            FontSize = 34,
            FontWeight = FontWeight.Bold,
            Foreground = LinuxTheme.TempBrush(tempC),
        };
        var fanText = new TextBlock
        {
            Text = $"Fan: %{(fanPct?.ToString(CultureInfo.InvariantCulture) ?? "-")}   ·   {(rpm?.ToString(CultureInfo.InvariantCulture) ?? "-")} devir/dk",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = LinuxTheme.Text,
        };
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = fanPct ?? 0,
            Height = 8,
            Foreground = LinuxTheme.TempBrush(tempC),
            Background = LinuxTheme.Sidebar,
        };
        return LinuxTheme.MakeCard(title, tempText, fanText, bar, LinuxTheme.Caption(powerLine));
    }

    // =========================================================================
    // 2. COOLING & PROFILES PAGE
    // =========================================================================
    private Control BuildCoolingPage(LinuxStatus? s)
    {
        var page = new StackPanel { Spacing = 4 };
        page.Children.Add(LinuxTheme.SectionTitle("Soğutma ve Profiller", "Fan eğrisi, özel profiller ve geçici sabit hız"));

        var activeProfile = s?.Profiles?.FirstOrDefault(p => string.Equals(p.Name, s.Profile, StringComparison.OrdinalIgnoreCase));
        var canvas = new FanCurveCanvas
        {
            Points = activeProfile?.Cpu ?? FanProfile.Balanced.CpuCurve.Points,
            CpuTempC = s?.CpuTempC,
            CpuPercent = s?.CpuPercent,
            GpuTempC = s?.GpuTempC,
            GpuPercent = s?.GpuPercent,
        };

        var profileWrap = new WrapPanel();
        if (s?.Profiles is { } list)
        {
            foreach (var p in list)
            {
                var prof = p;
                bool active = string.Equals(s.Profile, prof.Name, StringComparison.OrdinalIgnoreCase);
                string sub = prof.BuiltIn ? "Yerleşik" : "Özel profil";
                profileWrap.Children.Add(LinuxTheme.MakeChoiceButton(ProfileLabel(prof.Name), active, () =>
                {
                    if (!prof.BuiltIn && prof.Cpu is { Count: > 0 })
                    {
                        _customProfileName = prof.Name;
                        _customProfileCurve = string.Join(", ", prof.Cpu.Select(pt => $"{pt.TempC}:{pt.Percent}"));
                    }
                    _client.Send(new LinuxRequest("setProfile", Profile: prof.Name));
                }, sub));
            }
        }

        page.Children.Add(LinuxTheme.MakeCard(
            $"Aktif Fan Eğrisi — {ProfileLabel(s?.Profile ?? "Balanced")}",
            LinuxTheme.Caption("Turuncu kesikli çizgi güvenlik tabanıdır (fanlar %25'in altına inmez; 70 °C'de %80, 78 °C'de %90, 80 °C'de 3 sn kalırsa %100). Camgöbeği nokta işlemciyi, yeşil nokta ekran kartını gösterir."),
            canvas,
            profileWrap));

        // Custom profile editor card
        var nameBox = new TextBox { Text = _customProfileName, Width = 180, Watermark = "Profil adı (ör. Gece)" };
        nameBox.TextChanged += (_, _) => _customProfileName = nameBox.Text ?? "";

        var curveBox = new TextBox { Text = _customProfileCurve, Width = 340, Watermark = "45:25, 60:40, 70:70, 80:100" };
        curveBox.TextChanged += (_, _) => _customProfileCurve = curveBox.Text ?? "";

        var customRow = new WrapPanel
        {
            Children =
            {
                new StackPanel { Margin = new Thickness(0, 0, 12, 8), Children = { LinuxTheme.Caption("Profil adı"), nameBox } },
                new StackPanel { Margin = new Thickness(0, 0, 12, 8), Children = { LinuxTheme.Caption("Eğri noktaları (sıcaklık:yüzde)"), curveBox } },
            },
        };

        var customBtns = new WrapPanel
        {
            Children =
            {
                LinuxTheme.MakeActionButton("Özel Profili Kaydet ve Etkinleştir", () =>
                {
                    if (TryParseCurvePoints(_customProfileCurve, out var pts, out string? err))
                    {
                        _infoBanner = $"'{_customProfileName.Trim()}' profili kaydedildi.";
                        _client.Send(new LinuxRequest("saveProfile", CustomProfile: new CustomProfile(_customProfileName.Trim(), pts, null)));
                        _client.Send(new LinuxRequest("setProfile", Profile: _customProfileName.Trim()));
                    }
                    else
                    {
                        _infoBanner = null;
                        _bannerError.Text = $"Hata: {err}";
                        _bannerError.Foreground = LinuxTheme.Bad;
                        _bannerError.IsVisible = true;
                    }
                }, primary: true),
                LinuxTheme.MakeActionButton("Seçili Özel Profili Sil", () =>
                {
                    _infoBanner = null;
                    _client.Send(new LinuxRequest("deleteProfile", Profile: _customProfileName.Trim()));
                }),
            },
        };
        page.Children.Add(LinuxTheme.MakeCard("Özel Fan Profili Oluştur / Düzenle",
            LinuxTheme.Caption("En az 2 nokta girin (30–95 °C, %25–100; ör. 45:25, 60:40, 70:70, 80:100)."),
            customRow, customBtns));

        // Manual speed card
        var pctLabel = new TextBlock { Text = $"Sabit hız: %{_manualPercent}", FontWeight = FontWeight.SemiBold };
        var pctSlider = new Slider { Minimum = 25, Maximum = 100, Value = _manualPercent, Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        pctSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _manualPercent = (int)Math.Round(pctSlider.Value);
                pctLabel.Text = $"Sabit hız: %{_manualPercent}";
            }
        };
        var minWrap = new WrapPanel();
        foreach (int m in new[] { 2, 5, 10 })
        {
            int mins = m;
            minWrap.Children.Add(LinuxTheme.MakeChoiceButton($"{mins} dk", _manualMinutes == mins, () =>
            {
                _manualMinutes = mins;
                RefreshUi();
            }));
        }
        var manualActions = new WrapPanel
        {
            Children =
            {
                LinuxTheme.MakeActionButton("Geçici Sabit Hızı Uygula", () =>
                {
                    _infoBanner = $"Fanlar {_manualMinutes} dakika boyunca %{_manualPercent} hızına sabitlendi.";
                    _client.Send(new LinuxRequest("setManual", CpuPercent: _manualPercent, GpuPercent: _manualPercent, Minutes: _manualMinutes));
                }, primary: true),
                LinuxTheme.MakeActionButton("Normal Profile Dön", () =>
                {
                    _infoBanner = null;
                    _client.Send(new LinuxRequest("setProfile", Profile: s?.Profile ?? "Balanced"));
                }),
            },
        };
        page.Children.Add(LinuxTheme.MakeCard("Geçici Sabit Fan Hızı (Manuel)",
            LinuxTheme.Caption("Süre dolduğunda fanlar otomatik olarak seçili profile döner; 80 °C üzeri güvenlik kuralı yine geçerlidir."),
            pctLabel, pctSlider, minWrap, manualActions));

        return page;
    }

    // =========================================================================
    // 3. POWER & AUTOMATION PAGE
    // =========================================================================
    private Control BuildPowerPage(LinuxStatus? s)
    {
        var page = new StackPanel { Spacing = 4 };
        page.Children.Add(LinuxTheme.SectionTitle("Performans ve Otomasyon", "Güç modu, işlemci/ekran kartı watt sınırları ve şarj/pil kuralları"));

        var pwr = s?.Power;
        var modeWrap = new WrapPanel();
        foreach (var (mode, label, sub) in new[]
        {
            (PowerMode.Office, "Ofis", "İşlemci 35 W"),
            (PowerMode.Gaming, "Oyun", "İşlemci 45 W"),
            (PowerMode.HighPerformance, "Yüksek performans", "İşlemci 55 W"),
        })
        {
            PowerMode target = mode;
            modeWrap.Children.Add(LinuxTheme.MakeChoiceButton(label, pwr?.Mode == target,
                () => { _infoBanner = null; _client.Send(new LinuxRequest("setPowerMode", PowerMode: target)); }, sub));
        }
        page.Children.Add(LinuxTheme.MakeCard("Donanım Güç Modu",
            LinuxTheme.Caption(pwr?.OnBattery == true
                ? "Bilgisayar şu an pilde çalıştığı için Ofis moduna kilitlidir. Şarja takıldığında seçtiğiniz priz tercihine geçer."
                : "Linux'ta güç modu şimdilik işlemci gücünü ve dizüstünün kendi fan ayarını değiştirir; ekran kartının watt tavanı değişmez. Ekran kartını sınırlamak için aşağıdaki watt hedefini kullanın."),
            modeWrap));

        // CPU Limit card (PL1 / PL2)
        var pl1Text = new TextBlock { Text = $"Uzun süreli sınır (PL1): {_cpuPl1Input} W", FontWeight = FontWeight.SemiBold };
        var pl2Text = new TextBlock { Text = $"Kısa süreli sınır (PL2): {_cpuPl2Input} W", FontWeight = FontWeight.SemiBold };
        var pl1Slider = new Slider { Minimum = 15, Maximum = 55, Value = _cpuPl1Input, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var pl2Slider = new Slider { Minimum = 30, Maximum = 115, Value = _cpuPl2Input, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        pl1Slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _cpuPl1Input = (int)Math.Round(pl1Slider.Value / 5.0) * 5;
                if (_cpuPl2Input < _cpuPl1Input) { _cpuPl2Input = _cpuPl1Input; pl2Slider.Value = _cpuPl2Input; }
                pl1Text.Text = $"Uzun süreli sınır (PL1): {_cpuPl1Input} W";
                pl2Text.Text = $"Kısa süreli sınır (PL2): {_cpuPl2Input} W";
            }
        };
        pl2Slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _cpuPl2Input = Math.Max(_cpuPl1Input, (int)Math.Round(pl2Slider.Value / 5.0) * 5);
                pl2Text.Text = $"Kısa süreli sınır (PL2): {_cpuPl2Input} W";
            }
        };
        string cpuCurrentNote = pwr?.CpuLimit is { } activeCl
            ? $"Şu an özel sınır etkin: {activeCl.Pl1Watts} / {activeCl.Pl2Watts} W (anlık çekiş: {pwr.CpuWatts?.ToString(CultureInfo.InvariantCulture) ?? "-"} W)."
            : $"Şu an güç modunun kendi sınırı etkin ({pwr?.CpuPl1Watts?.ToString(CultureInfo.InvariantCulture) ?? "-"} / {pwr?.CpuPl2Watts?.ToString(CultureInfo.InvariantCulture) ?? "-"} W).";

        var cpuBtns = new WrapPanel
        {
            Children =
            {
                LinuxTheme.MakeActionButton("İşlemci Sınırını Uygula", () =>
                {
                    _infoBanner = $"İşlemci watt sınırı {_cpuPl1Input}/{_cpuPl2Input} W olarak uygulandı.";
                    _client.Send(new LinuxRequest("setCpuLimit", CpuLimit: new CpuLimit(_cpuPl1Input, _cpuPl2Input)));
                }, primary: true),
                LinuxTheme.MakeActionButton("Özel Sınırı Kaldır (Modun Ayarına Dön)", () =>
                {
                    _infoBanner = "Özel işlemci sınırı kaldırıldı.";
                    _client.Send(new LinuxRequest("setCpuLimit", CpuLimit: null));
                }),
            },
        };
        page.Children.Add(LinuxTheme.MakeCard("İşlemci Güç Sınırı (Intel RAPL PL1 / PL2)",
            LinuxTheme.Caption(cpuCurrentNote), pl1Text, pl1Slider, pl2Text, pl2Slider, cpuBtns));

        // GPU Limit card
        var gpuText = new TextBlock { Text = $"Ekran kartı watt hedefi: {_gpuWattsInput} W", FontWeight = FontWeight.SemiBold };
        var gpuSlider = new Slider { Minimum = 30, Maximum = 140, Value = _gpuWattsInput, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        gpuSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _gpuWattsInput = (int)Math.Round(gpuSlider.Value / 5.0) * 5;
                gpuText.Text = $"Ekran kartı watt hedefi: {_gpuWattsInput} W";
            }
        };
        string gpuCurrentNote = pwr?.GpuLimitWatts is { } activeGw
            ? $"Şu an özel ekran kartı hedefi etkin: {activeGw} W{(pwr.GpuLockMhz is { } mhz ? $" (saat kilidi: {mhz} MHz)" : "")}."
            : $"Şu an güç modunun kendi ekran kartı tavanı etkin ({pwr?.GpuTgpWatts?.ToString(CultureInfo.InvariantCulture) ?? "-"} W).";

        var gpuBtns = new WrapPanel
        {
            Children =
            {
                LinuxTheme.MakeActionButton("Ekran Kartı Hedefini Uygula", () =>
                {
                    _infoBanner = $"Ekran kartı watt hedefi {_gpuWattsInput} W olarak ayarlandı.";
                    _client.Send(new LinuxRequest("setGpuLimit", GpuLimitWatts: _gpuWattsInput));
                }, primary: true),
                LinuxTheme.MakeActionButton("Özel Hedefi Kaldır", () =>
                {
                    _infoBanner = "Özel ekran kartı hedefi kaldırıldı.";
                    _client.Send(new LinuxRequest("setGpuLimit", GpuLimitWatts: null));
                }),
            },
        };
        page.Children.Add(LinuxTheme.MakeCard("Ekran Kartı Güç Sınırı (NVML)",
            LinuxTheme.Caption(gpuCurrentNote), gpuText, gpuSlider, gpuBtns));

        // Charger / Battery Automation card
        var auto = pwr?.Automation ?? new PowerAutomation();
        var autoToggleWrap = new WrapPanel
        {
            Children =
            {
                LinuxTheme.MakeChoiceButton("Otomasyon Açık", auto.Enabled, () =>
                    _client.Send(new LinuxRequest("setAutomation", Automation: auto with { Enabled = true }))),
                LinuxTheme.MakeChoiceButton("Otomasyon Kapalı", !auto.Enabled, () =>
                    _client.Send(new LinuxRequest("setAutomation", Automation: auto with { Enabled = false }))),
            },
        };

        var acModeWrap = new WrapPanel();
        foreach (var (m, lbl) in new (PowerMode?, string)[]
        {
            (PowerMode.Office, "Ofis"), (PowerMode.Gaming, "Oyun"), (PowerMode.HighPerformance, "Yüksek performans"), (null, "Değiştirme"),
        })
        {
            var modeVal = m;
            acModeWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, auto.OnAc.Mode == modeVal, () =>
                _client.Send(new LinuxRequest("setAutomation", Automation: auto with { Enabled = true, OnAc = auto.OnAc with { Mode = modeVal } }))));
        }

        var acProfWrap = new WrapPanel();
        var batProfWrap = new WrapPanel();
        foreach (var (profName, lbl) in new (string?, string)[]
        {
            ("Quiet", "Sessiz"), ("Balanced", "Dengeli"), ("Performance", "Performans"), ("EcAuto", "Orijinal"), (null, "Değiştirme"),
        })
        {
            string? pVal = profName;
            acProfWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, string.Equals(auto.OnAc.FanProfile, pVal, StringComparison.OrdinalIgnoreCase), () =>
                _client.Send(new LinuxRequest("setAutomation", Automation: auto with { Enabled = true, OnAc = auto.OnAc with { FanProfile = pVal } }))));
            batProfWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, string.Equals(auto.OnBattery.FanProfile, pVal, StringComparison.OrdinalIgnoreCase), () =>
                _client.Send(new LinuxRequest("setAutomation", Automation: auto with { Enabled = true, OnBattery = auto.OnBattery with { FanProfile = pVal } }))));
        }

        page.Children.Add(LinuxTheme.MakeCard("Şarj ve Pil Otomasyonu",
            LinuxTheme.Caption("Şarj aleti takıldığında veya çıkarıldığında güç modunu ve fan profilini otomatik değiştirir."),
            autoToggleWrap,
            LinuxTheme.Caption("Prize takılınca güç modu:"), acModeWrap,
            LinuxTheme.Caption("Prize takılınca fan profili:"), acProfWrap,
            LinuxTheme.Caption("Pile geçince fan profili (güç modu her zaman Ofis olur):"), batProfWrap));

        return page;
    }

    // =========================================================================
    // 4. LIGHTING PAGE
    // =========================================================================
    private Control BuildLightingPage(LinuxStatus? s)
    {
        var page = new StackPanel { Spacing = 4 };
        page.Children.Add(LinuxTheme.SectionTitle("Klavye Aydınlatması", "Üç bölgeli renk, efekt, parlaklık ve boşta kapatma"));

        var light = s?.Lighting ?? new KeyboardLighting();
        var preview = new KeyboardPreviewControl
        {
            LeftColor = LinuxTheme.ParseHex(light.Left, Colors.Cyan),
            MiddleColor = LinuxTheme.ParseHex(light.Middle, Colors.Cyan),
            RightColor = LinuxTheme.ParseHex(light.Right, Colors.Cyan),
            Brightness = light.Effect == LightEffect.Off ? 0 : light.Brightness,
            SelectedZone = _selectedLightZone,
        };
        preview.ZoneClicked += z =>
        {
            _selectedLightZone = _selectedLightZone == z ? -1 : z;
            RefreshUi();
        };

        var zoneWrap = new WrapPanel();
        foreach (var (z, lbl) in new[] { (-1, "Tüm Klavye"), (0, "Sol Bölge"), (1, "Orta Bölge"), (2, "Sağ Bölge") })
        {
            int targetZone = z;
            zoneWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, _selectedLightZone == targetZone, () =>
            {
                _selectedLightZone = targetZone;
                RefreshUi();
            }));
        }

        page.Children.Add(LinuxTheme.MakeCard("Klavye Bölgeleri",
            LinuxTheme.Caption("Tek bir bölgeye renk vermek için klavye üzerindeki bölgeye veya aşağıdaki düğmelere tıklayın."),
            preview, zoneWrap));

        // Effects & Brightness
        var effectWrap = new WrapPanel();
        foreach (var (eff, lbl) in new[]
        {
            (LightEffect.Static, "Sabit Renk"),
            (LightEffect.Breathing, "Nefes Alma"),
            (LightEffect.ColorCycle, "Renk Döngüsü"),
            (LightEffect.Off, "Kapalı"),
        })
        {
            var targetEff = eff;
            effectWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, light.Effect == targetEff, () =>
            {
                int b = targetEff != LightEffect.Off && light.Brightness == 0 ? 2 : light.Brightness;
                _client.Send(new LinuxRequest("setLighting", Lighting: light with { Effect = targetEff, Brightness = b }));
            }));
        }

        var levelWrap = new WrapPanel();
        foreach (var (lvl, lbl) in new[] { (0, "Kapalı (0)"), (1, "Düşük (1)"), (2, "Yüksek (2)") })
        {
            int targetLvl = lvl;
            levelWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, light.Brightness == targetLvl, () =>
            {
                var eff = targetLvl == 0 ? LightEffect.Off : (light.Effect == LightEffect.Off ? LightEffect.Static : light.Effect);
                _client.Send(new LinuxRequest("setLighting", Lighting: light with { Brightness = targetLvl, Effect = eff }));
            }));
        }

        // Color swatches
        var swatchWrap = new WrapPanel();
        foreach (var (name, hex) in ColorSwatches)
        {
            string targetHex = hex;
            var col = LinuxTheme.ParseHex(targetHex, Colors.Cyan);
            var btn = new Button
            {
                Background = new SolidColorBrush(col),
                BorderBrush = LinuxTheme.Border,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(5),
                Width = 96,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new TextBlock
                {
                    Text = name,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = col.R + col.G + col.B > 450 ? Brushes.Black : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            btn.Click += (_, _) =>
            {
                var updated = _selectedLightZone switch
                {
                    0 => light with { Effect = LightEffect.Static, Left = targetHex, Brightness = light.Brightness == 0 ? 2 : light.Brightness },
                    1 => light with { Effect = LightEffect.Static, Middle = targetHex, Brightness = light.Brightness == 0 ? 2 : light.Brightness },
                    2 => light with { Effect = LightEffect.Static, Right = targetHex, Brightness = light.Brightness == 0 ? 2 : light.Brightness },
                    _ => light with
                    {
                        Effect = light.Effect is LightEffect.Off or LightEffect.ColorCycle ? LightEffect.Static : light.Effect,
                        Left = targetHex,
                        Middle = targetHex,
                        Right = targetHex,
                        Brightness = light.Brightness == 0 ? 2 : light.Brightness,
                    },
                };
                _client.Send(new LinuxRequest("setLighting", Lighting: updated));
            };
            swatchWrap.Children.Add(btn);
        }

        // Idle timeout
        var idleWrap = new WrapPanel();
        foreach (var (mins, lbl) in new[] { (0, "Hiçbir zaman"), (1, "1 dakika"), (5, "5 dakika"), (15, "15 dakika"), (30, "30 dakika") })
        {
            int targetMins = mins;
            idleWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, light.IdleOffMinutes == targetMins, () =>
                _client.Send(new LinuxRequest("setLighting", Lighting: light with { IdleOffMinutes = targetMins }))));
        }

        page.Children.Add(LinuxTheme.MakeCard("Efekt, Parlaklık ve Renk Seçimi",
            LinuxTheme.Caption("Efekt:"), effectWrap,
            LinuxTheme.Caption("Parlaklık (klavyedeki ışık tuşuyla senkron çalışır):"), levelWrap,
            LinuxTheme.Caption("Hızlı renkler:"), swatchWrap,
            LinuxTheme.Caption("Boşta kalınca ışığı kapat:"), idleWrap));

        return page;
    }

    // =========================================================================
    // 5. BATTERY & HISTORY PAGE
    // =========================================================================
    private Control BuildBatteryAndHistoryPage(LinuxStatus? s)
    {
        var page = new StackPanel { Spacing = 4 };
        page.Children.Add(LinuxTheme.SectionTitle("Pil ve Geçmiş", "Pil sağlığı, süre tahminleri ve sıcaklık/fan geçmişi"));

        if (s?.Battery is { } bat)
        {
            var batStack = new StackPanel { Spacing = 6 };
            if (bat.Live is { } live)
            {
                string state = live.State switch
                {
                    BatteryState.OnBattery => "Pilde çalışıyor",
                    BatteryState.Charging => "Şarj oluyor",
                    BatteryState.Full => "Tam dolu",
                    BatteryState.NotCharging => "Prizde (şarj duraklatıldı)",
                    _ => live.State.ToString(),
                };
                string watts = bat.WattsNow is { } w ? $"   ·   Anlık çekiş: {w:0.0} W" : "";
                batStack.Children.Add(new TextBlock
                {
                    Text = $"%{live.Percent ?? 0} — {state}{watts}",
                    FontSize = 24,
                    FontWeight = FontWeight.Bold,
                    Foreground = LinuxTheme.Value,
                });
            }
            if (bat.Health is { } h)
            {
                batStack.Children.Add(new TextBlock
                {
                    Text = $"Pil sağlığı: %{(h.Percent?.ToString(CultureInfo.InvariantCulture) ?? "-")}   ({h.FullMWh} / {h.DesignedMWh} mWh{(h.Chemistry is { Length: > 0 } c ? $", {c}" : "")}{(h.Cycles is { } cyc ? $", {cyc} döngü" : "")})",
                    FontSize = 14,
                    Foreground = LinuxTheme.Text,
                });
            }

            var intervalWrap = new WrapPanel();
            foreach (int mins in new[] { 2, 5, 10, 30 })
            {
                int targetMins = mins;
                intervalWrap.Children.Add(LinuxTheme.MakeChoiceButton($"Her {targetMins} dk", bat.ReadMinutes == targetMins, () =>
                    _client.Send(new LinuxRequest("setBatteryInterval", BatteryReadMinutes: targetMins))));
            }
            batStack.Children.Add(LinuxTheme.Caption("Pildeyken okuma sıklığı (seyrek okuma EC yükünü azaltır):"));
            batStack.Children.Add(intervalWrap);

            if (bat.Estimates.Count > 0)
            {
                int fullMWh = bat.Health?.FullMWh ?? 60_000;
                batStack.Children.Add(LinuxTheme.Caption("Mod ve profile göre tam pil süresi tahminleri:"));
                foreach (var e in bat.Estimates)
                {
                    string dur = e.Hours(fullMWh) is { } hr ? FormatDuration(TimeSpan.FromHours(hr)) : "-";
                    batStack.Children.Add(new TextBlock
                    {
                        Text = $"• {PowerModeLabel(e.Mode)} + {ProfileLabel(e.Profile)}: ort. {e.Watts:0.0} W → tam pil ~{dur} ({e.Minutes} dk veri)",
                        FontSize = 13,
                        Foreground = LinuxTheme.Text,
                    });
                }
            }
            page.Children.Add(LinuxTheme.MakeCard("Pil Durumu ve Sağlığı", batStack));
        }

        // History card
        var hist = s?.History;
        var modeWrap = new WrapPanel();
        foreach (var (hm, lbl) in new[]
        {
            (HistoryMode.Days7, "7 Gün (Diskte)"),
            (HistoryMode.Days30, "30 Gün (Diskte)"),
            (HistoryMode.Hour1, "Yalnız Son 1 Saat (Bellekte)"),
            (HistoryMode.Off, "Kapalı"),
        })
        {
            var targetHm = hm;
            modeWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, hist?.Mode == targetHm, () =>
                _client.Send(new LinuxRequest("setHistoryMode", HistoryMode: targetHm))));
        }

        var rangeWrap = new WrapPanel();
        foreach (var (mins, lbl) in new[] { (15, "Son 15 dk"), (60, "Son 1 saat"), (360, "Son 6 saat"), (1440, "Son 24 saat") })
        {
            int targetMins = mins;
            rangeWrap.Children.Add(LinuxTheme.MakeChoiceButton(lbl, _client.HistoryMinutes == targetMins, () =>
            {
                _client.HistoryMinutes = targetMins;
                _client.Poll();
            }));
        }

        var summaryStack = new StackPanel { Spacing = 6 };
        if (hist?.Summary is { } sum)
        {
            summaryStack.Children.Add(new TextBlock
            {
                Text = $"İşlemci: ortalama {sum.Cpu.Average} °C · en yüksek {sum.Cpu.Max} °C · ≥{HistorySummary.HotC} °C süre: {(int)Math.Round(sum.Cpu.Hot.TotalMinutes)} dk",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = LinuxTheme.Text,
            });
            summaryStack.Children.Add(new TextBlock
            {
                Text = $"Ekran Kartı: ortalama {sum.Gpu.Average} °C · en yüksek {sum.Gpu.Max} °C · ≥{HistorySummary.HotC} °C süre: {(int)Math.Round(sum.Gpu.Hot.TotalMinutes)} dk",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = LinuxTheme.Text,
            });
        }
        else
        {
            summaryStack.Children.Add(LinuxTheme.Caption("Seçilen aralıkta henüz yeterli geçmiş kaydı yok."));
        }

        page.Children.Add(LinuxTheme.MakeCard("Fan ve Sıcaklık Geçmişi",
            LinuxTheme.Caption("Saklama süresi:"), modeWrap,
            LinuxTheme.Caption("Özet aralığı:"), rangeWrap,
            summaryStack));

        return page;
    }

    // =========================================================================
    // 6. ABOUT & DIAGNOSTICS PAGE
    // =========================================================================
    private Control BuildAboutPage(LinuxStatus? s)
    {
        var page = new StackPanel { Spacing = 4 };
        page.Children.Add(LinuxTheme.SectionTitle("Hakkında ve Sorun Bildirimi", $"Excalibur Reimagined {_client.Version} (Linux)"));

        string modeLabel = s?.Mode switch
        {
            "Running" => "Açık (fan kontrolü devrede)",
            "MonitorOnly" => "Yalnız izleniyor (kısa deneme gerekiyor)",
            "Faulted" => "Sorun var (fanlar dizüstünün kendi ayarında)",
            null => "Bağlı değil",
            var m => m,
        };
        string summary = $"Sürüm: Excalibur Reimagined {_client.Version} (Linux)\n" +
                         $"EC Sürümü: {s?.EcVersion ?? "bilinmiyor"}\n" +
                         $"Çalışma Modu: {modeLabel}{(s?.Simulated == true ? " — deneme modu" : "")}\n" +
                         $"Aktif Profil: {ProfileLabel(s?.Profile ?? "-")}\n" +
                         $"Güç Modu: {PowerModeLabel(s?.Power?.Mode)}\n" +
                         $"Çekirdek: {Environment.OSVersion.VersionString}";

        var infoText = new TextBlock
        {
            Text = summary,
            FontSize = 14,
            Foreground = LinuxTheme.Text,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var actions = new WrapPanel
        {
            Children =
            {
                LinuxTheme.MakeActionButton("Bilgileri Panoya Kopyala", async () =>
                {
                    if (Clipboard is not null) await Clipboard.SetTextAsync(summary);
                    _infoBanner = "Sistem bilgileri panoya kopyalandı.";
                    RefreshUi();
                }, primary: true),
                LinuxTheme.MakeActionButton("Sorun Bildir (Günlükleri Zip'e Kaydet)", () =>
                {
                    string home = Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } user
                        ? $"/home/{user}"
                        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    try
                    {
                        string zip = BugReport.Create(home, _client.Paths.Data, summary.Replace('\n', ' '), [], DateTime.Now, _client.Paths.Logs);
                        _infoBanner = $"Sorun raporu kaydedildi: {zip}";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _infoBanner = $"Sorun raporu oluşturulamadı ({ex.Message}). Terminalde deneyin: sudo exr report";
                    }
                    RefreshUi();
                }),
            },
        };

        if (s?.Mode == "MonitorOnly")
        {
            actions.Children.Add(LinuxTheme.MakeActionButton("Kısa Fan Denemesini Başlat (EC Doğrulama)", () =>
            {
                _infoBanner = "Kısa fan denemesi başlatılıyor...";
                RefreshUi();
                Task.Run(() =>
                {
                    // The service only starts the trial here; its outcome shows up later in the status and recent events.
                    var res = _client.SendNow(new LinuxRequest("trial"));
                    Dispatcher.UIThread.Post(() =>
                    {
                        _infoBanner = res is null
                            ? "Servise ulaşılamadı; deneme başlatılamadı."
                            : res.Error is { } err
                                ? $"Deneme başlatılamadı: {err}"
                                : "Deneme başladı (yaklaşık bir dakika). Sonuç bu sayfada ve Panel'deki son olaylarda görünecek.";
                        RefreshUi();
                    });
                });
            }));
        }

        page.Children.Add(LinuxTheme.MakeCard("Sistem Özeti ve Tanılama",
            infoText,
            LinuxTheme.Caption("Sorun bildir düğmesi, kullanıcı adınızı ve klasör yollarınızı temizleyerek son günlükleri Masaüstüne bir zip olarak kaydeder."),
            actions));

        return page;
    }

    // =========================================================================
    // HELPERS
    // =========================================================================
    public static string ProfileLabel(string name) => name switch
    {
        "Quiet" => "Sessiz",
        "Balanced" => "Dengeli",
        "Performance" => "Performans",
        "EcAuto" => "Orijinal (EC)",
        _ => name,
    };

    public static string PowerModeLabel(PowerMode? mode) => mode switch
    {
        PowerMode.Office => "Ofis",
        PowerMode.Gaming => "Oyun",
        PowerMode.HighPerformance => "Yüksek performans",
        _ => "Seçilmedi",
    };

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} sa {d.Minutes} dk" : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))} dk";

    private static bool TryParseCurvePoints(string text, out IReadOnlyList<CurvePoint> points, out string? error)
    {
        points = [];
        error = null;
        var list = new List<CurvePoint>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', StringSplitOptions.TrimEntries);
            if (kv.Length != 2 ||
                !int.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int temp) ||
                !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
            {
                error = $"'{part}' geçersiz (sıcaklık:yüzde olmalı, ör. 60:45).";
                return false;
            }
            list.Add(new CurvePoint(temp, pct));
        }
        var dummy = new CustomProfile("Kontrol", list, null);
        var problems = dummy.Validate();
        if (problems.Count > 0)
        {
            error = string.Join("; ", problems);
            return false;
        }
        points = list;
        return true;
    }
}
