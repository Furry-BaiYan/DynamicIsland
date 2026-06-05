using Windows.Media.Control;

namespace DynamicIsland.Audio;

/// <summary>
/// 监听系统全局媒体播放状态（支持 Spotify、网易云、QQ音乐、浏览器等）
/// 通过 Windows.Media.Control (SMTC) 获取歌曲信息
/// </summary>
public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>当前播放信息更新时触发</summary>
    public event Action<MediaInfo>? MediaChanged;

    /// <summary>播放停止或无会话时触发</summary>
    public event Action? MediaStopped;

    /// <summary>当前来源应用 ID（用于点击跳转）</summary>
    public string? SourceAppId => _session?.SourceAppUserModelId;

    // ── 初始化 ──

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnSessionChanged;

            // 立即检查是否已有播放中的会话
            var current = _manager.GetCurrentSession();
            if (current != null)
                await AttachSessionAsync(current);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaService] 初始化失败: {ex.Message}");
        }
    }

    // ── 会话切换 ──

    private async void OnSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        var newSession = sender.GetCurrentSession();
        if (newSession != null)
            await AttachSessionAsync(newSession);
        else
            MediaStopped?.Invoke();
    }

    private async Task AttachSessionAsync(GlobalSystemMediaTransportControlsSession session)
    {
        // 取消旧会话的事件监听
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }

        _session = session;
        _session.MediaPropertiesChanged += OnPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackChanged;

        await FetchAndNotifyAsync();
    }

    // ── 属性/播放状态变化 ──

    private async void OnPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        await FetchAndNotifyAsync();
    }

    private async void OnPlaybackChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        var playback = sender.GetPlaybackInfo();
        if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
            || playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
            || playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
        {
            MediaStopped?.Invoke();
        }
        else
        {
            await FetchAndNotifyAsync();
        }
    }

    // ── 读取媒体信息 ──

    private async Task FetchAndNotifyAsync()
    {
        if (_session == null) return;

        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            byte[]? coverData = null;

            // 读取封面缩略图
            if (props.Thumbnail != null)
                if (props.Thumbnail != null)
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    var size = (uint)stream.Size;
                    var buffer = new Windows.Storage.Streams.Buffer(size);
                    await stream.ReadAsync(buffer, size, Windows.Storage.Streams.InputStreamOptions.None);

                    using var dataReader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
                    coverData = new byte[buffer.Length];
                    dataReader.ReadBytes(coverData);
                }

            var info = new MediaInfo
            {
                Title = props.Title ?? "",
                Artist = props.Artist ?? "",
                CoverData = coverData,
                SourceAppId = _session.SourceAppUserModelId
            };

            MediaChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaService] 读取媒体信息失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }

        if (_manager != null)
            _manager.CurrentSessionChanged -= OnSessionChanged;
    }
}

// ── 媒体信息数据类 ──

public class MediaInfo
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public byte[]? CoverData { get; set; }
    public string? SourceAppId { get; set; }
}
