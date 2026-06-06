using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace DynamicIsland.Services;

public class NotificationData
{
    public string AppName  { get; set; } = "";
    public string Title    { get; set; } = "";
    public string Content  { get; set; } = "";
    public byte[]? AppIcon { get; set; }
    public bool IsPhoneCall { get; set; }
}

public sealed class NotificationService : IDisposable
{
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess,
        uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint EVENT_OBJECT_CREATE     = 0x8000;
    private const uint EVENT_OBJECT_SHOW       = 0x8002;
    private const uint EVENT_SYSTEM_ALERT      = 0x0002;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // QQ/WeChat 等都走 ShellExperienceHost 系统通知，不直接匹配进程
    private static readonly Dictionary<string, string> WatchedProcesses =
        new(StringComparer.OrdinalIgnoreCase)
    {
    };

    // 系统通知宿主进程 —— 弹出的窗口需要读取内容来判断来自哪个 App
    private static readonly HashSet<string> NotificationHosts =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost",
    };

    // 宿主路径下从文本内容识别 App 的关键词
    private static readonly string[] AppKeywords =
        ["QQ", "微信", "WeChat", "Discord", "Telegram", "钉钉", "来电", "电话"];

    private static readonly string[] CallKeywords =
        ["来电", "呼叫", "incoming call", "正在呼叫", "视频通话", "语音通话", "calling"];

    public event Action<NotificationData>? NotificationReceived;
    public event Action<NotificationData>? PhoneCallReceived;

    private IntPtr _hook1;
    private IntPtr _hook2;
    private WinEventDelegate? _hookProc;   // 防止被 GC 回收
    private readonly HashSet<string> _recentNotifs = [];
    private DateTime _lastNotifTime = DateTime.MinValue;

    public Task InitializeAsync()
    {
        _hookProc = OnWinEvent;

        // hook1：窗口创建和显示
        _hook1 = SetWinEventHook(
            EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW,
            IntPtr.Zero, _hookProc,
            0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // hook2：前台切换和警告
        _hook2 = SetWinEventHook(
            EVENT_SYSTEM_ALERT, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc,
            0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        Debug.WriteLine(_hook1 != IntPtr.Zero
            ? "[Notif] SetWinEventHook 已启动"
            : "[Notif] SetWinEventHook 启动失败");

        return Task.CompletedTask;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;

            Process? proc;
            try { proc = Process.GetProcessById((int)pid); }
            catch { return; }

            var processName = proc.ProcessName;

            bool isDirect = WatchedProcesses.TryGetValue(processName, out var appName);
            bool isHost   = !isDirect && NotificationHosts.Contains(processName);
            if (!isDirect && !isHost) return;

            // 直接进程才做 visible 检查；宿主进程 Toast 刚出现时可能还未 visible
            if (isDirect && !IsWindowVisible(hwnd)) return;

            var sbClass = new StringBuilder(256);
            GetClassNameW(hwnd, sbClass, 256);
            var className = sbClass.ToString();

            var sbTitle = new StringBuilder(512);
            GetWindowTextW(hwnd, sbTitle, 512);
            var windowText = sbTitle.ToString();

            // 路径 A（直接进程）：按尺寸过滤主窗口
            if (isDirect)
            {
                GetWindowRect(hwnd, out var rect);
                int w = rect.Right  - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w > 1000 || h > 800 || w < 10 || h < 10) return;
            }

            // 防重复：500 ms 内同一进程只处理一次
            var dedupeKey = isDirect ? processName : $"host_{processName}";
            if ((DateTime.Now - _lastNotifTime).TotalMilliseconds < 500
                && _recentNotifs.Contains(dedupeKey))
            {
                Debug.WriteLine($"[Notif] {processName} 被跳过: 防重复");
                return;
            }

            _lastNotifTime = DateTime.Now;
            _recentNotifs.Clear();
            _recentNotifs.Add(dedupeKey);

            // 路径 B（宿主进程 ShellExperienceHost）：异步处理，避免阻塞 UI 线程
            if (isHost)
            {
                var capturedHwnd       = hwnd;
                var capturedWinText    = windowText;
                var capturedProcName   = processName;
                Task.Run(async () =>
                {
                    await Task.Delay(300); // 等 Toast 渲染完成

                    try
                    {
                        var texts = new List<string>();
                        try
                        {
                            var element = AutomationElement.FromHandle(capturedHwnd);
                            ExtractTexts(element, texts, 0);
                        }
                        catch { }

                        var allTexts = texts.Count > 0 ? texts : [capturedWinText];
                        var allText  = string.Join(" ", allTexts);
                        Debug.WriteLine($"[Notif] {capturedProcName} 提取文本: [{string.Join(", ", allTexts)}]");

                        if (string.IsNullOrWhiteSpace(allText) || allText.Length < 2)
                        {
                            Debug.WriteLine($"[Notif] {capturedProcName} 被跳过: 文本为空");
                            return;
                        }

                        string[] systemPatterns = ["新通知", "此通知的设置", "将此通知移动到通知中心",
                            "New notification", "Notification settings", "Move to",
                            "第 ", "共 ", "项，"];

                        var cleanTexts = allTexts
                            .Where(t => !systemPatterns.Any(p => t.Contains(p, StringComparison.OrdinalIgnoreCase)))
                            .Where(t => t.Length >= 1 && t.Length < 100)
                            .Where(t => !t.StartsWith("来自"))
                            .ToList();

                        Debug.WriteLine($"[Notif] 清洗后文本: [{string.Join(", ", cleanTexts)}]");

                        string[] appKeywords = ["QQ", "微信", "WeChat", "Discord", "Telegram", "钉钉"];
                        string? foundApp = null;
                        string? matchedKeyword = null;
                        foreach (var kw in appKeywords)
                        {
                            if (cleanTexts.Any(t => t.Equals(kw, StringComparison.OrdinalIgnoreCase)))
                            {
                                foundApp = kw;
                                matchedKeyword = kw;
                                break;
                            }
                        }
                        if (foundApp == null)
                        {
                            foreach (var kw in appKeywords)
                            {
                                if (allText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                                {
                                    foundApp = kw;
                                    matchedKeyword = kw;
                                    break;
                                }
                            }
                        }
                        if (foundApp == null)
                        {
                            Debug.WriteLine($"[Notif] {capturedProcName} 宿主通知: 未匹配到关注的 App");
                            return;
                        }

                        var contentTexts = cleanTexts
                            .Where(t => !t.Equals(matchedKeyword, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        var sender  = contentTexts.Count > 0 ? contentTexts[0] : foundApp;
                        var message = contentTexts.Count > 1 ? contentTexts[1] : "";

                        var data = new NotificationData
                        {
                            AppName     = foundApp,
                            Title       = sender,
                            Content     = message,
                            IsPhoneCall = CallKeywords.Any(kw => allText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        };

                        Debug.WriteLine($"[Notif] ✓ 通知: App={data.AppName}, 发送人={data.Title}, 内容={data.Content}");

                        if (data.IsPhoneCall)
                            PhoneCallReceived?.Invoke(data);
                        else
                            NotificationReceived?.Invoke(data);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Notif] 宿主异步处理异常: {ex.Message}");
                    }
                });
                return;
            }

            // 路径 A（直接进程）：同步提取文本后组装通知
            var directTexts = new List<string>();
            try
            {
                var element = AutomationElement.FromHandle(hwnd);
                ExtractTexts(element, directTexts, 0);
            }
            catch { }

            var directAllTexts = directTexts.Count > 0 ? directTexts : [windowText];
            var directAllText  = string.Join(" ", directAllTexts);
            Debug.WriteLine($"[Notif] {processName} 提取文本: [{string.Join(", ", directAllTexts)}]");

            if (string.IsNullOrWhiteSpace(directAllText) || directAllText.Length < 2)
            {
                Debug.WriteLine($"[Notif] {processName} 被跳过: 文本为空");
                return;
            }

            string[] directSystemTexts = ["新通知", "此通知的设置", "将此通知移动到通知中心",
                "New notification", "Notification settings", "Move to notification center",
                "通知", "第 ", "共 "];

            var directCleanTexts = directAllTexts
                .Where(t => !directSystemTexts.Any(s => t.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Where(t => t.Length >= 2 && t.Length < 100)
                .ToList();

            var fromText = directAllTexts.FirstOrDefault(t => t.Contains("来自") && t.Contains("的新通知"));
            string directSender = "";
            if (fromText != null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    fromText, @"来自\s*\S+[、，]\s*(.+?)[、，\d]");
                if (match.Success)
                    directSender = match.Groups[1].Value.Trim();
            }

            if (string.IsNullOrEmpty(directSender) && directCleanTexts.Count > 0)
                directSender = directCleanTexts.Last();

            var directData = new NotificationData
            {
                AppName     = appName!,
                Title       = !string.IsNullOrEmpty(directSender) ? directSender : appName!,
                Content     = directCleanTexts.FirstOrDefault(t => t != directSender && t != appName) ?? "",
                IsPhoneCall = CallKeywords.Any(kw => directAllText.Contains(kw, StringComparison.OrdinalIgnoreCase))
            };

            Debug.WriteLine($"[Notif] ✓ 发送通知: App={directData.AppName}, Title={directData.Title}, Content={directData.Content}");

            if (directData.IsPhoneCall)
                PhoneCallReceived?.Invoke(directData);
            else
                NotificationReceived?.Invoke(directData);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notif] 异常: {ex.Message}");
        }
    }

    private static void ExtractTexts(AutomationElement element, List<string> texts, int depth)
    {
        if (depth > 8) return;
        try
        {
            var name = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(name) && name.Length is > 1 and < 200
                && !texts.Contains(name))
                texts.Add(name);

            var walker = TreeWalker.ContentViewWalker;
            var child  = walker.GetFirstChild(element);
            while (child != null)
            {
                ExtractTexts(child, texts, depth + 1);
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { }
    }

    public void Dispose()
    {
        if (_hook1 != IntPtr.Zero) { UnhookWinEvent(_hook1); _hook1 = IntPtr.Zero; }
        if (_hook2 != IntPtr.Zero) { UnhookWinEvent(_hook2); _hook2 = IntPtr.Zero; }
    }
}
