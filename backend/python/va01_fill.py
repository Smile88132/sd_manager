# -*- coding: utf-8 -*-
"""
UGES SD Kontrol — SAP GUI VA01 Otomatik Açma ve Alan Doldurma (KAYDETMEDEN BIRAKIR!)
====================================================================================
- Kimlik bilgileri yalnızca stdin JSON üzerinden okunur; diske veya loga YAZILMAZ.
- Açık SAP GUI oturumu varsa ve reuseOpenSession=true ise şifresiz doğrudan bağlanır.
- Yoksa saplogon.exe üzerinden SAP_CONNECTION ('KKP CANLI') açıp giriş yapar.
- /nVA01 işlemini açar, başlık ve kalem alanlarını doldurur.
- KESİNLİKLE KAYDETMEZ (Ctrl+S / Save çağrılmaz); ekranı kullanıcının önünde açık bırakır.
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


def set_if_exists(session, ids: list[str], val: str) -> bool:
    if val is None or str(val).strip() == "":
        return False
    v = str(val).strip()
    for fid in ids:
        try:
            obj = session.findById(fid)
            obj.text = v
            return True
        except Exception:
            continue
    return False


def find_open_logged_in_session(application):
    try:
        for c_idx in range(application.Children.Count):
            conn = application.Children(c_idx)
            for s_idx in range(conn.Children.Count):
                sess = conn.Children(s_idx)
                try:
                    # Giriş ekranında değilse aktif bir oturumdur
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

    # 1) Açık oturum varsa yakala
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

    # 2) Açık oturum yoksa kullanıcı adı/şifre ile yeni bağlantı aç
    if not user or not password:
        user_error("auth", "Açık bir SAP GUI oturumu bulunamadı. Lütfen SAP kullanıcı adı ve şifrenizi girin (veya önce SAP Logon'dan giriş yapın).")
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
        user_error("gui", f"'{connection_name}' bağlantısı açılamadı (SAP Scripting açık olmalı): {e}")
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

    # Giriş sonucunu bekle
    deadline = time.time() + 25.0
    while time.time() < deadline:
        # Çoklu oturum penceresi
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

    # Bilgi popup'ı varsa kapat
    try:
        session.findById("wnd[1]/tbar[0]/btn[0]").press()
    except Exception:
        pass

    return session


def fill_va01(session, payload: dict) -> dict:
    talep = payload.get("talep") or {}
    kalemler = payload.get("kalemler") or []
    doldurulan = []
    uyarilar = []

    try:
        session.findById("wnd[0]").maximize()
    except Exception:
        pass

    # 1) /nVA01 Başlangıç Ekranı (SAPMV45A - 0101)
    session.findById("wnd[0]/tbar[0]/okcd").text = "/nVA01"
    session.findById("wnd[0]").sendVKey(0)
    time.sleep(0.5)

    siparis_turu = talep.get("siparisTuru") or "ZSTD"
    satis_org = talep.get("satisOrganizasyonu") or "9990"
    dagitim = talep.get("dagitimKanali") or "10"
    bolum = talep.get("bolum") or "10"
    satis_burosu = talep.get("satisBurosu") or ""
    satis_grubu = talep.get("satisGrubu") or ""

    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-AUART"], siparis_turu):
        doldurulan.append(f"Sipariş Türü={siparis_turu}")
    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VKORG"], satis_org):
        doldurulan.append(f"Satış Org.={satis_org}")
    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VTWEG"], dagitim):
        doldurulan.append(f"Dağıtım Kanalı={dagitim}")
    if set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-SPART"], bolum):
        doldurulan.append(f"Bölüm={bolum}")
    set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VKBUR"], satis_burosu)
    set_if_exists(session, ["wnd[0]/usr/ctxtVBAK-VKGRP"], satis_grubu)

    # Enter -> VA01 Genel Bakış ekranına geç
    session.findById("wnd[0]").sendVKey(0)
    time.sleep(0.7)

    sbar1 = status_text(session)
    if sbar1:
        log(f"INFO: VA01 ilk ekran mesajı: {sbar1}")

    # 2) Başlık & Muhatap Alanları
    siparis_veren = talep.get("siparisVeren") or ""
    teslim_alan = talep.get("maliTeslimAlan") or siparis_veren
    sas_no = talep.get("sasNo") or talep.get("aselsanSozlesmeNo") or talep.get("belgeNo") or ""
    sas_tarihi = talep.get("sasTarihi") or ""
    istenen_teslim = talep.get("ilkTeslimTarihi") or ""

    if set_if_exists(session, [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/subPART-SUB:SAPMV45A:4701/ctxtKUAGV-KUNNR",
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtKUAGV-KUNNR"
    ], siparis_veren):
        doldurulan.append(f"Sipariş Veren={siparis_veren}")
    elif not siparis_veren:
        uyarilar.append("Sipariş Veren muhatap kodu raporda boş olduğu için VA01 ekranında sizin girmeniz bekleniyor.")

    set_if_exists(session, [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/subPART-SUB:SAPMV45A:4701/ctxtKUWEV-KUNNR",
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtKUWEV-KUNNR"
    ], teslim_alan)

    if set_if_exists(session, [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/txtVBKD-BSTKD",
        "wnd[0]/usr/txtVBKD-BSTKD"
    ], sas_no):
        doldurulan.append(f"SAS/Sözleşme No={sas_no}")

    set_if_exists(session, [
        "wnd[0]/usr/subSUBSCREEN_HEADER:SAPMV45A:4021/ctxtVBKD-BSTDK",
        "wnd[0]/usr/ctxtVBKD-BSTDK"
    ], sas_tarihi)

    set_if_exists(session, [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/ssubHEADER_FRAME:SAPMV45A:4440/ctxtRV45A-KETDAT",
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\02/ssubSUBSCREEN_BODY:SAPMV45A:4401/ssubHEADER_FRAME:SAPMV45A:4440/ctxtRV45A-KETDAT"
    ], istenen_teslim)

    # 3) Kalem Tablosu (T\01 Satış veya T\02 Kalem Genel Bakışı)
    table_ids = [
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\01/ssubSUBSCREEN_BODY:SAPMV45A:4400/subSUBSCREEN_TC:SAPMV45A:4900/tblSAPMV45ATCTRL_U_ERF_AUFTRAG",
        "wnd[0]/usr/tabsTAXI_TABSTRIP_OVERVIEW/tabpT\\02/ssubSUBSCREEN_BODY:SAPMV45A:4401/subSUBSCREEN_TC:SAPMV45A:4900/tblSAPMV45ATCTRL_U_ERF_AUFTRAG",
    ]

    tbl = None
    tbl_prefix = ""
    for tid in table_ids:
        try:
            tbl = session.findById(tid)
            tbl_prefix = tid
            break
        except Exception:
            continue

    kalem_yazilan = 0
    if tbl is not None:
        visible_rows = max(1, int(getattr(tbl, "VisibleRowCount", 10)))
        for idx, k in enumerate(kalemler):
            row_in_page = idx if idx < visible_rows else visible_rows - 1
            if idx >= visible_rows:
                try:
                    tbl.verticalScrollbar.position = idx - visible_rows + 1
                    tbl = session.findById(tbl_prefix)
                except Exception:
                    break

            malz = str(k.get("malzemeNo") or "").strip()
            miktar = str(k.get("miktar") or "").strip()
            birim = str(k.get("olcuBirimi") or "").strip()
            kalem_tipi = str(k.get("kalemTipi") or "").strip()
            uretim_yeri = str(talep.get("uretimYeri") or "").strip()
            teslim_trh = str(k.get("teslimTarihi") or "").strip()
            pyp = str(k.get("pypOgesi") or "").strip()
            birim_fiyat = str(k.get("birimFiyat") or "").strip()

            ok_m = set_if_exists(session, [f"{tbl_prefix}/ctxtRV45A-MABNR[1,{row_in_page}]"], malz)
            set_if_exists(session, [f"{tbl_prefix}/txtRV45A-KWMENG[2,{row_in_page}]", f"{tbl_prefix}/txtRV45A-KWMENG[3,{row_in_page}]"], miktar)
            set_if_exists(session, [f"{tbl_prefix}/ctxtVBAP-VRKME[3,{row_in_page}]", f"{tbl_prefix}/ctxtVBAP-VRKME[4,{row_in_page}]"], birim)
            set_if_exists(session, [f"{tbl_prefix}/ctxtVBAP-PSTYV[5,{row_in_page}]", f"{tbl_prefix}/ctxtVBAP-PSTYV[6,{row_in_page}]"], kalem_tipi)
            set_if_exists(session, [f"{tbl_prefix}/ctxtVBAP-WERKS[7,{row_in_page}]", f"{tbl_prefix}/ctxtVBAP-WERKS[8,{row_in_page}]"], uretim_yeri)
            set_if_exists(session, [f"{tbl_prefix}/ctxtRV45A-ETDAT[9,{row_in_page}]", f"{tbl_prefix}/ctxtRV45A-ETDAT[10,{row_in_page}]"], teslim_trh)
            set_if_exists(session, [f"{tbl_prefix}/txtKOMV-KBETR[11,{row_in_page}]"], birim_fiyat)
            set_if_exists(session, [f"{tbl_prefix}/ctxtVBAP-PS_PSP_PNR[14,{row_in_page}]"], pyp)
            if ok_m:
                kalem_yazilan += 1
        doldurulan.append(f"Kalem Satırı={kalem_yazilan}/{len(kalemler)}")
    else:
        uyarilar.append("VA01 kalem tablosuna henüz geçilemedi (SAP zorunlu başlık/muhatap alanı bekliyor olabilir). Başlık alanları ekrana yazıldı.")

    # DİKKAT: KESİNLİKLE KAYDETME (Ctrl+S / Save YOK)! Kullanıcı kontrol edip kendisi kaydedecek.
    return {
        "ok": True,
        "kaydedildiMi": False,
        "mesaj": "VA01 ekranı açıldı ve alanlar dolduruldu. KAYDEDİLMEDİ — lütfen SAP ekranından kontrol edip kendiniz kaydedin.",
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
