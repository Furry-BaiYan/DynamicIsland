using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// 开机自启管理：通过注册表 HKCU\...\Run 实现，无需管理员权限
/// </summary>
public static class AutoStartService
{
    private const string AppName = "DynamicIsland";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 查询当前是否已设置开机自启
    /// </summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) is not null;
    }

    /// <summary>
    /// 开启或关闭开机自启
    /// </summary>
    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key is null) return;

        if (enable)
        {
            // 获取当前 exe 路径，加引号防止路径有空格
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
