using System.Runtime.InteropServices;

namespace DynamicIsland.Helpers;

public static class NativeMethods
{
    // ── 窗口扩展样式 ──
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ── 窗口置顶 ──
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    // ── 激活外部窗口 ──
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ── 显示窗口（从最小化恢复等） ──
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    // ── 检查窗口是否最小化 ──
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    // ── 鼠标模拟 ──
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    // ── 全局热键 ──
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_NONE   = 0x0000;
    public const uint VK_F9      = 0x78;
    public const int  WM_HOTKEY  = 0x0312;
    public const int  HOTKEY_F9  = 9001;
}
