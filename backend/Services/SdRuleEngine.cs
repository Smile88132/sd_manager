using System.Globalization;

namespace SdKontrol.API.Services;

public record KuralBulgu(
    string Kod,
    string Seviye, // "Kirmizi" | "Turuncu" | "Sari"
    string Baslik,
    string Detay,
    int? KalemNo = null
);

public record TalepKontrolSonucu(
    string GenelDurum, // "ENGEL" | "UYARI" | "ONAYLI" | "HAZIR"
    bool Va01Acilabilir,
    int KirmiziSayisi,
    int TuruncuSayisi,
    int SariSayisi,
    List<KuralBulgu> Bulgular
);

/// <summary>
/// UGES TYM SD Giriş Şartnamesi kurallarını değerlendirir.
/// Kırmızı = Engelleyici (VA01 açılamaz), Turuncu / Sarı = Uyarı (istisna onayı ile geçilebilir).
/// </summary>
public static class SdRuleEngine
{
    private static readonly HashSet<string> SozlesmeYerTutucular = new(StringComparer.OrdinalIgnoreCase)
    {
        "SS", "-", ".", "YOK", "N/A", "NA", "0", "BOS", "BOŞ"
    };

    private static readonly string[] HizmetAnahtarKelimeler =
    {
        "HIZMET", "HİZMET", "SERVIS", "SERVİS", "BAKIM", "ONARIM", "EGITIM", "EĞİTİM",
        "DANISMANLIK", "DANIŞMANLIK", "ISCILIK", "İŞÇİLİK", "DESTEK", "GARANTI", "GARANTİ",
        "SOZLESME BEDELI", "SÖZLEŞME BEDELİ", "AVANS", "FATURA"
    };

    private static readonly HashSet<string> HizmetKalemTipleri = new(StringComparer.OrdinalIgnoreCase)
    {
        "TAD", "TAS", "Y28Y", "Z217"
    };

    public static bool IsHizmetVeyaSanalMalzeme(string? malzemeNo, string? kalemTipi)
    {
        var m = (malzemeNo ?? "").Trim().ToUpper(new CultureInfo("tr-TR"));
        var kt = (kalemTipi ?? "").Trim();
        if (string.IsNullOrEmpty(m)) return false;
        if (HizmetAnahtarKelimeler.Any(k => m.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;
        // Saf alfabetik kod (rakam içermeyen 'HIZMET', 'KURULUM' vb.) malzeme stok kodu değildir
        if (!m.Any(char.IsDigit))
            return true;
        if (!string.IsNullOrEmpty(kt) && HizmetKalemTipleri.Contains(kt))
            return true;
        return false;
    }

    public static TalepKontrolSonucu Degerlendir(
        IDictionary<string, object?>? talep,
        IList<IDictionary<string, object?>> kalemler,
        string? onayDurumu = null)
    {
        var bulgular = new List<KuralBulgu>();

        string S(IDictionary<string, object?>? d, string k) =>
            d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "" : "";

        decimal? N(IDictionary<string, object?>? d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return null;
            if (v is decimal dec) return dec;
            if (v is double dbl) return (decimal)dbl;
            if (decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) return p;
            return null;
        }

        DateTime? D(IDictionary<string, object?>? d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return null;
            if (v is DateTime dt) return dt.Date;
            if (v is DateOnly dOnly) return dOnly.ToDateTime(TimeOnly.MinValue);
            if (DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var p)) return p.Date;
            return null;
        }

        int? I(IDictionary<string, object?>? d, string k)
        {
            var n = N(d, k);
            return n.HasValue ? (int)n.Value : null;
        }

        // ── 1. Başlık Kuralları ──
        if (talep == null)
        {
            bulgular.Add(new("BASLIK_YOK", "Kirmizi", "Talep başlığı bulunamadı", "Kalem raporunda süreç var ancak talep başlık raporunda kaydı yok."));
        }
        else
        {
            var crm = S(talep, "crm_teklif_no");
            if (string.IsNullOrWhiteSpace(crm))
            {
                bulgular.Add(new("CRM_BOS", "Kirmizi", "CRM Teklif Numarası boş",
                    "UGES TYM SD Giriş Şartnamesi Md. 3 gereği ELD ve yurt dışı sözleşmeler dahil CRM teklif numarası zorunludur."));
            }

            var aselsanSoz = S(talep, "aselsan_sozlesme_no");
            if (string.IsNullOrWhiteSpace(aselsanSoz) || SozlesmeYerTutucular.Contains(aselsanSoz))
            {
                bulgular.Add(new("ASELSAN_SOZ_NO", "Turuncu", "ASELSAN Sözleşme No eksik / yer tutucu",
                    $"ASELSAN Sözleşme No '{(string.IsNullOrEmpty(aselsanSoz) ? "boş" : aselsanSoz)}' olarak girilmiş. Sözleşme veya PO numarası doğrulanmalıdır (Md. 6)."));
            }

            var imza = D(talep, "sozlesme_imza_tarihi");
            var belgeTarihi = D(talep, "belge_tarihi") ?? DateTime.Today;
            if (imza.HasValue && imza.Value.Year < belgeTarihi.Year)
            {
                bulgular.Add(new("IMZA_GECMIS_YIL", "Turuncu", "Sözleşme imza tarihi geçmiş yıla ait",
                    $"İmza tarihi ({imza:dd.MM.yyyy}) talep yılından ({belgeTarihi.Year}) önce. Gelir ve Satış Sözleşmeleri Yön. Mdl. onayı ile ilerlenmelidir (Md. 4)."));
            }

            // Muhataplar kontrolü
            var eksikMuhataplar = new List<string>();
            if (string.IsNullOrWhiteSpace(S(talep, "siparis_veren"))) eksikMuhataplar.Add("Sipariş Veren");
            if (string.IsNullOrWhiteSpace(S(talep, "mali_teslim_alan"))) eksikMuhataplar.Add("Malı Teslim Alan");
            if (string.IsNullOrWhiteSpace(S(talep, "fatura_alicisi"))) eksikMuhataplar.Add("Fatura Alıcısı");
            if (string.IsNullOrWhiteSpace(S(talep, "odeyen"))) eksikMuhataplar.Add("Ödeyen");
            if (eksikMuhataplar.Count > 0)
            {
                bulgular.Add(new("MUHATAP_EKSIK", "Turuncu", "Muhatap bilgileri raporda boş",
                    $"Eksik muhatap alanları: {string.Join(", ", eksikMuhataplar)}. SAP VA01 ekranında müşteri muhatap kodlarının tanımlı olduğu kontrol edilmelidir."));
            }

            var sipTuru = S(talep, "siparis_turu");
            if (sipTuru.Equals("ZIHR", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(S(talep, "incoterms")))
            {
                bulgular.Add(new("INCOTERMS_BOS", "Sari", "İhracat siparişinde (ZIHR) Incoterms boş",
                    "Yurt dışı siparişlerde Incoterms teslim şekli sözleşme ile uyumlu girilmelidir (Md. 6)."));
            }
        }

        // ── 2. Kalem Varlığı Kontrolü ──
        if (kalemler == null || kalemler.Count == 0)
        {
            bulgular.Add(new("KALEM_YOK", "Kirmizi", "Talebe ait kalem bulunamadı",
                "Kalem raporunda bu süreç numarasına bağlı hiçbir kalem satırı yok; SAP VA01 girişi yapılamaz."));
        }
        else
        {
            // Kalem No -> Kalem sözlüğü (Üst kalem kontrolü için)
            var kalemHaritasi = new Dictionary<int, IDictionary<string, object?>>();
            foreach (var k in kalemler)
            {
                var kno = I(k, "kalem_no");
                if (kno.HasValue && !kalemHaritasi.ContainsKey(kno.Value))
                    kalemHaritasi[kno.Value] = k;
            }

            decimal kalemToplam = 0m;

            foreach (var k in kalemler)
            {
                var kno = I(k, "kalem_no");
                var malz = S(k, "malzeme_no");
                var miktar = N(k, "miktar");
                var fiyat = N(k, "birim_fiyat") ?? 0m;
                var hazir = D(k, "hazir_edim_tarihi");
                var teslim = D(k, "teslim_tarihi");
                var pyp = S(k, "pyp_ogesi");
                var icSip = S(k, "ic_siparis");
                var ustKalemStr = S(k, "ust_kalem");
                var kalemTipi = S(k, "kalem_tipi");

                if (miktar.HasValue) kalemToplam += miktar.Value * fiyat;

                // Malzeme & miktar zorunluluğu
                if (string.IsNullOrWhiteSpace(malz) || !miktar.HasValue || miktar.Value <= 0)
                {
                    bulgular.Add(new("MALZEME_MIKTAR_BOS", "Kirmizi", $"Kalem {kno}: Malzeme veya miktar geçersiz",
                        $"Malzeme No ('{malz}') dolu ve Miktar ({miktar?.ToString() ?? "boş"}) sıfırdan büyük olmalıdır.", kno));
                }

                // Sanal stok "SOZLESME BEDELI" uyarısı (Md. 7)
                var malzUp = malz.ToUpper(new CultureInfo("tr-TR"));
                if (malzUp.Contains("SOZLESME BEDELI") || malzUp.Contains("SÖZLEŞME BEDELİ"))
                {
                    bulgular.Add(new("SANAL_STOK", "Sari", $"Kalem {kno}: Geçici 'SOZLESME BEDELI' sanal stoğu",
                        "Bu sanal malzeme MRP/üretim planını tetiklemez ve teslimat/fatura süreçlerinde kullanılamaz; en kısa sürede gerçek stok no ile değiştirilmelidir (Md. 7).", kno));
                }

                // Hazır edim vs Teslim tarihi kontrolleri (Md. 2 & Md. 9)
                if (!hazir.HasValue || !teslim.HasValue)
                {
                    bulgular.Add(new("TARIH_BOS", "Kirmizi", $"Kalem {kno}: Hazır edim veya teslim tarihi boş",
                        "Her kalemde hem Hazır Edim Tarihi hem de Teslim Tarihi bulunmalıdır.", kno));
                }
                else if (hazir.Value > teslim.Value)
                {
                    bulgular.Add(new("HAZIR_SONRA_TESLIM", "Kirmizi", $"Kalem {kno}: Hazır edim tarihi teslim tarihinden sonra!",
                        $"Hazır Edim ({hazir:dd.MM.yyyy}) > Teslim Tarihi ({teslim:dd.MM.yyyy}). Üretim hazır edimi teslimattan sonra olamaz.", kno));
                }
                else if (hazir.Value == teslim.Value)
                {
                    bulgular.Add(new("HAZIR_ESIT_TESLIM", "Sari", $"Kalem {kno}: Hazır edim ve teslim tarihi aynı gün ({teslim:dd.MM.yyyy})",
                        "Şartname Md. 2 gereği hazır edim ile teslim tarihi kabul süresini karşılayacak şekilde ayrılmalıdır (istisna durumlarında onay ile geçilebilir).", kno));
                }

                // PYP / İç Sipariş kontrolü
                if (string.IsNullOrWhiteSpace(pyp) && string.IsNullOrWhiteSpace(icSip))
                {
                    bulgular.Add(new("PYP_IC_SIPARIS_BOS", "Sari", $"Kalem {kno}: PYP öğesi ve İç Sipariş boş",
                        "Sözleşmeli/proje siparişlerinde PAYK tarafından verilen PYP öğesi veya İç Sipariş bağlantısı bulunmalıdır.", kno));
                }

                // HIZMET / malzeme olmayan kaleme alt kalem bağlanması uyarısı!
                if (!string.IsNullOrWhiteSpace(ustKalemStr) && int.TryParse(ustKalemStr, out var ustNo) && ustNo > 0)
                {
                    if (kalemHaritasi.TryGetValue(ustNo, out var ustKalem))
                    {
                        var ustMalz = S(ustKalem, "malzeme_no");
                        var ustTip = S(ustKalem, "kalem_tipi");
                        bool ustHizmet = IsHizmetVeyaSanalMalzeme(ustMalz, ustTip);
                        bool altFiziksel = !IsHizmetVeyaSanalMalzeme(malz, null);
                        if (ustHizmet)
                        {
                            bulgular.Add(new("HIZMET_ALT_KALEM", "Sari",
                                $"Kalem {kno}: HIZMET / sanal üst kaleme ({ustNo} - {ustMalz}) alt kalem bağlanmış",
                                $"Üst kalem ({ustNo}: '{ustMalz}', Tip: '{ustTip}') hizmet/sanal nitelikte iken '{malz}' alt kalem olarak bağlanmış. BOM ve MRP patlaması açısından tepe stok malzeme olmalıdır (Md. 8).",
                                kno));
                        }
                        else if (altFiziksel && fiyat == 0m && string.IsNullOrWhiteSpace(kalemTipi))
                        {
                            bulgular.Add(new("BEDELSIZ_ALT_KALEM_TIPI", "Sari",
                                $"Kalem {kno}: Üst kaleme bağlı bedelsiz kalemde Kalem Tipi boş",
                                "Alt kalem bağlantılarında uygun bedelsiz/alt kalem tipi (örn. Y21F / TANN) kontrol edilmelidir.", kno));
                        }
                    }
                    else
                    {
                        bulgular.Add(new("UST_KALEM_YOK", "Sari", $"Kalem {kno}: Tanımsız üst kalem referansı ({ustNo})",
                            $"Üst Kalem olarak '{ustNo}' belirtilmiş ancak bu süreçte {ustNo} numaralı kalem yok.", kno));
                    }
                }
            }

            // Sözleşme tutarı vs Kalem toplamı kontrolü
            if (talep != null)
            {
                var sozTutar = N(talep, "orijinal_sozlesme_tutari");
                if (sozTutar.HasValue && sozTutar.Value > 0 && kalemToplam > 0)
                {
                    var fark = Math.Abs(sozTutar.Value - kalemToplam);
                    var oran = fark / sozTutar.Value;
                    if (oran > 0.01m)
                    {
                        bulgular.Add(new("TUTAR_UYUMSUZ", "Sari", "Kalem toplamı ile Orijinal Sözleşme Tutarı farklı",
                            $"Kalemler toplamı ({kalemToplam:N2}) ile Orijinal Sözleşme Tutarı ({sozTutar.Value:N2}) arasında {fark:N2} fark var (ek sipariş / kısmi talep değilse kontrol edilmeli)."));
                    }
                }
            }
        }

        int kirmizi = bulgular.Count(b => b.Seviye == "Kirmizi");
        int turuncu = bulgular.Count(b => b.Seviye == "Turuncu");
        int sari = bulgular.Count(b => b.Seviye == "Sari");

        bool onaylandi = string.Equals(onayDurumu, "Onaylandı", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(onayDurumu, "VA01 Açıldı", StringComparison.OrdinalIgnoreCase);

        string genelDurum;
        bool va01Acilabilir;

        if (kirmizi > 0)
        {
            genelDurum = "ENGEL";
            va01Acilabilir = false;
        }
        else if (onaylandi)
        {
            genelDurum = "ONAYLI";
            va01Acilabilir = true;
        }
        else if (turuncu > 0 || sari > 0)
        {
            genelDurum = "UYARI";
            va01Acilabilir = false; // Kullanıcı istisna onayı verdiğinde ONAYLI olur ve VA01 açılır
        }
        else
        {
            genelDurum = "HAZIR";
            va01Acilabilir = true;
        }

        return new TalepKontrolSonucu(genelDurum, va01Acilabilir, kirmizi, turuncu, sari, bulgular);
    }
}
