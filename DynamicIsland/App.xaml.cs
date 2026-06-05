using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 创建主窗口 ──
        _mainWindow = new MainWindow();
        _mainWindow.Show();

        // ── 创建托盘图标 ──
        _trayIcon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Resources/icon.ico")),
            ToolTipText = "Dynamic Island",
            ContextMenu = BuildTrayMenu()
        };

        // 双击托盘图标 → 显示/隐藏
        _trayIcon.TrayMouseDoubleClick += (_, _) => ToggleIslandVisibility();
    }

    /// <summary>
    /// 构建托盘右键菜单
    /// </summary>
    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        // ── 显示 / 隐藏 ──
        var showHideItem = new MenuItem { Header = "显示 / 隐藏" };
        showHideItem.Click += (_, _) => ToggleIslandVisibility();
        menu.Items.Add(showHideItem);

        menu.Items.Add(new Separator());

        // ── 开机启动 ──
        var autoStartItem = new MenuItem
        {
            Header = "开机启动",
            IsCheckable = true,
            IsChecked = AutoStartService.IsEnabled()
        };
        autoStartItem.Click += (_, _) =>
        {
            AutoStartService.SetEnabled(autoStartItem.IsChecked);
        };
        menu.Items.Add(autoStartItem);

        menu.Items.Add(new Separator());

        // ── 退出 ──
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) =>
        {
            _trayIcon?.Dispose();
            _mainWindow?.Close();
            Shutdown();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ToggleIslandVisibility()
    {
        if (_mainWindow == null) return;

        if (_mainWindow.IsVisible)
            _mainWindow.Hide();
        else
        {
            _mainWindow.Show();
            _mainWindow.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
