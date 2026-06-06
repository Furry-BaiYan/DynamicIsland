using Windows.Media.Control;

namespace DynamicIsland.Audio;

/// <summary>
/// 监听系统全局媒体播放状态
/// </summary>
public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public event Action<MediaInfo>? MediaChanged;
    public event Action? MediaStopped;

    public string? SourceAppId => _session?.SourceAppUserModelId;

    /// <summary>获取当前播放位置（用于歌词同步）</summary>
    public TimeSpan GetCurrentPosition()
    {
        if (_session == null) return TimeSpan.Zero;

        try
        {
            var timeline = _session.GetTimelineProperties();
            var playback = _session.GetPlaybackInfo();

            // 如果正在播放，用 LastUpdatedTime 补偿到当前实际位置
            if (playback.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                return timeline.Position + elapsed;
            }

            return timeline.Position;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>当前是否正在播放</summary>
    public bool IsPlaying
    {
        get
        {
            try
            {
                var info = _session?.GetPlaybackInfo();
                return info?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { return false; }
        }
    }

    public async Task PlayAsync()
    {
        if (_session != null)
        {
            await _session.TryPlayAsync();
            System.Diagnostics.Debug.WriteLine("[Media] → Play");
        }
    }

    public async Task PauseAsync()
    {
        if (_session != null)
        {
            await _session.TryPauseAsync();
            System.Diagnostics.Debug.WriteLine("[Media] → Pause");
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_session == null) return;
        var info = _session.GetPlaybackInfo();
        if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            await _session.TryPauseAsync();
        else
            await _session.TryPlayAsync();
    }

    public async Task SkipNextAsync()
    {
        if (_session != null) await _session.TrySkipNextAsync();
    }

    public async Task SkipPreviousAsync()
    {
        if (_session != null) await _session.TrySkipPreviousAsync();
    }

    public double GetProgress()
    {
        if (_session == null) return 0;
        try
        {
            var timeline = _session.GetTimelineProperties();
            var total = timeline.EndTime - timeline.StartTime;
            if (total.TotalSeconds < 1) return 0;
            var pos = GetCurrentPosition();
            return Math.Clamp(pos.TotalSeconds / total.TotalSeconds, 0, 1);
        }
        catch { return 0; }
    }

    public TimeSpan GetDuration()
    {
        try
        {
            var timeline = _session?.GetTimelineProperties();
            return timeline != null ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
        }
        catch { return TimeSpan.Zero; }
    }

    // ── 初始化 ──

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnSessionChanged;

            var current = _manager.GetCurrentSession();
            if (current != null)
                await AttachSessionAsync(current);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaService] 初始化失败: {ex.Message}");
        }
    }

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

    private async Task FetchAndNotifyAsync()
    {
        if (_session == null) return;

        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            byte[]? coverData = null;

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

public class MediaInfo
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public byte[]? CoverData { get; set; }
    public string? SourceAppId { get; set; }
}
