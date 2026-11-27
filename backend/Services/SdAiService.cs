using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace SdKontrol.API.Services;

/// <summary>
/// SD Kontrol için bağımsız AI servisi.
/// Önce .env (AI_API_KEY vb.), boşsa aynı veritabanındaki SysJsonConfigs (Core/ai_config) veya ../backend/JSON/Core/ai_config.json üzerinden çalışır.
/// </summary>
public sealed class SdAiService
{
    private readonly EnvConfig _env;
    private readonly HttpClient _http;
    private readonly ILogger<SdAiService> _logger;

    public SdAiService(EnvConfig env, HttpClient http, ILogger<SdAiService> logger)
    {
        _env = env;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(180);
        _logger = logger;
    }

    private sealed record RuntimeAi(string Provider, string ApiUrl, string ApiKey, string Model);

    private async Task<RuntimeAi?> ResolveConfigAsync()
    {
        if (!string.IsNullOrWhiteSpace(_env.AiApiKey))
        {
            return new RuntimeAi(
                string.IsNullOrWhiteSpace(_env.AiProvider) ? "OpenAI" : _env.AiProvider,
                string.IsNullOrWhiteSpace(_env.AiApiUrl) ? "https://api.openai.com/v1" : _env.AiApiUrl,
                _env.AiApiKey,
                string.IsNullOrWhiteSpace(_env.AiModel) ? "gpt-4o-mini" : _env.AiModel
            );
        }

        // 1) Aynı PostgreSQL içindeki Kokpit SysJsonConfigs tablosunu dene (salt okunur)
        try
        {
            await using var conn = new NpgsqlConnection(_env.ConnectionString);
            await conn.OpenAsync();
            var json = await conn.ExecuteScalarAsync<string?>(@"
                SELECT ""JsonValue"" FROM public.""SysJsonConfigs""
                WHERE ""Category"" = 'Core' AND ""Key"" = 'ai_config'
                LIMIT 1");
            var parsed = ParseKokpitAiJson(json);
            if (parsed != null) return parsed;
        }
        catch { /* Tablo farklı şemada veya erişilemezse yerel dosyaya düş */ }

        // 2) ../backend/JSON/Core/ai_config.json yedeği
        try
        {
            var fallbackFile = Path.GetFullPath(Path.Combine(_env.SdRootDir, "..", "backend", "JSON", "Core", "ai_config.json"));
            if (File.Exists(fallbackFile))
            {
                var parsed = ParseKokpitAiJson(await File.ReadAllTextAsync(fallbackFile));
                if (parsed != null) return parsed;
            }
        }
        catch { }

        return null;
    }

    private static RuntimeAi? ParseKokpitAiJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var key = r.TryGetProperty("apiKey", out var kp) ? kp.GetString() : null;
        if (string.IsNullOrWhiteSpace(key) || key == "sk-dummy-key") return null;
        var provider = r.TryGetProperty("provider", out var pp) ? pp.GetString() ?? "OpenAI" : "OpenAI";
        var url = r.TryGetProperty("apiUrl", out var up) ? up.GetString() ?? "" : "";
        var model = r.TryGetProperty("model", out var mp) ? mp.GetString() ?? "gpt-4o" : "gpt-4o";
        return new RuntimeAi(provider, url, key, model);
    }

    public async Task<(string? yanit, string? model, string? hata)> TamamlaAsync(string systemPrompt, string userPrompt)
    {
        var cfg = await ResolveConfigAsync();
        if (cfg == null)
            return (null, null, "AI yapılandırması bulunamadı. SD/.env dosyasına AI_API_KEY ekleyin veya Kokpit AI ayarlarını kontrol edin.");

        try
        {
            // Google Gemini native API
            bool isGeminiNative = string.IsNullOrWhiteSpace(cfg.ApiUrl) &&
                (cfg.Provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || cfg.ApiKey.StartsWith("AIza"));

            if (isGeminiNative)
            {
                var gModel = string.IsNullOrWhiteSpace(cfg.Model) ? "gemini-2.5-flash" : cfg.Model;
                var gUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{gModel}:generateContent?key={cfg.ApiKey}";
                var gPayload = new
                {
                    systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                    generationConfig = new { temperature = 0.25, maxOutputTokens = 2048 }
                };
                using var gRes = await _http.PostAsync(gUrl, new StringContent(JsonSerializer.Serialize(gPayload), Encoding.UTF8, "application/json"));
                var gBody = await gRes.Content.ReadAsStringAsync();
                if (!gRes.IsSuccessStatusCode) return (null, gModel, $"Gemini API hatası ({(int)gRes.StatusCode}): {gBody}");
                using var gDoc = JsonDocument.Parse(gBody);
                var text = gDoc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                return (text?.Trim(), gModel, null);
            }

            // OpenAI / OpenRouter / vLLM / Ollama uyumlu endpoint
            var baseUrl = string.IsNullOrWhiteSpace(cfg.ApiUrl) ? "https://api.openai.com/v1" : cfg.ApiUrl.TrimEnd('/');
            var chatUrl = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : $"{baseUrl}/chat/completions";

            var model = string.IsNullOrWhiteSpace(cfg.Model) ? "gpt-4o-mini" : cfg.Model;
            using var req = new HttpRequestMessage(HttpMethod.Post, chatUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.ApiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.25,
                max_tokens = 4096,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            }), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                return (null, model, $"AI servis hatası ({(int)res.StatusCode}): {body[..Math.Min(body.Length, 400)]}");

            using var doc = JsonDocument.Parse(body);
            var msgEl = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var ans = msgEl.TryGetProperty("content", out var cp) ? cp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(ans))
                ans = Regex.Replace(ans, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
            return (ans, $"{cfg.Provider}/{model}", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SD AI] İstek hatası");
            return (null, cfg.Model, ex.Message);
        }
    }

    /// <summary>.docx veya .txt dosyasından düz metin çıkarır (Sözleşme ↔ Talep kıyaslaması için).</summary>
    public static string BelgedenMetinCikar(Stream stream, string dosyaAdi)
    {
        var ext = Path.GetExtension(dosyaAdi).ToLowerInvariant();
        if (ext == ".docx")
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidOperationException("Geçerli bir Word (.docx) belgesi değil.");
            using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
            var xml = sr.ReadToEnd();
            var sb = new StringBuilder();
            foreach (Match pm in Regex.Matches(xml, @"<w:p[ >].*?</w:p>", RegexOptions.Singleline))
            {
                var line = string.Concat(Regex.Matches(pm.Value, @"<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)));
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line.Trim());
            }
            return sb.ToString();
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
