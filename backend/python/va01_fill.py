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
    0) wnd[2] ikincil bilgi/uyarı pencerelerini,
    1) wnd[1] Bilgi / Uyarı pencerelerini ('Fatura termini değişikliği...' vb.),
    2) wnd[0] Kullanılabilirlik Kontrolü (ATP - SAPLATPC / 'Kullanılabilirlik kontrolü') ekranlarını
    otomatik onaylayıp VA01 Genel Bakış (SAPMV45A) ekranına geri döner.
    """
    notlar = []
    for _ in range(max_steps):
        progressed = False

        # 0) wnd[2] ve wnd[1] popup kontrolü (önce en üstteki modal pencere!)
        for wnd_name in ("wnd[2]", "wnd[1]"):
            try:
                w = session.findById(wnd_name)
                title_w = str(getattr(w, "Text", "") or "")
                for btn_id in (
                    f"{wnd_name}/tbar[0]/btn[0]",
                    f"{wnd_name}/usr/btnSPOP-OPTION1",
                    f"{wnd_name}/usr/btnBUTTON_1",
                ):
                    try:
                        session.findById(btn_id).press()
                        progressed = True
                        if title_w:
                            notlar.append(f"Popup geçildi: {title_w}")
                        time.sleep(0.25)
                        break
                    except Exception:
                        continue
                if not progressed:
                    w.sendVKey(0)
                    progressed = True
                    time.sleep(0.25)
                break
            except Exception:
                continue
        if progressed:
            continue

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


def _tr_upper(s: str) -> str:
    return s.replace("i", "İ").replace("ı", "I").upper()


def _norm_compare(s: str) -> str:
    t = _tr_upper(str(s or ""))
    for src, dst in (("İ", "I"), ("Ş", "S"), ("Ğ", "G"), ("Ü", "U"), ("Ö", "O"), ("Ç", "C")):
        t = t.replace(src, dst)
    return re.sub(r"[^A-Z0-9]+", " ", t).strip()


def _collect_controls(root, out: list | None = None, depth: int = 0) -> list:
    if out is None:
        out = []
    if depth > 9:
        return out
    try:
        cnt = int(root.Children.Count)
    except Exception:
        return out
    for i in range(min(cnt, 250)):
        try:
            ch = root.Children(i)
            out.append(ch)
            _collect_controls(ch, out, depth + 1)
        except Exception:
            continue
    return out


def _close_f4_windows(session) -> None:
    """F4 sırasında açık kalmış olabilecek wnd[2] ve wnd[1] pencerelerini güvenli kapatır."""
    for wname in ("wnd[2]", "wnd[1]"):
        for _ in range(2):
            try:
                w = session.findById(wname)
                try:
                    session.findById(f"{wname}/tbar[0]/btn[12]").press()
                except Exception:
                    try:
                        w.close()
                    except Exception:
                        w.sendVKey(12)
                time.sleep(0.2)
            except Exception:
                break


def _build_ad_candidates(customer_name: str, max_len: int = 25) -> list[str]:
    """
    SAP F4 'A: Müşteriler (genel)' -> 'Ad' (MC_NAME1, CHAR 25) alanı için
    MaxLength sınırını (25 karakter) ASLA aşmayan kademeli arama adayları üretir.
    """
    max_len = max(10, min(int(max_len or 25), 35))
    usable = max_len - 1  # sonuna '*' ekleneceği için en fazla max_len - 1 karakter

    clean = re.sub(r"[#*?\"']+", " ", str(customer_name or ""))
    clean = re.sub(r"\s+", " ", clean).strip(" ,;-/")
    if not clean:
        return []

    stop_words = {
        "VE", "SAN", "SAN.", "TIC", "TIC.", "TİC", "TİC.", "AS", "A.S", "A.S.", "A.Ş.", "A.Ş",
        "LTD", "LTD.", "STI", "ŞTİ", "ŞTİ.", "INC", "INC.", "LLC", "SDN", "BHD", "S.R.O", "S.R.O.", "TC", "T.C", "T.C.",
    }
    raw_words = [w.strip(".,;-/") for w in clean.split() if w.strip(".,;-/")]
    sig_words = [w for w in raw_words if _tr_upper(w) not in stop_words and len(w) >= 2]
    if not sig_words:
        sig_words = [w for w in raw_words if len(w) >= 2]

    candidates: list[str] = []

    def add(pat: str) -> None:
        pat = pat.strip()
        if not pat or pat == "*" or len(pat) > max_len:
            return
        if pat not in candidates:
            candidates.append(pat)

    # 1) Doğrudan 'Ad' ön eki: örn. 'Elegancia Electromechan*' (<= 25 karakter, KKP CANLI ekranındaki birebir davranış)
    prefix = clean[:usable].rstrip()
    if prefix:
        add(f"{prefix}*")
        add(f"{_tr_upper(prefix)}*")
        if prefix.upper() != _tr_upper(prefix):
            add(f"{prefix.upper()}*")

    # 2) İlk 2 anlamlı kelime ile 'Ad' araması: örn. 'Elegancia Electromech*' veya 'ROKETSAN Roket*'
    if len(sig_words) >= 2:
        two_w = f"{sig_words[0]} {sig_words[1]}"[:usable].rstrip()
        add(f"{two_w}*")
        two_wild = f"*{sig_words[0]}*{sig_words[1]}*"
        if len(two_wild) <= max_len:
            add(two_wild)

    # 3) İlk ayırt edici tek kelime ile 'Ad' araması: örn. 'Elegancia*' / '*ELEGANCIA*'
    if sig_words:
        w0 = sig_words[0][: max(3, usable - 1)]
        add(f"{w0}*")
        if len(w0) + 2 <= max_len:
            add(f"*{w0}*")
        w0_tr = _tr_upper(w0)
        if len(w0_tr) + 2 <= max_len:
            add(f"*{w0_tr}*")

    return candidates


def _find_ad_input_in_f4(w1):
    """
    wnd[1] ('Değer alanını sınırlandır') içindeki tüm G_SELFLD_TAB-LOW alanlarını tarar,
    diğer filtre alanlarını temizler ve tam olarak 'Ad' ('Ad 1' / 'Name' / 'Name 1') satırındaki
    input kontrolünü döner.
    NOT: SAP GUI step-loop koordinatları [col, row] şeklindedir (örn: txtG_SELFLD_TAB-LOW[24,3])!
    """
    ctrls = _collect_controls(w1)
    low_fields: list[tuple[int, int, object]] = []
    labels_by_row: dict[int, list[str]] = {}

    for c in ctrls:
        try:
            cid = str(getattr(c, "Id", "") or "")
            cname = str(getattr(c, "Name", "") or "").upper()
            ctype = str(getattr(c, "Type", "") or "")
            m = re.search(r"\[(\d+),\s*(\d+)\]$", cid)
            if not m:
                continue
            col_idx, row_idx = int(m.group(1)), int(m.group(2))

            if "G_SELFLD_TAB-LOW" in cname:
                low_fields.append((row_idx, col_idx, c))
            elif ctype == "GuiLabel":
                txt = str(getattr(c, "Text", "") or "").strip().rstrip(":")
                if txt:
                    labels_by_row.setdefault(row_idx, []).append(txt)
        except Exception:
            continue

    if not low_fields:
        return None

    low_fields.sort(key=lambda x: (x[0], x[1]))

    # Önce tüm arama kutularını temizle ki önceki aramadan kalan filtre ('Müşteri', 'Kent' vb.) aramayı bozmasın!
    for _, _, fobj in low_fields:
        try:
            if getattr(fobj, "Changeable", True) and str(getattr(fobj, "Text", "") or ""):
                fobj.text = ""
        except Exception:
            pass

    # 1. Öncelik: Etiketi tam olarak 'Ad' / 'Ad 1' / 'Name' / 'Name 1' olan satır
    exact_ad_labels = {"ad", "ad 1", "name", "name 1", "müşteri adı", "firma adı"}
    for row_idx, _, fobj in low_fields:
        row_lbls = [lbl.lower() for lbl in labels_by_row.get(row_idx, [])]
        if any(lbl in exact_ad_labels for lbl in row_lbls):
            return fobj

    # 2. Öncelik: Etiketi 'ad' ile başlayan (fakat 'arama' veya 'adres' olmayan) satır
    for row_idx, _, fobj in low_fields:
        row_lbls = [lbl.lower() for lbl in labels_by_row.get(row_idx, [])]
        if any(lbl.startswith("ad") and not lbl.startswith("ara") and not lbl.startswith("adr") for lbl in row_lbls):
            return fobj

    # 3. Öncelik: Standart DEBIA ('A: Müşteriler (genel)') diziliminde 4. satır (row_idx == 3 -> [24,3]) 'Ad' alanıdır
    for row_idx, _, fobj in low_fields:
        if row_idx == 3:
            return fobj

    return low_fields[0][2]


def _select_best_f4_hit(session, customer_name: str) -> str:
    """
    F4 araması sonrası wnd[1] üzerinde açılan sonuç listesinden (hem Klasik ABAP Listesi lbl[col,row]
    hem de ALV Grid / GuiGridView) müşteriye en uygun satırı seçer ve müşteri kodunu döner.
    """
    target_norm = _norm_compare(customer_name)
    target_tokens = [t for t in target_norm.split() if len(t) >= 2]

    def score_text(candidate_txt: str) -> int:
        cn = _norm_compare(candidate_txt)
        if not cn:
            return 0
        sc = 0
        if target_norm and cn.startswith(target_norm[:20]):
            sc += 100
        elif target_norm[:12] and target_norm[:12] in cn:
            sc += 60
        for tok in target_tokens:
            if tok in cn:
                sc += 15
        return sc

    try:
        w1 = session.findById("wnd[1]")
    except Exception:
        # wnd[1] kendiliğinden kapandıysa (tek sonuç otomatik aktarıldıysa)
        return "__AUTO_CLOSED__"

    ctrls = _collect_controls(w1)

    # Durum A: Hâlâ seçim ekranındaysa (G_SELFLD_TAB-LOW varsa) sonuç listesi açılmamıştır (0 kayıt)
    for c in ctrls:
        try:
            if "G_SELFLD_TAB-LOW" in str(getattr(c, "Name", "") or "").upper():
                return ""
        except Exception:
            continue

    # Durum B: ALV Grid (Control modu F4 listesi)
    for c in ctrls:
        try:
            if str(getattr(c, "Type", "") or "") == "GuiGridView":
                row_count = int(getattr(c, "RowCount", 0))
                if row_count <= 0:
                    return ""
                raw_cols = str(getattr(c, "ColumnOrder", "") or "")
                cols = [col.strip() for col in raw_cols.replace(";", "\t").split("\t") if col.strip()]
                best_row = 0
                best_score = -1
                best_kunnr = ""
                for r in range(min(row_count, 40)):
                    vals = []
                    row_kunnr = ""
                    for col in cols:
                        try:
                            v = str(c.GetCellValue(r, col) or "").strip()
                        except Exception:
                            v = ""
                        if v:
                            vals.append(v)
                            if ("KUNNR" in col.upper() or v.isdigit()) and 4 <= len(v) <= 10 and v.isdigit():
                                row_kunnr = v
                    sc = score_text(" ".join(vals))
                    if sc > best_score:
                        best_score = sc
                        best_row = r
                        best_kunnr = row_kunnr
                c.currentCellRow = best_row
                c.selectedRows = str(best_row)
                try:
                    c.doubleClickCurrentCell()
                except Exception:
                    session.findById("wnd[1]/tbar[0]/btn[0]").press()
                time.sleep(0.35)
                return best_kunnr or "__SELECTED__"
        except Exception:
            continue

    # Durum C: Klasik ABAP Hit Listesi (KKP CANLI Ekran Görüntüsü: wnd[1]/usr/lbl[col, row])
    rows_map: dict[int, list[tuple[int, object, str]]] = {}
    for c in ctrls:
        try:
            if str(getattr(c, "Type", "") or "") != "GuiLabel":
                continue
            cid = str(getattr(c, "Id", "") or "")
            m = re.search(r"/lbl\[(\d+),\s*(\d+)\]$", cid)
            if not m:
                continue
            col_idx, row_idx = int(m.group(1)), int(m.group(2))
            txt = str(getattr(c, "Text", "") or "").strip()
            if txt:
                rows_map.setdefault(row_idx, []).append((col_idx, c, txt))
        except Exception:
            continue

    if rows_map:
        sorted_rows = sorted(rows_map.keys())
        header_row = -1
        kunnr_col = -1
        for r_idx in sorted_rows[:4]:
            items = sorted(rows_map[r_idx], key=lambda x: x[0])
            joined_low = " ".join(x[2].lower() for x in items)
            if "müşteri" in joined_low or "ad 1" in joined_low or "arama" in joined_low or "customer" in joined_low:
                header_row = r_idx
                for col_i, _, t_i in items:
                    if "müşteri" in t_i.lower() or "customer" in t_i.lower():
                        kunnr_col = col_i
                break

        data_rows: list[tuple[int, list[tuple[int, object, str]]]] = []
        for r_idx in sorted_rows:
            if r_idx <= header_row:
                continue
            items = sorted(rows_map[r_idx], key=lambda x: x[0])
            joined = " ".join(x[2] for x in items).strip()
            if not joined or set(joined) <= {"-", "_", "=", " "}:
                continue
            data_rows.append((r_idx, items))

        if data_rows:
            best_items = data_rows[0][1]
            best_score = -1
            for _, items in data_rows:
                sc = score_text(" ".join(x[2] for x in items))
                if sc > best_score:
                    best_score = sc
                    best_items = items

            # Müşteri numarasını satırdaki 'Müşteri' kolonundan (veya 4-10 haneli sayısal kolondan) doğrudan oku
            extracted_kunnr = ""
            for col_i, _, t_i in reversed(best_items):
                t_clean = t_i.strip()
                if t_clean.isdigit() and 4 <= len(t_clean) <= 10:
                    if kunnr_col < 0 or abs(col_i - kunnr_col) <= 6:
                        extracted_kunnr = t_clean
                        break
            if not extracted_kunnr:
                for _, _, t_i in reversed(best_items):
                    if t_i.strip().isdigit() and 5 <= len(t_i.strip()) <= 10:
                        extracted_kunnr = t_i.strip()
                        break

            # Satırı odakla ve F2 (sendVKey(2) = Seç / Çift Tıkla) gönder
            focus_lbl = best_items[0][1]
            try:
                focus_lbl.setFocus()
                try:
                    focus_lbl.caretPosition = 0
                except Exception:
                    pass
                session.findById("wnd[1]").sendVKey(2)
                time.sleep(0.35)
            except Exception:
                pass

            # Hâlâ wnd[1] açıksa yeşil tik (btn[0]) dene
            try:
                session.findById("wnd[1]")
                session.findById("wnd[1]/tbar[0]/btn[0]").press()
                time.sleep(0.3)
            except Exception:
                pass

            return extracted_kunnr or "__SELECTED__"

    return ""


def resolve_customer_via_f4(session, field_id: str, customer_name: str) -> str:
    """
    Müşteri kodu yerine müşteri adı verilmişse (örn: 'Elegancia Electromechanical'),
    ilgili muhatap alanında F4 açar, 'A: Müşteriler (genel)' sekmesindeki 'Ad' ([24,3] / MC_NAME1)
    alanından arayıp müşteri kodunu seçer. Hata veya eşleşmeme durumunda pencereleri temiz kapatır, ASLA takılı kalmaz.
    """
    candidates = _build_ad_candidates(customer_name, max_len=25)
    if not candidates:
        return ""

    try:
        # Açık kalmış F4 penceresi varsa önce temizle
        _close_f4_windows(session)

        fld = session.findById(field_id)
        if getattr(fld, "Changeable", True):
            fld.text = ""
        fld.setFocus()
        session.findById("wnd[0]").sendVKey(4)  # F4
        time.sleep(0.5)

        w1 = session.findById("wnd[1]")

        # Eğer F4 doğrudan eski bir sonuç listesine düştüyse seçim kriterlerini (Shift+F7 / VKey 19) aç
        if _find_ad_input_in_f4(w1) is None:
            try:
                w1.sendVKey(19)
                time.sleep(0.35)
                w1 = session.findById("wnd[1]")
            except Exception:
                pass

        # 'A: Müşteriler (genel)' sekmesini bul ve seç
        for c in _collect_controls(w1):
            try:
                if str(getattr(c, "Type", "") or "") == "GuiTab":
                    ttxt = str(getattr(c, "Text", "") or "").strip()
                    tl = ttxt.lower()
                    if ttxt.startswith("A:") or "genel" in tl or "general" in tl:
                        c.select()
                        time.sleep(0.25)
                        break
            except Exception:
                continue
        else:
            for tab_id in (
                "wnd[1]/usr/tabsG_SELONETABSTRIP/tabpTAB001",
                "wnd[1]/usr/tabsG_SELONETABSTRIP/tabpTAB01",
            ):
                try:
                    session.findById(tab_id).select()
                    time.sleep(0.25)
                    break
                except Exception:
                    continue

        # Kademeli 'Ad' adaylarını sırayla dene (ilk eşleşmede hemen seç ve çık)
        for cand in candidates:
            try:
                w1 = session.findById("wnd[1]")
            except Exception:
                break

            ad_input = _find_ad_input_in_f4(w1)
            if ad_input is None:
                log(f"WARN: F4 penceresinde 'Ad' alanı bulunamadı ({customer_name}).")
                break

            max_len = int(getattr(ad_input, "MaxLength", 25) or 25)
            val_to_write = cand[:max_len] if max_len > 0 else cand[:25]

            try:
                ad_input.setFocus()
                ad_input.text = val_to_write
            except Exception as ex_w:
                log(f"WARN: 'Ad' alanına '{val_to_write}' yazılamadı: {ex_w}")
                continue

            # 'Ad' alanına gerçekten yazıldığından emin ol — boşsa ASLA Enter basma (600 rastgele kaydı listelemesin!)
            written_now = str(getattr(ad_input, "Text", "") or "").strip()
            if not written_now:
                continue

            # Aramayı çalıştır (Enter / btn[0])
            try:
                session.findById("wnd[1]/tbar[0]/btn[0]").press()
            except Exception:
                w1.sendVKey(0)
            time.sleep(0.55)

            # Eğer SAP 'Bu seçim için değer bulunamadı' için wnd[2] Bilgi popup'ı açtıysa kapat ve sonraki adayı dene
            no_hit = False
            try:
                w2 = session.findById("wnd[2]")
                try:
                    session.findById("wnd[2]/tbar[0]/btn[0]").press()
                except Exception:
                    w2.sendVKey(0)
                time.sleep(0.25)
                no_hit = True
            except Exception:
                pass

            sbar_low = status_text(session).lower()
            if no_hit or any(k in sbar_low for k in ("bulunamadı", "no values", "seçim için değer yok", "no entries")):
                continue

            picked = _select_best_f4_hit(session, customer_name)
            if not picked:
                continue

            # Eğer hala F4 penceresi açıksa ve elimizde doğrudan okunan sayısal müşteri kodu varsa kapatıp direkt yaz
            _close_f4_windows(session)

            resolved = str(session.findById(field_id).text or "").strip()
            if resolved and resolved != customer_name:
                log(f"INFO: F4 'Ad' ({val_to_write}) -> {resolved}")
                return resolved

            if picked not in ("", "__AUTO_CLOSED__", "__SELECTED__") and picked.isdigit():
                session.findById(field_id).text = picked
                log(f"INFO: F4 'Ad' listesinden doğrudan müşteri kodu aktarıldı ({val_to_write}) -> {picked}")
                return picked

        _close_f4_windows(session)
        return ""
    except Exception as e:
        log(f"WARN: F4 müşteri arama hatası ({customer_name}): {e}")
        _close_f4_windows(session)
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

    if not sv_code and sv_name and kuagv_obj is not None:
        sv_code = resolve_customer_via_f4(session, kuagv_obj.Id, sv_name)
        if sv_code:
            doldurulan.append(f"Sipariş Veren (F4)={sv_code}")
        else:
            uyarilar.append(f"'{sv_name}' için F4 'Ad' aramasında müşteri kodu bulunamadı; lütfen SAP ekranından seçin.")
    elif sv_code:
        if set_if_exists(session, kuagv_ids, sv_code):
            doldurulan.append(f"Sipariş Veren={sv_code}")

    # F4 sonrası muhatap alt ekranı yenilenebileceği için Malı Teslim Alan nesnesini taze bul
    kuwev_obj = find_first(session, kuwev_ids)

    # Malı teslim alan boşsa veya aynı isimse Sipariş Veren kodunu doğrudan kullan (ikinci kez F4 açmaya gerek yok)
    if not ta_code:
        if not ta_name or _norm_compare(ta_name) == _norm_compare(sv_name):
            ta_code = sv_code
        elif kuwev_obj is not None:
            ta_code = resolve_customer_via_f4(session, kuwev_obj.Id, ta_name)
            if ta_code:
                doldurulan.append(f"Malı Teslim Alan (F4)={ta_code}")
            else:
                uyarilar.append(f"'{ta_name}' için F4 'Ad' aramasında Malı Teslim Alan kodu bulunamadı.")

    if ta_code:
        if set_if_exists(session, kuwev_ids, ta_code) and f"Malı Teslim Alan (F4)={ta_code}" not in doldurulan:
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
        if status_type(session) == "E":
            err_msg = status_text(session)
            uyarilar.append(f"SAP Başlık/Muhatap Hatası: {err_msg}")
            return {
                "ok": True,
                "kaydedildiMi": False,
                "mesaj": f"VA01 ekranında SAP muhatap/başlık uyarısı oluştu ({err_msg}). Ekran açık bırakıldı, lütfen kontrol edin.",
                "doldurulan": doldurulan,
                "uyarilar": uyarilar,
                "sapDurumCubugu": err_msg,
            }

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
