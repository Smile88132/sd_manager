using Dapper;
using Npgsql;

namespace SdKontrol.API.Data;

/// <summary>
/// UGESUD ile aynı veritabanında yalnızca "sd" şemasını kullanır.
/// Uygulama açılışında şema ve tablolar yoksa oluşturulur (idempotent).
/// </summary>
public static class SchemaInitializer
{
    private const string Ddl = @"
CREATE SCHEMA IF NOT EXISTS sd;

-- İçe aktarma geçmişi
CREATE TABLE IF NOT EXISTS sd.import_log (
    id              BIGSERIAL PRIMARY KEY,
    baslangic       TIMESTAMP NOT NULL DEFAULT NOW(),
    bitis           TIMESTAMP,
    talep_dosya     TEXT,
    kalem_dosya     TEXT,
    talep_sayisi    INT NOT NULL DEFAULT 0,
    kalem_sayisi    INT NOT NULL DEFAULT 0,
    durum           VARCHAR(30) NOT NULL DEFAULT 'Devam',
    mesaj           TEXT,
    uyarilar        JSONB
);

-- SD talep başlığı (talep / süreç başına 1 satır)
CREATE TABLE IF NOT EXISTS sd.talep (
    surec_no                    BIGINT PRIMARY KEY,
    belge_no                    VARCHAR(60),
    belge_tarihi                TIMESTAMP,
    sicil                       VARCHAR(20),
    ad_soyad                    VARCHAR(200),
    talep_eden_sicil            VARCHAR(20),
    talep_eden_ad_soyad         VARCHAR(200),
    talep_eden_departman        VARCHAR(300),
    talep_eden_unvan            VARCHAR(200),
    sd_belge_no                 VARCHAR(30),
    siparis_turu                VARCHAR(20),
    crm_teklif_no               VARCHAR(60),
    satis_kategorisi            VARCHAR(20),
    satis_organizasyonu         VARCHAR(20),
    dagitim_kanali              VARCHAR(20),
    satis_burosu                VARCHAR(20),
    satis_grubu                 VARCHAR(20),
    sas_no                      VARCHAR(60),
    sas_tarihi                  DATE,
    orijinal_sozlesme_tutari    NUMERIC(20,4),
    orijinal_sozlesme_tutari_ham VARCHAR(60),
    sozlesme_baslangic_tarihi   DATE,
    sozlesme_bitis_tarihi       DATE,
    sozlesme_teslim_tarihi      DATE,
    sozlesme_imza_tarihi        DATE,
    aselsan_sozlesme_no         VARCHAR(100),
    sozlesme_adi                VARCHAR(500),
    uretim_yeri                 VARCHAR(20),
    odeme_sekli                 VARCHAR(20),
    fatura_tipi                 VARCHAR(20),
    siparis_veren               VARCHAR(100),
    mali_teslim_alan            VARCHAR(100),
    fatura_alicisi              VARCHAR(100),
    odeyen                      VARCHAR(100),
    satis_sorumlusu             VARCHAR(100),
    mip_sorumlusu               VARCHAR(100),
    son_kullanici               VARCHAR(100),
    proje_hesap_no              VARCHAR(100),
    incoterms                   VARCHAR(60),
    eskalasyona_tabi            VARCHAR(20),
    akis_statu                  VARCHAR(60),
    akis_durumu                 VARCHAR(100),
    ham_veri                    JSONB,
    kaynak_dosya                TEXT,
    import_id                   BIGINT,
    guncellendi                 TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_sd_talep_belge_no ON sd.talep (belge_no);
CREATE INDEX IF NOT EXISTS ix_sd_talep_akis ON sd.talep (akis_durumu);

-- SD talep kalemleri
CREATE TABLE IF NOT EXISTS sd.talep_kalem (
    id                          BIGSERIAL PRIMARY KEY,
    surec_no                    BIGINT NOT NULL,
    belge_no                    VARCHAR(60),
    belge_tarihi                TIMESTAMP,
    sicil                       VARCHAR(20),
    ad_soyad                    VARCHAR(200),
    kalem_no                    INT,
    malzeme_no                  VARCHAR(100),
    miktar                      NUMERIC(20,4),
    miktar_ham                  VARCHAR(60),
    olcu_birimi                 VARCHAR(20),
    garanti_suresi_ham          VARCHAR(60),
    garanti_suresi_ay           INT,
    sevkiyat_noktasi            VARCHAR(20),
    pyp_ogesi                   VARCHAR(100),
    ic_siparis                  VARCHAR(60),
    edim_kodu                   VARCHAR(60),
    para_birimi                 VARCHAR(10),
    birim_fiyat                 NUMERIC(20,4),
    birim_fiyat_ham             VARCHAR(60),
    eskalasyona_tabi            VARCHAR(20),
    ceza_karsiligi              VARCHAR(20),
    manuel_eskalasyon_degeri    NUMERIC(20,4),
    teslim_tarihi               DATE,
    hazir_edim_tarihi           DATE,
    sozlesmesel_teslim_tarihi   DATE,
    eskalasyon_baslangic_tarihi DATE,
    eskalasyon_bitis_tarihi     DATE,
    kalem_tipi                  VARCHAR(20),
    ust_kalem                   VARCHAR(20),
    vergi                       VARCHAR(60),
    akis_statu                  VARCHAR(60),
    akis_durumu                 VARCHAR(100),
    ham_veri                    JSONB,
    kaynak_dosya                TEXT,
    import_id                   BIGINT,
    guncellendi                 TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_sd_talep_kalem_surec ON sd.talep_kalem (surec_no, kalem_no);
CREATE INDEX IF NOT EXISTS ix_sd_talep_kalem_malzeme ON sd.talep_kalem (malzeme_no);

-- SD giriş onay / istisna kararı ve VA01 takip durumu
CREATE TABLE IF NOT EXISTS sd.talep_onay (
    surec_no                BIGINT PRIMARY KEY,
    onay_durumu             VARCHAR(30) NOT NULL DEFAULT 'Bekliyor',
    onaylayan               VARCHAR(200),
    istisna_notu            TEXT,
    onaylanan_uyarilar      JSONB,
    va01_override           JSONB,
    va01_acildi_mi          BOOLEAN NOT NULL DEFAULT FALSE,
    son_va01_tarihi         TIMESTAMP,
    girilen_sd_no           VARCHAR(40),
    guncellendi             TIMESTAMP NOT NULL DEFAULT NOW()
);
ALTER TABLE sd.talep_onay ADD COLUMN IF NOT EXISTS va01_override JSONB;

-- AI denetim, sözleşme kıyaslama ve mail taslağı kayıtları
CREATE TABLE IF NOT EXISTS sd.ai_analiz_log (
    id                      BIGSERIAL PRIMARY KEY,
    surec_no                BIGINT NOT NULL,
    analiz_turu             VARCHAR(40) NOT NULL,
    baslik                  VARCHAR(300),
    sonuc_metni             TEXT NOT NULL,
    model_bilgisi           VARCHAR(120),
    olusturuldu             TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_sd_ai_analiz_surec ON sd.ai_analiz_log (surec_no, id DESC);
";

    public static async Task EnsureAsync(string connectionString, ILogger logger)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(Ddl);
        logger.LogInformation("[SD] 'sd' şeması ve tabloları (talep, talep_kalem, talep_onay, ai_analiz_log, import_log) hazır.");
    }
}
