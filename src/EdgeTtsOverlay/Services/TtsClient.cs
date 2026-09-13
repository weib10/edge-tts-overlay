using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EdgeTtsOverlay.Models;

namespace EdgeTtsOverlay.Services;

public sealed record VoiceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("gender")] string Gender);

public interface ITtsSynthesizer
{
    Task<string> SynthesizeAsync(string text, AppSettings settings, CancellationToken cancellationToken);
}

public sealed class TtsClient(HttpClient httpClient) : ITtsSynthesizer
{
    public static void CleanupTempFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdgeTtsLocal");
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.mp3"))
            try { File.Delete(file); } catch { }
    }
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public const string VoiceListUnavailable = "就緒 · 語音清單暫時無法取得";

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/voices", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"無法取得語音清單（語音服務回覆 {(int)response.StatusCode}）。");
        return await response.Content.ReadFromJsonAsync<List<VoiceInfo>>(cancellationToken) ?? [];
    }

    /// <summary>啟動時的聲音檢查：清單取不到只降級狀態，朗讀本身不靠它。</summary>
    public async Task<string> DescribeVoiceStatusAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var voices = await GetVoicesAsync(cancellationToken);
            if (voices.Count == 0) return VoiceListUnavailable;
            return voices.Any(voice => voice.Id == voiceId) ? "就緒" : "預設聲音不存在，請在設定中選擇";
        }
        catch (OperationCanceledException) { throw; }
        catch { return VoiceListUnavailable; }
    }

    public async Task<string> SynthesizeAsync(string text, AppSettings settings, CancellationToken cancellationToken)
    {
        var payload = new { text, voice = settings.Voice, rate = settings.Rate, volume = settings.Volume, pitch = settings.Pitch };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await httpClient.PostAsJsonAsync("api/tts", payload, cancellationToken);
            var localBusy = response.StatusCode == HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("X-Edge-Tts-Error", out var values) && values.Contains("busy");
            if (localBusy && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 + attempt * 350 + Random.Shared.Next(0, 100)), cancellationToken);
                continue;
            }
            await EnsureSuccess(response);
            return await SaveTempFileAsync(response, cancellationToken);
        }
        throw new HttpRequestException("本機語音合成佇列持續忙碌，請停止目前朗讀後再試。");
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(response.StatusCode == HttpStatusCode.TooManyRequests
            ? "本機語音合成佇列忙碌，請稍候再試。"
            : $"語音服務回覆 {(int)response.StatusCode}：{message}");
    }

    private static async Task<string> SaveTempFileAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdgeTtsLocal");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.mp3");
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, cancellationToken);
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }
}
