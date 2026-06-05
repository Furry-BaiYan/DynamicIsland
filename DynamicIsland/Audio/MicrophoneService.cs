using NAudio.CoreAudioApi;

namespace DynamicIsland.Audio;

/// <summary>
/// 检测麦克风是否正在被使用
/// 遍历所有活跃的录音设备，检查是否有音频流量
/// </summary>
public sealed class MicrophoneService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private System.Timers.Timer? _pollTimer;
    private bool _wasMicActive;
    private int _inactiveCount;          // 连续静默计数（防抖）
    private const int DEACTIVATE_THRESHOLD = 6; // 连续 6 次静默才认为关闭（约 1.8 秒）

    public event Action? MicActivated;
    public event Action? MicDeactivated;
    public bool IsMicActive => _wasMicActive;

    public MicrophoneService()
    {
        _enumerator = new MMDeviceEnumerator();
    }

    public void Start(int pollIntervalMs = 300)
    {
        _pollTimer?.Stop();
        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => PollMicLevel();
        _pollTimer.AutoReset = true;
        _pollTimer.Start();

        System.Diagnostics.Debug.WriteLine("[Mic] 麦克风监听已启动");
    }

    public void Stop()
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void PollMicLevel()
    {
        try
        {
            bool anyActive = false;

            // ★ 遍历所有活跃的录音设备（不仅仅是默认设备）
            var devices = _enumerator.EnumerateAudioEndPoints(
                DataFlow.Capture, DeviceState.Active);

            foreach (var device in devices)
            {
                try
                {
                    float peak = device.AudioMeterInformation.MasterPeakValue;
                    if (peak > 0.005f)
                    {
                        anyActive = true;
                        System.Diagnostics.Debug.WriteLine(
                            $"[Mic] 检测到活跃: {device.FriendlyName}, peak={peak:F4}");
                        break;
                    }
                }
                catch { }
            }

            // 状态变化判定（带防抖）
            if (anyActive)
            {
                _inactiveCount = 0;

                if (!_wasMicActive)
                {
                    _wasMicActive = true;
                    System.Diagnostics.Debug.WriteLine("[Mic] → 麦克风已激活");
                    MicActivated?.Invoke();
                }
            }
            else
            {
                if (_wasMicActive)
                {
                    _inactiveCount++;

                    // 连续多次静默才触发关闭，避免说话间隙误判
                    if (_inactiveCount >= DEACTIVATE_THRESHOLD)
                    {
                        _wasMicActive = false;
                        _inactiveCount = 0;
                        System.Diagnostics.Debug.WriteLine("[Mic] → 麦克风已关闭");
                        MicDeactivated?.Invoke();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Mic] 轮询异常: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _enumerator.Dispose();
    }
}
