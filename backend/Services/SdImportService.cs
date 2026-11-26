using System.Text.Json;
using Dapper;
using Npgsql;
using OfficeOpenXml;

namespace SdKontrol.API.Services;

public enum SdRaporTuru { Bilinmiyor, Talep, Kalem }

public record ImportSonuc(long ImportId, int TalepSayisi, int KalemSayisi, string? TalepDosya, string? KalemDosya, List<string> Uyarilar);

/// <summary>
/// SD Excel raporlarını sd şemasına aktarır.
/// Dosya adına güvenmez: başlıklara bakarak hangi dosyanın talep başlığı, hangisinin kalem olduğunu tespit eder.
/// Rapor tam anlık görüntü (snapshot) kabul edilir; her içe aktarmada tablolar yenilenir.
/// </summary>
public class SdImportService
{
    private readonly string _cs;
    private readonly ILogger<SdImportService> _logger;

    public SdImportService(EnvConfig env, ILogger<SdImportService> logger)
    {
        _cs = env.ConnectionString;
        _logger = logger;
    }

    // ---- Excel okuma ----------------------------------------------------------------

    private sealed class SheetData
    {
        public string Dosya = "";
        public List<string> Basliklar = new();
        public List<Dictionary<string, object?>> Satirlar = new(); // HeaderKey → değer
        public List<Dictionary<string, object?>> HamSatirlar = new(); // Orijinal başlık → değer
    }

    private static SheetData Oku(Stream stream, string dosyaAdi)
    {
        using var pkg = new ExcelPackage(stream);
        var ws = pkg.Workbook.Worksheets.FirstOrDefault() ?? throw new InvalidOperationException($"{dosyaAdi}: sayfa bulunamadı.");
        var data = new SheetData { Dosya = dosyaAdi };
        if (ws.Dimension == null) return data;

        int maxCol = ws.Dimension.End.Column, maxRow = ws.Dimension.End.Row;
        for (int c = 1; c <= maxCol; c++) data.Basliklar.Add(ws.Cells[1, c].Text?.Trim() ?? "");

        for (int r = 2; r <= maxRow; r++)
        {
            var row = new Dictionary<string, object?>();
            var ham = new Dictionary<string, object?>();
            bool bos = true;
            for (int c = 1; c <= maxCol; c++)
            {
                var baslik = data.Basliklar[c - 1];
                if (string.IsNullOrEmpty(baslik)) continue;
                var val = ws.Cells[r, c].Value;
                if (val is string s && string.IsNullOrWhiteSpace(s)) val = null;
                if (val != null) bos = false;
                row[CellParser.HeaderKey(baslik)] = val;
                ham[baslik] = val is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : val;
            }
            if (bos) continue;
            data.Satirlar.Add(row);
            data.HamSatirlar.Add(ham);
        }
        return data;
    }

    public static SdRaporTuru TurTespit(IEnumerable<string> basliklar)
    {
        var keys = basliklar.Select(CellParser.HeaderKey).ToHashSet();
        bool kalem = keys.Contains("malzeme no") && keys.Contains("kalem no");
        bool talep = keys.Contains("siparis kodu turu") || keys.Contains("crm teklif numarasi") || keys.Contains("satis organizasyonu");
        if (kalem && !talep) return SdRaporTuru.Kalem;
        if (talep && !kalem) return SdRaporTuru.Talep;
        return SdRaporTuru.Bilinmiyor;
    }

    private static object? G(Dictionary<string, object?> r, params string[] keys)
    {
        foreach (var k in keys)
            if (r.TryGetValue(k, out var v) && v != null) return v;
        return null;
    }

    // ---- Dışa açık metotlar -----------------------------------------------------------

    /// <summary>Klasördeki .xlsx dosyalarını tarar, talep ve kalem raporlarını başlıklarına göre bulup aktarır.</summary>
    public async Task<ImportSonuc> KlasordenAktarAsync(string klasor)
    {
        if (!Directory.Exists(klasor)) throw new DirectoryNotFoundException($"Klasör bulunamadı: {klasor}");
        var dosyalar = Directory.GetFiles(klasor, "*.xlsx").Where(f => !Path.GetFileName(f).StartsWith("~$")).ToList();
        var akislar = new List<(Stream stream, string ad)>();
        try
        {
            foreach (var f in dosyalar)
                akislar.Add((new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), f));
            return await AktarAsync(akislar);
        }
        finally
        {
            foreach (var a in akislar) a.stream.Dispose();
        }
    }

    public async Task<ImportSonuc> AktarAsync(IEnumerable<(Stream stream, string ad)> dosyalar)
    {
        ExcelPackage.License.SetNonCommercialOrganization("UGES SD Kontrol");
        var uyarilar = new List<string>();
        SheetData? talep = null, kalem = null;

        foreach (var (stream, ad) in dosyalar)
        {
            SheetData sheet;
            try { sheet = Oku(stream, ad); }
            catch (Exception ex) { uyarilar.Add($"{Path.GetFileName(ad)} okunamadı: {ex.Message}"); continue; }

            switch (TurTespit(sheet.Basliklar))
            {
                case SdRaporTuru.Talep:
                    if (talep != null) uyarilar.Add($"Birden fazla talep raporu bulundu; '{Path.GetFileName(ad)}' kullanıldı.");
                    talep = sheet; break;
                case SdRaporTuru.Kalem:
                    if (kalem != null) uyarilar.Add($"Birden fazla kalem raporu bulundu; '{Path.GetFileName(ad)}' kullanıldı.");
                    kalem = sheet; break;
                default:
                    uyarilar.Add($"{Path.GetFileName(ad)}: SD talep/kalem raporu olarak tanınmadı, atlandı.");
                    break;
            }
        }

        if (talep == null && kalem == null)
            throw new InvalidOperationException("Talep veya kalem raporu bulunamadı. " + string.Join(" ", uyarilar));

        if (talep != null && Path.GetFileName(talep.Dosya).Contains("Kalem", StringComparison.OrdinalIgnoreCase))
            uyarilar.Add($"Bilgi: '{Path.GetFileName(talep.Dosya)}' içeriğine göre TALEP BAŞLIĞI raporu olarak alındı.");
        if (kalem != null && Path.GetFileName(kalem.Dosya).Contains("Detay", StringComparison.OrdinalIgnoreCase))
            uyarilar.Add($"Bilgi: '{Path.GetFileName(kalem.Dosya)}' içeriğine göre KALEM raporu olarak alındı.");

        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        var importId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO sd.import_log (talep_dosya, kalem_dosya) VALUES (@t, @k) RETURNING id",
            new { t = talep?.Dosya, k = kalem?.Dosya });

        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            int talepSay = 0, kalemSay = 0;

            if (talep != null)
            {
                await conn.ExecuteAsync("DELETE FROM sd.talep", transaction: tx);
                talepSay = await TalepleriYazAsync(conn, tx, talep, importId, uyarilar);
            }
            if (kalem != null)
            {
                await conn.ExecuteAsync("DELETE FROM sd.talep_kalem", transaction: tx);
                kalemSay = await KalemleriYazAsync(conn, tx, kalem, importId);
            }

            if (talep != null && kalem != null)
            {
                var kalemsiz = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM sd.talep t WHERE NOT EXISTS (SELECT 1 FROM sd.talep_kalem k WHERE k.surec_no = t.surec_no)", transaction: tx);
                var basliksiz = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(DISTINCT k.surec_no) FROM sd.talep_kalem k WHERE NOT EXISTS (SELECT 1 FROM sd.talep t WHERE t.surec_no = k.surec_no)", transaction: tx);
                if (kalemsiz > 0) uyarilar.Add($"{kalemsiz} talebin kalem raporunda hiç kalemi yok.");
                if (basliksiz > 0) uyarilar.Add($"{basliksiz} sürecin kalemleri var ama talep başlığı yok.");
            }

            await tx.CommitAsync();
            await conn.ExecuteAsync(
                "UPDATE sd.import_log SET bitis = NOW(), talep_sayisi = @ts, kalem_sayisi = @ks, durum = 'Başarılı', uyarilar = CAST(@u AS jsonb) WHERE id = @id",
                new { ts = talepSay, ks = kalemSay, u = JsonSerializer.Serialize(uyarilar), id = importId });

            _logger.LogInformation("[SD] İçe aktarma #{Id}: {T} talep, {K} kalem", importId, talepSay, kalemSay);
            return new ImportSonuc(importId, talepSay, kalemSay, talep?.Dosya, kalem?.Dosya, uyarilar);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            await conn.ExecuteAsync("UPDATE sd.import_log SET bitis = NOW(), durum = 'Hata', mesaj = @m WHERE id = @id", new { m = ex.Message, id = importId });
            throw;
        }
    }

    // ---- Yazma ------------------------------------------------------------------------

    private static async Task<int> TalepleriYazAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SheetData s, long importId, List<string> uyarilar)
    {
        const string sql = @"
INSERT INTO sd.talep (surec_no, belge_no, belge_tarihi, sicil, ad_soyad, talep_eden_sicil, talep_eden_ad_soyad, talep_eden_departman, talep_eden_unvan,
    sd_belge_no, siparis_turu, crm_teklif_no, satis_kategorisi, satis_organizasyonu, dagitim_kanali, satis_burosu, satis_grubu, sas_no, sas_tarihi,
    orijinal_sozlesme_tutari, orijinal_sozlesme_tutari_ham, sozlesme_baslangic_tarihi, sozlesme_bitis_tarihi, sozlesme_teslim_tarihi, sozlesme_imza_tarihi,
    aselsan_sozlesme_no, sozlesme_adi, uretim_yeri, odeme_sekli, fatura_tipi, siparis_veren, mali_teslim_alan, fatura_alicisi, odeyen, satis_sorumlusu,
    mip_sorumlusu, son_kullanici, proje_hesap_no, incoterms, eskalasyona_tabi, akis_statu, akis_durumu, ham_veri, kaynak_dosya, import_id)
VALUES (@surec_no, @belge_no, @belge_tarihi, @sicil, @ad_soyad, @talep_eden_sicil, @talep_eden_ad_soyad, @talep_eden_departman, @talep_eden_unvan,
    @sd_belge_no, @siparis_turu, @crm_teklif_no, @satis_kategorisi, @satis_organizasyonu, @dagitim_kanali, @satis_burosu, @satis_grubu, @sas_no, @sas_tarihi,
    @orijinal_sozlesme_tutari, @orijinal_sozlesme_tutari_ham, @sozlesme_baslangic_tarihi, @sozlesme_bitis_tarihi, @sozlesme_teslim_tarihi, @sozlesme_imza_tarihi,
    @aselsan_sozlesme_no, @sozlesme_adi, @uretim_yeri, @odeme_sekli, @fatura_tipi, @siparis_veren, @mali_teslim_alan, @fatura_alicisi, @odeyen, @satis_sorumlusu,
    @mip_sorumlusu, @son_kullanici, @proje_hesap_no, @incoterms, @eskalasyona_tabi, @akis_statu, @akis_durumu, CAST(@ham_veri AS jsonb), @kaynak_dosya, @import_id)
ON CONFLICT (surec_no) DO UPDATE SET
    belge_no = EXCLUDED.belge_no, belge_tarihi = EXCLUDED.belge_tarihi, akis_statu = EXCLUDED.akis_statu, akis_durumu = EXCLUDED.akis_durumu,
    ham_veri = EXCLUDED.ham_veri, import_id = EXCLUDED.import_id, guncellendi = NOW();";

        int n = 0, tekrar = 0;
        var gorulen = new HashSet<long>();
        for (int i = 0; i < s.Satirlar.Count; i++)
        {
            var r = s.Satirlar[i];
            var surec = CellParser.Number(G(r, "surec no"));
            if (surec == null) continue;
            if (!gorulen.Add((long)surec.Value)) tekrar++;
            var tutarHam = CellParser.Text(G(r, "orjinal sozlesme tutari", "orijinal sozlesme tutari"));

            await conn.ExecuteAsync(sql, new
            {
                surec_no = (long)surec.Value,
                belge_no = CellParser.Text(G(r, "belge numarasi")),
                belge_tarihi = CellParser.Date(G(r, "belge tarihi")),
                sicil = CellParser.Text(G(r, "sicil")),
                ad_soyad = CellParser.Text(G(r, "ad soyad")),
                talep_eden_sicil = CellParser.Text(G(r, "talebi yapan personel sicil")),
                talep_eden_ad_soyad = CellParser.Text(G(r, "talebi yapan personel ad soyad")),
                talep_eden_departman = CellParser.Text(G(r, "talebi yapan personel departman")),
                talep_eden_unvan = CellParser.Text(G(r, "talebi yapan personel unvan")),
                sd_belge_no = CellParser.Text(G(r, "sd belge numarasi")),
                siparis_turu = CellParser.Text(G(r, "siparis kodu turu")),
                crm_teklif_no = CellParser.Text(G(r, "crm teklif numarasi")),
                satis_kategorisi = CellParser.Text(G(r, "satis kategorisi")),
                satis_organizasyonu = CellParser.Text(G(r, "satis organizasyonu")),
                dagitim_kanali = CellParser.Text(G(r, "dagitim kanali")),
                satis_burosu = CellParser.Text(G(r, "satis burosu")),
                satis_grubu = CellParser.Text(G(r, "satis grubu")),
                sas_no = CellParser.Text(G(r, "sas no")),
                sas_tarihi = CellParser.Date(G(r, "sas tarihi")),
                orijinal_sozlesme_tutari = CellParser.Number(tutarHam),
                orijinal_sozlesme_tutari_ham = tutarHam,
                sozlesme_baslangic_tarihi = CellParser.Date(G(r, "sozlesme baslangic tarihi")),
                sozlesme_bitis_tarihi = CellParser.Date(G(r, "sozlesme bitis tarihi")),
                sozlesme_teslim_tarihi = CellParser.Date(G(r, "sozlesme teslim tarihi")),
                sozlesme_imza_tarihi = CellParser.Date(G(r, "sozlesme imza tarihi")),
                aselsan_sozlesme_no = CellParser.Text(G(r, "aselsan sozlesme no")),
                sozlesme_adi = CellParser.Text(G(r, "sozlesme adi")),
                uretim_yeri = CellParser.Text(G(r, "uretim yeri")),
                odeme_sekli = CellParser.Text(G(r, "odeme sekli")),
                fatura_tipi = CellParser.Text(G(r, "fatura tipi")),
                siparis_veren = CellParser.Text(G(r, "siparis veren")),
                mali_teslim_alan = CellParser.Text(G(r, "mali teslim alan")),
                fatura_alicisi = CellParser.Text(G(r, "fatura alicisi")),
                odeyen = CellParser.Text(G(r, "odeyen")),
                satis_sorumlusu = CellParser.Text(G(r, "satis sorumlusu")),
                mip_sorumlusu = CellParser.Text(G(r, "mip sorumlusu")),
                son_kullanici = CellParser.Text(G(r, "son kullanici")),
                proje_hesap_no = CellParser.Text(G(r, "proje hesap no")),
                incoterms = CellParser.Text(G(r, "incoterms")),
                eskalasyona_tabi = CellParser.Text(G(r, "eskalasyona tabi mi")),
                akis_statu = CellParser.Text(G(r, "akis statu")),
                akis_durumu = CellParser.Text(G(r, "akis durumu")),
                ham_veri = JsonSerializer.Serialize(s.HamSatirlar[i]),
                kaynak_dosya = s.Dosya,
                import_id = importId
            }, tx);
            n++;
        }
        if (tekrar > 0) uyarilar.Add($"Talep raporunda {tekrar} tekrarlı Süreç No vardı; son satır esas alındı.");
        return n - tekrar;
    }

    private static async Task<int> KalemleriYazAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SheetData s, long importId)
    {
        const string sql = @"
INSERT INTO sd.talep_kalem (surec_no, belge_no, belge_tarihi, sicil, ad_soyad, kalem_no, malzeme_no, miktar, miktar_ham, olcu_birimi,
    garanti_suresi_ham, garanti_suresi_ay, sevkiyat_noktasi, pyp_ogesi, ic_siparis, edim_kodu, para_birimi, birim_fiyat, birim_fiyat_ham,
    eskalasyona_tabi, ceza_karsiligi, manuel_eskalasyon_degeri, teslim_tarihi, hazir_edim_tarihi, sozlesmesel_teslim_tarihi,
    eskalasyon_baslangic_tarihi, eskalasyon_bitis_tarihi, kalem_tipi, ust_kalem, vergi, akis_statu, akis_durumu, ham_veri, kaynak_dosya, import_id)
VALUES (@surec_no, @belge_no, @belge_tarihi, @sicil, @ad_soyad, @kalem_no, @malzeme_no, @miktar, @miktar_ham, @olcu_birimi,
    @garanti_suresi_ham, @garanti_suresi_ay, @sevkiyat_noktasi, @pyp_ogesi, @ic_siparis, @edim_kodu, @para_birimi, @birim_fiyat, @birim_fiyat_ham,
    @eskalasyona_tabi, @ceza_karsiligi, @manuel_eskalasyon_degeri, @teslim_tarihi, @hazir_edim_tarihi, @sozlesmesel_teslim_tarihi,
    @eskalasyon_baslangic_tarihi, @eskalasyon_bitis_tarihi, @kalem_tipi, @ust_kalem, @vergi, @akis_statu, @akis_durumu, CAST(@ham_veri AS jsonb), @kaynak_dosya, @import_id);";

        int n = 0;
        for (int i = 0; i < s.Satirlar.Count; i++)
        {
            var r = s.Satirlar[i];
            var surec = CellParser.Number(G(r, "surec no"));
            if (surec == null) continue;
            var miktarHam = CellParser.Text(G(r, "miktar"));
            var fiyatHam = CellParser.Text(G(r, "birim fiyat"));
            var garantiHam = CellParser.Text(G(r, "garanti suresi"));

            await conn.ExecuteAsync(sql, new
            {
                surec_no = (long)surec.Value,
                belge_no = CellParser.Text(G(r, "belge numarasi")),
                belge_tarihi = CellParser.Date(G(r, "belge tarihi")),
                sicil = CellParser.Text(G(r, "sicil")),
                ad_soyad = CellParser.Text(G(r, "ad soyad")),
                kalem_no = CellParser.Int(G(r, "kalem no")),
                malzeme_no = CellParser.Text(G(r, "malzeme no")),
                miktar = CellParser.Number(miktarHam),
                miktar_ham = miktarHam,
                olcu_birimi = CellParser.Text(G(r, "olcu birimi")),
                garanti_suresi_ham = garantiHam,
                garanti_suresi_ay = CellParser.GarantiAy(garantiHam),
                sevkiyat_noktasi = CellParser.Text(G(r, "sevkiyat noktasi")),
                pyp_ogesi = CellParser.Text(G(r, "pyp ogesi")),
                ic_siparis = CellParser.Text(G(r, "ic siparis")),
                edim_kodu = CellParser.Text(G(r, "edim kodu")),
                para_birimi = CellParser.Text(G(r, "para birimi")),
                birim_fiyat = CellParser.Number(fiyatHam),
                birim_fiyat_ham = fiyatHam,
                eskalasyona_tabi = CellParser.Text(G(r, "eskalasyona tabi mi")),
                ceza_karsiligi = CellParser.Text(G(r, "ceza karsiligi mi")),
                manuel_eskalasyon_degeri = CellParser.Number(G(r, "manuel eskalasyon degeri")),
                teslim_tarihi = CellParser.Date(G(r, "teslim tarihi")),
                hazir_edim_tarihi = CellParser.Date(G(r, "hazir edim tarihi")),
                sozlesmesel_teslim_tarihi = CellParser.Date(G(r, "sozlesmesel teslim tarihi")),
                eskalasyon_baslangic_tarihi = CellParser.Date(G(r, "eskalasyon baslangic tarihi")),
                eskalasyon_bitis_tarihi = CellParser.Date(G(r, "eskalasyon bitis tarihi")),
                kalem_tipi = CellParser.Text(G(r, "kalem tipi")),
                ust_kalem = CellParser.Text(G(r, "ust kalem")),
                vergi = CellParser.Text(G(r, "vergi")),
                akis_statu = CellParser.Text(G(r, "akis statu")),
                akis_durumu = CellParser.Text(G(r, "akis durumu")),
                ham_veri = JsonSerializer.Serialize(s.HamSatirlar[i]),
                kaynak_dosya = s.Dosya,
                import_id = importId
            }, tx);
            n++;
        }
        return n;
    }
}
