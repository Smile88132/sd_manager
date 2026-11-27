using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SdKontrol.API.Services;

public sealed class LlmSuiteSonuc
{
    public bool Ok { get; set; }
    public string? Yanit { get; set; }
    public string? Hata { get; set; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public long SureMs { get; set; }
    public string? HamYanit { get; set; }
}

/// <summary>
/// ASELSAN LLM Suite Flow (Langflow) /api/v1/run/{flowId} istemcisi.
/// Akış içindeki LLM Agent, bu uygulamanın MCP sunucusundaki (MCP_PORT) SD araçlarını çağırarak yorum üretir;
/// biz de akışın Chat Output yanıtını alıp ekrana getiririz.
/// </summary>
public sealed class LlmSuiteService
{
    private readonly EnvConfig _env;
    private readonly ILogger<LlmSuiteService> _logger;

    public LlmSuiteService(EnvConfig env, ILogger<LlmSuiteService> logger)
    {
        _env = env;
        _logger = logger;
    }

    private HttpClient YeniClient(int? timeoutSec = null)
    {
        var handler = new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials };
        if (!_env.LlmSuiteSslDogrula)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? _env.LlmSuiteTimeoutSec, 10, 1800)) };
    }

    public bool Yapilandirildi =>
        !string.IsNullOrWhiteSpace(_env.LlmSuiteBaseUrl) && !string.IsNullOrWhiteSpace(_env.LlmSuiteFlowId) && !string.IsNullOrWhiteSpace(_env.LlmSuiteApiKey);

    public static string VarsayilanPrompt(long surecNo, string? ekTalimat, bool kurallariDahilEt = false)
    {
        if (!kurallariDahilEt)
        {
            return $@"SD süreç no **{surecNo}** için bağımsız SAP SD ve veri tutarlılığı analizi yap.

1. MCP aracı `sd_talep_dataframe` (`surec_no={surecNo}`, `kurallari_dahil_et=false`) veya `sd_kalemler_dataframe` (`surec_no={surecNo}`, `kurallari_dahil_et=false`) ile yalnızca veritabanındaki ham başlık ve kalem DataFrame verilerini çek (sistemin iç kural motorunu çağırma).
2. İhtiyaç duyarsan `sd_sql_sorgu` ile veritabanındaki geçmiş benzer sipariş/müşteri/malzeme kayıtlarına bakabilirsin.
3. Kendi SAP SD, ERP, ticari sipariş ve tedarik zinciri bilginle verilerdeki eksiklikleri, riskleri, dikkat çeken noktaları ve telafi önerilerini özgürce yorumla.
4. Akış sistem mesajında tanımlı tüm font, renk, Markdown tablo, vurgu ve simge/emoji formatlarını eksiksiz koruyarak Türkçe yanıt üret.{(string.IsNullOrWhiteSpace(ekTalimat) ? "" : $"\n\nEk talimat: {ekTalimat}")}";
        }

        return $@"UGES TYM SAP SD (VA01) giriş uzmanı olarak SD süreç no **{surecNo}** için kapsamlı kural + eksik tamamlama analizi yap.

1. MCP aracı `sd_talep_dataframe` (`surec_no={surecNo}`, `kurallari_dahil_et=true`) ile başlık + kalem DataFrame ve kural bulgularını al.
2. MCP aracı `sd_eksik_alan_analizi` (`surec_no={surecNo}`) ile eksik/tutarsız alanları al; gerekirse `sd_sql_sorgu` ile geçmiş referansları kontrol et.
3. Akış sistem mesajında tanımlı tüm font, renk, Markdown tablo, vurgu ve simge/emoji formatlarını eksiksiz koruyarak Türkçe yanıt üret.{(string.IsNullOrWhiteSpace(ekTalimat) ? "" : $"\n\nEk talimat: {ekTalimat}")}";
    }

    public async Task<LlmSuiteSonuc> CalistirAsync(string mesaj, string? sessionId, CancellationToken ct,
        string? baseUrl = null, string? flowId = null, string? apiKey = null, int? timeoutSec = null)
    {
        var bUrl = (baseUrl ?? _env.LlmSuiteBaseUrl ?? "").Trim().TrimEnd('/');
        var fId = (flowId ?? _env.LlmSuiteFlowId ?? "").Trim();
        var key = (apiKey ?? _env.LlmSuiteApiKey ?? "").Trim();
        var sid = string.IsNullOrWhiteSpace(sessionId) ? $"uges-sd-{Guid.NewGuid():N}" : sessionId;

        if (string.IsNullOrEmpty(bUrl) || string.IsNullOrEmpty(fId) || string.IsNullOrEmpty(key))
            return new LlmSuiteSonuc { Ok = false, Hata = "LLM Suite ayarları eksik (Base URL / Flow ID / API Key). Üstteki '⚙ LLM Suite & MCP' ayarlarından girin." };

        // NOT: Önceki 400 Bad Request hatasının nedeni, örnek koddaki 'tweaks.ChatInput-xxx.files = [\"chat_file_path_1\"]'
        // gibi dosya yüklemeden gönderilen tweak'lerdi. Dosya yoksa tweaks GÖNDERİLMEZ.
        var payload = new JsonObject
        {
            ["input_value"] = mesaj,
            ["input_type"] = "chat",
            ["output_type"] = "chat",
            ["session_id"] = sid
        };
        if (!string.IsNullOrWhiteSpace(_env.LlmSuiteChatInputId))
            payload["tweaks"] = new JsonObject { [_env.LlmSuiteChatInputId] = new JsonObject { ["input_value"] = mesaj } };

        var url = $"{bUrl}/api/v1/run/{Uri.EscapeDataString(fId)}?stream=false";
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = YeniClient(timeoutSec);
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!res.IsSuccessStatusCode)
            {
                var kisa = body.Length > 1200 ? body[..1200] + "…" : body;
                return new LlmSuiteSonuc
                {
                    Ok = false, SessionId = sid, SureMs = sw.ElapsedMilliseconds,
                    Hata = $"LLM Suite Flow HTTP {(int)res.StatusCode} {res.ReasonPhrase}: {kisa}"
                };
            }

            var (metin, model) = YanitMetniCikar(body);
            return new LlmSuiteSonuc
            {
                Ok = !string.IsNullOrWhiteSpace(metin),
                Yanit = metin?.Trim(),
                Model = model,
                SessionId = sid,
                SureMs = sw.ElapsedMilliseconds,
                HamYanit = body.Length > 4000 ? body[..4000] : body,
                Hata = string.IsNullOrWhiteSpace(metin) ? "Akış yanıt döndü ancak Chat Output metni bulunamadı. Akışta 'Chat Output' bileşeni olduğundan emin olun." : null
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmSuiteSonuc
            {
                Ok = false, SessionId = sid, SureMs = sw.ElapsedMilliseconds,
                Hata = $"LLM Suite Flow {_env.LlmSuiteTimeoutSec} sn içinde yanıt vermedi (zaman aşımı). .env içindeki LLMSUITE_TIMEOUT_SEC değerini artırın veya akıştaki LLM Agent'ın MCP sunucusuna erişebildiğini kontrol edin."
            };
        }
        catch (HttpRequestException ex)
        {
            return new LlmSuiteSonuc
            {
                Ok = false, SessionId = sid, SureMs = sw.ElapsedMilliseconds,
                Hata = $"LLM Suite Flow bağlantı hatası: {ex.Message}{(ex.InnerException != null ? $" ({ex.InnerException.Message})" : "")}. Ağ/proxy veya SSL ayarlarını (LLMSUITE_SSL_DOGRULA) kontrol edin."
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LLM Suite] Çalıştırma hatası");
            return new LlmSuiteSonuc { Ok = false, SessionId = sid, SureMs = sw.ElapsedMilliseconds, Hata = ex.Message };
        }
    }

    /// <summary>Langflow run yanıtından Chat Output metnini çıkarır (farklı sürüm formatlarına dayanıklı).</summary>
    public static (string? metin, string? model) YanitMetniCikar(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            string? model = null;

            // Standart: outputs[0].outputs[0].results.message.text
            var outputs = root?["outputs"] as JsonArray;
            if (outputs != null)
            {
                foreach (var o in outputs)
                {
                    foreach (var inner in (o?["outputs"] as JsonArray) ?? new JsonArray())
                    {
                        var msg = inner?["results"]?["message"];
                        var t = msg?["text"]?.GetValue<string>()
                                ?? msg?["data"]?["text"]?.GetValue<string>()
                                ?? inner?["outputs"]?["message"]?["message"]?.GetValue<string>()
                                ?? inner?["messages"]?[0]?["message"]?.GetValue<string>()
                                ?? inner?["artifacts"]?["message"]?.GetValue<string>();
                        model ??= msg?["properties"]?["source"]?["display_name"]?.GetValue<string>()
                                  ?? msg?["sender_name"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(t)) return (t, model);
                    }
                }
            }

            // Yedek: JSON ağacında 'message' altındaki en uzun 'text' alanı
            string? enUzun = null;
            void Gez(JsonNode? n, bool mesajAltinda)
            {
                switch (n)
                {
                    case JsonObject obj:
                        foreach (var (k, v) in obj)
                        {
                            var alt = mesajAltinda || k is "message" or "results" or "messages";
                            if ((k is "text" or "message") && v is JsonValue jv && jv.TryGetValue<string>(out var s) && alt)
                            {
                                if (enUzun == null || s.Length > enUzun.Length) enUzun = s;
                            }
                            else Gez(v, alt);
                        }
                        break;
                    case JsonArray arr:
                        foreach (var x in arr) Gez(x, mesajAltinda);
                        break;
                }
            }
            Gez(root, false);
            return (enUzun, model);
        }
        catch
        {
            return (body, null);
        }
    }

    /// <summary>ASELSAN AI (OpenAI uyumlu) /models uç noktasından modelleri listeler.</summary>
    public async Task<(List<string> modeller, string? hata)> ModelleriGetirAsync(string apiUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            using var http = YeniClient(30);
            var url = apiUrl.TrimEnd('/');
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) url = url[..^"/chat/completions".Length];
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/models");
            if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (new(), $"HTTP {(int)res.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
            using var doc = JsonDocument.Parse(body);
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id)) list.Add(id.GetString() ?? "");
            return (list.Where(x => x.Length > 0).OrderBy(x => x).ToList(), null);
        }
        catch (Exception ex)
        {
            return (new(), ex.Message);
        }
    }
}
