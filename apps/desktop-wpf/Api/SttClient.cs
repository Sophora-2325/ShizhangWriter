using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

public sealed class SttClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private bool _disposed;

    public SttClient(string? baseUrl = null)
    {
        _http = new HttpClient(new LanguageHeaderHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(baseUrl ?? BackendLauncher.BaseUrl),
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    public async Task<SttStatusDto> StatusAsync()
    {
        var resp = await _http.GetAsync("/stt/status");
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractMessage(text, resp.StatusCode.ToString()));
        return JsonSerializer.Deserialize<SttStatusDto>(text, JsonOpts)
               ?? throw new InvalidOperationException(L.T("Api.EmptyResponse"));
    }

    public async Task<SttResultDto> TranscribeAsync(string wavPath, string lang = "zh")
    {
        await using var fs = File.OpenRead(wavPath);
        return await TranscribeAsync(fs, Path.GetFileName(wavPath), lang);
    }

    public async Task<SttResultDto> TranscribeAsync(Stream wav, string fileName = "audio.wav", string lang = "zh")
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(wav);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(streamContent, "file", fileName);
        content.Add(new StringContent(lang), "lang");
        var resp = await _http.PostAsync("/stt/transcribe", content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractMessage(text, resp.StatusCode.ToString()));
        return JsonSerializer.Deserialize<SttResultDto>(text, JsonOpts)
               ?? throw new InvalidOperationException(L.T("Api.EmptyResponse"));
    }

    private static string ExtractMessage(string text, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? fallback;
                if (detail.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? fallback;
            }
        }
        catch { /* ignore */ }
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private sealed class LanguageHeaderHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.AcceptLanguage.Clear();
            request.Headers.AcceptLanguage.ParseAdd(LanguageManager.Current);
            return base.SendAsync(request, ct);
        }
    }
}
