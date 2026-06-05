using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // ── 频谱条 UI ──
    private readonly Rectangle[] _spectrumBars = new Rectangle[8];
    private readonly DispatcherTimer _spectrumTimer;

    // ── 状态 ──
    private bool _isMusicPlaying;
    private bool _isMicVisible;
    private string _currentTitle = "";

    // ═══════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════

    public MainWindow()
    {
        InitializeComponent();

        _spectrumTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _spectrumTimer.Tick += OnSpectrumTimerTick;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ═══════════════════════════════════════════════
    //  初始化
    // ═══════════════════════════════════════════════

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 1. 隐藏 Alt+Tab
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW);

        // 2. 定位到屏幕顶部居中
        PositionToCenter();

        // 3. 监听显示器变化
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        // 4. 鼠标交互
        IslandBorder.MouseEnter += OnIslandMouseEnter;
        IslandBorder.MouseLeave += OnIslandMouseLeave;
        IslandBorder.MouseLeftButtonDown += OnIslandMouseDown;
        IslandBorder.MouseLeftButtonUp += OnIslandMouseUp;

        // 5. 创建频谱条
        CreateSpectrumBars();

        // 6. 入场动画
        var slideIn = (Storyboard)FindResource("SlideInAnimation");
        slideIn.Begin(this);

        // 7. 启动服务
        await InitializeServicesAsync();
    }

    private async Task InitializeServicesAsync()
    {
        // ── 媒体服务 ──
        _mediaService.MediaChanged += info =>
            Dispatcher.Invoke(() => OnMediaChanged(info));
        _mediaService.MediaStopped += () =>
            Dispatcher.Invoke(OnMediaStopped);

        await _mediaService.InitializeAsync();

        // ── 频谱服务 ──
        _spectrumService.Start();
        Debug.WriteLine($"[Main] 频谱服务状态: {_spectrumService.IsRunning}");

        // ── 麦克风服务 ──
        _micService.MicActivated += () =>
            Dispatcher.Invoke(() => ShowMicIsland(true));
        _micService.MicDeactivated += () =>
            Dispatcher.Invoke(() => ShowMicIsland(false));

        _micService.Start();
    }

    // ═══════════════════════════════════════════════
    //  频谱条 UI
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
                    Color.FromRgb(0x22, 0xD3, 0xEE),
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
        if (!_isMusicPlaying) return;

        var spectrum = _spectrumService.Spectrum;

        for (int i = 0; i < _spectrumBars.Length && i < spectrum.Length; i++)
        {
            double targetHeight = 3 + spectrum[i] * 28;
            double current = _spectrumBars[i].Height;
            _spectrumBars[i].Height = current + (targetHeight - current) * 0.4;
        }
    }

    // ═══════════════════════════════════════════════
    //  媒体状态变化
    // ═══════════════════════════════════════════════

    private void OnMediaChanged(MediaInfo info)
    {
        _isMusicPlaying = true;
        _currentTitle = info.Title;

        TitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? "正在播放" : info.Title;
        SubtitleText.Text = string.IsNullOrWhiteSpace(info.Artist) ? "未知艺术家" : info.Artist;

        Debug.WriteLine($"[Main] 媒体更新: {info.Title} - {info.Artist}, AppId={info.SourceAppId}");

        // 更新封面
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

        // 显示频谱条
        SpectrumPanel.Visibility = Visibility.Visible;
        if (!_spectrumTimer.IsEnabled)
            _spectrumTimer.Start();
    }

    private void OnMediaStopped()
    {
        _isMusicPlaying = false;
        _currentTitle = "";

        TitleText.Text = "Dynamic Island";
        SubtitleText.Text = "就绪";
        CoverImage.ImageSource = new BitmapImage(
            new Uri("pack://application:,,,/Resources/default_cover.png"));

        _spectrumTimer.Stop();
        SpectrumPanel.Visibility = Visibility.Collapsed;

        foreach (var bar in _spectrumBars)
            bar.Height = 2;
    }

    // ═══════════════════════════════════════════════
    //  麦克风岛分裂动画
    // ═══════════════════════════════════════════════

    private void ShowMicIsland(bool show)
    {
        if (show && !_isMicVisible)
        {
            _isMicVisible = true;
            var sb = (Storyboard)FindResource("MicSplitInAnimation");
            sb.Begin(this);
        }
        else if (!show && _isMicVisible)
        {
            _isMicVisible = false;
            var sb = (Storyboard)FindResource("MicSplitOutAnimation");
            sb.Begin(this);
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
    //  鼠标交互动画
    // ═══════════════════════════════════════════════

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
        => ((Storyboard)FindResource("HoverEnterAnimation")).Begin(this);

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
        => ((Storyboard)FindResource("HoverLeaveAnimation")).Begin(this);

    private void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
        => ((Storyboard)FindResource("PressAnimation")).Begin(this);

    private void OnIslandMouseUp(object sender, MouseButtonEventArgs e)
    {
        ((Storyboard)FindResource("ReleaseAnimation")).Begin(this);
        OnIslandClicked();
    }

    // ═══════════════════════════════════════════════
    //  ★ 点击跳转到音乐软件（修复版）
    // ═══════════════════════════════════════════════

    private void OnIslandClicked()
    {
        if (!_isMusicPlaying) return;

        var appId = _mediaService.SourceAppId;
        Debug.WriteLine($"[Click] SourceAppId = {appId}");

        // ── 策略 1：从 AppId 提取进程名关键字 ──
        if (!string.IsNullOrEmpty(appId))
        {
            // AppId 常见格式:
            //   "cloudmusic.exe"
            //   "Spotify.exe"
            //   "QQMusic.exe"
            //   "SpotifyAB.SpotifyMusic_xxx!Spotify"
            //   "Microsoft.ZuneMusic_xxx!App"
            //   "{浏览器相关的ID}"

            // 提取有意义的关键字
            var keywords = ExtractProcessKeywords(appId);
            Debug.WriteLine($"[Click] 匹配关键字: {string.Join(", ", keywords)}");

            foreach (var keyword in keywords)
            {
                if (TryActivateByProcessName(keyword))
                    return;
            }
        }

        // ── 策略 2：用歌曲名匹配窗口标题 ──
        if (!string.IsNullOrEmpty(_currentTitle))
        {
            Debug.WriteLine($"[Click] 尝试标题匹配: {_currentTitle}");

            if (TryActivateByWindowTitle(_currentTitle))
                return;
        }

        // ── 策略 3：常见音乐软件进程名硬匹配 ──
        string[] commonPlayers = [
            "cloudmusic",      // 网易云音乐
            "QQMusic",         // QQ 音乐
            "KuGou",           // 酷狗
            "kuwo",            // 酷我
            "Spotify",         // Spotify
            "foobar2000",      // foobar
            "AIMP",            // AIMP
            "MusicBee",        // MusicBee
            "wmplayer",        // Windows Media Player
            "msrdc",           // Groove Music
        ];

        foreach (var name in commonPlayers)
        {
            if (TryActivateByProcessName(name))
                return;
        }

        Debug.WriteLine("[Click] 未找到匹配的音乐软件窗口");
    }

    /// <summary>从 AppUserModelId 提取可能的进程名关键字</summary>
    private static List<string> ExtractProcessKeywords(string appId)
    {
        var keywords = new List<string>();

        // 去掉 .exe 后缀
        var clean = appId.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

        // 按分隔符拆分
        var parts = clean.Split(['\\', '/', '!', '_', '.'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            // 过滤掉纯数字、太短或太长的 hash
            if (part.Length >= 3 && part.Length <= 30 && !IsHexHash(part))
                keywords.Add(part);
        }

        // 也试整个 ID 去掉 .exe
        if (clean.Length <= 30)
            keywords.Insert(0, clean);

        return keywords;
    }

    private static bool IsHexHash(string s)
        => s.Length >= 10 && s.All(c => char.IsAsciiHexDigit(c));

    /// <summary>按进程名查找并激活窗口</summary>
    private static bool TryActivateByProcessName(string keyword)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;

                    if (proc.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        ActivateWindow(proc.MainWindowHandle);
                        Debug.WriteLine($"[Click] ✓ 通过进程名激活: {proc.ProcessName}");
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    /// <summary>按窗口标题查找并激活</summary>
    private static bool TryActivateByWindowTitle(string titleKeyword)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;

                    if (!string.IsNullOrEmpty(proc.MainWindowTitle)
                        && proc.MainWindowTitle.Contains(titleKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        ActivateWindow(proc.MainWindowHandle);
                        Debug.WriteLine($"[Click] ✓ 通过标题激活: {proc.MainWindowTitle}");
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    /// <summary>激活窗口（处理最小化的情况）</summary>
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
        _spectrumService.Dispose();
        _micService.Dispose();
        _mediaService.Dispose();
    }
}
