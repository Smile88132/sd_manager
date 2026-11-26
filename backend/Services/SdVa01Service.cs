using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SdKontrol.API.Services;

public sealed class Va01AcIstek
{
    public string? SapUser { get; set; }
    public string? SapPassword { get; set; }
    public bool ReuseOpenSession { get; set; } = true;
    public string? Bolum { get; set; }
    public string? SiparisVerenOverride { get; set; }
}

public sealed class Va01AcSonuc
{
    public bool Ok { get; set; }
    public string Mesaj { get; set; } = "";
    public string? HataKodu { get; set; }
    public List<string> Doldurulan { get; set; } = new();
    public List<string> Uyarilar { get; set; } = new();
    public string? SapDurumCubugu { get; set; }
}

/// <summary>
/// SAP GUI VA01 taslak doldurucu (Kaydetmeden kullanıcıya bırakır).
/// Hem Python COM (aynı makine) hem de indirilebilir .vbs scripti (uzak istemci/VDI) üretir.
/// Şifre diske/veritabanına ASLA kaydedilmez.
/// </summary>
public sealed class SdVa01Service
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly EnvConfig _env;
    private readonly IWebHostEnvironment _hostEnv;
    private readonly ILogger<SdVa01Service> _logger;

    public SdVa01Service(EnvConfig env, IWebHostEnvironment hostEnv, ILogger<SdVa01Service> logger)
    {
        _env = env;
        _hostEnv = hostEnv;
        _logger = logger;
    }

    private static string S(IDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "" : "";

    private static string TrDate(IDictionary<string, object?> d, string k)
    {
        if (!d.TryGetValue(k, out var v) || v == null) return "";
        if (v is DateTime dt) return dt.ToString("dd.MM.yyyy");
        if (v is DateOnly dOnly) return dOnly.ToString("dd.MM.yyyy");
        return DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var p) ? p.ToString("dd.MM.yyyy") : "";
    }

    public async Task<Va01AcSonuc> AcVeDoldurAsync(
        IDictionary<string, object?> talep,
        IList<IDictionary<string, object?>> kalemler,
        Va01AcIstek istek,
        CancellationToken ct)
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(3), ct))
            return new Va01AcSonuc { Ok = false, HataKodu = "busy", Mesaj = "Şu anda başka bir SAP VA01 penceresi hazırlanıyor, birkaç saniye sonra tekrar deneyin." };

        try
        {
            var scriptPath = Path.Combine(_hostEnv.ContentRootPath, "python", "va01_fill.py");
            if (!File.Exists(scriptPath))
                scriptPath = Path.Combine(AppContext.BaseDirectory, "python", "va01_fill.py");
            if (!File.Exists(scriptPath))
                return new Va01AcSonuc { Ok = false, HataKodu = "setup", Mesaj = $"Python scripti bulunamadı: {scriptPath}" };

            var ilkTeslim = kalemler.Select(k => TrDate(k, "teslim_tarihi")).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
            var payload = new
            {
                sapUser = (istek.SapUser ?? "").Trim(),
                sapPassword = istek.SapPassword ?? "",
                reuseOpenSession = istek.ReuseOpenSession,
                sapConnection = _env.SapConnection,
                sapClient = _env.SapClient,
                sapLanguage = _env.SapLanguage,
                sapLogonPath = _env.SapLogonPath,
                talep = new
                {
                    surecNo = S(talep, "surec_no"),
                    belgeNo = S(talep, "belge_no"),
                    siparisTuru = string.IsNullOrEmpty(S(talep, "siparis_turu")) ? "ZSTD" : S(talep, "siparis_turu"),
                    satisOrganizasyonu = string.IsNullOrEmpty(S(talep, "satis_organizasyonu")) ? "9990" : S(talep, "satis_organizasyonu"),
                    dagitimKanali = string.IsNullOrEmpty(S(talep, "dagitim_kanali")) ? "10" : S(talep, "dagitim_kanali"),
                    bolum = string.IsNullOrWhiteSpace(istek.Bolum) ? _env.SapDefaultSpart : istek.Bolum!.Trim(),
                    satisBurosu = S(talep, "satis_burosu"),
                    satisGrubu = S(talep, "satis_grubu"),
                    siparisVeren = !string.IsNullOrWhiteSpace(istek.SiparisVerenOverride) ? istek.SiparisVerenOverride!.Trim() : S(talep, "siparis_veren"),
                    maliTeslimAlan = S(talep, "mali_teslim_alan"),
                    sasNo = S(talep, "sas_no"),
                    sasTarihi = TrDate(talep, "sas_tarihi"),
                    aselsanSozlesmeNo = S(talep, "aselsan_sozlesme_no"),
                    crmTeklifNo = S(talep, "crm_teklif_no"),
                    uretimYeri = S(talep, "uretim_yeri"),
                    ilkTeslimTarihi = ilkTeslim
                },
                kalemler = kalemler.Select(k => new
                {
                    kalemNo = S(k, "kalem_no"),
                    malzemeNo = S(k, "malzeme_no"),
                    miktar = S(k, "miktar_ham").Length > 0 ? S(k, "miktar_ham") : S(k, "miktar"),
                    olcuBirimi = S(k, "olcu_birimi"),
                    birimFiyat = S(k, "birim_fiyat_ham").Length > 0 ? S(k, "birim_fiyat_ham") : S(k, "birim_fiyat"),
                    paraBirimi = S(k, "para_birimi"),
                    pypOgesi = S(k, "pyp_ogesi"),
                    kalemTipi = S(k, "kalem_tipi"),
                    teslimTarihi = TrDate(k, "teslim_tarihi"),
                    hazirEdimTarihi = TrDate(k, "hazir_edim_tarihi")
                }).ToList()
            };

            var psi = new ProcessStartInfo
            {
                FileName = _env.PythonExe,
                Arguments = $"\"{scriptPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Python başlatılamadı ({_env.PythonExe}). .env içindeki PYTHON_EXE yolunu kontrol edin.");

            await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(payload));
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("USER_ERROR:"))
                {
                    var rest = line["USER_ERROR:".Length..];
                    var parts = rest.Split('|', 2);
                    return new Va01AcSonuc
                    {
                        Ok = false,
                        HataKodu = parts[0],
                        Mesaj = parts.Length > 1 ? parts[1] : rest
                    };
                }
                if (line.StartsWith("RESULT_JSON:"))
                {
                    var json = line["RESULT_JSON:".Length..];
                    using var doc = JsonDocument.Parse(json);
                    var r = doc.RootElement;
                    return new Va01AcSonuc
                    {
                        Ok = r.GetProperty("ok").GetBoolean(),
                        Mesaj = r.GetProperty("mesaj").GetString() ?? "",
                        SapDurumCubugu = r.TryGetProperty("sapDurumCubugu", out var sb) ? sb.GetString() : null,
                        Doldurulan = r.TryGetProperty("doldurulan", out var dl) ? dl.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new(),
                        Uyarilar = r.TryGetProperty("uyarilar", out var ul) ? ul.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new()
                    };
                }
            }

            return new Va01AcSonuc
            {
                Ok = false,
                HataKodu = "unknown",
                Mesaj = string.IsNullOrWhiteSpace(stderr) ? $"SAP scripti beklenmeyen şekilde sonlandı (Kod {proc.ExitCode})." : stderr.Trim()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SD VA01] Çalıştırma hatası");
            return new Va01AcSonuc { Ok = false, HataKodu = "exception", Mesaj = ex.Message };
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Kullanıcının kendi bilgisayarındaki açık SAP GUI oturumunda çift tıklayarak VA01 dolduran (şifresiz ve kaydetmeyen) VBS üretir.</summary>
    public string VbsUret(IDictionary<string, object?> talep, IList<IDictionary<string, object?>> kalemler, string? bolum = null, string? siparisVerenOverride = null)
    {
        static string Esc(string s) => s.Replace("\"", "\"\"");
        var sipTuru = string.IsNullOrEmpty(S(talep, "siparis_turu")) ? "ZSTD" : S(talep, "siparis_turu");
        var vkorg = string.IsNullOrEmpty(S(talep, "satis_organizasyonu")) ? "9990" : S(talep, "satis_organizasyonu");
        var vtweg = string.IsNullOrEmpty(S(talep, "dagitim_kanali")) ? "10" : S(talep, "dagitim_kanali");
        var spart = string.IsNullOrWhiteSpace(bolum) ? _env.SapDefaultSpart : bolum!.Trim();
        var kunnr = !string.IsNullOrWhiteSpace(siparisVerenOverride) ? siparisVerenOverride!.Trim() : S(talep, "siparis_veren");
        var bstkd = !string.IsNullOrEmpty(S(talep, "sas_no")) ? S(talep, "sas_no") : S(talep, "belge_no");

        var sb = new StringBuilder();
        sb.AppendLine("' UGES SD Kontrol - Otomatik VA01 Taslak Doldurucu (KAYDETMEZ - Kontrol Kullanicidadiir)");
        sb.AppendLine("Option Explicit");
        sb.AppendLine("On Error Resume Next");
        sb.AppendLine("Dim SapGuiAuto, application, connection, session");
        sb.AppendLine("Set SapGuiAuto = GetObject(\"SAPGUI\")");
        sb.AppendLine("If Err.Number <> 0 Then MsgBox \"Acik bir SAP GUI oturumu bulunamadi. Lutfen once SAP Logon ile giris yapin.\", 48, \"UGES SD Kontrol\" : WScript.Quit 1");
        sb.AppendLine("Set application = SapGuiAuto.GetScriptingEngine");
        sb.AppendLine("Set connection = application.Children(0)");
        sb.AppendLine("Set session = connection.Children(0)");
        sb.AppendLine("Err.Clear");
        sb.AppendLine("session.findById(\"wnd[0]\").maximize");
        sb.AppendLine("session.findById(\"wnd[0]/tbar[0]/okcd\").text = \"/nVA01\"");
        sb.AppendLine("session.findById(\"wnd[0]\").sendVKey 0");
        sb.AppendLine("WScript.Sleep 500");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-AUART\").text = \"{Esc(sipTuru)}\"");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VKORG\").text = \"{Esc(vkorg)}\"");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VTWEG\").text = \"{Esc(vtweg)}\"");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-SPART\").text = \"{Esc(spart)}\"");
        if (!string.IsNullOrEmpty(S(talep, "satis_burosu")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VKBUR\").text = \"{Esc(S(talep, "satis_burosu"))}\"");
        if (!string.IsNullOrEmpty(S(talep, "satis_grubu")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VKGRP\").text = \"{Esc(S(talep, "satis_grubu"))}\"");
        sb.AppendLine("session.findById(\"wnd[0]\").sendVKey 0");
        sb.AppendLine("WScript.Sleep 700");
        if (!string.IsNullOrEmpty(kunnr))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/subPART-SUB:SAPMV45A:4701/ctxtKUAGV-KUNNR\").text = \"{Esc(kunnr)}\"");
        if (!string.IsNullOrEmpty(bstkd))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/txtVBKD-BSTKD\").text = \"{Esc(bstkd)}\"");

        const string tbl = "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/subSUBSCREEN_TC:SAPMV45A:4900/tblSAPMV45ATCTRL_U_ERF_AUFTRAG";
        for (int i = 0; i < Math.Min(kalemler.Count, 12); i++)
        {
            var k = kalemler[i];
            var malz = Esc(S(k, "malzeme_no"));
            var mkt = Esc(S(k, "miktar_ham").Length > 0 ? S(k, "miktar_ham") : S(k, "miktar"));
            var brm = Esc(S(k, "olcu_birimi"));
            sb.AppendLine($"session.findById(\"{tbl}/ctxtRV45A-MABNR[1,{i}]\").text = \"{malz}\"");
            sb.AppendLine($"session.findById(\"{tbl}/txtRV45A-KWMENG[2,{i}]\").text = \"{mkt}\"");
            if (!string.IsNullOrEmpty(brm))
                sb.AppendLine($"session.findById(\"{tbl}/ctxtVBAP-VRKME[3,{i}]\").text = \"{brm}\"");
        }
        sb.AppendLine("MsgBox \"VA01 alanlari dolduruldu (KAYDEDILMEDI). Lutfen kontrol edip SAP ekranindan kendiniz kaydedin.\", 64, \"UGES SD Kontrol\"");
        return sb.ToString();
    }
}
