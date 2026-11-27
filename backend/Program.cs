using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using SdKontrol.API.Data;
using SdKontrol.API.Services;

// UGES SD Kontrol — UGESUD Kokpit'ten bağımsız çalışan, aynı veritabanında "sd" şemasını kullanan API.

var exeWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = Directory.Exists(exeWebRoot)
    ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, WebRootPath = exeWebRoot })
    : WebApplication.CreateBuilder(args);

var envCfg = new EnvConfig(builder.Environment);
// Ana uygulama (UI + API) ve LLM Suite için bağımsız MCP sunucusu portu aynı exe içinde ayrı portlarda dinlenir
builder.WebHost.UseUrls($"http://0.0.0.0:{envCfg.BackendPort}", $"http://0.0.0.0:{envCfg.McpPort}");

builder.Services.AddSingleton(envCfg);
builder.Services.AddSingleton<SdImportService>();
builder.Services.AddSingleton<SdVa01Service>();
builder.Services.AddSingleton<SdMcpServer>();
builder.Services.AddSingleton<LlmSuiteService>();
builder.Services.AddHttpClient<SdAiService>();

var frontendOrigin = $"http://localhost:{envCfg.FrontendPort}";
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
var cs = envCfg.ConnectionString;

await SchemaInitializer.EnsureAsync(cs, app.Logger);

// ---- MCP PORTU: Bu porta gelen TÜM istekler yalnızca MCP sunucusuna yönlenir (UI/API bu portta sunulmaz) ----
var mcpServer = app.Services.GetRequiredService<SdMcpServer>();
var mcpPort = envCfg.McpPort;
app.MapWhen(ctx => ctx.Connection.LocalPort == mcpPort, branch => branch.Run(mcpServer.DispatchAsync));

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

NpgsqlConnection Db() => new(cs);

static IDictionary<string, object?> ToDict(dynamic row) =>
    (IDictionary<string, object?>)row;

async Task<(IDictionary<string, object?>? talep, List<IDictionary<string, object?>> kalemler, dynamic? onay)> GetTalepTamAsync(NpgsqlConnection c, long surecNo)
{
    var tRow = await c.QueryFirstOrDefaultAsync("SELECT *, ham_veri::text AS ham_veri FROM sd.talep WHERE surec_no = @surecNo", new { surecNo });
    var kRows = await c.QueryAsync("SELECT *, ham_veri::text AS ham_veri FROM sd.talep_kalem WHERE surec_no = @surecNo ORDER BY kalem_no, id", new { surecNo });
    var oRow = await c.QueryFirstOrDefaultAsync("SELECT *, onaylanan_uyarilar::text AS onaylanan_uyarilar, va01_override::text AS va01_override FROM sd.talep_onay WHERE surec_no = @surecNo", new { surecNo });
    return (tRow != null ? ToDict(tRow) : null, kRows.Select(r => ToDict(r)).ToList(), oRow);
}

static Va01OverrideDto? ParseOverride(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try { return JsonSerializer.Deserialize<Va01OverrideDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
    catch { return null; }
}

var api = app.MapGroup("/api");

// ---- Ortam ve Sağlık Kontrolü ----
api.MapGet("/health", async () =>
{
    await using var c = Db();
    var talep = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sd.talep");
    var kalem = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sd.talep_kalem");
    return Results.Ok(new
    {
        durum = "OK",
        envDosyasi = envCfg.EnvFilePath,
        envOkundu = File.Exists(envCfg.EnvFilePath),
        veritabani = envCfg.DbName,
        dbHost = $"{envCfg.DbHost}:{envCfg.DbPort}",
        sema = envCfg.DbSchema,
        talep,
        kalem,
        veriKlasoru = envCfg.VeriKlasoru,
        pythonExe = envCfg.PythonExe,
        pythonMevcut = File.Exists(envCfg.PythonExe),
        sapGuiMevcut = File.Exists(envCfg.SapLogonPath),
        sapBaglanti = envCfg.SapConnection,
        mcpPort = envCfg.McpPort,
        llmSuiteHazir = !string.IsNullOrWhiteSpace(envCfg.LlmSuiteFlowId) && !string.IsNullOrWhiteSpace(envCfg.LlmSuiteApiKey)
    });
});

// ---- ASELSAN LLM Suite Flow + MCP Ayar / Durum ----
static string Maskele(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Length <= 8 ? "••••" : $"{s[..4]}••••{s[^4..]}";

api.MapGet("/llmsuite/durum", (SdMcpServer mcp) => Results.Ok(new
{
    llmSuite = new
    {
        baseUrl = envCfg.LlmSuiteBaseUrl,
        flowId = envCfg.LlmSuiteFlowId,
        apiKeyMaskeli = Maskele(envCfg.LlmSuiteApiKey),
        apiKeyVar = !string.IsNullOrWhiteSpace(envCfg.LlmSuiteApiKey),
        timeoutSec = envCfg.LlmSuiteTimeoutSec,
        chatInputId = envCfg.LlmSuiteChatInputId,
        sslDogrula = envCfg.LlmSuiteSslDogrula
    },
    direktAi = new
    {
        apiUrl = envCfg.AiApiUrl,
        model = envCfg.AiModel,
        apiKeyMaskeli = Maskele(envCfg.AiApiKey),
        apiKeyVar = !string.IsNullOrWhiteSpace(envCfg.AiApiKey)
    },
    mcp = mcp.BaglantiBilgisi()
}));

api.MapPost("/llmsuite/ayarlar", (LlmSuiteAyarDto dto) =>
{
    void SetIf(string key, string? val, bool gizli = false)
    {
        if (val == null) return;
        if (gizli && val.Contains('•')) return; // maskeli değer geri gönderildiyse değiştirme
        envCfg.Set(key, val);
    }
    SetIf("LLMSUITE_BASE_URL", dto.BaseUrl);
    SetIf("LLMSUITE_FLOW_ID", dto.FlowId);
    SetIf("LLMSUITE_API_KEY", dto.ApiKey, gizli: true);
    if (dto.TimeoutSec is > 0) envCfg.Set("LLMSUITE_TIMEOUT_SEC", dto.TimeoutSec.Value.ToString());
    SetIf("LLMSUITE_CHAT_INPUT_ID", dto.ChatInputId);
    if (dto.SslDogrula.HasValue) envCfg.Set("LLMSUITE_SSL_DOGRULA", dto.SslDogrula.Value ? "true" : "false");
    SetIf("AI_API_URL", dto.AiApiUrl);
    SetIf("AI_API_KEY", dto.AiApiKey, gizli: true);
    SetIf("AI_MODEL", dto.AiModel);
    SetIf("MCP_PUBLIC_URL", dto.McpPublicUrl);
    if (!string.IsNullOrWhiteSpace(dto.AiApiUrl) && string.IsNullOrWhiteSpace(envCfg.AiProvider)) envCfg.Set("AI_PROVIDER", "ASELSAN AI");
    return Results.Ok(new { mesaj = "Ayarlar SD/.env dosyasına kaydedildi." });
});

api.MapPost("/llmsuite/test", async (LlmSuiteService llm, CancellationToken ct) =>
{
    var r = await llm.CalistirAsync(
        "Bağlantı testi: Kısaca 'LLM Suite bağlantısı başarılı' yaz ve erişebildiğin MCP araçlarının adlarını madde madde listele.",
        null, ct, timeoutSec: Math.Min(envCfg.LlmSuiteTimeoutSec, 180));
    return r.Ok ? Results.Ok(r) : Results.BadRequest(new { hata = r.Hata, ham = r.HamYanit });
});

api.MapGet("/llm/modeller", async (string? apiUrl, string? apiKey, LlmSuiteService llm, CancellationToken ct) =>
{
    var url = string.IsNullOrWhiteSpace(apiUrl) ? envCfg.AiApiUrl : apiUrl;
    var key = string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains('•') ? envCfg.AiApiKey : apiKey;
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest(new { hata = "AI API URL boş (örn: https://api.aselixai.aselsan.com.tr/v1)." });
    var (modeller, hata) = await llm.ModelleriGetirAsync(url, key, ct);
    return hata == null ? Results.Ok(new { modeller }) : Results.BadRequest(new { hata });
});

api.MapGet("/mcp/araclar", async (SdMcpServer mcp, CancellationToken ct) =>
{
    var r = await mcp.IsleAsync(new System.Text.Json.Nodes.JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, ct);
    return Results.Content(r?.ToJsonString() ?? "{}", "application/json");
});

api.MapPost("/mcp/arac-dene", async (McpAracDeneDto dto, SdMcpServer mcp, CancellationToken ct) =>
{
    var args = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(dto.ArgumentsJson) ? "{}" : dto.ArgumentsJson) as System.Text.Json.Nodes.JsonObject ?? new();
    var r = await mcp.IsleAsync(new System.Text.Json.Nodes.JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
        ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = dto.Name, ["arguments"] = args }
    }, ct);
    var text = r?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? r?.ToJsonString();
    return Results.Ok(new { metin = text });
});

// ---- LLM Suite: SD Süreci için MCP destekli yorum üret ve ekrana getir ----
api.MapPost("/talepler/{surecNo:long}/llmsuite-analiz", async (long surecNo, LlmSuiteAnalizDto dto, LlmSuiteService llm, SdMcpServer mcp, CancellationToken ct) =>
{
    await using var c = Db();
    var var_ = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sd.talep WHERE surec_no = @surecNo", new { surecNo })
             + await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sd.talep_kalem WHERE surec_no = @surecNo", new { surecNo });
    if (var_ == 0) return Results.NotFound(new { hata = "Talep bulunamadı." });

    var mesaj = string.IsNullOrWhiteSpace(dto.OzelPrompt)
        ? LlmSuiteService.VarsayilanPrompt(surecNo, dto.EkTalimat)
        : dto.OzelPrompt.Replace("{surec_no}", surecNo.ToString());

    if (dto.VeriyiEkle)
        mesaj += "\n\n---\n[MCP erişimi yoksa kullanılacak SD VERİSİ (DataFrame)]\n" + await mcp.SatirIciVeriAsync(surecNo);

    var r = await llm.CalistirAsync(mesaj, $"uges-sd-{surecNo}-{DateTime.Now:yyyyMMddHHmmss}", ct);
    if (!r.Ok) return Results.BadRequest(new { hata = r.Hata, ham = r.HamYanit, sureMs = r.SureMs });

    var model = $"ASELSAN LLM Suite Flow{(string.IsNullOrWhiteSpace(r.Model) ? "" : $" / {r.Model}")}";
    await c.ExecuteAsync(
        "INSERT INTO sd.ai_analiz_log (surec_no, analiz_turu, baslik, sonuc_metni, model_bilgisi) VALUES (@surecNo, 'llmsuite', 'LLM Suite (MCP) SD Yorumu', @yanit, @model)",
        new { surecNo, yanit = r.Yanit, model });
    return Results.Ok(new { yanit = r.Yanit, model, sureMs = r.SureMs, sessionId = r.SessionId });
});

// ---- İçe Aktarma ----
api.MapPost("/import/klasor", async (SdImportService svc) =>
{
    try { return Results.Ok(await svc.KlasordenAktarAsync(envCfg.VeriKlasoru)); }
    catch (Exception ex) { return Results.BadRequest(new { hata = ex.Message }); }
});

api.MapPost("/import/yukle", async (HttpRequest req, SdImportService svc) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { hata = "multipart/form-data bekleniyor." });
    var form = await req.ReadFormAsync();
    if (form.Files.Count == 0) return Results.BadRequest(new { hata = "En az bir .xlsx dosyası seçin." });
    var akislar = new List<(Stream, string)>();
    try
    {
        foreach (var f in form.Files)
        {
            var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            ms.Position = 0;
            akislar.Add((ms, f.FileName));
        }
        return Results.Ok(await svc.AktarAsync(akislar));
    }
    catch (Exception ex) { return Results.BadRequest(new { hata = ex.Message }); }
    finally { foreach (var (s, _) in akislar) s.Dispose(); }
}).DisableAntiforgery();

api.MapGet("/import/gecmis", async () =>
{
    await using var c = Db();
    var rows = await c.QueryAsync("SELECT id, baslangic, bitis, talep_dosya, kalem_dosya, talep_sayisi, kalem_sayisi, durum, mesaj, uyarilar::text AS uyarilar FROM sd.import_log ORDER BY id DESC LIMIT 20");
    return Results.Ok(rows);
});

// ---- Talepler + Kural Motoru + Özet ----
api.MapGet("/talepler", async (string? ara, string? durum, string? kontrolDurumu) =>
{
    await using var c = Db();
    var a = string.IsNullOrWhiteSpace(ara) ? null : ara.Trim();
    var dFiltre = string.IsNullOrWhiteSpace(durum) ? null : durum.Trim();

    var talepler = (await c.QueryAsync(@"
        SELECT * FROM sd.talep t
        WHERE (CAST(@durum AS text) IS NULL OR t.akis_durumu = CAST(@durum AS text))
          AND (CAST(@ara AS text) IS NULL OR t.belge_no ILIKE CAST(@like AS text) OR t.sd_belge_no ILIKE CAST(@like AS text)
               OR CAST(t.surec_no AS TEXT) ILIKE CAST(@like AS text)
               OR t.talep_eden_ad_soyad ILIKE CAST(@like AS text) OR t.crm_teklif_no ILIKE CAST(@like AS text)
               OR EXISTS (SELECT 1 FROM sd.talep_kalem k2 WHERE k2.surec_no = t.surec_no AND k2.malzeme_no ILIKE CAST(@like AS text)))
        ORDER BY t.belge_tarihi DESC NULLS LAST",
        new { ara = a, like = a == null ? null : $"%{a}%", durum = dFiltre })).ToList();

    var kalemler = (await c.QueryAsync("SELECT * FROM sd.talep_kalem ORDER BY surec_no, kalem_no, id")).ToList();
    var onaylar = (await c.QueryAsync("SELECT * FROM sd.talep_onay")).ToDictionary(x => (long)x.surec_no, x => x);

    var kalemGrup = kalemler
        .GroupBy(k => (long)k.surec_no)
        .ToDictionary(g => g.Key, g => g.Select(r => ToDict(r)).ToList());

    var liste = new List<object>();
    int topEngel = 0, topUyari = 0, topOnayli = 0, topHazir = 0;

    foreach (var t in talepler)
    {
        long sno = (long)t.surec_no;
        kalemGrup.TryGetValue(sno, out var kList);
        kList ??= new List<IDictionary<string, object?>>();
        onaylar.TryGetValue(sno, out var onay);
        string? onayDurum = onay?.onay_durumu;

        var kontrol = SdRuleEngine.Degerlendir(ToDict(t), kList, onayDurum);
        switch (kontrol.GenelDurum)
        {
            case "ENGEL": topEngel++; break;
            case "UYARI": topUyari++; break;
            case "ONAYLI": topOnayli++; break;
            case "HAZIR": topHazir++; break;
        }

        if (!string.IsNullOrWhiteSpace(kontrolDurumu) && !string.Equals(kontrol.GenelDurum, kontrolDurumu, StringComparison.OrdinalIgnoreCase))
            continue;

        decimal kalemToplam = 0m;
        string? pb = null;
        foreach (var kd in kList)
        {
            var m = kd.TryGetValue("miktar", out var mv) && mv is decimal md ? md : 0m;
            var f = kd.TryGetValue("birim_fiyat", out var fv) && fv is decimal fd ? fd : 0m;
            kalemToplam += m * f;
            if (pb == null && kd.TryGetValue("para_birimi", out var pv) && pv != null) pb = pv.ToString();
        }

        liste.Add(new
        {
            surec_no = sno,
            belge_no = (string?)t.belge_no,
            belge_tarihi = (DateTime?)t.belge_tarihi,
            sd_belge_no = (string?)t.sd_belge_no,
            siparis_turu = (string?)t.siparis_turu,
            crm_teklif_no = (string?)t.crm_teklif_no,
            aselsan_sozlesme_no = (string?)t.aselsan_sozlesme_no,
            talep_eden = (string?)(t.talep_eden_ad_soyad ?? t.ad_soyad),
            talep_eden_departman = (string?)t.talep_eden_departman,
            orijinal_sozlesme_tutari = (decimal?)t.orijinal_sozlesme_tutari,
            sozlesme_imza_tarihi = (DateTime?)t.sozlesme_imza_tarihi,
            akis_statu = (string?)t.akis_statu,
            akis_durumu = (string?)t.akis_durumu,
            kalem_sayisi = kList.Count,
            kalem_toplam_tutar = kList.Count > 0 ? (decimal?)kalemToplam : null,
            para_birimi = pb,
            kontrol_durumu = kontrol.GenelDurum,
            va01_acilabilir = kontrol.Va01Acilabilir,
            kirmizi_sayisi = kontrol.KirmiziSayisi,
            turuncu_sayisi = kontrol.TuruncuSayisi,
            sari_sayisi = kontrol.SariSayisi,
            onay_durumu = onayDurum ?? "Bekliyor",
            va01_acildi_mi = (bool?)(onay?.va01_acildi_mi) ?? false,
            girilen_sd_no = (string?)onay?.girilen_sd_no
        });
    }

    return Results.Ok(new
    {
        sayilar = new { engel = topEngel, uyari = topUyari, onayli = topOnayli, hazir = topHazir, toplam = talepler.Count },
        kayitlar = liste
    });
});

api.MapGet("/ozet", async () =>
{
    await using var c = Db();
    var genel = await c.QueryFirstAsync(@"
        SELECT (SELECT COUNT(*) FROM sd.talep) AS talep_sayisi,
               (SELECT COUNT(*) FROM sd.talep_kalem) AS kalem_sayisi,
               (SELECT COUNT(*) FROM sd.talep t WHERE NOT EXISTS (SELECT 1 FROM sd.talep_kalem k WHERE k.surec_no = t.surec_no)) AS kalemsiz_talep,
               (SELECT COUNT(*) FROM sd.talep_onay WHERE onay_durumu IN ('Onaylandı','VA01 Açıldı')) AS onaylanan_talep,
               (SELECT MAX(bitis) FROM sd.import_log WHERE durum = 'Başarılı') AS son_aktarim");
    var akis = await c.QueryAsync("SELECT COALESCE(akis_durumu,'(boş)') AS ad, COUNT(*)::int AS adet FROM sd.talep GROUP BY 1 ORDER BY 2 DESC");
    var tur = await c.QueryAsync("SELECT COALESCE(siparis_turu,'(boş)') AS ad, COUNT(*)::int AS adet FROM sd.talep GROUP BY 1 ORDER BY 2 DESC");
    return Results.Ok(new { genel, akis, tur });
});

// ---- Tekil Talep Detayı + Kural Bulguları + VA01 Önizleme + AI Geçmişi ----
api.MapGet("/talepler/{surecNo:long}", async (long surecNo, SdVa01Service va01) =>
{
    await using var c = Db();
    var (talep, kalemler, onay) = await GetTalepTamAsync(c, surecNo);
    if (talep == null && kalemler.Count == 0) return Results.NotFound(new { hata = "Talep bulunamadı." });

    var savedOv = ParseOverride((string?)onay?.va01_override);
    var va01Varsayilan = talep != null ? va01.HazirlaTalepPayload(talep, kalemler, savedOv) : null;
    var kontrol = SdRuleEngine.Degerlendir(talep, kalemler, (string?)onay?.onay_durumu);
    var aiGecmis = await c.QueryAsync(
        "SELECT id, analiz_turu, baslik, sonuc_metni, model_bilgisi, olusturuldu FROM sd.ai_analiz_log WHERE surec_no = @surecNo ORDER BY id DESC LIMIT 10",
        new { surecNo });

    return Results.Ok(new { talep, kalemler, kontrol, onay, va01Varsayilan, aiGecmis });
});

// ---- Onay / İstisna Kararı Kaydetme ----
api.MapPost("/talepler/{surecNo:long}/onayla", async (long surecNo, OnayIstekDto dto) =>
{
    await using var c = Db();
    var (talep, kalemler, _) = await GetTalepTamAsync(c, surecNo);
    if (talep == null) return Results.NotFound(new { hata = "Talep bulunamadı." });

    var onKontrol = SdRuleEngine.Degerlendir(talep, kalemler, null);
    if (dto.Onayla && onKontrol.KirmiziSayisi > 0)
        return Results.BadRequest(new { hata = $"Bu talepte {onKontrol.KirmiziSayisi} adet Kırmızı (engelleyici) kural ihlali var. Düzeltilmeden onaylanamaz." });

    var yeniDurum = dto.Onayla ? "Onaylandı" : "Bekliyor";
    var uyariKodlari = JsonSerializer.Serialize(onKontrol.Bulgular.Select(b => b.Kod).Distinct().ToList());

    await c.ExecuteAsync(@"
        INSERT INTO sd.talep_onay (surec_no, onay_durumu, onaylayan, istisna_notu, onaylanan_uyarilar, girilen_sd_no, guncellendi)
        VALUES (@surecNo, @durum, @onaylayan, @not, CAST(@uyarilar AS jsonb), @sdNo, NOW())
        ON CONFLICT (surec_no) DO UPDATE SET
            onay_durumu = EXCLUDED.onay_durumu,
            onaylayan = EXCLUDED.onaylayan,
            istisna_notu = EXCLUDED.istisna_notu,
            onaylanan_uyarilar = EXCLUDED.onaylanan_uyarilar,
            girilen_sd_no = COALESCE(NULLIF(EXCLUDED.girilen_sd_no, ''), sd.talep_onay.girilen_sd_no),
            guncellendi = NOW();",
        new
        {
            surecNo,
            durum = yeniDurum,
            onaylayan = string.IsNullOrWhiteSpace(dto.Onaylayan) ? "SD Giriş Sorumlusu" : dto.Onaylayan.Trim(),
            not = dto.IstisnaNotu?.Trim(),
            uyarilar = uyariKodlari,
            sdNo = dto.GirilenSdNo?.Trim()
        });

    var guncelKontrol = SdRuleEngine.Degerlendir(talep, kalemler, yeniDurum);
    return Results.Ok(new { mesaj = dto.Onayla ? "Talep SAP VA01 girişi için onaylandı." : "Onay geri alındı.", kontrol = guncelKontrol });
});

// ---- VA01 Başlık/Muhatap/Tarih Düzenlemelerini Kaydetme ----
api.MapPost("/talepler/{surecNo:long}/va01-kaydet", async (long surecNo, Va01OverrideDto ov) =>
{
    await using var c = Db();
    var ovJson = JsonSerializer.Serialize(ov);
    await c.ExecuteAsync(@"
        INSERT INTO sd.talep_onay (surec_no, onay_durumu, va01_override, guncellendi)
        VALUES (@surecNo, 'Bekliyor', CAST(@ov AS jsonb), NOW())
        ON CONFLICT (surec_no) DO UPDATE SET
            va01_override = CAST(@ov AS jsonb),
            guncellendi = NOW();", new { surecNo, ov = ovJson });
    return Results.Ok(new { mesaj = "VA01 alan bilgileri kaydedildi." });
});

// ---- SAP VA01 Aç & Doldur (KAYDETMEDEN KULLANICIYA BIRAKIR!) ----
api.MapPost("/talepler/{surecNo:long}/va01-ac", async (long surecNo, Va01AcIstek istek, SdVa01Service va01, CancellationToken ct) =>
{
    await using var c = Db();
    var (talep, kalemler, onay) = await GetTalepTamAsync(c, surecNo);
    if (talep == null) return Results.NotFound(new { hata = "Talep bulunamadı." });

    var kontrol = SdRuleEngine.Degerlendir(talep, kalemler, (string?)onay?.onay_durumu);
    if (!kontrol.Va01Acilabilir)
    {
        return Results.BadRequest(new
        {
            hata = kontrol.KirmiziSayisi > 0
                ? "Kırmızı engelleyici hatalar giderilmeden VA01 açılamaz."
                : "Sarı/Turuncu uyarılar mevcut. Lütfen önce uyarıları inceleyip 'Onayla' butonuna basın."
        });
    }

    istek.Override ??= ParseOverride((string?)onay?.va01_override);
    var ovJson = istek.Override != null ? JsonSerializer.Serialize(istek.Override) : null;

    var sonuc = await va01.AcVeDoldurAsync(talep, kalemler, istek, ct);
    if (sonuc.Ok)
    {
        await c.ExecuteAsync(@"
            INSERT INTO sd.talep_onay (surec_no, onay_durumu, va01_override, va01_acildi_mi, son_va01_tarihi, guncellendi)
            VALUES (@surecNo, 'VA01 Açıldı', CAST(@ov AS jsonb), TRUE, NOW(), NOW())
            ON CONFLICT (surec_no) DO UPDATE SET
                va01_override = COALESCE(CAST(@ov AS jsonb), sd.talep_onay.va01_override),
                va01_acildi_mi = TRUE,
                son_va01_tarihi = NOW(),
                guncellendi = NOW();", new { surecNo, ov = ovJson });
    }
    return sonuc.Ok ? Results.Ok(sonuc) : Results.BadRequest(new { hata = sonuc.Mesaj, kod = sonuc.HataKodu });
});

// ---- Uzak PC / VDI için Şifresiz İndirilebilir VA01 .vbs Scripti ----
api.MapGet("/talepler/{surecNo:long}/va01-script.vbs", async (long surecNo, SdVa01Service va01) =>
{
    await using var c = Db();
    var (talep, kalemler, onay) = await GetTalepTamAsync(c, surecNo);
    if (talep == null) return Results.NotFound(new { hata = "Talep bulunamadı." });

    var kontrol = SdRuleEngine.Degerlendir(talep, kalemler, (string?)onay?.onay_durumu);
    if (!kontrol.Va01Acilabilir)
        return Results.BadRequest(new { hata = "VA01 scripti indirmek için önce talebi onaylayın." });

    var savedOv = ParseOverride((string?)onay?.va01_override);
    var vbs = va01.VbsUret(talep, kalemler, savedOv);
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(vbs)).ToArray();
    return Results.File(bytes, "text/vbscript", $"VA01_Taslak_{surecNo}.vbs");
});

// ---- AI 1: Akıllı SD Talep Denetçisi ----
api.MapPost("/talepler/{surecNo:long}/ai-denetim", async (long surecNo, SdAiService ai) =>
{
    await using var c = Db();
    var (talep, kalemler, onay) = await GetTalepTamAsync(c, surecNo);
    if (talep == null) return Results.NotFound(new { hata = "Talep bulunamadı." });

    var kontrol = SdRuleEngine.Degerlendir(talep, kalemler, (string?)onay?.onay_durumu);
    var sys = @"Sen ASELSAN UGES TYM SAP SD (Satış ve Dağıtım) Kıdemli Uzman Denetçisisin.
Görevin, SAP VA01 ekranında SD siparişi açılmadan önce talep başlığını, kalem yapısını ve sistemin bulduğu kural uyarılarını UGES TYM SD Giriş Şartnamesi'ne göre analiz etmektir.
Özellikle şu noktalara dikkat et:
1) HIZMET / SOZLESME BEDELI gibi sanal kalemler ile fiziksel teslimat kalemlerinin ayrımı, alt kalem (Üst Kalem) bağlantılarının MRP/BOM patlamasına etkisi,
2) Kalem tipleri (Y210 bedelli, Y21F bedelsiz alt kalem, TAS/TAD vb.) ile birim fiyat ve garanti sürelerinin tutarlılığı,
3) Hazır Edim Tarihi ile Teslim Tarihi arasındaki kabul marjının makullüğü (ÜPM MIP onayı gereksinimi),
4) CRM Teklif No, Sözleşme İmza Tarihi, Muhataplar ve PYP öğelerinin tamlığı.
Yanıtını Türkçe, net maddeler halinde: '1. Kritik Bulgular', '2. Kalem & BOM Yapısı Değerlendirmesi', '3. VA01 Giriş Öncesi Önerilen Aksiyonlar' başlıklarıyla kısa ve uygulanabilir yaz.";

    var veriOzeti = JsonSerializer.Serialize(new
    {
        talep = talep.Where(kv => kv.Key != "ham_veri").ToDictionary(k => k.Key, v => v.Value),
        kalemler = kalemler.Select(k => k.Where(kv => kv.Key != "ham_veri").ToDictionary(x => x.Key, v => v.Value)),
        kuralMotoru = kontrol
    }, new JsonSerializerOptions { WriteIndented = true });

    var (yanit, model, hata) = await ai.TamamlaAsync(sys, $"Aşağıdaki SD talebini denetle:\n{veriOzeti}");
    if (hata != null) return Results.BadRequest(new { hata });

    await c.ExecuteAsync(
        "INSERT INTO sd.ai_analiz_log (surec_no, analiz_turu, baslik, sonuc_metni, model_bilgisi) VALUES (@surecNo, 'denetim', 'AI Uzman SD Denetimi', @yanit, @model)",
        new { surecNo, yanit, model });

    return Results.Ok(new { yanit, model });
});

// ---- AI 2: Sözleşme / PO Metni ↔ SD Talep Çapraz Kıyaslama ----
api.MapPost("/talepler/{surecNo:long}/ai-sozlesme-kiyas", async (long surecNo, HttpRequest req, SdAiService ai) =>
{
    await using var c = Db();
    var (talep, kalemler, _) = await GetTalepTamAsync(c, surecNo);
    if (talep == null) return Results.NotFound(new { hata = "Talep bulunamadı." });

    string sozlesmeMetni = "";
    string kaynakAd = "Yapıştırılan Sözleşme Metni";

    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync();
        sozlesmeMetni = form["metin"].ToString();
        if (form.Files.Count > 0)
        {
            var f = form.Files[0];
            kaynakAd = f.FileName;
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            ms.Position = 0;
            sozlesmeMetni = SdAiService.BelgedenMetinCikar(ms, f.FileName);
        }
    }

    if (string.IsNullOrWhiteSpace(sozlesmeMetni))
        return Results.BadRequest(new { hata = "Kıyaslama için bir sözleşme (.docx / .txt) yükleyin veya sözleşme/PO metnini yapıştırın." });

    if (sozlesmeMetni.Length > 18000) sozlesmeMetni = sozlesmeMetni[..18000];

    var sys = @"Sen ASELSAN UGES TYM Sözleşme ve SAP SD Uyum Denetçisisin (Şartname Md. 5 ve Md. 6).
Sana verilen Sözleşme/PO metni ile SD Talep verilerini çapraz kontrol et:
- Sözleşme No / Adı,
- Orijinal Sözleşme Tutarı ve Para Birimi,
- Teslimat Kalemleri, Miktarlar ve Birim Fiyatlar,
- Sözleşme İmza / Teslimat Tarihleri,
- Garanti Süreleri ve Incoterms şartları.
Uyumlu olanları ✓, uyuşmayan veya eksik olanları ⚠ ile tablo/madde halinde açıkça raporla.";

    var talepJson = JsonSerializer.Serialize(new
    {
        talep = talep.Where(kv => kv.Key != "ham_veri").ToDictionary(k => k.Key, v => v.Value),
        kalemler = kalemler.Select(k => k.Where(kv => kv.Key != "ham_veri").ToDictionary(x => x.Key, v => v.Value))
    });

    var (yanit, model, hata) = await ai.TamamlaAsync(sys, $"[SD TALEP VERİSİ]\n{talepJson}\n\n[SÖZLEŞME / PO METNİ ({kaynakAd})]\n{sozlesmeMetni}");
    if (hata != null) return Results.BadRequest(new { hata });

    await c.ExecuteAsync(
        "INSERT INTO sd.ai_analiz_log (surec_no, analiz_turu, baslik, sonuc_metni, model_bilgisi) VALUES (@surecNo, 'sozlesme_kiyas', @baslik, @yanit, @model)",
        new { surecNo, baslik = $"Sözleşme Kıyaslama ({kaynakAd})", yanit, model });

    return Results.Ok(new { yanit, model });
}).DisableAntiforgery();

// ---- AI 3: İlgili Birime (Talep Eden / ÜPM MIP / PKD) Mail Taslağı Üretme ----
api.MapPost("/talepler/{surecNo:long}/ai-mail-taslak", async (long surecNo, MailTaslakIstekDto dto, SdAiService ai) =>
{
    await using var c = Db();
    var (talep, kalemler, _) = await GetTalepTamAsync(c, surecNo);
    if (talep == null) return Results.NotFound(new { hata = "Talep bulunamadı." });

    var kontrol = SdRuleEngine.Degerlendir(talep, kalemler, null);
    var hedefAciklama = (dto.Hedef ?? "talep_eden").ToLowerInvariant() switch
    {
        "mip" => "ÜPM MIP Sorumlusuna: Şartname Md. 9 kapsamında kalemlerin Malzeme Hazır Edim Tarihlerinin üretim planlaması açısından uygunluk onayı talebi.",
        "pkd" => "TYM PKD Sorumlusuna: Şartname Md. 5, 7 ve 8 kapsamında sözleşme-form uyumu, stok numarası / BOM yapısı ve hizmet-malzeme ayrımı teyidi.",
        "gssym" => "Gelir ve Satış Sözleşmeleri Yönetimi Müdürlüğüne: Şartname Md. 4 kapsamında geçmiş yıla ait sözleşme imza tarihi / eksik müşteri kodu onayı.",
        _ => "Talebi açan personele: Tespit edilen eksik ve uyarıların düzeltilmesi / teyit edilmesi talebi."
    };

    var sys = @"Sen ASELSAN UGES TYM SD Giriş Sorumlusu adına kurumsal, net ve kibar e-posta taslağı hazırlayan bir asistansın.
İlk satırda 'Konu: ...' yaz, ardından doğrudan kopyalanıp Outlook'a yapıştırılabilecek kurumsal e-posta metnini oluştur.";

    var veri = JsonSerializer.Serialize(new
    {
        hedef = hedefAciklama,
        ekNot = dto.EkNot,
        surecNo,
        belgeNo = talep.TryGetValue("belge_no", out var bn) ? bn : null,
        talepEden = talep.TryGetValue("talep_eden_ad_soyad", out var te) ? te : null,
        kalemler = kalemler.Select(k => new
        {
            kalemNo = k["kalem_no"],
            malzemeNo = k["malzeme_no"],
            miktar = k["miktar"],
            hazirEdim = k["hazir_edim_tarihi"],
            teslim = k["teslim_tarihi"],
            pyp = k["pyp_ogesi"]
        }),
        uyarilarVeHatalar = kontrol.Bulgular
    });

    var (yanit, model, hata) = await ai.TamamlaAsync(sys, veri);
    if (hata != null) return Results.BadRequest(new { hata });

    await c.ExecuteAsync(
        "INSERT INTO sd.ai_analiz_log (surec_no, analiz_turu, baslik, sonuc_metni, model_bilgisi) VALUES (@surecNo, 'mail_taslak', @baslik, @yanit, @model)",
        new { surecNo, baslik = $"Mail Taslağı ({dto.Hedef ?? "talep_eden"})", yanit, model });

    return Results.Ok(new { yanit, model });
});

app.MapFallbackToFile("index.html");

app.Logger.LogInformation("[SD] UGES SD Kontrol Standalone Uygulaması: http://localhost:{Port} (Swagger: /swagger) — DB: {Host}/{Db} (şema: {Schema})",
    envCfg.BackendPort, envCfg.DbHost, envCfg.DbName, envCfg.DbSchema);
app.Logger.LogInformation("[MCP] LLM Suite MCP Sunucusu: http://{Ip}:{Port}/sse (SSE) ve /mcp (Streamable HTTP){Auth}",
    SdMcpServer.YerelIpAdresleri().FirstOrDefault() ?? "localhost", envCfg.McpPort,
    string.IsNullOrWhiteSpace(envCfg.McpApiKey) ? "" : " — x-api-key gerekli");
app.Run();

public record OnayIstekDto(bool Onayla, string? Onaylayan, string? IstisnaNotu, string? GirilenSdNo);
public record MailTaslakIstekDto(string? Hedef, string? EkNot);
public record LlmSuiteAyarDto(string? BaseUrl, string? FlowId, string? ApiKey, int? TimeoutSec, string? ChatInputId, bool? SslDogrula, string? AiApiUrl, string? AiApiKey, string? AiModel, string? McpPublicUrl);
public record LlmSuiteAnalizDto(string? EkTalimat, bool VeriyiEkle, string? OzelPrompt);
public record McpAracDeneDto(string Name, string? ArgumentsJson);
