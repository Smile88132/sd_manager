# -*- coding: utf-8 -*-
"""
UGES SD Kontrol — KKP CANLI SAP GUI VA01 Tam Otomatik Alan Doldurucu (KAYDETMEZ!)
=================================================================================
Çalışma Sırası (Gerçek KKP CANLI VA01 Ekran Akışı):
1. /nVA01 Başlangıç Ekranı: Sipariş Türü, Satış Org, Dağıtım Kanalı, Bölüm, Satış Bürosu, Satış Grubu -> Enter
2. Muhatap Çözümleme:
   - Sipariş Veren ve Malı Teslim Alan sayısal kod ise (örn: 601607) doğrudan yazar.
   - Metin/Firma adı ise (örn: 'Elegancia Electromechanical') SAP F4 'Müşteriler (genel)' -> 'Ad' alanından arayıp kodu seçer!
3. Başlık Alanları: SAS Numarası, SAS Tarihi, İstenen Teslim Tarihi -> Enter (Bilgi/Fatura termini pencerelerini otomatik geçer).
4. Satış Sekmesi Ek Başlık Alanları: Belge Para Birimi (USD vb.), Sözleşme Başlangıç/Bitiş, Ödeme Koşulu, Incoterms (Kod + Yer) -> Enter.
5. Kalem Tablosu (tblSAPMV45ATCTRL_U_ERF_AUFTRAG):
   - Tablo kolon indekslerini sabit varsaymaz; kolon teknik adlarından (RV45A-MABNR, KOMV-KBETR, RV45A-ETDAT, VBAP-PS_PSP_PNR vb.) dinamik bulur!
   - 1. Geçiş: Malzeme, Miktar, ÖB, 1.Tarih (Teslim Tarihi), Üst Kalem (ve 9999 değilse Üretim Yeri / Sevkiyat Noktası) -> Enter
   - Kullanılabilirlik Kontrolü (ATP - SAPLATPC) ekranlarını ve bilgi pencerelerini otomatik 'Devam' ile geçip Genel Bakış'a döner.
   - 2. Geçiş: Tutar (Birim Fiyat), PB (Para Birimi), PYP Öğesi (ve farklıysa Termin Satırında Malzeme Hazır Edim Tarihi) -> Enter.
6. KESİNLİKLE KAYDETMEZ (Ctrl+S çağrılmaz); doldurulmuş VA01 ekranını kullanıcının önünde açık bırakır.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time

try:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

try:
    import pythoncom  # type: ignore
    import win32com.client  # type: ignore
except ImportError:
    print("USER_ERROR:setup|Python ortamında 'pywin32' (win32com) kurulu değil. Kurmak için: pip install pywin32", flush=True)
    sys.exit(2)


def log(msg: str) -> None:
    print(msg, flush=True)


def user_error(code: str, msg: str) -> None:
    clean = re.sub(r"[\r\n]+", " ", str(msg or "")).strip()
    log(f"USER_ERROR:{code}|{clean}")


def read_stdin_json() -> dict:
    raw_bytes = sys.stdin.buffer.read()
    raw = raw_bytes.decode("utf-8-sig", errors="replace").strip()
    brace = raw.find("{")
    if brace > 0:
        raw = raw[brace:]
    if not raw:
        raise ValueError("stdin JSON verisi boş.")
    return json.loads(raw)


def status_text(session) -> str:
    try:
        return str(session.findById("wnd[0]/sbar").Text or "").strip()
    except Exception:
        return ""


def status_type(session) -> str:
    try:
        return str(session.findById("wnd[0]/sbar").MessageType or "").strip().upper()
    except Exception:
        return ""


def find_first(session, ids: list[str]):
    for fid in ids:
        try:
            return session.findById(fid)
        except Exception:
            continue
    return None


def set_if_exists(session, ids: list[str], val: str) -> bool:
    if val is None or str(val).strip() == "":
        return False
    v = str(val).strip()
    for fid in ids:
        try:
            obj = session.findById(fid)
            if getattr(obj, "Changeable", True):
                obj.text = v
                return True
        except Exception:
            continue
    return False


def format_sap_dec(val_str: str) -> str:
    """SAP TR ekranı için ondalık ayırıcıyı virgül yapar ('1128.50' -> '1128,50', '1128.0000' -> '1128')."""
    s = str(val_str or "").strip()
    if not s:
        return ""
    # Eğer '1.128,00' gibi zaten TR formatındaysa noktaları kaldır
    if "," in s and "." in s:
        s = s.replace(".", "")
    elif "." in s:
        parts = s.split(".")
        if len(parts) == 2 and set(parts[1]) <= {"0"}:
            s = parts[0]
        else:
            s = s.replace(".", ",")
    return s


def extract_kunnr_or_name(raw: str) -> tuple[str, str]:
    """
    '601607' veya '601607 - Elegancia' -> ('601607', '')
    'Elegancia Electromechanical' -> ('', 'Elegancia Electromechanical')
    """
    s = str(raw or "").strip()
    if not s:
        return ("", "")
    if s.isdigit():
        return (s, "")
    m = re.match(r"^(\d{4,10})\s*[-:/|]\s*(.*)$", s)
    if m:
        return (m.group(1), m.group(2).strip())
    return ("", s)


def dismiss_popups_and_atp(session, max_steps: int = 25) -> list[str]:
    """
    SAP VA01 Enter sonrası çıkan:
    1) wnd[1] Bilgi / Uyarı pencerelerini ('Fatura termini değişikliği...' vb.),
    2) wnd[0] Kullanılabilirlik Kontrolü (ATP - SAPLATPC / 'Kullanılabilirlik kontrolü') ekranlarını
    otomatik onaylayıp VA01 Genel Bakış (SAPMV45A) ekranına geri döner.
    """
    notlar = []
    for _ in range(max_steps):
        progressed = False

        # 1) wnd[1] popup kontrolü
        try:
            w1 = session.findById("wnd[1]")
            title1 = str(getattr(w1, "Text", "") or "")
            # Seçim listesi değilse (bilgi/onay kutusu ise) onayla
            for btn_id in (
                "wnd[1]/tbar[0]/btn[0]",
                "wnd[1]/usr/btnSPOP-OPTION1",
                "wnd[1]/usr/btnBUTTON_1",
            ):
                try:
                    session.findById(btn_id).press()
                    progressed = True
                    if title1:
                        notlar.append(f"Popup geçildi: {title1}")
                    time.sleep(0.25)
                    break
                except Exception:
                    continue
            if not progressed:
                w1.sendVKey(0)
                progressed = True
                time.sleep(0.25)
            continue
        except Exception:
            pass

        # 2) Kullanılabilirlik kontrolü (ATP - SAPLATPC) ekranı kontrolü
        try:
            prog = str(getattr(session.Info, "Program", "") or "").upper()
            title0 = str(session.findById("wnd[0]").Text or "")
            is_atp = (
                prog == "SAPLATPC"
                or "kullanılabilirlik" in title0.lower()
                or "availability" in title0.lower()
            )
            if is_atp:
                # 'Devam' / Öneriyi kabul et (tbar[0]/btn[0] veya VKey 0)
                pressed = False
                for atp_btn in (
                    "wnd[0]/tbar[1]/btn[8]",
                    "wnd[0]/tbar[0]/btn[0]",
                ):
                    try:
                        session.findById(atp_btn).press()
                        pressed = True
                        break
                    except Exception:
                        continue
                if not pressed:
                    session.findById("wnd[0]").sendVKey(0)
                notlar.append("Kullanılabilirlik kontrolü (ATP) ekranı geçildi.")
                time.sleep(0.3)
                continue
        except Exception:
            pass

        # 3) Durum çubuğunda sarı uyarı (W) bekliyorsa bir Enter daha gönder
        stype = status_type(session)
        if stype == "W":
            stxt = status_text(session)
            session.findById("wnd[0]").sendVKey(0)
            notlar.append(f"SAP uyarısı geçildi: {stxt}")
            time.sleep(0.25)
            continue

        if not progressed:
            break

    return notlar


def resolve_customer_via_f4(session, field_id: str, customer_name: str) -> str:
    """
    Müşteri kodu yerine müşteri adı verilmişse (örn: 'Elegancia Electromechanical'),
    ilgili muhatap alanında F4 açar, 'Ad' (MC_NAME1) alanına *isim* yazar ve ilk eşleşen müşteri kodunu seçer.
    """
    words = [w for w in re.split(r"\s+", customer_name.strip()) if len(w) >= 2]
    search_term = " ".join(words[:3]) if words else customer_name.strip()
    pattern = f"*{search_term[:28]}*"

    try:
        fld = session.findById(field_id)
        fld.setFocus()
        session.findById("wnd[0]").sendVKey(4)  # F4
        time.sleep(0.5)

        w1 = session.findById("wnd[1]")
        # 'A: Müşteriler (genel)' sekmesi varsa seç
        for tab_id in (
            "wnd[1]/usr/tabsG_SELONETABSTRIP/tabpTAB001",
            "wnd[1]/usr/tabsG_SELONETABSTRIP/tabpTAB01",
        ):
            try:
                session.findById(tab_id).select()
                time.sleep(0.2)
                break
            except Exception:
                continue

        # 'Ad' (Name 1 - genelde satır 3: [3,24]) alanını bulup doldur
        ad_written = set_if_exists(
            session,
            [
                "wnd[1]/usr/tabsG_SELONETABSTRIP/tabpTAB001/ssubSUBSCR_PRESEL:SAPLSDH4:0220/sub:SAPLSDH4:0220/txtG_SELFLD_TAB-LOW[3,24]",
                "wnd[1]/usr/tabsG_SELONETABSTRIP/tabpTAB001/ssubSUBSCR_PRESEL:SAPLSDH4:0220/txtG_SELFLD_TAB-LOW[3,24]",
                "wnd[1]/usr/sub:SAPLSDH4:0220/txtG_SELFLD_TAB-LOW[3,24]",
            ],
            pattern,
        )

        if not ad_written:
            # Dinamik olarak usr altındaki tüm txtG_SELFLD_TAB-LOW alanlarını tara
            for idx in (3, 0, 1, 2, 4):
                if set_if_exists(
                    session,
                    [f"wnd[1]/usr/tabsG_SELONETABSTRIP/tabpTAB001/ssubSUBSCR_PRESEL:SAPLSDH4:0220/sub:SAPLSDH4:0220/txtG_SELFLD_TAB-LOW[{idx},24]"],
                    pattern,
                ):
                    ad_written = True
                    break

        # Aramayı çalıştır (Enter / tbar[0]/btn[0])
        session.findById("wnd[1]/tbar[0]/btn[0]").press()
        time.sleep(0.6)

        # Sonuç listesi açıksa (wnd[1] hala varsa) ilk satırı seç ve onayla
        try:
            w1_after = session.findById("wnd[1]")
            # Eğer 'Giriş bulunamadı' mesajı değilse seçimi onayla
            sbar = status_text(session).lower()
            if "bulunamadı" in sbar or "no values" in sbar:
                w1_after.close()
                return ""
            session.findById("wnd[1]/tbar[0]/btn[0]").press()
            time.sleep(0.4)
        except Exception:
            pass

        resolved = str(session.findById(field_id).text or "").strip()
        return resolved if resolved and resolved != customer_name else ""
    except Exception as e:
        log(f"WARN: F4 müşteri arama hatası ({customer_name}): {e}")
        try:
            session.findById("wnd[1]").close()
        except Exception:
            pass
        return ""


def find_open_logged_in_session(application):
    try:
        for c_idx in range(application.Children.Count):
            conn = application.Children(c_idx)
            for s_idx in range(conn.Children.Count):
                sess = conn.Children(s_idx)
                try:
                    sess.findById("wnd[0]/usr/txtRSYST-BNAME")
                except Exception:
                    return sess
    except Exception:
        pass
    return None


def get_or_open_session(cfg: dict):
    pythoncom.CoInitialize()
    reuse = bool(cfg.get("reuseOpenSession", True))
    user = str(cfg.get("sapUser") or "").strip()
    password = str(cfg.get("sapPassword") or "")
    connection_name = str(cfg.get("sapConnection") or "KKP CANLI").strip()
    mandt = str(cfg.get("sapClient") or "500").strip()
    lang = str(cfg.get("sapLanguage") or "TR").strip()
    sap_logon_path = str(cfg.get("sapLogonPath") or r"C:\Program Files (x86)\SAP\FrontEnd\SAPgui\saplogon.exe").strip()

    if reuse:
        try:
            sap_gui = win32com.client.GetObject("SAPGUI")
            app = sap_gui.GetScriptingEngine
            existing = find_open_logged_in_session(app)
            if existing is not None:
                log("INFO: Açık SAP GUI oturumu bulundu ve kullanılıyor.")
                return existing
        except Exception:
            pass

    if not user or not password:
        user_error("auth", "Açık bir SAP GUI oturumu bulunamadı. Lütfen SAP kullanıcı adı ve şifrenizi girin (veya önce SAP Logon ile giriş yapın).")
        sys.exit(3)

    if sap_logon_path and os.path.exists(sap_logon_path):
        subprocess.Popen([sap_logon_path])

    sap_gui = None
    for _ in range(24):
        try:
            sap_gui = win32com.client.GetObject("SAPGUI")
            break
        except Exception:
            time.sleep(0.5)

    if sap_gui is None:
        user_error("gui", "SAPGUI COM nesnesi alınamadı. SAP GUI kurulu ve Scripting açık mı?")
        sys.exit(4)

    try:
        app = sap_gui.GetScriptingEngine
        conn = app.OpenConnection(connection_name, True)
        session = conn.Children(0)
    except Exception as e:
        user_error("gui", f"'{connection_name}' bağlantısı açılamadı: {e}")
        sys.exit(4)

    try:
        session.findById("wnd[0]/usr/txtRSYST-MANDT").text = mandt
        session.findById("wnd[0]/usr/txtRSYST-BNAME").text = user
        session.findById("wnd[0]/usr/pwdRSYST-BCODE").text = password
        session.findById("wnd[0]/usr/txtRSYST-LANGU").text = lang
        session.findById("wnd[0]").sendVKey(0)
    except Exception as e:
        user_error("auth", f"SAP giriş ekranı doldurulamadı: {e}")
        sys.exit(3)

    deadline = time.time() + 25.0
    while time.time() < deadline:
        try:
            opt = session.findById("wnd[1]/usr/radMULTI_LOGON_OPT2")
            opt.select()
            session.findById("wnd[1]/tbar[0]/btn[0]").press()
            time.sleep(0.6)
        except Exception:
            pass

        sbar = status_text(session).lower()
        if any(k in sbar for k in ("parola", "password", "şifre", "logon failed", "oturum açılamadı")):
            user_error("auth", f"SAP giriş hatası: {status_text(session)}")
            sys.exit(3)

        try:
            session.findById("wnd[0]/usr/txtRSYST-BNAME")
            time.sleep(0.4)
        except Exception:
            break

    dismiss_popups_and_atp(session, max_steps=5)
    return session


def find_item_table(session):
    """VA01 Satış / Kaleme Genel Bakış sekmesindeki kalem tablosunu ve kolon haritasını döner."""
    table_ids = [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/subSUBSCREEN_TC:SAPMV45A:4900/tblSAPMV45ATCTRL_U_ERF_AUFTRAG",
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\02/ssubSUBSCREEN_BODY:SAPMV45A:4401/subSUBSCREEN_TC:SAPMV45A:4900/tblSAPMV45ATCTRL_U_ERF_AUFTRAG",
    ]
    for tid in table_ids:
        try:
            tbl = session.findById(tid)
            col_map: dict[str, str] = {}
            # 0. satırdaki hücrelerin teknik adlarını ve tam ID şablonunu çıkar
            col_count = int(getattr(tbl.Columns, "Count", 0))
            for c_idx in range(col_count):
                try:
                    cell = tbl.GetCell(0, c_idx)
                    cname = str(getattr(cell, "Name", "") or "").upper()
                    cid = str(getattr(cell, "Id", "") or "")
                    # cid örn: .../tblSAPMV45ATCTRL_U_ERF_AUFTRAG/ctxtRV45A-MABNR[1,0]
                    if cname and cid and cname not in col_map:
                        # Satır indeksini {row} yer tutucusuna çevir
                        tpl = re.sub(r"\[(\d+),\s*0\]$", r"[\1,{row}]", cid)
                        col_map[cname] = tpl
                except Exception:
                    continue
            return tbl, tid, col_map
        except Exception:
            continue
    return None, "", {}


def set_table_cell(session, col_map: dict[str, str], field_names: list[str], row_in_page: int, val: str) -> bool:
    if val is None or str(val).strip() == "":
        return False
    v = str(val).strip()
    for fname in field_names:
        tpl = col_map.get(fname.upper())
        if not tpl:
            continue
        cell_id = tpl.replace("{row}", str(row_in_page))
        try:
            cell = session.findById(cell_id)
            if getattr(cell, "Changeable", True):
                cell.text = v
                return True
        except Exception:
            continue
    return False


def fill_va01(session, payload: dict) -> dict:
    talep = payload.get("talep") or {}
    kalemler = payload.get("kalemler") or []
    doldurulan = []
    uyarilar = []

    try:
        session.findById("wnd[0]").maximize()
    except Exception:
        pass

    # ── AŞAMA 1: /nVA01 İlk Ekran (Sipariş Türü & Organizasyon Verileri) ──
    session.findById("wnd[0]/tbar[0]/okcd").text = "/nVA01"
    session.findById("wnd[0]").sendVKey(0)
    time.sleep(0.5)

    siparis_turu = talep.get("siparisTuru") or "ZSTD"
    satis_org = talep.get("satisOrganizasyonu") or "9990"
    if satis_org == "9990" and talep.get("satisOrgOverride"):
        satis_org = talep.get("satisOrgOverride")
    dagitim = talep.get("dagitimKanali") or "10"
    bolum = talep.get("bolum") or "10"
    satis_burosu = talep.get("satisBurosu") or ""
    satis_grubu = talep.get("satisGrubu") or ""

    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-AUART"], siparis_turu):
        doldurulan.append(f"Tür={siparis_turu}")
    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VKORG"], satis_org):
        doldurulan.append(f"Satış Org={satis_org}")
    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VTWEG"], dagitim):
        doldurulan.append(f"Dağıtım Knl={dagitim}")
    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-SPART"], bolum):
        doldurulan.append(f"Bölüm={bolum}")
    set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VKBUR"], satis_burosu)
    set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VKGRP"], satis_grubu)

    session.findById("wnd[0]").sendVKey(0)
    time.sleep(0.6)
    dismiss_popups_and_atp(session, max_steps=5)

    # ── AŞAMA 2: Muhataplar (Sipariş Veren & Malı Teslim Alan) + SAS & İstenen Teslim Tarihi ──
    kuagv_ids = [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/subPART-SUB:SAPMV45A:4701/ctxtKUAGV-KUNNR",
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtKUAGV-KUNNR",
    ]
    kuwev_ids = [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/subPART-SUB:SAPMV45A:4701/ctxtKUWEV-KUNNR",
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtKUWEV-KUNNR",
    ]

    raw_sv = str(talep.get("siparisVeren") or "").strip()
    raw_ta = str(talep.get("maliTeslimAlan") or "").strip()

    sv_code, sv_name = extract_kunnr_or_name(raw_sv)
    ta_code, ta_name = extract_kunnr_or_name(raw_ta)

    kuagv_obj = find_first(session, kuagv_ids)
    kuwev_obj = find_first(session, kuwev_ids)

    if not sv_code and sv_name and kuagv_obj is not None:
        sv_code = resolve_customer_via_f4(session, kuagv_obj.Id, sv_name)
        if sv_code:
            doldurulan.append(f"Sipariş Veren (F4)={sv_code}")
        else:
            uyarilar.append(f"'{sv_name}' için F4 aramasında tekil müşteri kodu bulunamadı; lütfen SAP ekranından seçin.")
    elif sv_code:
        if set_if_exists(session, kuagv_ids, sv_code):
            doldurulan.append(f"Sipariş Veren={sv_code}")

    # Malı teslim alan boşsa veya aynı isimse Sipariş Veren kodunu kullan
    if not ta_code:
        if not ta_name or ta_name.lower() == sv_name.lower():
            ta_code = sv_code
        elif kuwev_obj is not None:
            ta_code = resolve_customer_via_f4(session, kuwev_obj.Id, ta_name)

    if ta_code:
        if set_if_exists(session, kuwev_ids, ta_code):
            doldurulan.append(f"Malı Teslim Alan={ta_code}")

    if not sv_code:
        uyarilar.append("Sipariş Veren müşteri kodu boş olduğu için SAP kalem doğrulamasını bekletebilir!")

    # SAS Numarası ve SAS Tarihi
    sas_no = str(talep.get("sasNo") or "").strip()
    sas_tarihi = str(talep.get("sasTarihi") or "").strip()
    istenen_teslim = str(talep.get("istenenTeslimTarihi") or "").strip()

    if set_if_exists(session, [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/txtVBKD-BSTKD",
        "wnd[0]/usr/txtVBKD-BSTKD",
    ], sas_no):
        doldurulan.append(f"SAS No={sas_no}")

    if set_if_exists(session, [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtVBKD-BSTDK",
        "wnd[0]/usr/ctxtVBKD-BSTDK",
    ], sas_tarihi):
        doldurulan.append(f"SAS Trh={sas_tarihi}")

    # İstenen Teslim Tarihi (Satış sekmesi üst çerçevesi)
    set_if_exists(session, [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtRV45A-PRGBZ",
    ], "D")
    if set_if_exists(session, [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtRV45A-KETDAT",
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\02/ssubSUBSCREEN_BODY:SAPMV45A:4401/ssubHEADER_FRAME:SAPMV45A:4440/ctxtRV45A-KETDAT",
    ], istenen_teslim):
        doldurulan.append(f"İstenen Tsl.Trh={istenen_teslim}")

    # ÖNEMLİ: Muhatap ve başlık tarihlerinden sonra ENTER bas ki SAP müşteriyi doğrulayıp alanları açsın!
    if sv_code:
        session.findById("wnd[0]").sendVKey(0)
        time.sleep(0.6)
        dismiss_popups_and_atp(session, max_steps=10)

    # ── AŞAMA 3: Satış Sekmesi Diğer Başlık Alanları (PB, Sözleşme Başlangıç/Bitiş, Ödeme Koşulu, Incoterms) ──
    para_birimi = str(talep.get("paraBirimi") or "").strip()
    soz_bas = str(talep.get("sozlesmeBaslangic") or "").strip()
    soz_bit = str(talep.get("sozlesmeBitis") or "").strip()
    odeme_kosulu = str(talep.get("odemeKosulu") or "").strip()
    inco1 = str(talep.get("inco1") or "").strip()
    inco2 = str(talep.get("inco2") or "").strip()

    if set_if_exists(session, [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtVBAK-WAERK",
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBAK-WAERK",
    ], para_birimi):
        doldurulan.append(f"Belge PB={para_birimi}")

    if set_if_exists(session, [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-GUEBG",
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVEDA-VBEGDAT",
    ], soz_bas):
        doldurulan.append(f"Söz.Bşl={soz_bas}")

    if set_if_exists(session, [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-GUEEN",
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVEDA-VENDDAT",
    ], soz_bit):
        doldurulan.append(f"Söz.Bitiş={soz_bit}")

    if odeme_kosulu and set_if_exists(session, [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-ZTERM",
    ], odeme_kosulu):
        doldurulan.append(f"Ödeme Koşulu={odeme_kosulu}")

    if inco1:
        set_if_exists(session, [
            "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtVBKD-INCO1",
        ], inco1)
    if inco2:
        if set_if_exists(session, [
            "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/txtVBKD-INCO2",
            "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/txtVBKD-INCO2_L",
        ], inco2):
            doldurulan.append(f"Incoterms={inco1} {inco2}".strip())

    if sv_code and any([para_birimi, soz_bas, soz_bit, odeme_kosulu, inco1, inco2]):
        session.findById("wnd[0]").sendVKey(0)
        time.sleep(0.4)
        dismiss_popups_and_atp(session, max_steps=8)

    # ── AŞAMA 4: Tüm Kalemlerin Dinamik Kolon Haritasıyla Yazılması ──
    tbl, tbl_id, col_map = find_item_table(session)
    if tbl is None:
        uyarilar.append("VA01 kalem tablosuna ulaşılamadı (SAP zorunlu başlık/muhatap alanı bekliyor olabilir).")
        return {
            "ok": True,
            "kaydedildiMi": False,
            "mesaj": "VA01 başlık alanları yazıldı, lütfen SAP ekranında eksik muhatabı seçip devam edin.",
            "doldurulan": doldurulan,
            "uyarilar": uyarilar,
            "sapDurumCubugu": status_text(session),
        }

    visible_rows = max(1, int(getattr(tbl, "VisibleRowCount", 10)))
    uretim_yeri_hdr = str(talep.get("uretimYeri") or "").strip()
    if uretim_yeri_hdr == "9999":
        uretim_yeri_hdr = ""  # 9999 iş akışı yer tutucusudur; SAP malzeme kartından gerçek ÜY'yi (örn: 5100) alır
    zorla_kalem_tipi = bool(talep.get("zorlaKalemTipi", False))

    kalem_yazilan = 0

    # 1. Geçiş: Malzeme, Miktar, ÖB, 1.Tarih (Teslim), Üst Kalem, ÜY, Sevkiyat Noktası
    for idx, k in enumerate(kalemler):
        tbl, tbl_id, col_map = find_item_table(session)
        if tbl is None:
            break
        if idx >= visible_rows:
            try:
                tbl.verticalScrollbar.position = idx - visible_rows + 2
                time.sleep(0.2)
                tbl, tbl_id, col_map = find_item_table(session)
                row_in_page = visible_rows - 2
            except Exception:
                break
        else:
            row_in_page = idx

        malz = str(k.get("malzemeNo") or "").strip()
        miktar = format_sap_dec(str(k.get("miktar") or ""))
        birim = str(k.get("olcuBirimi") or "").strip()
        teslim_trh = str(k.get("teslimTarihi") or "").strip()
        ust_kalem = str(k.get("ustKalem") or "").strip()
        kalem_tipi = str(k.get("kalemTipi") or "").strip()
        sevkiyat_nok = str(k.get("sevkiyatNoktasi") or "").strip()
        if sevkiyat_nok == "9999":
            sevkiyat_nok = ""

        ok_m = set_table_cell(session, col_map, ["RV45A-MABNR"], row_in_page, malz)
        set_table_cell(session, col_map, ["RV45A-KWMENG"], row_in_page, miktar)
        set_table_cell(session, col_map, ["VBAP-VRKME"], row_in_page, birim)
        if teslim_trh:
            set_table_cell(session, col_map, ["RV45A-ETDAT"], row_in_page, teslim_trh)
        if ust_kalem:
            set_table_cell(session, col_map, ["VBAP-UEPOS"], row_in_page, ust_kalem)
        if zorla_kalem_tipi and kalem_tipi:
            set_table_cell(session, col_map, ["VBAP-PSTYV"], row_in_page, kalem_tipi)
        elif ust_kalem and kalem_tipi in ("Y21F", "TANN"):
            set_table_cell(session, col_map, ["VBAP-PSTYV"], row_in_page, kalem_tipi)
        if uretim_yeri_hdr:
            set_table_cell(session, col_map, ["VBAP-WERKS"], row_in_page, uretim_yeri_hdr)
        if sevkiyat_nok:
            set_table_cell(session, col_map, ["VBAP-VSTEL"], row_in_page, sevkiyat_nok)

        if ok_m:
            kalem_yazilan += 1

    # Malzemeleri doğrulatmak ve ATP (Kullanılabilirlik kontrolü) ekranlarını geçmek için Enter!
    if sv_code and kalem_yazilan > 0:
        session.findById("wnd[0]").sendVKey(0)
        time.sleep(0.6)
        dismiss_popups_and_atp(session, max_steps=max(20, len(kalemler) * 4))

        # En başa kaydır ve 2. Geçişte Tutar (Birim Fiyat), PB ve PYP Öğesi alanlarını doldur!
        tbl, tbl_id, col_map = find_item_table(session)
        if tbl is not None:
            try:
                if int(getattr(tbl.verticalScrollbar, "position", 0)) > 0:
                    tbl.verticalScrollbar.position = 0
                    time.sleep(0.2)
            except Exception:
                pass

            fiyat_yazilan = 0
            pyp_yazilan = 0
            for idx, k in enumerate(kalemler):
                tbl, tbl_id, col_map = find_item_table(session)
                if tbl is None:
                    break
                if idx >= visible_rows:
                    try:
                        tbl.verticalScrollbar.position = idx - visible_rows + 2
                        time.sleep(0.2)
                        tbl, tbl_id, col_map = find_item_table(session)
                        row_in_page = visible_rows - 2
                    except Exception:
                        break
                else:
                    row_in_page = idx

                fiyat = format_sap_dec(str(k.get("birimFiyat") or ""))
                pb = str(k.get("paraBirimi") or "").strip()
                pyp = str(k.get("pypOgesi") or "").strip()

                if fiyat and set_table_cell(session, col_map, ["KOMV-KBETR", "RV45A-KBETR"], row_in_page, fiyat):
                    fiyat_yazilan += 1
                if pb:
                    set_table_cell(session, col_map, ["KOMV-WAERS", "KOMV-KOEI1", "VBAP-WAERK"], row_in_page, pb)
                if pyp and set_table_cell(session, col_map, ["VBAP-PS_PSP_PNR", "COBL-PS_POSID"], row_in_page, pyp):
                    pyp_yazilan += 1

            if fiyat_yazilan > 0 or pyp_yazilan > 0:
                session.findById("wnd[0]").sendVKey(0)
                time.sleep(0.5)
                dismiss_popups_and_atp(session, max_steps=15)
                doldurulan.append(f"Birim Fiyat={fiyat_yazilan}, PYP={pyp_yazilan}")

            # Tekrar 1. satıra kaydır ki kullanıcı tüm tabloyu baştan görsün
            try:
                tbl, _, _ = find_item_table(session)
                if tbl is not None and int(getattr(tbl.verticalScrollbar, "position", 0)) > 0:
                    tbl.verticalScrollbar.position = 0
            except Exception:
                pass

    doldurulan.append(f"Kalem={kalem_yazilan}/{len(kalemler)}")

    # DİKKAT: KESİNLİKLE KAYDETME (Ctrl+S / Save YOK)!
    return {
        "ok": True,
        "kaydedildiMi": False,
        "mesaj": "VA01 başlık, muhatap, tarih, fiyat ve kalem alanları dolduruldu (KAYDEDİLMEDİ). Lütfen SAP ekranından kontrol edip kendiniz kaydedin.",
        "doldurulan": doldurulan,
        "uyarilar": uyarilar,
        "sapDurumCubugu": status_text(session),
    }


def main() -> None:
    try:
        payload = read_stdin_json()
        session = get_or_open_session(payload)
        res = fill_va01(session, payload)
        log("RESULT_JSON:" + json.dumps(res, ensure_ascii=False))
    except SystemExit:
        raise
    except Exception as e:
        user_error("fatal", str(e))
        sys.exit(1)


if __name__ == "__main__":
    main()
