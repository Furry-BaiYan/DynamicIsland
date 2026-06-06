using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using DynamicIsland.Audio;
using DynamicIsland.Helpers;
using DynamicIsland.Services;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    // ── 服务 ──
    private readonly MediaService _mediaService = new();
    private readonly SpectrumService _spectrumService = new(8);
    private readonly MicrophoneService _micService = new();
    private readonly NotificationService _notifService = new();

    // ── 频谱条 ──
    private readonly Rectangle[] _spectrumBars = new Rectangle[8];
    private readonly DispatcherTimer _spectrumTimer;

    // ── 状态 ──
    private bool _isMusicPlaying;
    private bool _isExpanded;
    private bool _isMicVisible;
    private bool _isDarkMode = true;
    private bool _isShowingNotification;
    private string _currentTitle = "";
    private string _currentArtist = "";
    
    // ★ 用于消除切歌闪烁的防抖 Token
    private CancellationTokenSource? _debounceCts;

    // ═══════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════

    public MainWindow()
    {
        InitializeComponent();

        // 频谱刷新 ~60fps
        _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spectrumTimer.Tick += OnSpectrumTimerTick;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ═══════════════════════════════════════════════
    //  初始化
    // ═══════════════════════════════════════════════

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW);

        PositionToCenter();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        IslandBorder.MouseEnter += (_, _) =>
            ((Storyboard)FindResource("HoverEnterAnimation")).Begin(this);
        IslandBorder.MouseLeave += (_, _) =>
            ((Storyboard)FindResource("HoverLeaveAnimation")).Begin(this);
        IslandBorder.MouseLeftButtonDown += (_, _) =>
            ((Storyboard)FindResource("PressAnimation")).Begin(this);
        IslandBorder.MouseLeftButtonUp += (_, _) =>
        {
            ((Storyboard)FindResource("ReleaseAnimation")).Begin(this);
            OnIslandClicked();
        };

        CreateSpectrumBars();

        ((Storyboard)FindResource("SlideInAnimation")).Begin(this);

        await InitializeServicesAsync();
    }

    private async Task InitializeServicesAsync()
    {
        // ── 媒体 ──
        _mediaService.MediaChanged += info =>
            Dispatcher.Invoke(() => OnMediaChanged(info));
        _mediaService.MediaStopped += () =>
            Dispatcher.Invoke(OnMediaStopped);
        await _mediaService.InitializeAsync();

        // ── 频谱 ──
        _spectrumService.Start();

        // ── 麦克风 ──
        _micService.MicActivated += () =>
            Dispatcher.Invoke(() => ShowMicIsland(true));
        _micService.MicDeactivated += () =>
            Dispatcher.Invoke(() => ShowMicIsland(false));
        _micService.Start();

        // ── 社交通知 ──
        _notifService.NotificationReceived += data =>
            Dispatcher.Invoke(() => ShowNotification(data));
        await _notifService.InitializeAsync();
    }

    // ═══════════════════════════════════════════════
    //  展开 / 收缩
    // ═══════════════════════════════════════════════

    private void ExpandIsland()
    {
        if (_isExpanded) return;
        _isExpanded = true;

        ((Storyboard)FindResource("ExpandAnimation")).Begin(this);

        // ★ 动态计算初始宽度
        var width = CalcIslandWidth(_currentTitle, _currentArtist);
        AnimateWidth(width, 500);

        if (!_spectrumTimer.IsEnabled)
            _spectrumTimer.Start();
    }

    private void ContractIsland()
    {
        if (!_isExpanded) return;
        _isExpanded = false;

        ((Storyboard)FindResource("ContractAnimation")).Begin(this);
        AnimateWidth(50, 400);  // ★ 收缩回小药丸

        _spectrumTimer.Stop();
        foreach (var bar in _spectrumBars) bar.Height = 2;
    }

    // ═══════════════════════════════════════════════
    //  频谱条
    // ═══════════════════════════════════════════════

    private void CreateSpectrumBars()
    {
        SpectrumPanel.Children.Clear();
        for (int i = 0; i < _spectrumBars.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = 2,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),  // #CCFFFFFF
                    Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),  // #66FFFFFF
                    90),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 1, 0),
                Opacity = 0.9
            };
            _spectrumBars[i] = bar;
            SpectrumPanel.Children.Add(bar);
        }
    }

    private void OnSpectrumTimerTick(object? sender, EventArgs e)
    {
        if (!_isMusicPlaying || !_isExpanded) return;
        var spectrum = _spectrumService.Spectrum;
        for (int i = 0; i < _spectrumBars.Length && i < spectrum.Length; i++)
        {
            double target = 3 + spectrum[i] * 28;
            double current = _spectrumBars[i].Height;
            _spectrumBars[i].Height = current + (target - current) * 0.4;
        }
    }


    // ═══════════════════════════════════════════════
    //  ★ 动态宽度计算
    // ═══════════════════════════════════════════════

    /// <summary>测量文本像素宽度</summary>
    private double MeasureTextWidth(string text, double fontSize, FontWeight weight)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Segoe UI Variable, Segoe UI, Microsoft YaHei UI"),
                FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private double CalcIslandWidth(string title, string artist)
    {
        double titleW = MeasureTextWidth(title, 13, FontWeights.SemiBold);
        double artistW = MeasureTextWidth(artist, 11, FontWeights.Normal);
        double textW = Math.Max(titleW, artistW) + 12; // 额外增加一些文字安全留白，避免边缘太挤

        // ★ 修改：提高最小宽度的下限，防止歌名/歌手名字太短时，胶囊缩得太小显得很挤
        textW = Math.Clamp(textW, 100, 600);  

        double cover = 38;     // 封面 Width=30 + MarginRight=8
        double spectrum = 42;  // 频谱实际占用空间 + MarginLeft=8
        double padding = 28;   // 外壳左右两端的内边距整体加大，让整体显得更大气

        return cover + textW + spectrum + padding;
    }

    /// <summary>弹性动画到目标宽度</summary>
    private void AnimateWidth(double targetWidth, double durationMs = 300)
    {
        // 先清除 Storyboard 对 Width 的控制
        IslandBorder.BeginAnimation(WidthProperty, null);

        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
        };
        IslandBorder.BeginAnimation(WidthProperty, anim);
    }

    // ═══════════════════════════════════════════════
    //  媒体状态
    // ═══════════════════════════════════════════════

    private async void OnMediaChanged(MediaInfo info)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try { await Task.Delay(200, token); }
        catch (TaskCanceledException) { return; }

        _isMusicPlaying = true;
        _currentTitle = info.Title;
        _currentArtist = info.Artist;

        if (_isExpanded)
        {
            // ← 这段必须在 OnMediaChanged 里，不是 UpdateContent 里
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

            CoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, fadeOut);
            CoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, fadeOut.Clone());
            TextContent.BeginAnimation(OpacityProperty, fadeOut.Clone());

            await Task.Delay(130, token);

            UpdateContent(info);

            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

            CoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, fadeIn);
            CoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, fadeIn.Clone());
            TextContent.BeginAnimation(OpacityProperty, fadeIn.Clone());

            var width = CalcIslandWidth(_currentTitle, _currentArtist);
            AnimateWidth(width, 300);
        }
        else
        {
            UpdateContent(info);
            ExpandIsland();
        }
    }

    /// <summary>只更新封面和文字，不触发动画</summary>
    private void UpdateContent(MediaInfo info)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? "正在播放" : info.Title;
        SubtitleText.Text = string.IsNullOrWhiteSpace(info.Artist) ? "未知艺术家" : info.Artist;

        if (info.CoverData is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(info.CoverData);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                CoverImage.ImageSource = bitmap;
            }
            catch
            {
                CoverImage.ImageSource = new BitmapImage(
                    new Uri("pack://application:,,,/Resources/default_cover.png"));
            }
        }
        else
        {
            CoverImage.ImageSource = new BitmapImage(
                new Uri("pack://application:,,,/Resources/default_cover.png"));
        }
    }
    private async void OnMediaStopped()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            // 防抖：等待 200ms，因为切歌时底层经常是先抛出 Stop 再瞬间抛出 Play
            // 如果 200ms 内又 Play 了，这个 Stop 就会被取消，从而避免“闪一下收缩又闪一下展开”
            await Task.Delay(200, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        _isMusicPlaying = false;
        _currentTitle = "";
        _currentArtist = "";

        ContractIsland();
    }

    // ═══════════════════════════════════════════════
    //  麦克风岛
    // ═══════════════════════════════════════════════

    private void ShowMicIsland(bool show)
    {
        if (show && !_isMicVisible)
        {
            _isMicVisible = true;
            ((Storyboard)FindResource("MicSplitInAnimation")).Begin(this);
        }
        else if (!show && _isMicVisible)
        {
            _isMicVisible = false;
            ((Storyboard)FindResource("MicSplitOutAnimation")).Begin(this);
        }
    }

    // ═══════════════════════════════════════════════
    //  社交通知展示
    // ═══════════════════════════════════════════════

    private async void ShowNotification(NotificationData data)
    {
        Debug.WriteLine($"[Main] ShowNotification 开始: isExpanded={_isExpanded}, isShowingNotif={_isShowingNotification}");

        if (_isShowingNotification)
        {
            Debug.WriteLine("[Main] 通知被跳过: 正在显示另一条");
            return;
        }

        _isShowingNotification = true;

        if (!_isExpanded)
        {
            Debug.WriteLine("[Main] 通知触发展开");
            _isExpanded = true;
            ((Storyboard)FindResource("ExpandAnimation")).Begin(this);
            var w = CalcIslandWidth(data.Title, data.Content);
            AnimateWidth(w, 400);
            await Task.Delay(500);
        }

        NotifTitle.Text   = data.Title;
        NotifContent.Text = data.Content;
        NotifIconText.Text = data.AppName.Length > 0 ? data.AppName[..1] : "?";
        var iconColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["QQ"]       = "#12B7F5",
            ["微信"]     = "#07C160",
            ["WeChat"]   = "#07C160",
            ["Discord"]  = "#5865F2",
            ["Telegram"] = "#2AABEE",
            ["钉钉"]     = "#3089DC",
        };
        var iconColor = iconColors.GetValueOrDefault(data.AppName, "#3399FF");
        NotifIconBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor));
        Debug.WriteLine($"[Main] 通知内容已设置: {data.Title} - {data.Content}");

        var notifWidth = CalcIslandWidth(data.Title, data.Content);
        AnimateWidth(notifWidth, 300);
        Debug.WriteLine($"[Main] 宽度已调整: {notifWidth}");

        // 确保初始状态可见（防止上次动画 HoldEnd 将属性锁在不可见值）
        NotificationContent.Opacity = 1;
        NotifTranslate.Y = 0;
        Debug.WriteLine($"[Main] NotificationContent.Opacity 强制设为 1");

        var scrollIn = FindResource("NotifScrollInAnimation") as Storyboard;
        if (scrollIn != null)
        {
            scrollIn.Begin(this);
            Debug.WriteLine("[Main] NotifScrollInAnimation 已播放");
        }
        else
        {
            Debug.WriteLine("[Main] ⚠ NotifScrollInAnimation 找不到！");
        }

        await Task.Delay(3000);

        var scrollOut = FindResource("NotifScrollOutAnimation") as Storyboard;
        if (scrollOut != null)
        {
            scrollOut.Begin(this);
            Debug.WriteLine("[Main] NotifScrollOutAnimation 已播放");
        }

        if (_isMusicPlaying)
        {
            var musicWidth = CalcIslandWidth(_currentTitle, _currentArtist);
            AnimateWidth(musicWidth, 300);
        }

        await Task.Delay(500);
        _isShowingNotification = false;
        Debug.WriteLine("[Main] 通知显示结束");
    }

    // ═══════════════════════════════════════════════
    //  深色 / 浅色主题
    // ═══════════════════════════════════════════════

    public void SetTheme(bool isDark)
    {
        _isDarkMode = isDark;
        var bgColor  = (Color)ColorConverter.ConvertFromString(isDark ? "#CC1A1A1A" : "#CCF5F5F5");
        var bgBrush  = new SolidColorBrush(bgColor);
        var fgMain   = isDark ? Colors.White : Color.FromRgb(0x22, 0x22, 0x22);
        var fgSub    = isDark
            ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x99, 0x22, 0x22, 0x22);

        IslandBg.Background  = bgBrush;
        MicIsland.Background = bgBrush.Clone();

        TitleText.Foreground    = new SolidColorBrush(fgMain);
        SubtitleText.Foreground = new SolidColorBrush(fgSub);
    }

    // ═══════════════════════════════════════════════
    //  屏幕定位
    // ═══════════════════════════════════════════════

    public void PositionToCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top;
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(PositionToCenter);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PositionToCenter();
    }

    // ═══════════════════════════════════════════════
    //  点击跳转
    // ═══════════════════════════════════════════════

    private void OnIslandClicked()
    {
        if (!_isMusicPlaying) return;

        var appId = _mediaService.SourceAppId;
        if (!string.IsNullOrEmpty(appId))
        {
            var keywords = ExtractProcessKeywords(appId);
            foreach (var kw in keywords)
                if (TryActivateByProcessName(kw)) return;
        }

        if (!string.IsNullOrEmpty(_currentTitle))
            if (TryActivateByWindowTitle(_currentTitle)) return;

        string[] common = [
            "cloudmusic", "QQMusic", "KuGou", "kuwo",
            "Spotify", "foobar2000", "AIMP", "MusicBee", "wmplayer"
        ];
        foreach (var name in common)
            if (TryActivateByProcessName(name)) return;
    }

    private static List<string> ExtractProcessKeywords(string appId)
    {
        var keywords = new List<string>();
        var clean = appId.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        var parts = clean.Split(['\\', '/', '!', '_', '.'],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
            if (p.Length >= 3 && p.Length <= 30 && !p.All(char.IsAsciiHexDigit))
                keywords.Add(p);
        if (clean.Length <= 30)
            keywords.Insert(0, clean);
        return keywords;
    }

    private static bool TryActivateByProcessName(string keyword)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero
                        && proc.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        ActivateWindow(proc.MainWindowHandle);
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static bool TryActivateByWindowTitle(string keyword)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero
                        && !string.IsNullOrEmpty(proc.MainWindowTitle)
                        && proc.MainWindowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        ActivateWindow(proc.MainWindowHandle);
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static void ActivateWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        else
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    // ═══════════════════════════════════════════════
    //  清理
    // ═══════════════════════════════════════════════

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _spectrumTimer.Stop();
        _spectrumService.Dispose();
        _micService.Dispose();
        _mediaService.Dispose();
        _notifService.Dispose();
    }
}
