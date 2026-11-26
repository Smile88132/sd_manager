using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdKontrol.API.Services;

public sealed class Va01OverrideDto
{
    public string? SiparisVeren { get; set; }
    public string? MaliTeslimAlan { get; set; }
    public string? SasNo { get; set; }
    public string? SasTarihi { get; set; }
    public string? IstenenTeslimTarihi { get; set; }
    public string? SozlesmeBaslangic { get; set; }
    public string? SozlesmeBitis { get; set; }
    public string? OdemeKosulu { get; set; }
    public string? Inco1 { get; set; }
    public string? Inco2 { get; set; }
    public string? ParaBirimi { get; set; }
    public string? SatisOrg { get; set; }
    public string? DagitimKanali { get; set; }
    public string? Bolum { get; set; }
    public string? UretimYeri { get; set; }
    public bool ZorlaKalemTipi { get; set; }
}

public sealed class Va01AcIstek
{
    public string? SapUser { get; set; }
    public string? SapPassword { get; set; }
    public bool ReuseOpenSession { get; set; } = true;
    public Va01OverrideDto? Override { get; set; }
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
/// SAP GUI VA01 tam taslak doldurucu (Kaydetmeden kullanıcıya bırakır).
/// Hem Python COM (aynı makine) hem de indirilebilir .vbs scripti (uzak istemci/VDI) üretir.
/// </summary>
public sealed class SdVa01Service
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> KnownIncoterms = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXW", "FCA", "FAS", "FOB", "CFR", "CIF", "CPT", "CIP", "DAP", "DPU", "DDP", "DAT"
    };

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

    private static string Pick(string? overrideVal, params string[] fallbacks)
    {
        if (!string.IsNullOrWhiteSpace(overrideVal)) return overrideVal.Trim();
        foreach (var f in fallbacks)
            if (!string.IsNullOrWhiteSpace(f)) return f.Trim();
        return "";
    }

    private static string TrDate(IDictionary<string, object?> d, string k)
    {
        if (!d.TryGetValue(k, out var v) || v == null) return "";
        if (v is DateTime dt) return dt.ToString("dd.MM.yyyy");
        if (v is DateOnly dOnly) return dOnly.ToString("dd.MM.yyyy");
        return DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var p) ? p.ToString("dd.MM.yyyy") : "";
    }

    public static (string inco1, string inco2) SplitIncoterms(string? rawInco)
    {
        var s = (rawInco ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return ("", "");
        var parts = s.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 1 && parts[0].Length == 3 && KnownIncoterms.Contains(parts[0]))
            return (parts[0].ToUpperInvariant(), parts.Length > 1 ? parts[1] : "");
        return ("", s);
    }

    public object HazirlaTalepPayload(
        IDictionary<string, object?> talep,
        IList<IDictionary<string, object?>> kalemler,
        Va01OverrideDto? ov)
    {
        var (defInco1, defInco2) = SplitIncoterms(S(talep, "incoterms"));
        var ilkKalemTeslim = kalemler
            .Select(k => TrDate(k, "teslim_tarihi"))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";

        var aselsanSoz = S(talep, "aselsan_sozlesme_no");
        if (aselsanSoz.Equals("SS", StringComparison.OrdinalIgnoreCase)) aselsanSoz = "";

        var sv = Pick(ov?.SiparisVeren, S(talep, "siparis_veren"));
        var ta = Pick(ov?.MaliTeslimAlan, S(talep, "mali_teslim_alan"), sv);
        var ilkPb = kalemler.Select(k => S(k, "para_birimi")).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";

        return new
        {
            surecNo = S(talep, "surec_no"),
            belgeNo = S(talep, "belge_no"),
            siparisTuru = Pick(null, S(talep, "siparis_turu"), "ZSTD"),
            satisOrganizasyonu = Pick(ov?.SatisOrg, S(talep, "satis_organizasyonu"), "9990"),
            dagitimKanali = Pick(ov?.DagitimKanali, S(talep, "dagitim_kanali"), "10"),
            bolum = Pick(ov?.Bolum, _env.SapDefaultSpart, "10"),
            satisBurosu = S(talep, "satis_burosu"),
            satisGrubu = S(talep, "satis_grubu"),
            siparisVeren = sv,
            maliTeslimAlan = ta,
            sasNo = Pick(ov?.SasNo, S(talep, "sas_no"), aselsanSoz, S(talep, "belge_no")),
            sasTarihi = Pick(ov?.SasTarihi, TrDate(talep, "sas_tarihi"), TrDate(talep, "sozlesme_imza_tarihi")),
            istenenTeslimTarihi = Pick(ov?.IstenenTeslimTarihi, ilkKalemTeslim, TrDate(talep, "sozlesme_teslim_tarihi")),
            sozlesmeBaslangic = Pick(ov?.SozlesmeBaslangic, TrDate(talep, "sozlesme_baslangic_tarihi")),
            sozlesmeBitis = Pick(ov?.SozlesmeBitis, TrDate(talep, "sozlesme_bitis_tarihi")),
            odemeKosulu = Pick(ov?.OdemeKosulu, S(talep, "odeme_sekli")),
            inco1 = Pick(ov?.Inco1, defInco1),
            inco2 = Pick(ov?.Inco2, defInco2),
            paraBirimi = Pick(ov?.ParaBirimi, ilkPb),
            uretimYeri = Pick(ov?.UretimYeri, S(talep, "uretim_yeri")),
            zorlaKalemTipi = ov?.ZorlaKalemTipi ?? false
        };
    }

    public List<object> HazirlaKalemlerPayload(IList<IDictionary<string, object?>> kalemler) =>
        kalemler.Select(k => (object)new
        {
            kalemNo = S(k, "kalem_no"),
            malzemeNo = S(k, "malzeme_no"),
            miktar = S(k, "miktar_ham").Length > 0 ? S(k, "miktar_ham") : S(k, "miktar"),
            olcuBirimi = S(k, "olcu_birimi"),
            birimFiyat = S(k, "birim_fiyat_ham").Length > 0 ? S(k, "birim_fiyat_ham") : S(k, "birim_fiyat"),
            paraBirimi = S(k, "para_birimi"),
            pypOgesi = S(k, "pyp_ogesi"),
            icSiparis = S(k, "ic_siparis"),
            kalemTipi = S(k, "kalem_tipi"),
            ustKalem = S(k, "ust_kalem"),
            sevkiyatNoktasi = S(k, "sevkiyat_noktasi"),
            teslimTarihi = TrDate(k, "teslim_tarihi"),
            hazirEdimTarihi = TrDate(k, "hazir_edim_tarihi")
        }).ToList();

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

            var talepObj = HazirlaTalepPayload(talep, kalemler, istek.Override);
            var payload = new
            {
                sapUser = (istek.SapUser ?? "").Trim(),
                sapPassword = istek.SapPassword ?? "",
                reuseOpenSession = istek.ReuseOpenSession,
                sapConnection = _env.SapConnection,
                sapClient = _env.SapClient,
                sapLanguage = _env.SapLanguage,
                sapLogonPath = _env.SapLogonPath,
                talep = talepObj,
                kalemler = HazirlaKalemlerPayload(kalemler)
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

    /// <summary>Kullanıcının kendi bilgisayarındaki açık SAP GUI oturumunda çift tıklayarak tam VA01 dolduran (şifresiz ve kaydetmeyen) VBS üretir.</summary>
    public string VbsUret(IDictionary<string, object?> talep, IList<IDictionary<string, object?>> kalemler, Va01OverrideDto? ov = null)
    {
        static string Esc(string s) => (s ?? "").Replace("\"", "\"\"");
        static string SapDec(string s)
        {
            s = (s ?? "").Trim();
            if (s.Contains(',') && s.Contains('.')) return s.Replace(".", "");
            if (s.Contains('.'))
            {
                var p = s.Split('.');
                if (p.Length == 2 && p[1].All(c => c == '0')) return p[0];
                return s.Replace(".", ",");
            }
            return s;
        }

        var tJson = JsonSerializer.SerializeToElement(HazirlaTalepPayload(talep, kalemler, ov));
        string T(string prop) => tJson.TryGetProperty(prop, out var el) ? el.ToString() : "";

        var sb = new StringBuilder();
        sb.AppendLine("' UGES SD Kontrol - Tam VA01 Taslak Doldurucu (KAYDETMEZ - Kontrol Kullanicidadir)");
        sb.AppendLine("Option Explicit");
        sb.AppendLine("On Error Resume Next");
        sb.AppendLine("Dim SapGuiAuto, application, connection, session, tbl, i, c, cell, cname");
        sb.AppendLine("Set SapGuiAuto = GetObject(\"SAPGUI\")");
        sb.AppendLine("If Err.Number <> 0 Then MsgBox \"Acik bir SAP GUI oturumu bulunamadi. Lutfen once SAP Logon ile giris yapin.\", 48, \"UGES SD Kontrol\" : WScript.Quit 1");
        sb.AppendLine("Set application = SapGuiAuto.GetScriptingEngine");
        sb.AppendLine("Set connection = application.Children(0)");
        sb.AppendLine("Set session = connection.Children(0)");
        sb.AppendLine("Err.Clear");
        sb.AppendLine();
        sb.AppendLine("Sub DismissPopups()");
        sb.AppendLine("  Dim stepIdx, pTitle");
        sb.AppendLine("  For stepIdx = 1 To 20");
        sb.AppendLine("    Err.Clear");
        sb.AppendLine("    session.findById(\"wnd[1]/tbar[0]/btn[0]\").press");
        sb.AppendLine("    If Err.Number = 0 Then WScript.Sleep 250 Else");
        sb.AppendLine("      Err.Clear");
        sb.AppendLine("      If UCase(session.Info.Program) = \"SAPLATPC\" Then");
        sb.AppendLine("        session.findById(\"wnd[0]/tbar[0]/btn[0]\").press");
        sb.AppendLine("        WScript.Sleep 300");
        sb.AppendLine("      Else");
        sb.AppendLine("        Exit For");
        sb.AppendLine("      End If");
        sb.AppendLine("    End If");
        sb.AppendLine("  Next");
        sb.AppendLine("End Sub");
        sb.AppendLine();
        sb.AppendLine("Sub SetCol(rowIdx, targetName, val)");
        sb.AppendLine("  If val = \"\" Then Exit Sub");
        sb.AppendLine("  Dim colIdx, cl");
        sb.AppendLine("  Err.Clear");
        sb.AppendLine("  Set tbl = session.findById(\"wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/subSUBSCREEN_TC:SAPMV45A:4900/tblSAPMV45ATCTRL_U_ERF_AUFTRAG\")");
        sb.AppendLine("  If Err.Number <> 0 Then Exit Sub");
        sb.AppendLine("  For colIdx = 0 To tbl.Columns.Count - 1");
        sb.AppendLine("    Err.Clear");
        sb.AppendLine("    Set cl = tbl.GetCell(rowIdx, colIdx)");
        sb.AppendLine("    If Err.Number = 0 Then");
        sb.AppendLine("      If UCase(cl.Name) = UCase(targetName) And cl.Changeable Then cl.text = val : Exit Sub");
        sb.AppendLine("    End If");
        sb.AppendLine("  Next");
        sb.AppendLine("End Sub");
        sb.AppendLine();
        sb.AppendLine("session.findById(\"wnd[0]\").maximize");
        sb.AppendLine("session.findById(\"wnd[0]/tbar[0]/okcd\").text = \"/nVA01\"");
        sb.AppendLine("session.findById(\"wnd[0]\").sendVKey 0");
        sb.AppendLine("WScript.Sleep 500");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-AUART\").text = \"{Esc(T("siparisTuru"))}\"");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VKORG\").text = \"{Esc(T("satisOrganizasyonu"))}\"");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VTWEG\").text = \"{Esc(T("dagitimKanali"))}\"");
        sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-SPART\").text = \"{Esc(T("bolum"))}\"");
        if (!string.IsNullOrEmpty(T("satisBurosu")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VKBUR\").text = \"{Esc(T("satisBurosu"))}\"");
        if (!string.IsNullOrEmpty(T("satisGrubu")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/ctxtVBAK-VKGRP\").text = \"{Esc(T("satisGrubu"))}\"");
        sb.AppendLine("session.findById(\"wnd[0]\").sendVKey 0");
        sb.AppendLine("WScript.Sleep 600");
        sb.AppendLine("Call DismissPopups()");

        if (!string.IsNullOrEmpty(T("siparisVeren")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/subPART-SUB:SAPMV45A:4701/ctxtKUAGV-KUNNR\").text = \"{Esc(T("siparisVeren"))}\"");
        if (!string.IsNullOrEmpty(T("maliTeslimAlan")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/subPART-SUB:SAPMV45A:4701/ctxtKUWEV-KUNNR\").text = \"{Esc(T("maliTeslimAlan"))}\"");
        if (!string.IsNullOrEmpty(T("sasNo")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/txtVBKD-BSTKD\").text = \"{Esc(T("sasNo"))}\"");
        if (!string.IsNullOrEmpty(T("sasTarihi")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtVBKD-BSTDK\").text = \"{Esc(T("sasTarihi"))}\"");
        if (!string.IsNullOrEmpty(T("istenenTeslimTarihi")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtRV45A-KETDAT\").text = \"{Esc(T("istenenTeslimTarihi"))}\"");
        sb.AppendLine("session.findById(\"wnd[0]\").sendVKey 0");
        sb.AppendLine("WScript.Sleep 600");
        sb.AppendLine("Call DismissPopups()");

        if (!string.IsNullOrEmpty(T("paraBirimi")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtVBAK-WAERK\").text = \"{Esc(T("paraBirimi"))}\"");
        if (!string.IsNullOrEmpty(T("sozlesmeBaslangic")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-GUEBG\").text = \"{Esc(T("sozlesmeBaslangic"))}\"");
        if (!string.IsNullOrEmpty(T("sozlesmeBitis")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-GUEEN\").text = \"{Esc(T("sozlesmeBitis"))}\"");
        if (!string.IsNullOrEmpty(T("odemeKosulu")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-ZTERM\").text = \"{Esc(T("odemeKosulu"))}\"");
        if (!string.IsNullOrEmpty(T("inco1")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-INCO1\").text = \"{Esc(T("inco1"))}\"");
        if (!string.IsNullOrEmpty(T("inco2")))
            sb.AppendLine($"session.findById(\"wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/txtVBKD-INCO2\").text = \"{Esc(T("inco2"))}\"");

        var maxRows = Math.Min(kalemler.Count, 12);
        for (int i = 0; i < maxRows; i++)
        {
            var k = kalemler[i];
            var malz = Esc(S(k, "malzeme_no"));
            var mkt = Esc(SapDec(S(k, "miktar_ham").Length > 0 ? S(k, "miktar_ham") : S(k, "miktar")));
            var brm = Esc(S(k, "olcu_birimi"));
            var tsl = Esc(TrDate(k, "teslim_tarihi"));
            var ust = Esc(S(k, "ust_kalem"));
            sb.AppendLine($"Call SetCol({i}, \"RV45A-MABNR\", \"{malz}\")");
            sb.AppendLine($"Call SetCol({i}, \"RV45A-KWMENG\", \"{mkt}\")");
            sb.AppendLine($"Call SetCol({i}, \"VBAP-VRKME\", \"{brm}\")");
            sb.AppendLine($"Call SetCol({i}, \"RV45A-ETDAT\", \"{tsl}\")");
            if (!string.IsNullOrEmpty(ust))
                sb.AppendLine($"Call SetCol({i}, \"VBAP-UEPOS\", \"{ust}\")");
        }
        sb.AppendLine("session.findById(\"wnd[0]\").sendVKey 0");
        sb.AppendLine("WScript.Sleep 600");
        sb.AppendLine("Call DismissPopups()");
        for (int i = 0; i < maxRows; i++)
        {
            var k = kalemler[i];
            var fyt = Esc(SapDec(S(k, "birim_fiyat_ham").Length > 0 ? S(k, "birim_fiyat_ham") : S(k, "birim_fiyat")));
            var pb = Esc(S(k, "para_birimi"));
            var pyp = Esc(S(k, "pyp_ogesi"));
            sb.AppendLine($"Call SetCol({i}, \"KOMV-KBETR\", \"{fyt}\")");
            sb.AppendLine($"Call SetCol({i}, \"KOMV-WAERS\", \"{pb}\")");
            sb.AppendLine($"Call SetCol({i}, \"VBAP-PS_PSP_PNR\", \"{pyp}\")");
        }
        sb.AppendLine("session.findById(\"wnd[0]\").sendVKey 0");
        sb.AppendLine("WScript.Sleep 400");
        sb.AppendLine("Call DismissPopups()");
        sb.AppendLine("MsgBox \"VA01 tum baslik, muhatap, tarih, fiyat ve kalem alanlari dolduruldu (KAYDEDILMEDI). Lutfen kontrol edip SAP ekranindan kendiniz kaydedin.\", 64, \"UGES SD Kontrol\"");
        return sb.ToString();
    }
}
