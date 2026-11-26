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

function TalepDetay({ surecNo, health, onClose, onRefresh }) {
  const [data, setData] = useState(null);
  const [sekme, setSekme] = useState('kontrol'); // kontrol | baslik | ai | va01
  const [istisnaNotu, setIstisnaNotu] = useState('');
  const [onaylayan, setOnaylayan] = useState('');
  const [girilenSdNo, setGirilenSdNo] = useState('');
  const [busy, setBusy] = useState(false);

  // AI state
  const [aiCikti, setAiCikti] = useState('');
  const [sozMetin, setSozMetin] = useState('');
  const [sozFile, setSozFile] = useState(null);
  const [mailHedef, setMailHedef] = useState('talep_eden');
  const [mailEkNot, setMailEkNot] = useState('');

  // SAP VA01 state (şifre yalnızca anlık state'te tutulur)
  const [sapUser, setSapUser] = useState('');
  const [sapPassword, setSapPassword] = useState('');
  const [reuseOpenSession, setReuseOpenSession] = useState(true);
  const [bolum, setBolum] = useState('10');
  const [siparisVerenOverride, setSiparisVerenOverride] = useState('');
  const [va01Sonuc, setVa01Sonuc] = useState(null);

  const yukle = useCallback(() => {
    api.talep(surecNo)
      .then((d) => {
        setData(d);
        setIstisnaNotu(d.onay?.istisna_notu || '');
        setOnaylayan(d.onay?.onaylayan || '');
        setGirilenSdNo(d.onay?.girilen_sd_no || '');
        if (d.aiGecmis?.length && !aiCikti) setAiCikti(d.aiGecmis[0].sonuc_metni);
      })
      .catch((e) => toast.error(e.message));
  }, [surecNo]);

  useEffect(() => { yukle(); }, [yukle]);

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

  const va01Baslat = async (e) => {
    e.preventDefault();
    setBusy(true);
    setVa01Sonuc(null);
    try {
      const r = await api.va01Ac(surecNo, { sapUser, sapPassword, reuseOpenSession, bolum, siparisVerenOverride });
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
            3. Yapay Zeka (Denetim / Sözleşme / Mail)
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
                <h2>Yapay Zeka Destek Araçları (UGES TYM Şartname Uzmanı)</h2>
              </div>
              <div style={{ padding: 14, display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: 12 }}>
                {/* Kart 1: Uzman Denetim */}
                <div className="panel" style={{ margin: 0, padding: 12 }}>
                  <h4 style={{ margin: '0 0 6px' }}>1. Akıllı Kalem & BOM / Hizmet Denetimi</h4>
                  <p className="muted" style={{ margin: '0 0 10px', whiteSpace: 'normal' }}>
                    HIZMET vs fiziksel teslimat ayrımını, alt kalem bağlantılarını, kalem tiplerini ve hazır edim marjını şartnameye göre yorumlar.
                  </p>
                  <button className="btn primary" disabled={busy} onClick={calistirAiDenetim}>
                    {busy ? 'Analiz ediliyor...' : '✦ AI Uzman Denetimi Çalıştır'}
                  </button>
                </div>

                {/* Kart 2: Sözleşme Kıyaslama */}
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

                {/* Kart 3: Mail Taslağı */}
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
                  <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 6 }}>
                    <b>AI Çıktısı</b>
                    <button className="btn" onClick={() => { navigator.clipboard.writeText(aiCikti); toast.success('Panoya kopyalandı.'); }}>
                      Kopyala
                    </button>
                  </div>
                  <div className="ai-box">{aiCikti}</div>
                </div>
              )}
            </div>
          </>
        ) : (
          <div className="panel">
            <div className="panel-head">
              <h2>SAP GUI VA01 Otomatik Alan Doldurma (Kaydetmeden Kullanıcıya Bırakır)</h2>
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
                <div className="form-grid">
                  <label>
                    SAP Bağlantı Sistemi (.env)
                    <input value={health?.sapBaglanti || 'KKP CANLI'} disabled />
                  </label>
                  <label>
                    Bölüm (SPART - VA01 İlk Ekran)
                    <input value={bolum} onChange={(e) => setBolum(e.target.value)} placeholder="10" />
                  </label>
                  <label>
                    Sipariş Veren Muhatap Kodu (Raporda boşsa opsiyonel)
                    <input value={siparisVerenOverride} onChange={(e) => setSiparisVerenOverride(e.target.value)} placeholder="Örn: 1000245" />
                  </label>
                  <label style={{ justifyContent: 'center' }}>
                    <span style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 16 }}>
                      <input type="checkbox" checked={reuseOpenSession} onChange={(e) => setReuseOpenSession(e.target.checked)} />
                      Açık SAP GUI oturumu varsa şifresiz kullan
                    </span>
                  </label>
                  <label>
                    SAP Kullanıcı Adı (Açık oturum yoksa)
                    <input value={sapUser} onChange={(e) => setSapUser(e.target.value)} autoComplete="off" placeholder="SAP kullanıcınız" />
                  </label>
                  <label>
                    SAP Şifresi (Diske/DB'ye kaydedilmez!)
                    <input type="password" value={sapPassword} onChange={(e) => setSapPassword(e.target.value)} autoComplete="new-password" placeholder="••••••••" />
                  </label>
                </div>

                <div style={{ padding: '0 14px 14px', display: 'flex', gap: 10, flexWrap: 'wrap', alignItems: 'center' }}>
                  <button type="submit" className="btn sap" disabled={busy}>
                    {busy ? 'SAP GUI Hazırlanıyor...' : '🚀 SAP VA01 Ekranını Aç & Alanları Doldur (Kaydetme)'}
                  </button>
                  <a
                    className="btn"
                    href={api.va01VbsUrl(surecNo, bolum, siparisVerenOverride)}
                    download
                    title="Uzak sunucudan çalışıyorsanız kendi bilgisayarınızdaki açık SAP oturumunda çift tıklayarak çalıştırabilirsiniz"
                  >
                    ⬇ Şifresiz VA01 .vbs Scripti İndir (VDI / Uzak PC)
                  </a>
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
            SAP GUI: {health?.sapGuiMevcut ? `✓ ${health.sapBaglanti}` : '⚠ Yok (.vbs modu aktif)'}
          </small>
        </div>
        <div className="toolbar">
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

      {secili && <TalepDetay surecNo={secili} health={health} onClose={() => setSecili(null)} onRefresh={yukle} />}
    </div>
  );
}
