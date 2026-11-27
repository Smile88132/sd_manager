using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Dapper;
using Npgsql;

namespace SdKontrol.API.Services;

/// <summary>
/// UGES SD Kontrol — MCP (Model Context Protocol) Sunucusu.
/// Ana uygulamadan bağımsız bir portta (MCP_PORT, varsayılan 5101) çalışır ve ASELSAN LLM Suite Flow
/// içindeki 'MCP Tools' bileşenine SD talep / kalem verilerini DataFrame (Markdown tablo + JSON records) olarak sunar.
///
/// Desteklenen taşıma (transport) yöntemleri:
///   • Streamable HTTP : POST http://HOST:5101/mcp
///   • SSE (eski)      : GET  http://HOST:5101/sse   +  POST /messages?session_id=...
/// </summary>
public sealed class SdMcpServer
{
    private static readonly string[] DesteklenenSurumler = { "2025-06-18", "2025-03-26", "2024-11-05" };
    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = false };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly EnvConfig _env;
    private readonly ILogger<SdMcpServer> _logger;
    private readonly ConcurrentDictionary<string, Channel<string>> _sseSessions = new();

    public SdMcpServer(EnvConfig env, ILogger<SdMcpServer> logger)
    {
        _env = env;
        _logger = logger;
    }

    // =====================================================================
    //  BAĞLANTI BİLGİLERİ (UI ve start.bat için)
    // =====================================================================
    public static List<string> YerelIpAdresleri()
    {
        var list = new List<string>();
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    list.Add(ip.ToString());
            }
        }
        catch { }
        return list.Distinct().ToList();
    }

    public object BaglantiBilgisi()
    {
        var port = _env.McpPort;
        var host = Dns.GetHostName();
        var ipler = YerelIpAdresleri();
        var anaAdres = !string.IsNullOrWhiteSpace(_env.McpPublicUrl)
            ? _env.McpPublicUrl.TrimEnd('/')
            : $"http://{(ipler.FirstOrDefault() ?? host)}:{port}";
        return new
        {
            port,
            bilgisayarAdi = host,
            ipAdresleri = ipler,
            sseUrl = $"{anaAdres}/sse",
            streamableUrl = $"{anaAdres}/mcp",
            alternatifUrller = ipler.Select(ip => $"http://{ip}:{port}/sse").Append($"http://{host}:{port}/sse").Distinct().ToList(),
            apiKeyGerekli = !string.IsNullOrWhiteSpace(_env.McpApiKey),
            sqlAraciAcik = _env.McpSqlEnabled,
            araclar = AracTanimlari().Select(t => t!["name"]!.GetValue<string>()).ToList(),
            langflowJson = JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["uges_sd_mcp"] = string.IsNullOrWhiteSpace(_env.McpApiKey)
                        ? new { url = $"{anaAdres}/sse" }
                        : new { url = $"{anaAdres}/sse", headers = new Dictionary<string, string> { ["x-api-key"] = _env.McpApiKey } }
                }
            }, new JsonSerializerOptions { WriteIndented = true })
        };
    }

    // =====================================================================
    //  HTTP DAĞITICI (yalnızca MCP portuna gelen istekler buraya düşer)
    // =====================================================================
    public async Task DispatchAsync(HttpContext ctx)
    {
        var path = (ctx.Request.Path.Value ?? "/").TrimEnd('/').ToLowerInvariant();
        var method = ctx.Request.Method.ToUpperInvariant();

        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        ctx.Response.Headers["Access-Control-Expose-Headers"] = "Mcp-Session-Id";
        if (method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }

        if (path is "" or "/health")
        {
            // Eğer bir MCP istemcisi kök URL'ye (http://IP:5101) doğrudan POST veya SSE GET attıysa otomatik karşıla!
            if (method == "POST")
            {
                if (!YetkiliMi(ctx)) { ctx.Response.StatusCode = 401; return; }
                await StreamableHttpPostAsync(ctx);
                return;
            }
            if (method == "GET" && ctx.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                if (!YetkiliMi(ctx)) { ctx.Response.StatusCode = 401; return; }
                await SseBaglantiAsync(ctx);
                return;
            }

            await ctx.Response.WriteAsJsonAsync(new
            {
                durum = "OK",
                sunucu = "UGES SD Kontrol MCP Sunucusu",
                baglanti = BaglantiBilgisi()
            });
            return;
        }

        if (!YetkiliMi(ctx))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { hata = "MCP API anahtarı geçersiz (x-api-key veya Authorization: Bearer)." });
            return;
        }

        switch (path)
        {
            case "/mcp" when method == "POST":
                await StreamableHttpPostAsync(ctx);
                return;
            case "/mcp" when method == "GET":
                // Sunucu kaynaklı (server-initiated) akış desteklenmiyor — spec'e göre 405 döndürülür.
                ctx.Response.StatusCode = 405;
                ctx.Response.Headers["Allow"] = "POST, DELETE";
                return;
            case "/mcp" when method == "DELETE":
                ctx.Response.StatusCode = 200;
                return;
            case "/sse" when method == "GET":
                await SseBaglantiAsync(ctx);
                return;
            case "/messages" when method == "POST":
                await SseMesajAsync(ctx);
                return;
            default:
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { hata = $"MCP uç noktası bulunamadı: {method} {path}. Kullanın: POST /mcp veya GET /sse" });
                return;
        }
    }

    private bool YetkiliMi(HttpContext ctx)
    {
        var key = _env.McpApiKey;
        if (string.IsNullOrWhiteSpace(key)) return true;
        var h = ctx.Request.Headers;
        if (h.TryGetValue("x-api-key", out var k1) && k1.ToString() == key) return true;
        if (h.TryGetValue("Authorization", out var auth) && auth.ToString().Trim() == $"Bearer {key}") return true;
        if (ctx.Request.Query.TryGetValue("api_key", out var q) && q.ToString() == key) return true;
        return false;
    }

    private async Task StreamableHttpPostAsync(HttpContext ctx)
    {
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(Hata(null, -32700, $"JSON ayrıştırma hatası: {ex.Message}"));
            return;
        }

        var yanitlar = new JsonArray();
        var initializeVar = false;
        IEnumerable<JsonNode?> mesajlar = body is JsonArray arr ? arr : new[] { body };
        foreach (var m in mesajlar)
        {
            if (m is not JsonObject mo) continue;
            if (mo["method"]?.GetValue<string>() == "initialize") initializeVar = true;
            var y = await IsleAsync(mo, ctx.RequestAborted);
            if (y != null) yanitlar.Add(y);
        }

        if (initializeVar)
            ctx.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");

        if (yanitlar.Count == 0)
        {
            ctx.Response.StatusCode = 202;
            return;
        }

        ctx.Response.ContentType = "application/json; charset=utf-8";
        var cikti = body is JsonArray ? yanitlar.ToJsonString(JsonOpt) : yanitlar[0]!.ToJsonString(JsonOpt);
        await ctx.Response.WriteAsync(cikti, Encoding.UTF8);
    }

    private async Task SseBaglantiAsync(HttpContext ctx)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var ch = Channel.CreateUnbounded<string>();
        _sseSessions[sessionId] = ch;

        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
        ctx.Response.Headers["Connection"] = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var ct = ctx.RequestAborted;
        _logger.LogInformation("[MCP] SSE bağlantısı açıldı: {Session} ({Ip})", sessionId, ctx.Connection.RemoteIpAddress);
        try
        {
            await ctx.Response.WriteAsync($"event: endpoint\ndata: /messages?session_id={sessionId}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var bekle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bekle.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (await ch.Reader.WaitToReadAsync(bekle.Token))
                    {
                        while (ch.Reader.TryRead(out var msg))
                            await ctx.Response.WriteAsync($"event: message\ndata: {msg}\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 15 sn'de bir canlı tutma (keep-alive) yorumu gönder
                    await ctx.Response.WriteAsync(": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "[MCP] SSE akışı kapandı"); }
        finally
        {
            _sseSessions.TryRemove(sessionId, out _);
            _logger.LogInformation("[MCP] SSE bağlantısı kapandı: {Session}", sessionId);
        }
    }

    private async Task SseMesajAsync(HttpContext ctx)
    {
        var sid = ctx.Request.Query["session_id"].ToString();
        if (string.IsNullOrEmpty(sid)) sid = ctx.Request.Query["sessionId"].ToString();
        if (!_sseSessions.TryGetValue(sid, out var ch))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("SSE oturumu bulunamadı (session_id).");
            return;
        }

        JsonNode? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync($"JSON ayrıştırma hatası: {ex.Message}");
            return;
        }

        // İstemciye hemen 202 Accepted dön; yanıt SSE akışı üzerinden 'message' olayı olarak gider.
        ctx.Response.StatusCode = 202;
        await ctx.Response.WriteAsync("Accepted");

        IEnumerable<JsonNode?> mesajlar = body is JsonArray arr ? arr : new[] { body };
        foreach (var m in mesajlar)
        {
            if (m is not JsonObject mo) continue;
            var y = await IsleAsync(mo, CancellationToken.None);
            if (y != null) await ch.Writer.WriteAsync(y.ToJsonString(JsonOpt));
        }
    }

    // =====================================================================
    //  JSON-RPC 2.0 / MCP METOT İŞLEYİCİ
    // =====================================================================
    public async Task<JsonObject?> IsleAsync(JsonObject msg, CancellationToken ct)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>() ?? "";
        var prms = msg["params"] as JsonObject;

        // id'siz mesajlar bildirimdir (notifications/initialized vb.) — yanıt verilmez
        if (id == null)
        {
            return null;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var istenen = prms?["protocolVersion"]?.GetValue<string>() ?? DesteklenenSurumler[0];
                    var surum = DesteklenenSurumler.Contains(istenen) ? istenen : DesteklenenSurumler[0];
                    return Sonuc(id, new JsonObject
                    {
                        ["protocolVersion"] = surum,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "uges-sd-mcp", ["title"] = "UGES SD Kontrol MCP", ["version"] = "1.0.0" },
                        ["instructions"] =
                            "UGES TYM SAP SD (VA01) talep kontrol veritabanına erişim sağlar. " +
                            "Bir SD süreç numarası (surec_no) için önce 'sd_talep_dataframe' ile başlık + kalem verisini, " +
                            "'sd_eksik_alan_analizi' ile eksik/şüpheli alanları al; yorumunu Türkçe üret ve istenirse 'sd_yorum_kaydet' ile kaydet."
                    });
                }
                case "ping":
                    return Sonuc(id, new JsonObject());
                case "tools/list":
                    return Sonuc(id, new JsonObject { ["tools"] = AracTanimlari() });
                case "tools/call":
                {
                    var ad = prms?["name"]?.GetValue<string>() ?? "";
                    var args = prms?["arguments"] as JsonObject ?? new JsonObject();
                    _logger.LogInformation("[MCP] Araç çağrısı: {Arac} {Args}", ad, args.ToJsonString());
                    try
                    {
                        var metin = await AracCalistirAsync(ad, args, ct);
                        return Sonuc(id, new JsonObject
                        {
                            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = metin } },
                            ["isError"] = false
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[MCP] Araç hatası: {Arac}", ad);
                        return Sonuc(id, new JsonObject
                        {
                            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = $"HATA ({ad}): {ex.Message}" } },
                            ["isError"] = true
                        });
                    }
                }
                case "resources/list":
                    return Sonuc(id, new JsonObject { ["resources"] = new JsonArray() });
                case "prompts/list":
                    return Sonuc(id, new JsonObject { ["prompts"] = new JsonArray() });
                default:
                    return Hata(id, -32601, $"Desteklenmeyen MCP metodu: {method}");
            }
        }
        catch (Exception ex)
        {
            return Hata(id, -32603, ex.Message);
        }
    }

    private static JsonObject Sonuc(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Hata(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    // =====================================================================
    //  ARAÇ (TOOL) TANIMLARI
    // =====================================================================
    private JsonArray AracTanimlari()
    {
        static JsonObject Sema(JsonObject props, params string[] required)
        {
            var o = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
            return o;
        }
        static JsonObject P(string type, string desc) => new() { ["type"] = type, ["description"] = desc };

        var tools = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "sd_talep_listesi",
                ["description"] = "SD taleplerini kural motoru durumu ile birlikte DataFrame (tablo) olarak listeler. Hangi süreçlerin engel/uyarı içerdiğini bulmak için kullanın.",
                ["inputSchema"] = Sema(new JsonObject
                {
                    ["kontrol_durumu"] = P("string", "Opsiyonel filtre: ENGEL | UYARI | ONAYLI | HAZIR"),
                    ["ara"] = P("string", "Opsiyonel arama: belge no, süreç no, talep eden, CRM no veya malzeme no"),
                    ["limit"] = P("integer", "En fazla satır sayısı (varsayılan 50, en fazla 300)")
                })
            },
            new JsonObject
            {
                ["name"] = "sd_talep_dataframe",
                ["description"] = "Tek bir SD sürecinin başlık alanlarını, tüm kalemlerini (DataFrame), kural motoru bulgularını ve onay durumunu döner. SD yorumu/denetimi için ana veri kaynağıdır.",
                ["inputSchema"] = Sema(new JsonObject
                {
                    ["surec_no"] = P("integer", "SD süreç numarası (örn: 20609365)"),
                    ["format"] = P("string", "markdown (varsayılan) | json | csv")
                }, "surec_no")
            },
            new JsonObject
            {
                ["name"] = "sd_kural_kontrolu",
                ["description"] = "UGES TYM SD Giriş Şartnamesi kural motorunu çalıştırır; Kırmızı (engel), Turuncu ve Sarı uyarıları döner.",
                ["inputSchema"] = Sema(new JsonObject { ["surec_no"] = P("integer", "SD süreç numarası") }, "surec_no")
            },
            new JsonObject
            {
                ["name"] = "sd_eksik_alan_analizi",
                ["description"] = "SAP VA01 girişi için boş/eksik başlık ve kalem alanlarını, tarih tutarsızlıklarını, tutar-sözleşme farkını ve HIZMET kalemlerinin alt kalem durumunu tespit eder.",
                ["inputSchema"] = Sema(new JsonObject { ["surec_no"] = P("integer", "SD süreç numarası") }, "surec_no")
            },
            new JsonObject
            {
                ["name"] = "sd_kalemler_dataframe",
                ["description"] = "Tek bir SD sürecinin başlık + kalem + kural durumu + eksik alan bilgilerini birleştirilmiş düz DataFrame (JSON records dizisi) olarak döner. LLM Suite 'Type Convert -> DataFrame' veya Pandas analizi için idealdir.",
                ["inputSchema"] = Sema(new JsonObject
                {
                    ["surec_no"] = P("integer", "SD süreç numarası (örn: 20609365)")
                }, "surec_no")
            },
            new JsonObject
            {
                ["name"] = "sd_yorum_kaydet",
                ["description"] = "LLM tarafından üretilen SD yorumunu/eksik tamamlama önerisini UGES SD Kontrol ekranında gösterilmek üzere veritabanına kaydeder.",
                ["inputSchema"] = Sema(new JsonObject
                {
                    ["surec_no"] = P("integer", "SD süreç numarası"),
                    ["yorum"] = P("string", "Kaydedilecek Türkçe yorum metni (Markdown olabilir)"),
                    ["baslik"] = P("string", "Opsiyonel kısa başlık")
                }, "surec_no", "yorum")
            }
        };

        if (_env.McpSqlEnabled)
        {
            tools.Add(new JsonObject
            {
                ["name"] = "sd_sql_sorgu",
                ["description"] = "sd şemasında SALT OKUNUR SELECT sorgusu çalıştırır (tablolar: sd.talep, sd.talep_kalem, sd.talep_onay, sd.ai_analiz_log, sd.import_log). Sonucu DataFrame olarak döner.",
                ["inputSchema"] = Sema(new JsonObject
                {
                    ["sql"] = P("string", "Tek bir SELECT / WITH sorgusu (noktalı virgül ve DML/DDL yasaktır)"),
                    ["limit"] = P("integer", "En fazla satır (varsayılan 200, en fazla 1000)")
                }, "sql")
            });
        }
        return tools;
    }

    // =====================================================================
    //  ARAÇ (TOOL) UYGULAMALARI
    // =====================================================================
    private async Task<string> AracCalistirAsync(string ad, JsonObject a, CancellationToken ct)
    {
        return ad switch
        {
            "sd_talep_listesi" => await TalepListesiAsync(Str(a, "kontrol_durumu"), Str(a, "ara"), Int(a, "limit") ?? 50),
            "sd_talep_dataframe" => await TalepDataFrameAsync(Long(a, "surec_no"), Str(a, "format") ?? "markdown"),
            "sd_kalemler_dataframe" => await KalemlerBirlesikDataFrameJsonAsync(Long(a, "surec_no")),
            "sd_kural_kontrolu" => await KuralKontroluAsync(Long(a, "surec_no")),
            "sd_eksik_alan_analizi" => await EksikAlanAnaliziAsync(Long(a, "surec_no")),
            "sd_yorum_kaydet" => await YorumKaydetAsync(Long(a, "surec_no"), Str(a, "yorum") ?? "", Str(a, "baslik")),
            "sd_sql_sorgu" when _env.McpSqlEnabled => await SqlSorguAsync(Str(a, "sql") ?? "", Int(a, "limit") ?? 200, ct),
            _ => throw new InvalidOperationException($"Bilinmeyen araç: {ad}")
        };
    }

    private NpgsqlConnection Db() => new(_env.ConnectionString);

    private static IDictionary<string, object?> ToDict(object row) =>
        new Dictionary<string, object?>((IDictionary<string, object?>)row);

    public async Task<(IDictionary<string, object?>? talep, List<IDictionary<string, object?>> kalemler, IDictionary<string, object?>? onay)> TalepGetirAsync(long surecNo)
    {
        await using var c = Db();
        object? t = await c.QueryFirstOrDefaultAsync("SELECT * FROM sd.talep WHERE surec_no = @surecNo", new { surecNo });
        var k = await c.QueryAsync("SELECT * FROM sd.talep_kalem WHERE surec_no = @surecNo ORDER BY kalem_no, id", new { surecNo });
        object? o = await c.QueryFirstOrDefaultAsync("SELECT surec_no, onay_durumu, onaylayan, istisna_notu, va01_acildi_mi, son_va01_tarihi, girilen_sd_no, va01_override::text AS va01_override FROM sd.talep_onay WHERE surec_no = @surecNo", new { surecNo });
        return (t != null ? Temizle(ToDict(t)) : null, k.Cast<object>().Select(r => Temizle(ToDict(r))).ToList(), o != null ? ToDict(o) : null);
    }

    private static IDictionary<string, object?> Temizle(IDictionary<string, object?> d)
    {
        foreach (var gereksiz in new[] { "ham_veri", "kaynak_dosya", "import_id", "guncellendi" })
            d.Remove(gereksiz);
        return d;
    }

    private async Task<string> TalepListesiAsync(string? kontrolDurumu, string? ara, int limit)
    {
        limit = Math.Clamp(limit, 1, 300);
        await using var c = Db();
        var a = string.IsNullOrWhiteSpace(ara) ? null : ara.Trim();
        var talepler = (await c.QueryAsync(@"
            SELECT t.* FROM sd.talep t
            WHERE CAST(@ara AS text) IS NULL OR t.belge_no ILIKE CAST(@like AS text) OR CAST(t.surec_no AS text) ILIKE CAST(@like AS text)
               OR t.talep_eden_ad_soyad ILIKE CAST(@like AS text) OR t.crm_teklif_no ILIKE CAST(@like AS text)
               OR t.siparis_veren ILIKE CAST(@like AS text)
               OR EXISTS (SELECT 1 FROM sd.talep_kalem k2 WHERE k2.surec_no = t.surec_no AND k2.malzeme_no ILIKE CAST(@like AS text))
            ORDER BY t.belge_tarihi DESC NULLS LAST", new { ara = a, like = a == null ? null : $"%{a}%" })).Cast<object>().Select(ToDict).ToList();
        var kalemler = (await c.QueryAsync("SELECT * FROM sd.talep_kalem")).Cast<object>().Select(ToDict).GroupBy(k => Convert.ToInt64(k["surec_no"], Inv)).ToDictionary(g => g.Key, g => g.ToList());
        var onaylar = (await c.QueryAsync("SELECT surec_no, onay_durumu FROM sd.talep_onay")).Cast<object>().Select(ToDict).ToDictionary(x => Convert.ToInt64(x["surec_no"], Inv), x => x.GetValueOrDefault("onay_durumu") as string);

        var rows = new List<IDictionary<string, object?>>();
        foreach (var t in talepler)
        {
            var sno = Convert.ToInt64(t["surec_no"]);
            kalemler.TryGetValue(sno, out var kl);
            kl ??= new();
            onaylar.TryGetValue(sno, out var od);
            var k = SdRuleEngine.Degerlendir(t, kl, od);
            if (!string.IsNullOrWhiteSpace(kontrolDurumu) && !k.GenelDurum.Equals(kontrolDurumu.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new Dictionary<string, object?>
            {
                ["surec_no"] = sno,
                ["belge_no"] = t.GetValueOrDefault("belge_no"),
                ["belge_tarihi"] = t.GetValueOrDefault("belge_tarihi"),
                ["siparis_turu"] = t.GetValueOrDefault("siparis_turu"),
                ["siparis_veren"] = t.GetValueOrDefault("siparis_veren"),
                ["talep_eden"] = t.GetValueOrDefault("talep_eden_ad_soyad"),
                ["kalem_sayisi"] = kl.Count,
                ["kontrol_durumu"] = k.GenelDurum,
                ["kirmizi"] = k.KirmiziSayisi,
                ["turuncu"] = k.TuruncuSayisi,
                ["sari"] = k.SariSayisi,
                ["onay_durumu"] = od ?? "Bekliyor"
            });
            if (rows.Count >= limit) break;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# SD Talep Listesi ({rows.Count} satır{(string.IsNullOrWhiteSpace(kontrolDurumu) ? "" : $", filtre: {kontrolDurumu}")})");
        sb.AppendLine();
        sb.AppendLine(MarkdownTablo(rows));
        return sb.ToString();
    }

    private async Task<string> TalepDataFrameAsync(long surecNo, string format)
    {
        var (talep, kalemler, onay) = await TalepGetirAsync(surecNo);
        if (talep == null && kalemler.Count == 0) return $"Süreç {surecNo} veritabanında bulunamadı.";
        var kontrol = SdRuleEngine.Degerlendir(talep, kalemler, onay?.GetValueOrDefault("onay_durumu") as string);

        format = (format ?? "markdown").Trim().ToLowerInvariant();
        if (format == "json")
        {
            return JsonSerializer.Serialize(new
            {
                surec_no = surecNo,
                talep_basligi = talep?.ToDictionary(k => k.Key, v => Deger(v.Value)),
                kalemler = kalemler.Select(k => k.ToDictionary(x => x.Key, x => Deger(x.Value))),
                kural_motoru = kontrol,
                onay = onay?.ToDictionary(k => k.Key, v => Deger(v.Value))
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        if (format == "csv")
        {
            var sbc = new StringBuilder();
            sbc.AppendLine($"# SD Süreç {surecNo} — Kalem DataFrame (CSV)");
            sbc.AppendLine(Csv(kalemler));
            sbc.AppendLine();
            sbc.AppendLine("# Talep Başlığı (CSV)");
            sbc.AppendLine(Csv(talep != null ? new List<IDictionary<string, object?>> { talep } : new()));
            return sbc.ToString();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# SD Süreç {surecNo} — {talep?.GetValueOrDefault("belge_no")}");
        sb.AppendLine();
        sb.AppendLine($"**Kural Motoru:** {kontrol.GenelDurum} · Kırmızı: {kontrol.KirmiziSayisi} · Turuncu: {kontrol.TuruncuSayisi} · Sarı: {kontrol.SariSayisi} · VA01 açılabilir: {(kontrol.Va01Acilabilir ? "Evet" : "Hayır")}");
        sb.AppendLine($"**Onay Durumu:** {onay?.GetValueOrDefault("onay_durumu") ?? "Bekliyor"}{(onay?.GetValueOrDefault("istisna_notu") is string n && n.Length > 0 ? $" · İstisna notu: {n}" : "")}");
        sb.AppendLine();
        sb.AppendLine("## 1) Talep Başlığı");
        if (talep != null)
        {
            var kv = talep.Select(kv => (IDictionary<string, object?>)new Dictionary<string, object?> { ["alan"] = kv.Key, ["deger"] = Deger(kv.Value) ?? "— BOŞ —" }).ToList();
            sb.AppendLine(MarkdownTablo(kv));
        }
        else sb.AppendLine("_Talep başlığı raporda yok (yalnızca kalem verisi mevcut)._");
        sb.AppendLine();

        var kalemKolon = new[] { "kalem_no", "ust_kalem", "malzeme_no", "miktar", "olcu_birimi", "birim_fiyat", "para_birimi", "pyp_ogesi", "ic_siparis", "kalem_tipi", "hazir_edim_tarihi", "teslim_tarihi", "sozlesmesel_teslim_tarihi", "garanti_suresi_ham", "sevkiyat_noktasi", "edim_kodu", "vergi", "eskalasyona_tabi" };
        sb.AppendLine($"## 2) Kalemler DataFrame ({kalemler.Count} satır)");
        sb.AppendLine(MarkdownTablo(kalemler, kalemKolon));
        decimal toplam = kalemler.Sum(k => Dec(k.GetValueOrDefault("miktar")) * Dec(k.GetValueOrDefault("birim_fiyat")));
        sb.AppendLine();
        sb.AppendLine($"Kalem toplam tutarı: **{toplam.ToString("N2", new CultureInfo("tr-TR"))} {kalemler.Select(k => k.GetValueOrDefault("para_birimi")?.ToString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))}** · Orijinal sözleşme tutarı: **{Deger(talep?.GetValueOrDefault("orijinal_sozlesme_tutari")) ?? "-"}**");
        sb.AppendLine();
        sb.AppendLine("## 3) Kural Motoru Bulguları");
        if (kontrol.Bulgular.Count == 0) sb.AppendLine("Tüm kurallardan geçti.");
        else
            sb.AppendLine(MarkdownTablo(kontrol.Bulgular.Select(b => (IDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["seviye"] = b.Seviye, ["kod"] = b.Kod, ["kalem"] = b.KalemNo, ["baslik"] = b.Baslik, ["detay"] = b.Detay
            }).ToList()));
        sb.AppendLine();
        sb.AppendLine("## 4) DataFrame JSON (records)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(kalemler.Select(k => kalemKolon.ToDictionary(c => c, c => Deger(k.GetValueOrDefault(c)))),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private async Task<string> KalemlerBirlesikDataFrameJsonAsync(long surecNo)
    {
        var (talep, kalemler, onay) = await TalepGetirAsync(surecNo);
        if (talep == null && kalemler.Count == 0)
            return "[]";

        var kontrol = SdRuleEngine.Degerlendir(talep, kalemler, onay?.GetValueOrDefault("onay_durumu") as string);
        var genelUyarilar = string.Join(" | ", kontrol.Bulgular.Where(b => b.KalemNo == null).Select(b => $"[{b.Seviye}:{b.Kod}] {b.Baslik}"));

        var baslikEksikler = new List<string>();
        if (talep != null)
        {
            foreach (var (kol, etiket, _) in BaslikZorunlu)
            {
                var v = Deger(talep.GetValueOrDefault(kol));
                if (string.IsNullOrWhiteSpace(v) || v is "SS" or "-" or "0") baslikEksikler.Add(etiket);
            }
        }

        var kaynakSatirlar = kalemler.Count > 0 ? kalemler : new List<IDictionary<string, object?>> { new Dictionary<string, object?>() };
        var dfRows = kaynakSatirlar.Select(k =>
        {
            var kno = Int(new JsonObject { ["x"] = Deger(k.GetValueOrDefault("kalem_no")) }, "x");
            var kalemUyarilar = string.Join(" | ", kontrol.Bulgular.Where(b => b.KalemNo == kno).Select(b => $"[{b.Seviye}:{b.Kod}] {b.Baslik}"));
            var kalemEksikler = new List<string>();
            if (kalemler.Count > 0)
            {
                foreach (var (kol, etiket) in KalemZorunlu)
                    if (string.IsNullOrWhiteSpace(Deger(k.GetValueOrDefault(kol)))) kalemEksikler.Add(etiket);
            }

            var miktar = Dec(k.GetValueOrDefault("miktar"));
            var fiyat = Dec(k.GetValueOrDefault("birim_fiyat"));
            return new Dictionary<string, object?>
            {
                ["surec_no"] = surecNo,
                ["belge_no"] = Deger(talep?.GetValueOrDefault("belge_no")),
                ["siparis_turu"] = Deger(talep?.GetValueOrDefault("siparis_turu")),
                ["satis_org"] = Deger(talep?.GetValueOrDefault("satis_organizasyonu")),
                ["dagitim_kanali"] = Deger(talep?.GetValueOrDefault("dagitim_kanali")),
                ["satis_burosu"] = Deger(talep?.GetValueOrDefault("satis_burosu")),
                ["satis_grubu"] = Deger(talep?.GetValueOrDefault("satis_grubu")),
                ["crm_teklif_no"] = Deger(talep?.GetValueOrDefault("crm_teklif_no")),
                ["aselsan_sozlesme_no"] = Deger(talep?.GetValueOrDefault("aselsan_sozlesme_no")),
                ["sas_no"] = Deger(talep?.GetValueOrDefault("sas_no")),
                ["sas_tarihi"] = Deger(talep?.GetValueOrDefault("sas_tarihi")),
                ["sozlesme_imza_tarihi"] = Deger(talep?.GetValueOrDefault("sozlesme_imza_tarihi")),
                ["orijinal_sozlesme_tutari"] = Dec(talep?.GetValueOrDefault("orijinal_sozlesme_tutari")),
                ["siparis_veren"] = Deger(talep?.GetValueOrDefault("siparis_veren")),
                ["mali_teslim_alan"] = Deger(talep?.GetValueOrDefault("mali_teslim_alan")),
                ["odeme_sekli"] = Deger(talep?.GetValueOrDefault("odeme_sekli")),
                ["incoterms"] = Deger(talep?.GetValueOrDefault("incoterms")),
                ["kalem_no"] = kno,
                ["ust_kalem"] = Deger(k.GetValueOrDefault("ust_kalem")),
                ["malzeme_no"] = Deger(k.GetValueOrDefault("malzeme_no")),
                ["hizmet_mi"] = SdRuleEngine.IsHizmetVeyaSanalMalzeme(Deger(k.GetValueOrDefault("malzeme_no")), Deger(k.GetValueOrDefault("kalem_tipi"))),
                ["miktar"] = miktar,
                ["olcu_birimi"] = Deger(k.GetValueOrDefault("olcu_birimi")),
                ["birim_fiyat"] = fiyat,
                ["kalem_tutari"] = Math.Round(miktar * fiyat, 4),
                ["para_birimi"] = Deger(k.GetValueOrDefault("para_birimi")),
                ["pyp_ogesi"] = Deger(k.GetValueOrDefault("pyp_ogesi")),
                ["ic_siparis"] = Deger(k.GetValueOrDefault("ic_siparis")),
                ["kalem_tipi"] = Deger(k.GetValueOrDefault("kalem_tipi")),
                ["hazir_edim_tarihi"] = Deger(k.GetValueOrDefault("hazir_edim_tarihi")),
                ["teslim_tarihi"] = Deger(k.GetValueOrDefault("teslim_tarihi")),
                ["sozlesmesel_teslim_tarihi"] = Deger(k.GetValueOrDefault("sozlesmesel_teslim_tarihi")),
                ["garanti_suresi"] = Deger(k.GetValueOrDefault("garanti_suresi_ham")),
                ["kontrol_durumu"] = kontrol.GenelDurum,
                ["baslik_eksik_alanlar"] = string.Join(", ", baslikEksikler),
                ["kalem_eksik_alanlar"] = string.Join(", ", kalemEksikler),
                ["baslik_kural_uyarilari"] = genelUyarilar,
                ["kalem_kural_uyarilari"] = kalemUyarilar
            };
        }).ToList();

        return JsonSerializer.Serialize(dfRows, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private async Task<string> KuralKontroluAsync(long surecNo)
    {
        var (talep, kalemler, onay) = await TalepGetirAsync(surecNo);
        if (talep == null && kalemler.Count == 0) return $"Süreç {surecNo} veritabanında bulunamadı.";
        var k = SdRuleEngine.Degerlendir(talep, kalemler, onay?.GetValueOrDefault("onay_durumu") as string);
        var sb = new StringBuilder();
        sb.AppendLine($"# Süreç {surecNo} Kural Kontrolü: {k.GenelDurum}");
        sb.AppendLine($"Kırmızı (engel): {k.KirmiziSayisi} · Turuncu: {k.TuruncuSayisi} · Sarı: {k.SariSayisi} · VA01 açılabilir: {(k.Va01Acilabilir ? "Evet" : "Hayır")}");
        sb.AppendLine();
        sb.AppendLine(k.Bulgular.Count == 0 ? "Tüm kurallardan geçti." :
            MarkdownTablo(k.Bulgular.Select(b => (IDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["seviye"] = b.Seviye, ["kod"] = b.Kod, ["kalem"] = b.KalemNo, ["baslik"] = b.Baslik, ["detay"] = b.Detay
            }).ToList()));
        return sb.ToString();
    }

    private static readonly (string Kolon, string Etiket, bool Kritik)[] BaslikZorunlu =
    {
        ("siparis_veren", "Sipariş Veren (VA01 KUAGV)", true),
        ("mali_teslim_alan", "Malı Teslim Alan (VA01 KUWEV)", true),
        ("crm_teklif_no", "CRM Teklif No", true),
        ("siparis_turu", "Sipariş Türü", true),
        ("satis_burosu", "Satış Bürosu", false),
        ("satis_grubu", "Satış Grubu", false),
        ("sas_no", "SAS / PO No", false),
        ("sas_tarihi", "SAS Tarihi", false),
        ("aselsan_sozlesme_no", "ASELSAN Sözleşme No", false),
        ("sozlesme_adi", "Sözleşme Adı", false),
        ("sozlesme_imza_tarihi", "Sözleşme İmza Tarihi", true),
        ("sozlesme_baslangic_tarihi", "Sözleşme Başlangıç", false),
        ("sozlesme_bitis_tarihi", "Sözleşme Bitiş", false),
        ("sozlesme_teslim_tarihi", "Sözleşme Teslim Tarihi", false),
        ("orijinal_sozlesme_tutari", "Orijinal Sözleşme Tutarı", false),
        ("odeme_sekli", "Ödeme Koşulu", false),
        ("incoterms", "Incoterms", false),
        ("fatura_alicisi", "Fatura Alıcısı", false),
        ("odeyen", "Ödeyen", false),
        ("satis_sorumlusu", "Satış Sorumlusu", false),
        ("mip_sorumlusu", "MIP Sorumlusu", false),
        ("proje_hesap_no", "Proje Hesap No", false),
    };

    private static readonly (string Kolon, string Etiket)[] KalemZorunlu =
    {
        ("malzeme_no", "Malzeme No"), ("miktar", "Miktar"), ("olcu_birimi", "Ölçü Birimi"),
        ("birim_fiyat", "Birim Fiyat"), ("para_birimi", "Para Birimi"), ("pyp_ogesi", "PYP Öğesi"),
        ("teslim_tarihi", "Teslim Tarihi"), ("hazir_edim_tarihi", "Hazır Edim Tarihi"),
        ("sozlesmesel_teslim_tarihi", "Sözleşmesel Teslim Tarihi"), ("garanti_suresi_ham", "Garanti Süresi"),
    };

    private async Task<string> EksikAlanAnaliziAsync(long surecNo)
    {
        var (talep, kalemler, onay) = await TalepGetirAsync(surecNo);
        if (talep == null && kalemler.Count == 0) return $"Süreç {surecNo} veritabanında bulunamadı.";

        var bulgular = new List<IDictionary<string, object?>>();
        void Ekle(string seviye, string kapsam, string alan, string aciklama, string oneri) =>
            bulgular.Add(new Dictionary<string, object?> { ["seviye"] = seviye, ["kapsam"] = kapsam, ["alan"] = alan, ["aciklama"] = aciklama, ["oneri"] = oneri });

        if (talep == null)
            Ekle("KRİTİK", "Başlık", "-", "Talep başlığı raporda yok", "SD Kalem/Detay raporunu tekrar içe aktarın.");
        else
        {
            foreach (var (kol, etiket, kritik) in BaslikZorunlu)
            {
                var v = Deger(talep.GetValueOrDefault(kol));
                var bos = string.IsNullOrWhiteSpace(v) || v is "SS" or "-" or "0";
                if (bos) Ekle(kritik ? "KRİTİK" : "EKSİK", "Başlık", etiket, $"'{kol}' boş/yer tutucu ({v ?? "null"})",
                    kritik ? "VA01 öncesi talep sahibinden mutlaka tamamlatılmalı." : "Sözleşme/PO belgesinden tamamlanması önerilir.");
            }
            var imza = Tarih(talep.GetValueOrDefault("sozlesme_imza_tarihi"));
            if (imza.HasValue && imza.Value.Year < DateTime.Today.Year)
                Ekle("UYARI", "Başlık", "Sözleşme İmza Tarihi", $"Geçmiş yıla ait ({imza:dd.MM.yyyy})", "GSSYM onayı gerekebilir (Şartname Md. 4).");
            var sv = Deger(talep.GetValueOrDefault("siparis_veren"));
            if (!string.IsNullOrWhiteSpace(sv) && !sv.All(char.IsDigit))
                Ekle("BİLGİ", "Başlık", "Sipariş Veren", $"Müşteri kodu yerine ad girilmiş: '{sv}'", "VA01'de F4 'Ad' araması ile müşteri kodu bulunur; kodu teyit edin.");
        }

        if (kalemler.Count == 0)
            Ekle("KRİTİK", "Kalem", "-", "Hiç kalem yok", "SD Detay raporunu içe aktarın veya talep sahibinden kalemleri isteyin.");

        var pbler = kalemler.Select(k => Deger(k.GetValueOrDefault("para_birimi"))).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (pbler.Count > 1) Ekle("UYARI", "Kalem", "Para Birimi", $"Kalemlerde birden fazla para birimi: {string.Join(", ", pbler)}", "Tek belge para biriminde birleştirin.");

        var kalemNolar = kalemler.Select(k => Deger(k.GetValueOrDefault("kalem_no"))).ToHashSet();
        foreach (var k in kalemler)
        {
            var kno = Deger(k.GetValueOrDefault("kalem_no")) ?? "?";
            foreach (var (kol, etiket) in KalemZorunlu)
            {
                var v = Deger(k.GetValueOrDefault(kol));
                if (string.IsNullOrWhiteSpace(v) || (kol is "miktar" && Dec(k.GetValueOrDefault(kol)) <= 0))
                    Ekle(kol is "malzeme_no" or "miktar" or "teslim_tarihi" ? "KRİTİK" : "EKSİK", $"Kalem {kno}", etiket, $"'{kol}' boş", "Talep sahibinden tamamlatın.");
            }
            var he = Tarih(k.GetValueOrDefault("hazir_edim_tarihi"));
            var tt = Tarih(k.GetValueOrDefault("teslim_tarihi"));
            if (he.HasValue && tt.HasValue)
            {
                if (he.Value.Date == tt.Value.Date) Ekle("UYARI", $"Kalem {kno}", "Hazır Edim = Teslim", $"Aynı gün ({tt:dd.MM.yyyy})", "ÜPM MIP onayı ile istisna olarak geçilebilir (Şartname Md. 9).");
                else if (he.Value.Date > tt.Value.Date) Ekle("KRİTİK", $"Kalem {kno}", "Hazır Edim > Teslim", $"Hazır edim ({he:dd.MM.yyyy}) teslimden ({tt:dd.MM.yyyy}) sonra", "Tarihleri düzeltin.");
            }
            var malz = Deger(k.GetValueOrDefault("malzeme_no"));
            var kt = Deger(k.GetValueOrDefault("kalem_tipi"));
            if (SdRuleEngine.IsHizmetVeyaSanalMalzeme(malz, kt))
            {
                var altVar = kalemler.Any(x => Deger(x.GetValueOrDefault("ust_kalem")) == kno);
                Ekle(altVar ? "BİLGİ" : "UYARI", $"Kalem {kno}", "HIZMET / sanal kalem", $"'{malz}' fiziksel malzeme değil{(altVar ? " (alt kalemi mevcut)" : " ve bağlı alt malzeme kalemi yok")}",
                    altVar ? "Alt kalem bağlantısını (Y21F) teyit edin." : "Fiziksel teslimat varsa malzemeyi alt kalem olarak bağlayın.");
            }
            var ust = Deger(k.GetValueOrDefault("ust_kalem"));
            if (!string.IsNullOrWhiteSpace(ust) && ust != "0" && !kalemNolar.Contains(ust))
                Ekle("KRİTİK", $"Kalem {kno}", "Üst Kalem", $"Üst kalem {ust} talepte yok", "Üst kalem numarasını düzeltin.");
        }

        if (talep != null)
        {
            var sozTutar = Dec(talep.GetValueOrDefault("orijinal_sozlesme_tutari"));
            var toplam = kalemler.Sum(k => Dec(k.GetValueOrDefault("miktar")) * Dec(k.GetValueOrDefault("birim_fiyat")));
            if (sozTutar > 0 && toplam > 0 && Math.Abs(sozTutar - toplam) > Math.Max(1m, sozTutar * 0.005m))
                Ekle("UYARI", "Başlık/Kalem", "Tutar Farkı", $"Kalem toplamı {toplam:N2} ≠ sözleşme tutarı {sozTutar:N2} (fark {toplam - sozTutar:N2})", "Eksik kalem veya fiyat hatası olabilir; sözleşme ile kıyaslayın.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Süreç {surecNo} — Eksik Alan & Tutarlılık Analizi ({bulgular.Count} bulgu)");
        sb.AppendLine();
        sb.AppendLine(bulgular.Count == 0 ? "Eksik veya tutarsız alan bulunamadı." : MarkdownTablo(bulgular));
        return sb.ToString();
    }

    private async Task<string> YorumKaydetAsync(long surecNo, string yorum, string? baslik)
    {
        if (surecNo <= 0) throw new ArgumentException("surec_no zorunludur.");
        if (string.IsNullOrWhiteSpace(yorum)) throw new ArgumentException("yorum boş olamaz.");
        await using var c = Db();
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO sd.ai_analiz_log (surec_no, analiz_turu, baslik, sonuc_metni, model_bilgisi) VALUES (@surecNo, 'llmsuite_mcp', @baslik, @yorum, 'ASELSAN LLM Suite (MCP)') RETURNING id",
            new { surecNo, baslik = string.IsNullOrWhiteSpace(baslik) ? "LLM Suite MCP Yorumu" : baslik.Trim(), yorum = yorum.Trim() });
        return $"Yorum kaydedildi (kayıt id: {id}, süreç {surecNo}). UGES SD Kontrol ekranında 'Yapay Zeka' sekmesinde görüntülenecek.";
    }

    private static readonly Regex YasakSql = new(@"\b(insert|update|delete|drop|alter|create|truncate|grant|revoke|copy|vacuum|call|do|execute|prepare|listen|notify|set|reset|lock|comment|refresh|security|pg_sleep\w*|pg_read\w*|pg_write\w*|pg_ls_dir|pg_stat_file|pg_terminate_backend|pg_cancel_backend|lo_\w+|dblink\w*|current_setting|set_config)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<string> SqlSorguAsync(string sql, int limit, CancellationToken ct)
    {
        sql = (sql ?? "").Trim().TrimEnd(';').Trim();
        limit = Math.Clamp(limit, 1, 1000);
        if (!Regex.IsMatch(sql, @"^(select|with)\b", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Yalnızca SELECT / WITH sorgularına izin verilir.");
        if (sql.Contains(';')) throw new InvalidOperationException("Tek sorgu çalıştırılabilir (noktalı virgül yasak).");
        if (YasakSql.IsMatch(sql)) throw new InvalidOperationException("Sorguda yasaklı anahtar kelime var (yalnızca salt okunur SELECT).");

        await using var c = Db();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '15s'; SET LOCAL search_path = sd;", transaction: tx);
        var rows = (await c.QueryAsync($"SELECT * FROM ({sql}) AS mcp_q LIMIT {limit}", transaction: tx)).Cast<object>().Select(ToDict).ToList();
        await tx.RollbackAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# SQL Sonucu ({rows.Count} satır)");
        sb.AppendLine(MarkdownTablo(rows));
        return sb.ToString();
    }

    // =====================================================================
    //  YARDIMCILAR (DataFrame biçimleme)
    // =====================================================================
    private static string? Str(JsonObject a, string k)
    {
        var n = a[k];
        if (n == null) return null;
        var s = n is JsonValue v && v.TryGetValue<string>(out var str) ? str : n.ToJsonString().Trim('"');
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static int? Int(JsonObject a, string k) =>
        int.TryParse(Str(a, k), NumberStyles.Integer, Inv, out var i) ? i : null;

    private static long Long(JsonObject a, string k)
    {
        var s = Str(a, k);
        if (s != null && long.TryParse(Regex.Replace(s, @"[^\d]", ""), out var l)) return l;
        throw new ArgumentException($"'{k}' parametresi zorunludur (sayısal SD süreç numarası).");
    }

    private static decimal Dec(object? v) => v switch
    {
        null => 0m,
        decimal d => d,
        double db => (decimal)db,
        int i => i,
        long l => l,
        _ => decimal.TryParse(Convert.ToString(v, Inv), NumberStyles.Any, Inv, out var r) ? r : 0m
    };

    private static DateTime? Tarih(object? v) => v switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        string s when DateTime.TryParse(s, out var p) => p,
        _ => null
    };

    private static string? Deger(object? v) => v switch
    {
        null => null,
        DBNull => null,
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm"),
        DateOnly d => d.ToString("yyyy-MM-dd"),
        decimal d => d.ToString("0.####", Inv),
        double d => d.ToString("0.####", Inv),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, Inv)?.Trim()
    };

    private static string MarkdownTablo(IList<IDictionary<string, object?>> rows, IEnumerable<string>? kolonlar = null)
    {
        if (rows.Count == 0) return "_(Kayıt yok)_";
        var cols = (kolonlar ?? rows.SelectMany(r => r.Keys).Distinct()).ToList();
        static string Esc(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", cols) + " |");
        sb.AppendLine("|" + string.Join("|", cols.Select(_ => "---")) + "|");
        foreach (var r in rows)
            sb.AppendLine("| " + string.Join(" | ", cols.Select(c => Esc(Deger(r.GetValueOrDefault(c))))) + " |");
        return sb.ToString().TrimEnd();
    }

    private static string Csv(IList<IDictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return "";
        var cols = rows.SelectMany(r => r.Keys).Distinct().ToList();
        static string Q(string? s) => s == null ? "" : (s.Contains(';') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", cols));
        foreach (var r in rows) sb.AppendLine(string.Join(";", cols.Select(c => Q(Deger(r.GetValueOrDefault(c))))));
        return sb.ToString().TrimEnd();
    }

    /// <summary>LLM Suite akışına mesajla gömülecek kompakt veri özeti (MCP bağlı değilse yedek).</summary>
    public async Task<string> SatirIciVeriAsync(long surecNo)
    {
        var df = await TalepDataFrameAsync(surecNo, "markdown");
        var eksik = await EksikAlanAnaliziAsync(surecNo);
        var metin = df + "\n\n" + eksik;
        return metin.Length > 30000 ? metin[..30000] + "\n…(kısaltıldı)" : metin;
    }
}

internal static class SdDictExtensions
{
    public static object? GetValueOrDefault(this IDictionary<string, object?>? dict, string key) =>
        dict != null && dict.TryGetValue(key, out var val) ? val : null;
}
