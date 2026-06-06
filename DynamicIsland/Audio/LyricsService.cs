using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Audio;

/// <summary>
/// 歌词服务：根据歌名/歌手在线搜索歌词，解析 LRC 时间轴，同步当前播放位置
/// 数据源：网易云音乐 API（覆盖绝大多数中文歌曲）
/// </summary>
public sealed class LyricsService : IDisposable
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private List<LyricLine> _lines = [];
    private int _currentIndex = -1;
    private string _currentSongKey = "";

    /// <summary>当歌词行变化时触发，参数为当前歌词文本</summary>
    public event Action<string>? LyricChanged;

    /// <summary>歌词清空时触发</summary>
    public event Action? LyricCleared;

    /// <summary>当前是否有歌词</summary>
    public bool HasLyrics => _lines.Count > 0;

    /// <summary>当前歌词行文本</summary>
    public string CurrentLine => _currentIndex >= 0 && _currentIndex < _lines.Count
        ? _lines[_currentIndex].Text : "";

    // ═══════════════════════════════════════════════
    //  搜索并加载歌词
    // ═══════════════════════════════════════════════

    public async Task LoadLyricsAsync(string title, string artist)
    {
        _currentSongKey = $"{title}|{artist}";
        _lines.Clear();
        _currentIndex = -1;

        try
        {
            // 优先 QQ 音乐
            Debug.WriteLine($"[Lyrics] QQ音乐搜索: {title} - {artist}");
            var songmid = await QQSearchSongMidAsync(title, artist);
            if (!string.IsNullOrEmpty(songmid))
            {
                var lrc = await QQFetchLrcAsync(songmid);
                if (!string.IsNullOrWhiteSpace(lrc))
                {
                    _lines = ParseLrc(lrc);
                    if (_lines.Count > 0)
                    {
                        Debug.WriteLine($"[Lyrics] QQ音乐加载成功: {_lines.Count} 行");
                        return;
                    }
                }
                Debug.WriteLine($"[Lyrics] QQ音乐无歌词: mid={songmid}");
            }
            else
            {
                Debug.WriteLine("[Lyrics] QQ音乐未找到歌曲");
            }

            // 备用：网易云
            Debug.WriteLine("[Lyrics] 尝试网易云...");
            var songId = await SearchSongIdAsync(title, artist);
            if (songId > 0)
            {
                var lrc = await FetchLrcAsync(songId);
                if (!string.IsNullOrWhiteSpace(lrc))
                {
                    _lines = ParseLrc(lrc);
                    if (_lines.Count > 0)
                    {
                        Debug.WriteLine($"[Lyrics] 网易云加载成功: {_lines.Count} 行");
                        return;
                    }
                }
            }

            Debug.WriteLine("[Lyrics] 所有源都无歌词");
            LyricCleared?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lyrics] 加载失败: {ex.Message}");
            LyricCleared?.Invoke();
        }
    }

    // ═══════════════════════════════════════════════
    //  根据播放位置更新当前歌词行
    // ═══════════════════════════════════════════════

    public void UpdatePosition(TimeSpan position)
    {
        if (_lines.Count == 0) return;

        // 找到当前时间对应的歌词行（最后一个 time <= position 的行）
        int newIndex = -1;
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].Time <= position)
            {
                newIndex = i;
                break;
            }
        }

        if (newIndex != _currentIndex)
        {
            _currentIndex = newIndex;
            var text = _currentIndex >= 0 ? _lines[_currentIndex].Text : "";

            // 跳过空行和纯音乐标记
            if (!string.IsNullOrWhiteSpace(text))
                LyricChanged?.Invoke(text);
        }
    }

    /// <summary>清空歌词状态</summary>
    public void Clear()
    {
        _lines.Clear();
        _currentIndex = -1;
        _currentSongKey = "";
        LyricCleared?.Invoke();
    }

    // ═══════════════════════════════════════════════
    //  网易云音乐 API
    // ═══════════════════════════════════════════════

    private static async Task<string?> QQSearchSongMidAsync(string title, string artist)
    {
        var query = Uri.EscapeDataString($"{title} {artist}");
        var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={query}&format=json&p=1&n=5";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Referer", "https://y.qq.com/");
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("song", out var song)
            && song.TryGetProperty("list", out var list)
            && list.GetArrayLength() > 0)
        {
            foreach (var item in list.EnumerateArray())
            {
                var name = item.GetProperty("songname").GetString() ?? "";
                if (name.Equals(title, StringComparison.OrdinalIgnoreCase))
                    return item.GetProperty("songmid").GetString();
            }
            return list[0].GetProperty("songmid").GetString();
        }
        return null;
    }

    private static async Task<string?> QQFetchLrcAsync(string songmid)
    {
        var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={songmid}&format=json&nobase64=1";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Referer", "https://y.qq.com/");
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("lyric", out var lyric))
        {
            var lrcText = lyric.GetString();
            if (!string.IsNullOrWhiteSpace(lrcText))
                return lrcText;
        }
        return null;
    }

    private static async Task<long> SearchSongIdAsync(string title, string artist)
    {
        var query = $"{title} {artist}".Trim();
        var url = $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(query)}&type=1&limit=5&offset=0";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Referer", "https://music.163.com/");
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("result", out var result)
            && result.TryGetProperty("songs", out var songs)
            && songs.GetArrayLength() > 0)
        {
            // 优先匹配标题完全一致的
            foreach (var song in songs.EnumerateArray())
            {
                var name = song.GetProperty("name").GetString() ?? "";
                if (name.Equals(title, StringComparison.OrdinalIgnoreCase))
                    return song.GetProperty("id").GetInt64();
            }

            // 否则取第一个结果
            return songs[0].GetProperty("id").GetInt64();
        }

        return -1;
    }

    private static async Task<string?> FetchLrcAsync(long songId)
    {
        var url = $"https://music.163.com/api/song/lyric?id={songId}&lv=1";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Referer", "https://music.163.com/");
        request.Headers.Add("User-Agent", "Mozilla/5.0");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("lrc", out var lrc)
            && lrc.TryGetProperty("lyric", out var lyricText))
        {
            return lyricText.GetString();
        }

        return null;
    }

    // ═══════════════════════════════════════════════
    //  LRC 解析
    // ═══════════════════════════════════════════════

    private static readonly Regex LrcPattern =
        new(@"\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)");

    private static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();

        foreach (var rawLine in lrc.Split('\n'))
        {
            var match = LrcPattern.Match(rawLine.Trim());
            if (!match.Success) continue;

            int minutes = int.Parse(match.Groups[1].Value);
            int seconds = int.Parse(match.Groups[2].Value);
            var msStr = match.Groups[3].Value;
            int ms = int.Parse(msStr) * (msStr.Length == 2 ? 10 : 1);

            var text = match.Groups[4].Value.Trim();

            // 跳过空行、元数据行
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.StartsWith("作词") || text.StartsWith("作曲")
                || text.StartsWith("编曲") || text.StartsWith("制作")
                || text.StartsWith("词：") || text.StartsWith("词:")
                || text.StartsWith("曲：") || text.StartsWith("曲:")
                || text.StartsWith("编：") || text.StartsWith("编:")
                || text.StartsWith("混音") || text.StartsWith("母带")
                || text.StartsWith("录音") || text.StartsWith("监制")
                || text.StartsWith("制作人") || text.StartsWith("出品")
                || text.StartsWith("发行") || text.StartsWith("OP")
                || text.StartsWith("SP") || text.StartsWith("ST")
                || text.StartsWith("Lyrics") || text.StartsWith("Compose")
                || text.StartsWith("Arrange") || text.StartsWith("Produce")
                || text.StartsWith("Mix") || text.StartsWith("Master")
                || text.StartsWith("Written") || text.StartsWith("Music")
                || (text.Contains('：') && text.Length < 20 && !text.Contains('，') && !text.Contains('。')))
                continue;

            var time = new TimeSpan(0, 0, minutes, seconds, ms);
            lines.Add(new LyricLine(time, text));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    public void Dispose() { }
}

// ── 歌词行数据结构 ──

public record LyricLine(TimeSpan Time, string Text);
