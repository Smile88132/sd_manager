import React, { useCallback, useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import { api } from './api.js';

const fmtNum = (v, d = 2) =>
  v == null || v === '' || isNaN(v) ? '-' : new Intl.NumberFormat('tr-TR', { maximumFractionDigits: d }).format(v);
const fmtDate = (v) => {
  if (!v) return '-';
  const d = new Date(v);
  return isNaN(d) ? v : d.toLocaleDateString('tr-TR');
};

const KONTROL_ETIKET = {
  ENGEL: { cls: 'red', text: '🔴 Engel Var' },
  UYARI: { cls: 'orange', text: '🟠 Uyarı (Onay Bekliyor)' },
  ONAYLI: { cls: 'blue', text: '✓ Onaylandı (VA01 Hazır)' },
  HAZIR: { cls: 'green', text: '🟢 Doğrudan Hazır' },
};

const TALEP_ALANLARI = [
  ['surec_no', 'Süreç No'], ['belge_no', 'Belge No'], ['belge_tarihi', 'Belge Tarihi', 'date'],
  ['sd_belge_no', 'Mevcut SD Belge No'], ['siparis_turu', 'Sipariş Türü'], ['crm_teklif_no', 'CRM Teklif No'],
  ['aselsan_sozlesme_no', 'ASELSAN Sözleşme No'], ['sozlesme_adi', 'Sözleşme Adı'],
  ['orijinal_sozlesme_tutari_ham', 'Orijinal Sözleşme Tutarı'], ['sozlesme_imza_tarihi', 'Sözleşme İmza', 'date'],
  ['sozlesme_baslangic_tarihi', 'Sözleşme Başlangıç', 'date'], ['sozlesme_bitis_tarihi', 'Sözleşme Bitiş', 'date'],
  ['sozlesme_teslim_tarihi', 'Sözleşme Teslim', 'date'], ['sas_no', 'SAS No'], ['sas_tarihi', 'SAS Tarihi', 'date'],
  ['satis_organizasyonu', 'Satış Org.'], ['dagitim_kanali', 'Dağıtım Kanalı'], ['satis_kategorisi', 'Satış Kategorisi'],
  ['satis_burosu', 'Satış Bürosu'], ['satis_grubu', 'Satış Grubu'], ['uretim_yeri', 'Üretim Yeri'],
  ['odeme_sekli', 'Ödeme Şekli'], ['fatura_tipi', 'Fatura Tipi'], ['incoterms', 'Incoterms'],
  ['siparis_veren', 'Sipariş Veren'], ['mali_teslim_alan', 'Malı Teslim Alan'], ['fatura_alicisi', 'Fatura Alıcısı'],
  ['odeyen', 'Ödeyen'], ['son_kullanici', 'Son Kullanıcı'], ['satis_sorumlusu', 'Satış Sorumlusu'],
  ['mip_sorumlusu', 'MIP Sorumlusu'], ['proje_hesap_no', 'Proje Hesap No'], ['eskalasyona_tabi', 'Eskalasyona Tabi'],
  ['talep_eden_ad_soyad', 'Talep Eden'], ['talep_eden_departman', 'Departman'], ['akis_durumu', 'Akış Durumu'],
];

const BOS_VA01 = {
  siparisVeren: '',
  maliTeslimAlan: '',
  sasNo: '',
  sasTarihi: '',
  istenenTeslimTarihi: '',
  sozlesmeBaslangic: '',
  sozlesmeBitis: '',
  odemeKosulu: '',
  inco1: '',
  inco2: '',
  paraBirimi: '',
  satisOrg: '',
  dagitimKanali: '',
  bolum: '10',
  uretimYeri: '',
  zorlaKalemTipi: false,
};

const ASELIXAI_MODELLER = [
  'Qwen3-Next-80B-A3B-Instruct',
  'Qwen3.6-Flash-Next-FP8',
  'DeepSeek-V4.1-Flash-1017',
  'openai/gpt-oss-120b',
  'gemma-4-31B-it',
  'Qwen3-VL-8B-Instruct',
];

function LlmSuiteMcpModal({ onClose, onSaved }) {
  const [durum, setDurum] = useState(null);
  const [form, setForm] = useState({
    baseUrl: 'https://llmsuiteflow.aiplatform.aselsan.com.tr',
    flowId: '',
    apiKey: '',
    timeoutSec: 300,
    chatInputId: '',
    sslDogrula: true,
    aiApiUrl: 'https://api.aselixai.aselsan.com.tr/v1',
    aiApiKey: '',
    aiModel: 'Qwen3-Next-80B-A3B-Instruct',
    mcpPublicUrl: '',
  });
  const [modeller, setModeller] = useState(ASELIXAI_MODELLER);
  const [testSonuc, setTestSonuc] = useState(null);
  const [mcpOnizleme, setMcpOnizleme] = useState('');
  const [ornekSurec, setOrnekSurec] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.llmSuiteDurum()
      .then((d) => {
        setDurum(d);
        setForm({
          baseUrl: d.llmSuite?.baseUrl || 'https://llmsuiteflow.aiplatform.aselsan.com.tr',
          flowId: d.llmSuite?.flowId || '',
          apiKey: d.llmSuite?.apiKeyMaskeli || '',
          timeoutSec: d.llmSuite?.timeoutSec || 300,
          chatInputId: d.llmSuite?.chatInputId || '',
          sslDogrula: d.llmSuite?.sslDogrula ?? true,
          aiApiUrl: d.direktAi?.apiUrl || 'https://api.aselixai.aselsan.com.tr/v1',
          aiApiKey: d.direktAi?.apiKeyMaskeli || '',
          aiModel: d.direktAi?.model || 'Qwen3-Next-80B-A3B-Instruct',
          mcpPublicUrl: '',
        });
      })
      .catch((e) => toast.error(e.message));
  }, []);

  const setF = (k, v) => setForm((p) => ({ ...p, [k]: v }));

  const kaydet = async () => {
    setBusy(true);
    try {
      const r = await api.llmSuiteAyarlarKaydet({
        ...form,
        timeoutSec: Number(form.timeoutSec) || 300,
      });
      toast.success(r.mesaj || 'Ayarlar kaydedildi.');
      const guncel = await api.llmSuiteDurum();
      setDurum(guncel);
      onSaved?.();
    } catch (e) {
      toast.error(e.message);
    } finally {
      setBusy(false);
    }
  };

  const baglantiTest = async () => {
    setBusy(true);
    setTestSonuc(null);
    try {
      await api.llmSuiteAyarlarKaydet({ ...form, timeoutSec: Number(form.timeoutSec) || 300 });
      const r = await api.llmSuiteTest();
      setTestSonuc({ ok: true, metin: r.yanit || 'Bağlantı başarılı!', sureMs: r.sureMs });
      toast.success('ASELSAN LLM Suite Flow bağlantısı başarılı!');
      onSaved?.();
    } catch (e) {
      setTestSonuc({ ok: false, metin: e.message });
      toast.error(e.message);
    } finally {
      setBusy(false);
    }
  };

  const canliModelleriGetir = async () => {
    setBusy(true);
    try {
      const r = await api.llmModeller(form.aiApiUrl, form.aiApiKey);
      if (r.modeller?.length) {
        setModeller(r.modeller);
        toast.success(`${r.modeller.length} aktif model /models uç noktasından yüklendi.`);
      }
    } catch (e) {
      toast.error(e.message);
    } finally {
      setBusy(false);
    }
  };

  const mcpDene = async (aracAdi) => {
    setBusy(true);
    try {
      const args = aracAdi === 'sd_talep_listesi'
        ? { limit: 10 }
        : { surec_no: Number(ornekSurec) || 0 };
      const r = await api.mcpAracDene(aracAdi, args);
      setMcpOnizleme(r.metin || '');
    } catch (e) {
      toast.error(e.message);
    } finally {
      setBusy(false);
    }
  };

  const kopyala = (txt, baslik = 'Kopyalandı') => {
    navigator.clipboard.writeText(txt || '');
    toast.success(baslik);
  };

  const mcp = durum?.mcp;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-card" onClick={(e) => e.stopPropagation()}>
        <div className="panel-head" style={{ borderRadius: 8, marginBottom: 12, background: '#fff' }}>
          <div>
            <h2 style={{ fontSize: 15 }}>⚙ ASELSAN LLM Suite Flow & Bağımsız MCP Sunucusu Ayarları</h2>
            <span className="muted">MCP sunucusu start.bat ile ayrı portta otomatik çalışır; LLM Suite Flow içindeki MCP Tools bileşeninden bağlanır.</span>
          </div>
          <button className="btn" onClick={onClose}>Kapat ✕</button>
        </div>

        {/* 1. Bağımsız MCP Sunucu Bilgileri */}
        <div className="panel">
          <div className="panel-head">
            <h2>1. Bağımsız SD MCP Sunucusu (Port: {mcp?.port || '...' })</h2>
            <span className="badge green">✓ Aktif — DataFrame & SQL Hazır</span>
          </div>
          <div style={{ padding: 14, display: 'grid', gap: 10 }}>
            <div>
              <div style={{ fontWeight: 700, marginBottom: 4 }}>LLM Suite Flow → "MCP Tools" Bileşenine Girilecek Adresler:</div>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center', marginBottom: 6 }}>
                <span className="badge blue">SSE URL</span>
                <span className="code-pill">{mcp?.sseUrl || '...'}</span>
                <button className="btn" onClick={() => kopyala(mcp?.sseUrl, 'SSE URL kopyalandı')}>Kopyala</button>
              </div>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                <span className="badge gray">Streamable HTTP</span>
                <span className="code-pill">{mcp?.streamableUrl || '...'}</span>
                <button className="btn" onClick={() => kopyala(mcp?.streamableUrl, 'Streamable HTTP URL kopyalandı')}>Kopyala</button>
                {mcp?.langflowJson && (
                  <button className="btn" onClick={() => kopyala(mcp.langflowJson, 'Langflow MCP JSON kopyalandı')}>
                    📋 Langflow MCP JSON Kopyala
                  </button>
                )}
              </div>
              {mcp?.alternatifUrller?.length > 1 && (
                <div className="muted" style={{ marginTop: 6, fontSize: 11.5 }}>
                  Alternatif ağ adresleri: {mcp.alternatifUrller.join(' · ')}
                </div>
              )}
            </div>

            <div>
              <div style={{ fontWeight: 700, marginBottom: 4 }}>Sunulan MCP Araçları ({mcp?.araclar?.length || 0}):</div>
              <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 8 }}>
                {(mcp?.araclar || []).map((a) => (
                  <span key={a} className="badge blue" style={{ fontFamily: 'Consolas, monospace' }}>{a}</span>
                ))}
              </div>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                <button className="btn" disabled={busy} onClick={() => mcpDene('sd_talep_listesi')}>
                  ▶ Canlı Test: sd_talep_listesi
                </button>
                <input
                  placeholder="Örnek süreç no (örn: 20609365)"
                  value={ornekSurec}
                  onChange={(e) => setOrnekSurec(e.target.value)}
                  style={{ width: 190 }}
                />
                <button className="btn" disabled={busy || !ornekSurec} onClick={() => mcpDene('sd_kalemler_dataframe')}>
                  ▶ sd_kalemler_dataframe (JSON DataFrame)
                </button>
                <button className="btn" disabled={busy || !ornekSurec} onClick={() => mcpDene('sd_eksik_alan_analizi')}>
                  ▶ sd_eksik_alan_analizi
                </button>
              </div>
              {mcpOnizleme && (
                <div className="ai-box" style={{ marginTop: 8, maxHeight: 200 }}>{mcpOnizleme}</div>
              )}
            </div>
          </div>
        </div>

        {/* 2. ASELSAN LLM Suite Flow Ayarları */}
        <div className="panel">
          <div className="panel-head">
            <h2>2. ASELSAN LLM Suite Flow API Bağlantısı (/api/v1/run/&#123;flowId&#125;)</h2>
            <span className="muted">Akıştaki Chat Output yorumunu SD ekranına çeker</span>
          </div>
          <div className="form-grid">
            <label>
              Base URL
              <input value={form.baseUrl} onChange={(e) => setF('baseUrl', e.target.value)} placeholder="https://llmsuiteflow.aiplatform.aselsan.com.tr" />
            </label>
            <label>
              Flow ID (veya Endpoint Name)
              <input value={form.flowId} onChange={(e) => setF('flowId', e.target.value)} placeholder="c16ecc83-ff64-4fc7-bb52-0f594beaf70a" />
            </label>
            <label>
              API Key (x-api-key)
              <input type="password" value={form.apiKey} onChange={(e) => setF('apiKey', e.target.value)} placeholder="sk-..." />
            </label>
            <label>
              Zaman Aşımı (sn — MCP + Agent için 300 önerilir)
              <input type="number" min={30} max={1800} value={form.timeoutSec} onChange={(e) => setF('timeoutSec', e.target.value)} />
            </label>
            <label>
              ChatInput Bileşen ID (Standart akış için boş bırakın!)
              <input value={form.chatInputId} onChange={(e) => setF('chatInputId', e.target.value)} placeholder="Boş bırakın (400 hatasını önler)" />
            </label>
            <label style={{ justifyContent: 'center' }}>
              <span>Kurumsal SSL Sertifikası</span>
              <span style={{ display: 'flex', alignItems: 'center', gap: 6, fontWeight: 500, marginTop: 6 }}>
                <input type="checkbox" checked={!form.sslDogrula} onChange={(e) => setF('sslDogrula', !e.target.checked)} />
                İç ağ SSL sertifika doğrulamasını esnet
              </span>
            </label>
          </div>
          {testSonuc && (
            <div style={{ padding: '0 14px 12px' }}>
              <div className={`rule-item ${testSonuc.ok ? '' : 'Kirmizi'}`} style={testSonuc.ok ? { borderLeftColor: '#059669', background: '#ecfdf5' } : undefined}>
                <h4>{testSonuc.ok ? `✓ Bağlantı Başarılı (${testSonuc.sureMs || 0} ms)` : '✕ Bağlantı Hatası'}</h4>
                <p style={{ whiteSpace: 'pre-wrap' }}>{testSonuc.metin}</p>
              </div>
            </div>
          )}
        </div>

        {/* 3. Doğrudan ASELSAN AI (aselixai) Model & Endpoint Ayarları */}
        <div className="panel">
          <div className="panel-head">
            <h2>3. Doğrudan ASELSAN AI (aselixai) Modelleri (Denetim / Sözleşme / Mail Araçları İçin)</h2>
            <div style={{ display: 'flex', gap: 6 }}>
              <button className="btn" type="button" onClick={() => setF('aiApiUrl', 'https://api.aselixai.aselsan.com.tr/v1')}>
                Milli Ağ URL
              </button>
              <button className="btn" type="button" onClick={() => setF('aiApiUrl', 'https://agate.aselsan.com.tr/send/milli/api.aselixai/v1')}>
                Aynet Ağı URL
              </button>
              <button className="btn" type="button" disabled={busy} onClick={canliModelleriGetir}>
                ↻ /models Listele
              </button>
            </div>
          </div>
          <div className="form-grid">
            <label>
              AI API URL (/chat/completions, /models, /rerank, /embeddings)
              <input value={form.aiApiUrl} onChange={(e) => setF('aiApiUrl', e.target.value)} placeholder="https://api.aselixai.aselsan.com.tr/v1" />
            </label>
            <label>
              Aktif Model Seçimi
              <input list="aselixai-models" value={form.aiModel} onChange={(e) => setF('aiModel', e.target.value)} placeholder="Qwen3-Next-80B-A3B-Instruct" />
              <datalist id="aselixai-models">
                {modeller.map((m) => <option key={m} value={m} />)}
              </datalist>
            </label>
            <label>
              AI API Key (Bearer)
              <input type="password" value={form.aiApiKey} onChange={(e) => setF('aiApiKey', e.target.value)} placeholder="Bearer API Key..." />
            </label>
          </div>
        </div>

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
          <button className="btn" disabled={busy} onClick={baglantiTest}>
            ⚡ LLM Suite Bağlantısını Test Et
          </button>
          <button className="btn primary" disabled={busy} onClick={kaydet}>
            💾 Ayarları Kaydet (.env)
          </button>
        </div>
      </div>
    </div>
  );
}

function TalepDetay({ surecNo, health, onClose, onRefresh, onOpenSettings }) {
  const [data, setData] = useState(null);
  const [sekme, setSekme] = useState('kontrol');
  const [istisnaNotu, setIstisnaNotu] = useState('');
  const [onaylayan, setOnaylayan] = useState('');
  const [girilenSdNo, setGirilenSdNo] = useState('');
  const [busy, setBusy] = useState(false);

  // AI & LLM Suite (MCP) state
  const [aiCikti, setAiCikti] = useState('');
  const [aiBaslik, setAiBaslik] = useState('AI / LLM Suite Çıktısı');
  const [llmEkTalimat, setLlmEkTalimat] = useState('');
  const [llmVeriyiEkle, setLlmVeriyiEkle] = useState(false);
  const [sozMetin, setSozMetin] = useState('');
  const [sozFile, setSozFile] = useState(null);
  const [mailHedef, setMailHedef] = useState('talep_eden');
  const [mailEkNot, setMailEkNot] = useState('');

  // SAP VA01 state
  const [va01Form, setVa01Form] = useState(BOS_VA01);
  const [sapUser, setSapUser] = useState('');
  const [sapPassword, setSapPassword] = useState('');
  const [reuseOpenSession, setReuseOpenSession] = useState(true);
  const [va01Sonuc, setVa01Sonuc] = useState(null);

  const yukle = useCallback(() => {
    api.talep(surecNo)
      .then((d) => {
        setData(d);
        setIstisnaNotu(d.onay?.istisna_notu || '');
        setOnaylayan(d.onay?.onaylayan || '');
        setGirilenSdNo(d.onay?.girilen_sd_no || '');
        if (d.va01Varsayilan) {
          const v = d.va01Varsayilan;
          setVa01Form({
            siparisVeren: v.siparisVeren || '',
            maliTeslimAlan: v.maliTeslimAlan || '',
            sasNo: v.sasNo || '',
            sasTarihi: v.sasTarihi || '',
            istenenTeslimTarihi: v.istenenTeslimTarihi || '',
            sozlesmeBaslangic: v.sozlesmeBaslangic || '',
            sozlesmeBitis: v.sozlesmeBitis || '',
            odemeKosulu: v.odemeKosulu || '',
            inco1: v.inco1 || '',
            inco2: v.inco2 || '',
            paraBirimi: v.paraBirimi || '',
            satisOrg: v.satisOrganizasyonu || '9990',
            dagitimKanali: v.dagitimKanali || '10',
            bolum: v.bolum || '10',
            uretimYeri: v.uretimYeri === '9999' ? '' : (v.uretimYeri || ''),
            zorlaKalemTipi: Boolean(v.zorlaKalemTipi),
          });
        }
        if (d.aiGecmis?.length && !aiCikti) setAiCikti(d.aiGecmis[0].sonuc_metni);
      })
      .catch((e) => toast.error(e.message));
  }, [surecNo]);

  useEffect(() => { yukle(); }, [yukle]);

  const setF = (k, v) => setVa01Form((p) => ({ ...p, [k]: v }));

  const t = data?.talep;
  const kalemler = data?.kalemler || [];
  const kontrol = data?.kontrol;
  const toplam = kalemler.reduce((a, k) => a + (Number(k.miktar) || 0) * (Number(k.birim_fiyat) || 0), 0);

  const onaylaKaydet = async (onayla) => {
    setBusy(true);
    try {
      const r = await api.onayla(surecNo, { onayla, onaylayan, istisnaNotu, girilenSdNo });
      toast.success(r.mesaj);
      yukle();
      onRefresh();
    } catch (e) { toast.error(e.message); }
    finally { setBusy(false); }
  };

  const calistirLlmSuiteMcp = async () => {
    setBusy(true);
    try {
      const r = await api.llmSuiteAnaliz(surecNo, {
        ekTalimat: llmEkTalimat,
        veriyiEkle: llmVeriyiEkle,
      });
      setAiBaslik(`ASELSAN LLM Suite (MCP) Yorumu — ${(r.sureMs / 1000).toFixed(1)} sn`);
      setAiCikti(r.yanit);
      toast.success('LLM Suite (MCP) SD yorumu alındı!');
      yukle();
    } catch (e) {
      toast.error(e.message);
    } finally {
      setBusy(false);
    }
  };

  const onizleMcpDataFrame = async () => {
    setBusy(true);
    try {
      const r = await api.mcpAracDene('sd_talep_dataframe', { surec_no: surecNo, format: 'markdown' });
      setAiBaslik(`MCP DataFrame Önizleme (sd_talep_dataframe — Süreç ${surecNo})`);
      setAiCikti(r.metin || '');
    } catch (e) {
      toast.error(e.message);
    } finally {
      setBusy(false);
    }
  };

  const calistirAiDenetim = async () => {
    setBusy(true);
    try {
      const r = await api.aiDenetim(surecNo);
      setAiBaslik(`AI Uzman Denetimi (${r.model || ''})`);
      setAiCikti(r.yanit);
      toast.success('AI uzman denetimi tamamlandı.');
      yukle();
    } catch (e) { toast.error(e.message); }
    finally { setBusy(false); }
  };

  const calistirSozlesmeKiyas = async () => {
    setBusy(true);
    try {
      const r = await api.aiSozlesmeKiyas(surecNo, { file: sozFile, metin: sozMetin });
      setAiCikti(r.yanit);
      toast.success('Sözleşme çapraz kontrolü tamamlandı.');
      yukle();
    } catch (e) { toast.error(e.message); }
    finally { setBusy(false); }
  };

  const calistirMailTaslak = async () => {
    setBusy(true);
    try {
      const r = await api.aiMailTaslak(surecNo, { hedef: mailHedef, ekNot: mailEkNot });
      setAiCikti(r.yanit);
      toast.success('E-posta taslağı hazırlandı.');
      yukle();
    } catch (e) { toast.error(e.message); }
    finally { setBusy(false); }
  };

  const va01AlanlariniKaydet = async () => {
    setBusy(true);
    try {
      await api.va01Kaydet(surecNo, va01Form);
      toast.success('VA01 başlık ve muhatap alanları kaydedildi.');
    } catch (err) { toast.error(err.message); }
    finally { setBusy(false); }
  };

  const vbsIndir = async () => {
    if (!va01Form.siparisVeren.trim()) {
      toast.error('SAP VA01 için önce Sipariş Veren (Müşteri Kodu, örn: 601607) alanını doldurun.');
      return;
    }
    try {
      await api.va01Kaydet(surecNo, {
        ...va01Form,
        maliTeslimAlan: va01Form.maliTeslimAlan.trim() || va01Form.siparisVeren.trim(),
      });
      window.location.href = api.va01VbsUrl(surecNo);
    } catch (err) { toast.error(err.message); }
  };

  const va01Baslat = async (e) => {
    e.preventDefault();
    if (!va01Form.siparisVeren.trim()) {
      toast.error('Sipariş Veren boş bırakılamaz! Müşteri kodunu (örn: 601607) veya müşteri adını girin.');
      return;
    }
    const guncelOverride = {
      ...va01Form,
      maliTeslimAlan: va01Form.maliTeslimAlan.trim() || va01Form.siparisVeren.trim(),
    };
    setBusy(true);
    setVa01Sonuc(null);
    try {
      const r = await api.va01Ac(surecNo, {
        sapUser,
        sapPassword,
        reuseOpenSession,
        override: guncelOverride,
      });
      setVa01Sonuc(r);
      setSapPassword('');
      toast.success('SAP VA01 ekranı dolduruldu (kaydedilmedi).');
      yukle();
      onRefresh();
    } catch (err) {
      toast.error(err.message);
    } finally {
      setBusy(false);
    }
  };

  const et = KONTROL_ETIKET[kontrol?.genelDurum] || { cls: 'gray', text: '-' };
  const svBos = !va01Form.siparisVeren.trim();

  return (
    <div className="drawer-backdrop" onClick={onClose}>
      <div className="drawer" onClick={(e) => e.stopPropagation()}>
        <div className="panel-head" style={{ borderRadius: 10, marginBottom: 12, background: '#fff' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <h2 style={{ fontSize: 16 }}>Süreç {surecNo} {t?.belge_no ? `— ${t.belge_no}` : ''}</h2>
            <span className={`badge ${et.cls}`}>{et.text}</span>
            {data?.onay?.va01_acildi_mi && <span className="badge green">SAP VA01 Açıldı</span>}
          </div>
          <button className="btn" onClick={onClose}>Kapat ✕</button>
        </div>

        <div className="tabs">
          <button className={`tab ${sekme === 'kontrol' ? 'active' : ''}`} onClick={() => setSekme('kontrol')}>
            1. Kural Kontrolü & Onay ({kontrol?.bulgular?.length || 0})
          </button>
          <button className={`tab ${sekme === 'baslik' ? 'active' : ''}`} onClick={() => setSekme('baslik')}>
            2. Başlık & Kalem Detayları ({kalemler.length})
          </button>
          <button className={`tab ${sekme === 'ai' ? 'active' : ''}`} onClick={() => setSekme('ai')}>
            3. LLM Suite (MCP) & AI Yorum
          </button>
          <button className={`tab ${sekme === 'va01' ? 'active' : ''}`} onClick={() => setSekme('va01')}>
            4. SAP VA01 Aç & Doldur (Kaydetmeden)
          </button>
        </div>

        {!data ? (
          <div className="empty">Yükleniyor...</div>
        ) : sekme === 'kontrol' ? (
          <>
            <div className="panel" style={{ padding: 14 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 10, marginBottom: 10 }}>
                <div>
                  <b>Şartname Kural Motoru Özeti:</b>{' '}
                  <span className="badge red">🔴 Kırmızı: {kontrol.kirmiziSayisi}</span>{' '}
                  <span className="badge orange">🟠 Turuncu: {kontrol.turuncuSayisi}</span>{' '}
                  <span className="badge amber">🟡 Sarı: {kontrol.sariSayisi}</span>
                </div>
                {kontrol.va01Acilabilir && (
                  <button className="btn sap" onClick={() => setSekme('va01')}>
                    → SAP VA01 Doldurma Ekranına Geç
                  </button>
                )}
              </div>

              {kontrol.bulgular.length === 0 ? (
                <div className="rule-item" style={{ borderLeftColor: '#059669', background: '#ecfdf5' }}>
                  <h4>✓ Tüm kurallardan geçti</h4>
                  <p>Hiçbir engel veya uyarı bulunmadı; doğrudan SAP VA01 ekranında açabilirsiniz.</p>
                </div>
              ) : (
                kontrol.bulgular.map((b, i) => (
                  <div key={i} className={`rule-item ${b.seviye}`}>
                    <h4>
                      {b.seviye === 'Kirmizi' ? '🔴 [ENGEL]' : b.seviye === 'Turuncu' ? '🟠 [TURUNCU UYARI]' : '🟡 [SARI UYARI]'}{' '}
                      {b.baslik} <small className="muted">({b.kod})</small>
                    </h4>
                    <p>{b.detay}</p>
                  </div>
                ))
              )}
            </div>

            <div className="panel">
              <div className="panel-head">
                <h2>SD Giriş Onayı / İstisna Kaydı</h2>
                <span className="muted">Kırmızı engel yoksa Turuncu/Sarı uyarıları istisna notuyla onaylayıp VA01 kilidini açabilirsiniz.</span>
              </div>
              <div className="form-grid">
                <label>
                  Onaylayan Kişi
                  <input value={onaylayan} onChange={(e) => setOnaylayan(e.target.value)} placeholder="Ad Soyad / Sicil" />
                </label>
                <label>
                  SAP'de Oluşan SD Belge No (Kaydettikten sonra opsiyonel)
                  <input value={girilenSdNo} onChange={(e) => setGirilenSdNo(e.target.value)} placeholder="Örn: 123999" />
                </label>
                <label style={{ gridColumn: '1 / -1' }}>
                  İstisna / Kontrol Notu (Örn: GSSYM onayı alındı, Hazır edim aynı gün ÜPM ile teyitleşildi vb.)
                  <textarea rows={2} value={istisnaNotu} onChange={(e) => setIstisnaNotu(e.target.value)} placeholder="Uyarıları geçme gerekçesi veya kontrol notu..." />
                </label>
              </div>
              <div style={{ padding: '0 14px 14px', display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                <button
                  className="btn success"
                  disabled={busy || kontrol.kirmiziSayisi > 0}
                  onClick={() => onaylaKaydet(true)}
                >
                  ✓ Onayla & VA01 Girişine İzin Ver
                </button>
                {data.onay?.onay_durumu === 'Onaylandı' && (
                  <button className="btn" disabled={busy} onClick={() => onaylaKaydet(false)}>
                    Onayı Geri Al
                  </button>
                )}
                {kontrol.kirmiziSayisi > 0 && (
                  <span style={{ color: '#be123c', fontWeight: 600, alignSelf: 'center' }}>
                    * {kontrol.kirmiziSayisi} adet Kırmızı engel giderilmeden onay verilemez.
                  </span>
                )}
              </div>
            </div>
          </>
        ) : sekme === 'baslik' ? (
          <>
            <div className="panel">
              <div className="panel-head"><h2>Talep Başlığı Alanları</h2></div>
              {t ? (
                <div className="kv">
                  {TALEP_ALANLARI.map(([k, label, type]) => (
                    <div key={k}>
                      <span>{label}</span>
                      <span className={t[k] ? '' : 'muted'}>{type === 'date' ? fmtDate(t[k]) : t[k] || '— boş —'}</span>
                    </div>
                  ))}
                </div>
              ) : (
                <div className="empty">Bu sürecin talep başlığı raporda yok.</div>
              )}
            </div>
            <div className="panel">
              <div className="panel-head">
                <h2>Kalemler ({kalemler.length})</h2>
                <span>Kalem Toplamı: <b>{fmtNum(toplam)} {kalemler[0]?.para_birimi || ''}</b></span>
              </div>
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Kalem</th><th>Üst Kalem</th><th>Malzeme No</th><th className="num">Miktar</th><th>Birim</th>
                      <th className="num">Birim Fiyat</th><th className="num">Tutar</th><th>PYP Öğesi</th><th>İç Sipariş</th>
                      <th>Hazır Edim</th><th>Teslim Tarihi</th><th>Sözleşmesel Teslim</th><th>Garanti</th><th>Kalem Tipi</th>
                    </tr>
                  </thead>
                  <tbody>
                    {kalemler.map((k) => {
                      const ayniGun = k.hazir_edim_tarihi && k.teslim_tarihi && fmtDate(k.hazir_edim_tarihi) === fmtDate(k.teslim_tarihi);
                      return (
                        <tr key={k.id}>
                          <td>{k.kalem_no}</td>
                          <td>{k.ust_kalem || <span className="muted">-</span>}</td>
                          <td><b>{k.malzeme_no || '-'}</b></td>
                          <td className="num">{fmtNum(k.miktar, 3)}</td>
                          <td>{k.olcu_birimi}</td>
                          <td className="num">{fmtNum(k.birim_fiyat)}</td>
                          <td className="num">{fmtNum((Number(k.miktar) || 0) * (Number(k.birim_fiyat) || 0))}</td>
                          <td>{k.pyp_ogesi || <span className="muted">-</span>}</td>
                          <td>{k.ic_siparis || <span className="muted">-</span>}</td>
                          <td style={ayniGun ? { background: '#fef9c3', fontWeight: 700 } : undefined}>{fmtDate(k.hazir_edim_tarihi)}</td>
                          <td style={ayniGun ? { background: '#fef9c3', fontWeight: 700 } : undefined}>{fmtDate(k.teslim_tarihi)}</td>
                          <td>{fmtDate(k.sozlesmesel_teslim_tarihi)}</td>
                          <td>{k.garanti_suresi_ham || '-'}</td>
                          <td>{(k.kalem_tipi || '').trim() || '-'}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          </>
        ) : sekme === 'ai' ? (
          <>
            <div className="panel">
              <div className="panel-head">
                <h2>ASELSAN LLM Suite (MCP DataFrame) & Yapay Zeka SD Yorum Merkezi</h2>
                <button className="btn" onClick={onOpenSettings}>
                  ⚙ LLM Suite & MCP Ayarları (:{health?.mcpPort || 5101})
                </button>
              </div>

              {/* Ana Özellik: LLM Suite Flow + MCP DataFrame Yorumlayıcı */}
              <div className="llmsuite-hero">
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 8, marginBottom: 8 }}>
                  <div>
                    <h3 style={{ margin: 0, fontSize: 14, color: '#1e3a8a' }}>
                      ✦ ASELSAN LLM Suite Flow — MCP DataFrame ile Eksik & Tutarsızlık Yorumu
                    </h3>
                    <small className="muted" style={{ color: '#475569' }}>
                      LLM Suite akışındaki Agent, bağımsız MCP portundan (<code>:{health?.mcpPort || 5101}</code>) bu sürecin başlık + kalem DataFrame verisini ve eksik alan analizini çekerek eksiklikleri telafi edici yorum üretir.
                    </small>
                  </div>
                  <span className={`badge ${health?.llmSuiteHazir ? 'green' : 'amber'}`}>
                    {health?.llmSuiteHazir ? '✓ Flow Bağlı' : '⚠ Flow ID / API Key Girin'}
                  </span>
                </div>
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center', marginBottom: 8 }}>
                  <input
                    style={{ flex: '1 1 320px' }}
                    placeholder="Opsiyonel ek soru/odak (Örn: Benzer geçmiş siparişlere göre ödeme koşulu ve PYP yapısını da yorumla)..."
                    value={llmEkTalimat}
                    onChange={(e) => setLlmEkTalimat(e.target.value)}
                  />
                  <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 12, fontWeight: 600 }}>
                    <input type="checkbox" checked={llmVeriyiEkle} onChange={(e) => setLlmVeriyiEkle(e.target.checked)} />
                    DataFrame'i mesaja da göm (MCP erişimi yoksa yedek)
                  </label>
                </div>
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                  <button className="btn primary" disabled={busy} onClick={calistirLlmSuiteMcp}>
                    {busy ? 'LLM Suite yanıtı bekleniyor...' : '🚀 LLM Suite (MCP) Yorumunu Getir'}
                  </button>
                  <button className="btn" disabled={busy} onClick={onizleMcpDataFrame}>
                    📊 MCP DataFrame Çıktısını Önizle
                  </button>
                </div>
              </div>

              <div style={{ padding: '0 14px 14px', display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(290px, 1fr))', gap: 12 }}>
                <div className="panel" style={{ margin: 0, padding: 12 }}>
                  <h4 style={{ margin: '0 0 6px' }}>1. Doğrudan Model Denetimi (aselixai)</h4>
                  <p className="muted" style={{ margin: '0 0 10px', whiteSpace: 'normal' }}>
                    HIZMET vs fiziksel teslimat ayrımını, alt kalem bağlantılarını, kalem tiplerini ve hazır edim marjını şartnameye göre yorumlar.
                  </p>
                  <button className="btn primary" disabled={busy} onClick={calistirAiDenetim}>
                    {busy ? 'Analiz ediliyor...' : '✦ Hızlı AI Denetimi Çalıştır'}
                  </button>
                </div>

                <div className="panel" style={{ margin: 0, padding: 12 }}>
                  <h4 style={{ margin: '0 0 6px' }}>2. Sözleşme / PO ↔ Talep Çapraz Kontrol</h4>
                  <input type="file" accept=".docx,.txt" onChange={(e) => setSozFile(e.target.files?.[0] || null)} style={{ width: '100%', marginBottom: 6 }} />
                  <textarea
                    rows={2}
                    style={{ width: '100%', marginBottom: 8 }}
                    placeholder="Veya sözleşme/PO maddelerini buraya yapıştırın..."
                    value={sozMetin}
                    onChange={(e) => setSozMetin(e.target.value)}
                  />
                  <button className="btn primary" disabled={busy} onClick={calistirSozlesmeKiyas}>
                    ✦ Sözleşme ile Kıyasla
                  </button>
                </div>

                <div className="panel" style={{ margin: 0, padding: 12 }}>
                  <h4 style={{ margin: '0 0 6px' }}>3. Onay / Revize E-Posta Taslağı Üret</h4>
                  <select value={mailHedef} onChange={(e) => setMailHedef(e.target.value)} style={{ width: '100%', marginBottom: 6 }}>
                    <option value="talep_eden">Talep Sahibine (Eksik / Düzeltme Talebi)</option>
                    <option value="mip">ÜPM MIP Sorumlusuna (Hazır Edim Tarihi Onayı - Md. 9)</option>
                    <option value="pkd">TYM PKD Sorumlusuna (Sözleşme & Stok/BOM Kontrolü - Md. 5/7/8)</option>
                    <option value="gssym">GSSYM'ye (Geçmiş Yıl Sözleşme İmza Onayı - Md. 4)</option>
                  </select>
                  <input
                    style={{ width: '100%', marginBottom: 8 }}
                    placeholder="Maile eklenecek özel not (opsiyonel)..."
                    value={mailEkNot}
                    onChange={(e) => setMailEkNot(e.target.value)}
                  />
                  <button className="btn primary" disabled={busy} onClick={calistirMailTaslak}>
                    ✉ Mail Taslağı Hazırla
                  </button>
                </div>
              </div>

              {aiCikti && (
                <div style={{ padding: '0 14px 14px' }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 8, marginBottom: 6 }}>
                    <b>{aiBaslik}</b>
                    <div style={{ display: 'flex', gap: 6 }}>
                      <button
                        className="btn"
                        onClick={() => {
                          setIstisnaNotu((prev) => (prev ? `${prev}\n${aiCikti.slice(0, 500)}` : aiCikti.slice(0, 500)));
                          setSekme('kontrol');
                          toast.success('Yorum özeti İstisna / Kontrol Notu alanına aktarıldı.');
                        }}
                      >
                        ↳ Kontrol Notuna Aktar
                      </button>
                      <button className="btn" onClick={() => { navigator.clipboard.writeText(aiCikti); toast.success('Panoya kopyalandı.'); }}>
                        Kopyala
                      </button>
                    </div>
                  </div>
                  <div className="ai-box">{aiCikti}</div>
                </div>
              )}

              {data.aiGecmis?.length > 0 && (
                <div style={{ padding: '0 14px 14px' }}>
                  <div style={{ fontWeight: 700, marginBottom: 6 }}>Kaydedilen Yorum Geçmişi ({data.aiGecmis.length})</div>
                  <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                    {data.aiGecmis.map((g) => (
                      <button
                        key={g.id}
                        className="btn"
                        onClick={() => {
                          setAiBaslik(`${g.baslik} (${g.model_bilgisi || ''})`);
                          setAiCikti(g.sonuc_metni);
                        }}
                      >
                        {g.analiz_turu?.startsWith('llmsuite') ? '🤖 ' : '✦ '}
                        {g.baslik} · {fmtDate(g.olusturuldu)}
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </>
        ) : (
          <>
            <div className="panel">
              <div className="panel-head">
                <h2>SAP GUI VA01 Başlık, Muhatap & Tarih Hazırlığı (Kaydetmeden Açık Bırakır)</h2>
                <span className="badge amber">Ctrl+S (Kaydet) ÇAĞRILMAZ</span>
              </div>

              {!kontrol.va01Acilabilir ? (
                <div style={{ padding: 18 }}>
                  <div className="rule-item Kirmizi">
                    <h4>VA01 Henüz Açılamaz</h4>
                    <p>
                      {kontrol.kirmiziSayisi > 0
                        ? `Bu talepte ${kontrol.kirmiziSayisi} adet Kırmızı (engelleyici) kural hatası var.`
                        : 'Turuncu/Sarı uyarılar mevcut. Lütfen "1. Kural Kontrolü & Onay" sekmesinden uyarıları inceleyip onay verin.'}
                    </p>
                  </div>
                </div>
              ) : (
                <form onSubmit={va01Baslat}>
                  <div style={{ padding: '12px 14px 0' }}>
                    <div className="rule-item" style={{ borderLeftColor: svBos ? '#e11d48' : '#0284c7', background: svBos ? '#fff1f2' : '#f0f9ff', marginBottom: 4 }}>
                      <h4>{svBos ? '⚠ Sipariş Veren Zorunludur!' : '✓ Muhatap, Tarih ve Başlık Verileri Hazır'}</h4>
                      <p>
                        <b>Sipariş Veren</b> ve <b>Malı Teslim Alan</b> alanlarına doğrudan SAP müşteri kodunu (örn: <code>601607</code>) veya müşteri adını (örn: <code>Elegancia Electromechanical</code> — SAP F4 penceresinden otomatik aranır) girebilirsiniz. Önce muhataplar ve tarihler girilip Enter ile doğrulanır, ardından kalem fiyatları ve PYP alanları doldurulur.
                      </p>
                    </div>
                  </div>

                  {/* Bölüm A: Zorunlu Muhatap & Başlık Tarihleri */}
                  <div className="form-grid">
                    <label>
                      <span style={{ color: svBos ? '#be123c' : undefined }}>* Sipariş Veren (Müşteri Kodu veya Adı)</span>
                      <input
                        value={va01Form.siparisVeren}
                        onChange={(e) => {
                          const v = e.target.value;
                          setVa01Form((p) => ({
                            ...p,
                            siparisVeren: v,
                            maliTeslimAlan: !p.maliTeslimAlan || p.maliTeslimAlan === p.siparisVeren ? v : p.maliTeslimAlan,
                          }));
                        }}
                        style={svBos ? { borderColor: '#e11d48', background: '#fff1f2' } : undefined}
                        placeholder="Örn: 601607 veya Elegancia Electromechanical"
                      />
                    </label>

                    <label>
                      <span style={{ display: 'flex', justifyContent: 'space-between' }}>
                        <span>* Malı Teslim Alan (Kod veya Ad)</span>
                        <a
                          href="#ayni"
                          onClick={(e) => { e.preventDefault(); setF('maliTeslimAlan', va01Form.siparisVeren); }}
                          style={{ fontSize: 11, color: '#4338ca' }}
                        >
                          Sipariş Veren ile Aynı Yap
                        </a>
                      </span>
                      <input
                        value={va01Form.maliTeslimAlan}
                        onChange={(e) => setF('maliTeslimAlan', e.target.value)}
                        placeholder="Boşsa Sipariş Veren ile aynı girilir"
                      />
                    </label>

                    <label>
                      SAS Numarası (VBKD-BSTKD)
                      <input value={va01Form.sasNo} onChange={(e) => setF('sasNo', e.target.value)} placeholder="Örn: 4600019061" />
                    </label>

                    <label>
                      SAS Tarihi (gg.aa.yyyy)
                      <input value={va01Form.sasTarihi} onChange={(e) => setF('sasTarihi', e.target.value)} placeholder="Örn: 16.11.2026" />
                    </label>

                    <label>
                      İstenen Teslim Tarihi (gg.aa.yyyy)
                      <input value={va01Form.istenenTeslimTarihi} onChange={(e) => setF('istenenTeslimTarihi', e.target.value)} placeholder="Örn: 30.11.2026" />
                    </label>

                    <label>
                      Sözleşme Başlangıç / Bitiş (gg.aa.yyyy)
                      <div style={{ display: 'flex', gap: 6 }}>
                        <input style={{ width: '50%' }} value={va01Form.sozlesmeBaslangic} onChange={(e) => setF('sozlesmeBaslangic', e.target.value)} placeholder="Başlangıç" />
                        <input style={{ width: '50%' }} value={va01Form.sozlesmeBitis} onChange={(e) => setF('sozlesmeBitis', e.target.value)} placeholder="Bitiş" />
                      </div>
                    </label>

                    <label>
                      Belge Para Birimi & Ödeme Koşulu
                      <div style={{ display: 'flex', gap: 6 }}>
                        <input style={{ width: '38%' }} value={va01Form.paraBirimi} onChange={(e) => setF('paraBirimi', e.target.value)} placeholder="USD" />
                        <input style={{ width: '62%' }} value={va01Form.odemeKosulu} onChange={(e) => setF('odemeKosulu', e.target.value)} placeholder="Örn: 0030 / 0000" />
                      </div>
                    </label>

                    <label>
                      Incoterms (Kod + Yer)
                      <div style={{ display: 'flex', gap: 6 }}>
                        <input style={{ width: '35%' }} value={va01Form.inco1} onChange={(e) => setF('inco1', e.target.value)} placeholder="DAP" />
                        <input style={{ width: '65%' }} value={va01Form.inco2} onChange={(e) => setF('inco2', e.target.value)} placeholder="ASELSAN / ANKARA" />
                      </div>
                    </label>

                    <label>
                      Satış Org. / Dağıtım Knl. / Bölüm
                      <div style={{ display: 'flex', gap: 6 }}>
                        <input style={{ width: '38%' }} value={va01Form.satisOrg} onChange={(e) => setF('satisOrg', e.target.value)} placeholder="9990" />
                        <input style={{ width: '31%' }} value={va01Form.dagitimKanali} onChange={(e) => setF('dagitimKanali', e.target.value)} placeholder="10" />
                        <input style={{ width: '31%' }} value={va01Form.bolum} onChange={(e) => setF('bolum', e.target.value)} placeholder="10" />
                      </div>
                    </label>

                    <label>
                      Üretim Yeri (Boşsa SAP malzeme kartından alır, örn: 5100)
                      <input value={va01Form.uretimYeri} onChange={(e) => setF('uretimYeri', e.target.value)} placeholder="Otomatik (SAP Malzeme Kartı)" />
                    </label>

                    <label style={{ justifyContent: 'center' }}>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 14 }}>
                        <input type="checkbox" checked={reuseOpenSession} onChange={(e) => setReuseOpenSession(e.target.checked)} />
                        Açık SAP GUI oturumu varsa şifresiz kullan
                      </span>
                    </label>

                    <label style={{ justifyContent: 'center' }}>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 14 }}>
                        <input type="checkbox" checked={va01Form.zorlaKalemTipi} onChange={(e) => setF('zorlaKalemTipi', e.target.checked)} />
                        Kalem tipini (KlmT) rapordan zorla
                      </span>
                    </label>

                    {!reuseOpenSession && (
                      <>
                        <label>
                          SAP Kullanıcı Adı ({health?.sapBaglanti || 'KKP CANLI'})
                          <input value={sapUser} onChange={(e) => setSapUser(e.target.value)} autoComplete="off" placeholder="SAP kullanıcınız" />
                        </label>
                        <label>
                          SAP Şifresi (Diske/DB'ye yazılmaz!)
                          <input type="password" value={sapPassword} onChange={(e) => setSapPassword(e.target.value)} autoComplete="new-password" placeholder="••••••••" />
                        </label>
                      </>
                    )}
                  </div>

                  <div style={{ padding: '0 14px 14px', display: 'flex', gap: 10, flexWrap: 'wrap', alignItems: 'center' }}>
                    <button type="submit" className="btn sap" disabled={busy}>
                      {busy ? 'SAP GUI Dolduruluyor...' : '🚀 SAP VA01 Aç & Tüm Alanları Doldur (Kaydetme)'}
                    </button>
                    <button type="button" className="btn" disabled={busy} onClick={va01AlanlariniKaydet}>
                      💾 Bu Alan Bilgilerini Kaydet
                    </button>
                    <button
                      type="button"
                      className="btn"
                      disabled={busy}
                      onClick={vbsIndir}
                      title="Uzak sunucudan çalışıyorsanız kendi bilgisayarınızdaki açık SAP oturumunda çift tıklayarak çalıştırabilirsiniz"
                    >
                      ⬇ Şifresiz VA01 .vbs Scripti İndir (VDI / Uzak PC)
                    </button>
                  </div>

                  {va01Sonuc && (
                    <div style={{ padding: '0 14px 14px' }}>
                      <div className="rule-item" style={{ borderLeftColor: '#059669', background: '#ecfdf5' }}>
                        <h4>✓ {va01Sonuc.mesaj}</h4>
                        {va01Sonuc.doldurulan?.length > 0 && <p><b>Doldurulan:</b> {va01Sonuc.doldurulan.join(' · ')}</p>}
                        {va01Sonuc.sapDurumCubugu && <p><b>SAP Durum Çubuğu:</b> {va01Sonuc.sapDurumCubugu}</p>}
                        {va01Sonuc.uyarilar?.map((u, idx) => <p key={idx} style={{ color: '#b45309' }}>⚠ {u}</p>)}
                      </div>
                    </div>
                  )}
                </form>
              )}
            </div>

            {/* VA01'e Yazılacak Kalem Tablosu Önizlemesi */}
            <div className="panel">
              <div className="panel-head">
                <h2>VA01 Kalem Tablosuna Yazılacak Veriler ({kalemler.length} Kalem)</h2>
                <span className="muted">Önce Malzeme/Miktar/1.Tarih doğrulanır, ardından Tutar (Birim Fiyat), PB ve PYP Öğesi yazılır.</span>
              </div>
              <div className="table-wrap" style={{ maxHeight: '32vh' }}>
                <table>
                  <thead>
                    <tr>
                      <th>Kalem</th><th>Malzeme</th><th className="num">Sipariş Miktarı</th><th>ÖB</th>
                      <th className="num">Tutar (Birim Fiyat)</th><th>PB</th><th>KlmT</th><th>ÜDKlm</th>
                      <th>1.Tarih (Teslim)</th><th>Hazır Edim</th><th>PYP Öğesi</th>
                    </tr>
                  </thead>
                  <tbody>
                    {kalemler.map((k) => (
                      <tr key={k.id}>
                        <td>{k.kalem_no}</td>
                        <td><b>{k.malzeme_no}</b></td>
                        <td className="num">{k.miktar_ham || fmtNum(k.miktar, 3)}</td>
                        <td>{k.olcu_birimi}</td>
                        <td className="num"><b>{k.birim_fiyat_ham || fmtNum(k.birim_fiyat)}</b></td>
                        <td>{k.para_birimi || va01Form.paraBirimi}</td>
                        <td>{(k.kalem_tipi || '').trim() || '-'}</td>
                        <td>{k.ust_kalem || '-'}</td>
                        <td>{fmtDate(k.teslim_tarihi)}</td>
                        <td>{fmtDate(k.hazir_edim_tarihi)}</td>
                        <td><code>{k.pyp_ogesi || '-'}</code></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

export default function App() {
  const [health, setHealth] = useState(null);
  const [ozet, setOzet] = useState(null);
  const [sayilar, setSayilar] = useState({ engel: 0, uyari: 0, onayli: 0, hazir: 0, toplam: 0 });
  const [rows, setRows] = useState([]);
  const [ara, setAra] = useState('');
  const [durum, setDurum] = useState('');
  const [kontrolDurumu, setKontrolDurumu] = useState('');
  const [busy, setBusy] = useState(false);
  const [uyarilar, setUyarilar] = useState([]);
  const [secili, setSecili] = useState(null);
  const [ayarAcik, setAyarAcik] = useState(false);
  const fileRef = useRef(null);

  const yukle = useCallback(async () => {
    try {
      const [h, o, t] = await Promise.all([
        api.health(),
        api.ozet(),
        api.talepler({ ara, durum, kontrolDurumu }),
      ]);
      setHealth(h);
      setOzet(o);
      setSayilar(t.sayilar || { engel: 0, uyari: 0, onayli: 0, hazir: 0, toplam: 0 });
      setRows(t.kayitlar || []);
    } catch (e) {
      toast.error(`API'ye ulaşılamadı: ${e.message}`);
    }
  }, [ara, durum, kontrolDurumu]);

  useEffect(() => {
    const id = setTimeout(yukle, 220);
    return () => clearTimeout(id);
  }, [yukle]);

  const sonucuIsle = (r) => {
    toast.success(`${r.talepSayisi} talep, ${r.kalemSayisi} kalem içe aktarıldı.`);
    setUyarilar(r.uyarilar || []);
    yukle();
  };

  const klasordenAktar = async () => {
    setBusy(true);
    try { sonucuIsle(await api.importKlasor()); }
    catch (e) { toast.error(e.message); }
    finally { setBusy(false); }
  };

  const dosyaYukle = async (e) => {
    if (!e.target.files?.length) return;
    setBusy(true);
    try { sonucuIsle(await api.importYukle(e.target.files)); }
    catch (err) { toast.error(err.message); }
    finally { setBusy(false); e.target.value = ''; }
  };

  return (
    <div className="layout">
      <div className="topbar">
        <div>
          <h1>UGES SD Giriş Kontrol & SAP VA01 Hazırlık Merkezi</h1>
          <small>
            DB: <b>{health?.dbHost || '...'} / {health?.veritabani || '...'}</b> (şema: <b>{health?.sema || 'sd'}</b>) ·
            Python: {health?.pythonMevcut ? '✓ Hazır' : '⚠ Kontrol Edin'} ·
            SAP GUI: {health?.sapGuiMevcut ? `✓ ${health.sapBaglanti}` : '⚠ Yok (.vbs modu aktif)'} ·
            MCP Sunucusu: <b>✓ :{health?.mcpPort || 5101}</b> ·
            LLM Suite: {health?.llmSuiteHazir ? '✓ Hazır' : '⚠ Ayarlayın'}
          </small>
        </div>
        <div className="toolbar">
          <button className="btn light" onClick={() => setAyarAcik(true)}>
            ⚙ LLM Suite & MCP (:{health?.mcpPort || 5101})
          </button>
          <button className="btn light" disabled={busy} onClick={klasordenAktar} title={health?.veriKlasoru}>
            {busy ? 'Aktarılıyor...' : '↻ SD Klasöründen İçe Aktar'}
          </button>
          <button className="btn light" disabled={busy} onClick={() => fileRef.current?.click()}>
            ⬆ Excel Yükle
          </button>
          <input ref={fileRef} type="file" accept=".xlsx" multiple hidden onChange={dosyaYukle} />
        </div>
      </div>

      <div className="cards">
        <div className={`card ${kontrolDurumu === '' ? 'active' : ''}`} onClick={() => setKontrolDurumu('')}>
          <div className="label">Toplam Talep / Kalem</div>
          <div className="value">{fmtNum(sayilar.toplam, 0)} <small className="muted" style={{ fontSize: 13 }}>/ {fmtNum(ozet?.genel?.kalem_sayisi, 0)} kalem</small></div>
        </div>
        <div className={`card ${kontrolDurumu === 'ENGEL' ? 'active' : ''}`} onClick={() => setKontrolDurumu(kontrolDurumu === 'ENGEL' ? '' : 'ENGEL')}>
          <div className="label" style={{ color: '#be123c' }}>🔴 Kırmızı (Engel Var)</div>
          <div className="value" style={{ color: '#be123c' }}>{fmtNum(sayilar.engel, 0)}</div>
        </div>
        <div className={`card ${kontrolDurumu === 'UYARI' ? 'active' : ''}`} onClick={() => setKontrolDurumu(kontrolDurumu === 'UYARI' ? '' : 'UYARI')}>
          <div className="label" style={{ color: '#c2410c' }}>🟠/🟡 Uyarı (İnceleme Bekleyen)</div>
          <div className="value" style={{ color: '#c2410c' }}>{fmtNum(sayilar.uyari, 0)}</div>
        </div>
        <div className={`card ${kontrolDurumu === 'ONAYLI' ? 'active' : ''}`} onClick={() => setKontrolDurumu(kontrolDurumu === 'ONAYLI' ? '' : 'ONAYLI')}>
          <div className="label" style={{ color: '#1d4ed8' }}>✓ Onaylanan (VA01 Hazır)</div>
          <div className="value" style={{ color: '#1d4ed8' }}>{fmtNum(sayilar.onayli, 0)}</div>
        </div>
        <div className={`card ${kontrolDurumu === 'HAZIR' ? 'active' : ''}`} onClick={() => setKontrolDurumu(kontrolDurumu === 'HAZIR' ? '' : 'HAZIR')}>
          <div className="label" style={{ color: '#047857' }}>🟢 Kusursuz Hazır</div>
          <div className="value" style={{ color: '#047857' }}>{fmtNum(sayilar.hazir, 0)}</div>
        </div>
      </div>

      {uyarilar.length > 0 && (
        <div className="panel">
          <div className="panel-head">
            <h2>İçe Aktarma Notları</h2>
            <button className="btn" onClick={() => setUyarilar([])}>Gizle</button>
          </div>
          <ul className="warn-list">{uyarilar.map((u, i) => <li key={i}>{u}</li>)}</ul>
        </div>
      )}

      <div className="panel">
        <div className="panel-head">
          <h2>SD Talepleri ({rows.length})</h2>
          <div className="toolbar">
            <input
              placeholder="Süreç / Belge / SD No / CRM / Malzeme / Kişi ara..."
              value={ara}
              onChange={(e) => setAra(e.target.value)}
              style={{ width: 320 }}
            />
            <select value={kontrolDurumu} onChange={(e) => setKontrolDurumu(e.target.value)}>
              <option value="">Tüm Kontrol Durumları</option>
              <option value="ENGEL">🔴 Engel Var ({sayilar.engel})</option>
              <option value="UYARI">🟠 Uyarı Bekleyen ({sayilar.uyari})</option>
              <option value="ONAYLI">✓ Onaylandı ({sayilar.onayli})</option>
              <option value="HAZIR">🟢 Kusursuz Hazır ({sayilar.hazir})</option>
            </select>
            <select value={durum} onChange={(e) => setDurum(e.target.value)}>
              <option value="">Tüm Akış Durumları</option>
              {(ozet?.akis || []).map((a) => (
                <option key={a.ad} value={a.ad === '(boş)' ? '' : a.ad}>{a.ad} ({a.adet})</option>
              ))}
            </select>
          </div>
        </div>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Kontrol Durumu</th><th>Bulgular</th><th>Süreç No</th><th>Belge No</th><th>Tür</th>
                <th>CRM Teklif</th><th>ASELSAN Söz.</th><th>Talep Eden</th><th className="num">Kalem</th>
                <th className="num">Kalem Toplamı</th><th className="num">Sözleşme Tutarı</th><th>Akış Durumu</th><th>VA01</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                const et = KONTROL_ETIKET[r.kontrol_durumu] || { cls: 'gray', text: r.kontrol_durumu };
                return (
                  <tr key={r.surec_no} className="clickable" onClick={() => setSecili(r.surec_no)}>
                    <td><span className={`badge ${et.cls}`}>{et.text}</span></td>
                    <td>
                      {r.kirmizi_sayisi > 0 && <span className="badge red" style={{ marginRight: 4 }}>🔴 {r.kirmizi_sayisi}</span>}
                      {r.turuncu_sayisi > 0 && <span className="badge orange" style={{ marginRight: 4 }}>🟠 {r.turuncu_sayisi}</span>}
                      {r.sari_sayisi > 0 && <span className="badge amber">🟡 {r.sari_sayisi}</span>}
                    </td>
                    <td><b>{r.surec_no}</b></td>
                    <td>{r.belge_no}</td>
                    <td>{r.siparis_turu || '-'}</td>
                    <td>{r.crm_teklif_no || <span className="badge red">BOŞ</span>}</td>
                    <td>{r.aselsan_sozlesme_no || '-'}</td>
                    <td title={r.talep_eden_departman}>{r.talep_eden || '-'}</td>
                    <td className="num">{r.kalem_sayisi || <span className="badge red">0</span>}</td>
                    <td className="num">{r.kalem_toplam_tutar != null ? `${fmtNum(r.kalem_toplam_tutar)} ${r.para_birimi || ''}` : '-'}</td>
                    <td className="num">{fmtNum(r.orijinal_sozlesme_tutari)}</td>
                    <td>{r.akis_durumu || '-'}</td>
                    <td>
                      {r.va01_acildi_mi ? (
                        <span className="badge green">Açıldı {r.girilen_sd_no ? `(${r.girilen_sd_no})` : ''}</span>
                      ) : r.va01_acilabilir ? (
                        <span className="badge blue">Hazır</span>
                      ) : (
                        <span className="muted">-</span>
                      )}
                    </td>
                  </tr>
                );
              })}
              {rows.length === 0 && (
                <tr><td colSpan={13} className="empty">Seçilen filtreye uygun talep bulunamadı.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      {secili && (
        <TalepDetay
          surecNo={secili}
          health={health}
          onClose={() => setSecili(null)}
          onRefresh={yukle}
          onOpenSettings={() => setAyarAcik(true)}
        />
      )}
      {ayarAcik && <LlmSuiteMcpModal onClose={() => setAyarAcik(false)} onSaved={yukle} />}
    </div>
  );
}
