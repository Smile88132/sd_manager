# UGES SD Kontrol & SAP VA01 Hazırlık Sistemi

UGESUD Kokpit'ten **bağımsız** çalışan, aynı PostgreSQL veritabanında yalnızca **`sd`** şemasını kullanan SAP SD giriş öncesi talep kontrol, AI denetim ve `VA01` taslak doldurma uygulaması.

## Tek Tıkla Başlatma (`start.bat`)
`SD\start.bat` dosyasına çift tıkladığınızda:
1. `SD\.env` dosyasını kontrol eder (yoksa `.env.example`'dan oluşturur),
2. `.NET 8 SDK`, `Node.js`, `PYTHON_EXE` + `pywin32`, `SAP GUI` (`saplogon.exe`) ve `PostgreSQL` erişimini test edip ekrana yansıtır,
3. Backend'i derler ve hem Backend (`5100`) hem Frontend (`3100`) sunucularını başlatıp tarayıcıda açar.

## Yapılandırma (`SD/.env`)
- `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD`, `DB_SCHEMA=sd`
- `PYTHON_EXE` — SAP GUI Scripting (`pywin32`) kurulu Python tam yolu
- `SAP_LOGON_PATH`, `SAP_CONNECTION=KKP CANLI`, `SAP_CLIENT=500`, `SAP_LANGUAGE=TR`, `SAP_DEFAULT_SPART=10`
- `AI_PROVIDER`, `AI_API_URL`, `AI_API_KEY`, `AI_MODEL` *(boş bırakılırsa Kokpit veritabanındaki `ai_config` otomatik kullanılır)*

## Özellikler
1. **Şartname Kural Motoru (`SdRuleEngine`):**
   - 🔴 **Kırmızı (Engel):** CRM Teklif No boş, kalemsiz talep, malzeme/miktar boş, Hazır Edim > Teslim Tarihi.
   - 🟠 **Turuncu (Uyarı):** ASELSAN Sözleşme No eksik/yer tutucu (`SS`), geçmiş yıl sözleşme imza tarihi (GSSYM onayı), eksik muhataplar.
   - 🟡 **Sarı (Uyarı):** Hazır Edim == Teslim Tarihi (aynı gün), **HIZMET / sanal üst kaleme malzeme alt kalem bağlanması**, `SOZLESME BEDELI` sanal stok, PYP/İç Sipariş boşluğu, sözleşme tutarı ↔ kalem toplamı farkı.
2. **İstisna Onayı (`sd.talep_onay`):** Kırmızı engeli olmayan talepler açıklama notuyla onaylanarak `VA01` kilidi açılır.
3. **Yapay Zeka Araçları (`SdAiService`):**
   - Akıllı Kalem & BOM / Hizmet Denetçisi
   - Sözleşme / PO (`.docx` / `.txt` / metin) ↔ SD Talep Çapraz Kontrolü
   - Talep Sahibi / ÜPM MIP / PKD / GSSYM için tek tıkla e-posta taslağı
4. **SAP `VA01` Aç & Doldur (`SdVa01Service` + `python/va01_fill.py`):**
   - Açık SAP oturumunu yakalar veya ekrandan anlık alınan SAP kullanıcı/şifresiyle (diske/DB'ye yazmadan) `KKP CANLI` açar, `/nVA01` alanlarını doldurur ve **`Ctrl+S` (Kaydet) yapmadan** ekranı kullanıcıya bırakır. Ayrıca uzak PC/VDI için şifresiz `.vbs` taslak scripti indirilebilir.
