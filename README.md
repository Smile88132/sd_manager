# UGES SD Kontrol & SAP VA01 Hazırlık Sistemi (+ ASELSAN LLM Suite MCP Sunucusu)

UGESUD Kokpit'ten **bağımsız** çalışan, aynı PostgreSQL veritabanında yalnızca **`sd`** şemasını kullanan SAP SD giriş öncesi talep kontrol, **ASELSAN LLM Suite Flow (MCP DataFrame)** yorumlama ve `VA01` taslak doldurma uygulaması.

## Tek Tıkla Başlatma (`start.bat`)
`SD\start.bat` dosyasına çift tıkladığınızda (hedef bilgisayarda `.NET SDK` veya `Node.js` gerektirmez):
1. `SD\.env` dosyasını kontrol eder, PostgreSQL / Python / SAP GUI erişimini doğrular,
2. Ana Web Uygulamasını **`BACKEND_PORT` (varsayılan `5100`)** üzerinde başlatır,
3. Aynı süreç içinde **bağımsız bir portta (`MCP_PORT`, varsayılan `5101`)** **UGES SD MCP Sunucusunu** ayağa kaldırır:
   - **SSE Uç Noktası:** `http://<BILGISAYAR_IP>:5101/sse`
   - **Streamable HTTP Uç Noktası:** `http://<BILGISAYAR_IP>:5101/mcp`

## ASELSAN LLM Suite Flow ↔ MCP Entegrasyonu
1. **LLM Suite Flow (`MCP Tools` bileşeni):**
   - MCP Server URL olarak üst menüdeki **⚙ LLM Suite & MCP (`:5101`)** penceresinde gösterilen `http://<IP>:5101/sse` (veya `/mcp`) adresini girin.
   - `MCP Tools` bileşenini `Toolset` modunda **`LLM Agent` → `Tools`** girişine bağlayın (veya tekil araç olarak `sd_kalemler_dataframe` seçip `Type Convert -> DataFrame` üzerinden bağlayın).
   - **Sunulan MCP Araçları:**
     - `sd_talep_dataframe(surec_no, format)` — Başlık + Kalem DataFrame (Markdown tablo + JSON records / CSV) + Kural bulguları
     - `sd_kalemler_dataframe(surec_no)` — Başlık + Kalem + Eksik alanlar birleşik düz JSON DataFrame (`records`)
     - `sd_eksik_alan_analizi(surec_no)` — Boş/eksik başlık ve kalem alanları, tarih/tutar/HIZMET tutarsızlıkları ve telafi önerileri
     - `sd_kural_kontrolu(surec_no)` — UGES TYM SD Giriş Şartnamesi kural motoru çıktısı
     - `sd_talep_listesi(kontrol_durumu, ara, limit)` — Tüm SD taleplerinin özet DataFrame'i
     - `sd_sql_sorgu(sql, limit)` — `sd` şemasında salt okunur (`READ ONLY`) PostgreSQL `SELECT` sorgusu
     - `sd_yorum_kaydet(surec_no, yorum, baslik)` — Üretilen yorumu doğrudan `sd.ai_analiz_log` tablosuna kaydeder
2. **SD Ekranından Yorum Alma:**
   - Üst çubuktaki **⚙ LLM Suite & MCP** ekranından `Base URL` (`https://llmsuiteflow.aiplatform.aselsan.com.tr`), `Flow ID` ve `API Key (x-api-key)` bilgilerini kaydedin.
   - Herhangi bir SD talebinin **3. LLM Suite (MCP) & AI Yorum** sekmesinde **🚀 LLM Suite (MCP) Yorumunu Getir** butonuna tıkladığınızda akış tetiklenir, LLM Agent MCP üzerinden veritabanındaki SD verilerini DataFrame olarak çekip yorumlar ve Chat çıktısı doğrudan ekrana gelir.

## Doğrudan ASELSAN AI (`aselixai`) Modelleri
- **Milli Ağ:** `https://api.aselixai.aselsan.com.tr/v1`
- **Aynet Ağı:** `https://agate.aselsan.com.tr/send/milli/api.aselixai/v1`
- **Desteklenen Sohbet/Muhakeme Modelleri:** `Qwen3-Next-80B-A3B-Instruct`, `Qwen3.6-Flash-Next-FP8`, `DeepSeek-V4.1-Flash-1017`, `openai/gpt-oss-120b`, `gemma-4-31B-it`, `Qwen3-VL-8B-Instruct`.
