<div align="center">

# 🏝️ Dynamic Island for Windows

**将 iOS 灵动岛带到 Windows 桌面**

![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue?style=flat-square)
![Framework](https://img.shields.io/badge/.NET-10.0-purple?style=flat-square)
![UI](https://img.shields.io/badge/UI-WPF-green?style=flat-square)
![License](https://img.shields.io/badge/License-MIT-yellow?style=flat-square)

一款灵感来自 Apple iOS 灵动岛（Dynamic Island）的 Windows 桌面悬浮工具，集成音乐控制、实时歌词、消息通知、来电显示等功能。

</div>

---

## ✨ 功能特性

### 🎵 音乐控制
- **自动识别**正在播放的音乐（支持 QQ音乐、Spotify、网易云等所有走系统媒体控件的播放器）
- 显示**歌曲封面**（圆形裁剪）、**歌名**、**歌手**
- **实时频谱声波条**：8根频谱柱随音乐节奏跳动（FFT 频谱分析）
- **展开控制面板**：点击灵动岛向下展开，显示进度条、播放/暂停、上一首/下一首
- **实时歌词同步**：优先 QQ 音乐歌词源，网易云作为备用

### 📱 消息通知
- 监听 **Telegram** 消息通知（通过 Windows 系统 Toast 捕获）
- 灵动岛内**滚动显示**通知内容，3秒后自动消失
- 显示应用图标（首字母圆形）+ 发送人 + 消息内容

### 📞 来电显示
- 检测语音/视频通话邀请（Telegram）
- 灵动岛**左右扩展**显示来电人 + 接听/挂断按钮
- 点击接听/挂断通过 **UI Automation** 自动操作对应应用
- 操作后显示状态 4 秒，再弹性收缩回音乐显示

### 🎤 麦克风检测
- 实时检测麦克风使用状态
- 麦克风激活时从灵动岛右侧**分裂弹出**绿色麦克风图标
- 停用后弹性缩回

### 🎨 视觉效果
- **半透明毛玻璃**背景 + 微光边框 + 高光条
- **深色/浅色**模式切换（托盘菜单）
- iOS 风格**弹性动画**：展开、收缩、悬停、按压、回弹
- **紧凑态**小药丸 ↔ **展开态**完整信息，弹性膨胀/收缩过渡
- 切歌时内容**淡入淡出**平滑过渡，灵动岛本体不抖动

### ⚙️ 系统集成
- **屏幕顶部居中**置顶显示，不出现在 Alt+Tab
- **多显示器** / DPI 缩放自适应
- **开机自启**（注册表，托盘菜单可开关）
- **托盘图标**：显示/隐藏、开机启动、深色/浅色切换、退出

---

## 📸 预览

| 紧凑态 | 音乐播放 | 展开控制 |
|:---:|:---:|:---:|
| 小药丸待机 | 封面 + 歌名 + 频谱 | 歌词 + 进度条 + 按钮 |

| 消息通知 | 来电显示 | 麦克风检测 |
|:---:|:---:|:---:|
| 滚动显示消息 | 接听/挂断按钮 | 分裂弹出图标 |

---

## 🛠️ 技术栈

| 技术 | 用途 |
|---|---|
| **WPF (.NET 10)** | 桌面 UI 框架 |
| **Windows.Media.Control** | 系统媒体信息监听（SMTC） |
| **NAudio** | 音频频谱捕获、麦克风检测 |
| **SetWinEventHook** | 系统通知/窗口事件监听 |
| **UI Automation** | 通知内容读取、来电按钮操作 |
| **Hardcodet.NotifyIcon.Wpf** | 系统托盘图标 |
| **网易云/QQ音乐 API** | 在线歌词获取 |

---

## 📦 安装

### 直接运行（推荐）

1. 前往 [Releases](../../releases) 下载最新版 `DynamicIsland.exe`
2. 双击运行
3. 灵动岛出现在屏幕顶部中央

### 从源码构建

**环境要求：**
- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022+（勾选 .NET 桌面开发工作负载）

```bash
# 克隆仓库
git clone https://github.com/yourusername/DynamicIsland.git
cd DynamicIsland/DynamicIsland

# 还原依赖
dotnet restore

# 调试运行
dotnet run

# 发布为独立 exe
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

---

## 📁 项目结构

```
DynamicIsland/
├── App.xaml / App.xaml.cs              # 应用入口、托盘图标
├── MainWindow.xaml / .xaml.cs          # 灵动岛主窗口（UI + 逻辑）
├── Audio/
│   ├── MediaService.cs                 # 系统媒体监听（歌曲信息、封面、控制）
│   ├── SpectrumService.cs              # FFT 音频频谱分析
│   ├── MicrophoneService.cs            # 麦克风使用检测
│   └── LyricsService.cs               # 在线歌词搜索与同步
├── Services/
│   ├── AutoStartService.cs             # 开机自启（注册表）
│   └── NotificationService.cs          # 系统通知/来电检测
├── Helpers/
│   └── NativeMethods.cs                # Win32 API 封装
└── Resources/
    ├── icon.ico                        # 应用图标
    └── default_cover.png               # 默认封面占位图
```

---

## 🎮 使用说明

| 操作 | 效果 |
|---|---|
| **鼠标悬停**灵动岛 | 轻微放大 |
| **点击**灵动岛 | 展开/收回音乐控制面板 |
| 播放/暂停音乐 | 灵动岛自动膨胀显示歌曲信息 |
| 停止音乐 | 灵动岛收缩为小药丸 |
| 收到 Telegram 消息 | 灵动岛滚动显示通知 3 秒 |
| 收到语音通话邀请 | 灵动岛扩展显示接听/挂断 |
| **右键**托盘图标 | 显示/隐藏、开机启动、深浅模式、退出 |

---

## ⚠️ 已知限制

- **频谱可视化**依赖 WASAPI Loopback 捕获系统音频输出，部分虚拟声卡（如 SteelSeries Sonar）可能导致频谱不准确
- **歌词同步**依赖在线 API（QQ音乐/网易云），部分新歌或冷门歌曲可能无歌词
- **通知捕获**通过 `SetWinEventHook` + `ShellExperienceHost` 实现，仅能捕获走 Windows 系统 Toast 的通知
- **来电操作**通过 UI Automation 模拟点击实现，对 Chromium 渲染的应用（QQ NT）使用鼠标坐标模拟
- 独占全屏游戏/应用会遮挡灵动岛

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

1. Fork 本仓库
2. 创建功能分支：`git checkout -b feature/amazing-feature`
3. 提交更改：`git commit -m 'Add amazing feature'`
4. 推送分支：`git push origin feature/amazing-feature`
5. 提交 Pull Request

---

## 📄 License

本项目采用 [MIT License](LICENSE) 开源。

---

<div align="center">

**如果觉得有用，请给个 ⭐ Star！**

</div>
