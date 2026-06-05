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
using DynamicIsland.Audio;
using DynamicIsland.Helpers;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    // ── 服务 ──
    private readonly MediaService _mediaService = new();
    private readonly SpectrumService _spectrumService = new(8);
    private readonly MicrophoneService _micService = new();
    private readonly LyricsService _lyricsService = new();

    // ── 频谱条 ──
    private readonly Rectangle[] _spectrumBars = new Rectangle[8];
    private readonly DispatcherTimer _spectrumTimer;

    // ── 歌词同步定时器 ──
    private readonly DispatcherTimer _lyricTimer;
    private bool _isLyricMode;

    // ── 状态 ──
    private bool _isMusicPlaying;
    private bool _isExpanded;
    private bool _isMicVisible;
    private string _currentTitle = "";
    private string _currentArtist = "";

    // ═══════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════

    public MainWindow()
    {
        InitializeComponent();

        // 频谱刷新 ~60fps
        _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spectrumTimer.Tick += OnSpectrumTimerTick;

        // 歌词同步 ~100ms 精度
        _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _lyricTimer.Tick += OnLyricTimerTick;

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

        // ── 歌词 ──
        _lyricsService.LyricChanged += line =>
            Dispatcher.Invoke(() => OnLyricLineChanged(line));
        _lyricsService.LyricCleared += () =>
            Dispatcher.Invoke(ExitLyricMode);
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
        var text = _isLyricMode ? (LyricText.Text ?? _currentTitle) : _currentTitle;
        var width = CalcIslandWidth(
            string.IsNullOrEmpty(text) ? "Dynamic Island" : text,
            _isLyricMode ? 12.5 : 13, _isLyricMode ? FontWeights.Medium : FontWeights.SemiBold);
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
        _lyricTimer.Stop();
        foreach (var bar in _spectrumBars) bar.Height = 2;
        ExitLyricMode();
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
                    Color.FromRgb(0x4A, 0xDE, 0x80),
                    Color.FromRgb(0x22, 0xD3, 0xEE), 90),
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

    /// <summary>根据文本内容计算灵动岛目标宽度</summary>
    private double CalcIslandWidth(string text, double fontSize, FontWeight weight)
    {
        double textW = MeasureTextWidth(text, fontSize, weight);
        textW = Math.Clamp(textW, 40, 260);  // 文本宽度上下限

        double cover = 40;     // 封面 30 + margin 10
        double spectrum = 46;  // 8条×5 + margin 6
        double padding = 22;   // 左右内边距

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
    //  ★ 歌词同步
    // ═══════════════════════════════════════════════

    /// <summary>歌词定时器：每 100ms 用播放位置同步歌词行</summary>
    private void OnLyricTimerTick(object? sender, EventArgs e)
    {
        if (!_isMusicPlaying || !_lyricsService.HasLyrics) return;

        var position = _mediaService.GetCurrentPosition();
        _lyricsService.UpdatePosition(position);
    }

    /// <summary>歌词行变化时：切换到歌词模式，显示当前行</summary>
    private void OnLyricLineChanged(string line)
    {
        if (!_isExpanded || string.IsNullOrWhiteSpace(line)) return;

        if (!_isLyricMode)
            EnterLyricMode();

        // ★ 宽度跟随歌词长度变化
        var targetWidth = CalcIslandWidth(line, 12.5, FontWeights.Medium);
        AnimateWidth(targetWidth);

        var fadeOut = (Storyboard)FindResource("LyricFadeOut");
        fadeOut.Completed -= OnLyricFadeOutDone;
        fadeOut.Completed += OnLyricFadeOutDone;
        _pendingLyricLine = line;
        fadeOut.Begin(this);
    }

    private string _pendingLyricLine = "";

    private void OnLyricFadeOutDone(object? sender, EventArgs e)
    {
        LyricText.Text = _pendingLyricLine;
        ((Storyboard)FindResource("LyricFadeIn")).Begin(this);
    }

    private void EnterLyricMode()
    {
        _isLyricMode = true;
        InfoPanel.Visibility = Visibility.Collapsed;
        LyricText.Visibility = Visibility.Visible;
        LyricText.Opacity = 0;
    }

    private void ExitLyricMode()
    {
        _isLyricMode = false;
        InfoPanel.Visibility = Visibility.Visible;
        LyricText.Visibility = Visibility.Collapsed;
        LyricText.Text = "";
    }

    // ═══════════════════════════════════════════════
    //  媒体状态
    // ═══════════════════════════════════════════════

    private async void OnMediaChanged(MediaInfo info)
    {
        _isMusicPlaying = true;
        _currentTitle = info.Title;
        _currentArtist = info.Artist;

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

        ExpandIsland();

        // ★ 异步加载歌词
        try
        {
            ExitLyricMode();  // 先回到信息模式，歌词加载完再切换
            await _lyricsService.LoadLyricsAsync(info.Title, info.Artist);

            if (_lyricsService.HasLyrics && _isMusicPlaying)
            {
                if (!_lyricTimer.IsEnabled)
                    _lyricTimer.Start();

                Debug.WriteLine($"[Main] 歌词已加载，启动同步");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Main] 歌词加载异常: {ex.Message}");
        }
    }

    private void OnMediaStopped()
    {
        _isMusicPlaying = false;
        _currentTitle = "";
        _currentArtist = "";

        _lyricTimer.Stop();
        _lyricsService.Clear();

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
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _spectrumTimer.Stop();
        _lyricTimer.Stop();
        _spectrumService.Dispose();
        _micService.Dispose();
        _mediaService.Dispose();
        _lyricsService.Dispose();
    }
}
