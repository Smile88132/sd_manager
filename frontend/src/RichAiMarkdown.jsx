import React, { useMemo } from 'react';

const ALLOWED_TAGS = new Set([
  'B', 'STRONG', 'I', 'EM', 'U', 'S', 'DEL', 'MARK', 'CODE', 'PRE',
  'BR', 'HR', 'P', 'DIV', 'SPAN', 'FONT', 'SMALL', 'SUB', 'SUP',
  'H1', 'H2', 'H3', 'H4', 'H5', 'H6',
  'UL', 'OL', 'LI', 'BLOCKQUOTE',
  'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD',
]);

const ALLOWED_STYLE_PROPS = new Set([
  'color', 'background', 'background-color',
  'font-size', 'font-weight', 'font-style', 'font-family',
  'text-decoration', 'text-align', 'line-height', 'letter-spacing',
  'padding', 'padding-left', 'padding-right', 'padding-top', 'padding-bottom',
  'margin', 'margin-top', 'margin-bottom',
  'border', 'border-left', 'border-bottom', 'border-radius', 'display',
]);

const FONT_SIZE_MAP = {
  '1': '11px', '2': '12.5px', '3': '14px', '4': '16px',
  '5': '19px', '6': '22px', '7': '26px',
};

function escapeHtml(str) {
  return String(str || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/**
 * Korunan HTML etiketlerini yer tutucuya alır, kalan Markdown sözdizimini HTML'e çevirir,
 * ardından DOMParser ile güvenli etiket/stil beyaz listesinden geçirir.
 */
export function markdownToSafeHtml(raw) {
  if (!raw) return '';
  let text = String(raw).replace(/\r\n/g, '\n');

  // 1) Fenced code bloklarını (```lang ... ```) yer tutucuya al
  const codeBlocks = [];
  text = text.replace(/```([^\n`]*)\n([\s\S]*?)```/g, (_, lang, code) => {
    const idx = codeBlocks.length;
    const safeLang = escapeHtml(lang.trim());
    codeBlocks.push(
      `<pre class="md-pre">${safeLang ? `<div class="md-code-lang">${safeLang}</div>` : ''}<code>${escapeHtml(code.replace(/\n$/, ''))}</code></pre>`
    );
    return `\u0000CODEBLOCK_${idx}\u0000`;
  });

  // 2) Satır içi kodları (`...`) yer tutucuya al
  const inlineCodes = [];
  text = text.replace(/`([^`\n]+)`/g, (_, code) => {
    const idx = inlineCodes.length;
    inlineCodes.push(`<code class="md-inline-code">${escapeHtml(code)}</code>`);
    return `\u0000INLINECODE_${idx}\u0000`;
  });

  // 3) AI çıktısında güvenli HTML font/span/vurgu etiketleri varsa koru, diğer < > işaretlerini kaçır
  const htmlTags = [];
  text = text.replace(/<\/?([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>/g, (full, tagName) => {
    if (!ALLOWED_TAGS.has(tagName.toUpperCase())) return escapeHtml(full);
    const idx = htmlTags.length;
    htmlTags.push(full);
    return `\u0000HTMLTAG_${idx}\u0000`;
  });

  // Kalan çıplak &, <, > karakterlerini güvenli hale getir
  text = text
    .replace(/&(?![a-zA-Z0-9#]{2,8};)/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');

  function formatInline(line) {
    let s = line;
    // Bold + Italic (***...***)
    s = s.replace(/\*\*\*([^\n*]+?)\*\*\*/g, '<strong><em>$1</em></strong>');
    // Bold (**...**)
    s = s.replace(/\*\*([^\n*]+?)\*\*/g, '<strong>$1</strong>');
    // __Bold__ (kelime sınırında; PYP_OGESI gibi alt çizgileri bozmasın!)
    s = s.replace(/(^|\s)__([^\n_]+?)__(?=\s|[.,;:!?)]|$)/g, '$1<strong>$2</strong>');
    // Italic (*...*)
    s = s.replace(/(^|[\s(])\*([^\s*][^\n*]*?)\*(?=[\s.,;:!?)]|$)/g, '$1<em>$2</em>');
    // Strikethrough (~~...~~)
    s = s.replace(/~~([^\n~]+?)~~/g, '<del>$1</del>');
    return s;
  }

  // 4) Satır bazlı blok ayrıştırma (Başlık, GFM Tablo, Liste, Alıntı, Ayırıcı, Paragraf)
  const lines = text.split('\n');
  const out = [];
  let i = 0;

  const isTableSep = (ln) => /^\s*\|?(\s*:?-{2,}:?\s*\|)+\s*:?-{2,}:?\s*\|?\s*$/.test(ln);
  const splitTableRow = (ln) => {
    let t = ln.trim();
    if (t.startsWith('|')) t = t.slice(1);
    if (t.endsWith('|')) t = t.slice(0, -1);
    return t.split('|').map((c) => formatInline(c.trim()));
  };

  while (i < lines.length) {
    const line = lines[i];
    const trimmed = line.trim();

    if (!trimmed) {
      i++;
      continue;
    }

    // Code block tek satır yer tutucu
    if (/^\u0000CODEBLOCK_\d+\u0000$/.test(trimmed)) {
      out.push(trimmed);
      i++;
      continue;
    }

    // Yatay çizgi (---, ***)
    if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) {
      out.push('<hr class="md-hr" />');
      i++;
      continue;
    }

    // Markdown Başlık (# .. ######)
    const hMatch = trimmed.match(/^(#{1,6})\s+(.+)$/);
    if (hMatch) {
      const lvl = Math.min(hMatch[1].length, 6);
      out.push(`<h${lvl} class="md-h${lvl}">${formatInline(hMatch[2])}</h${lvl}>`);
      i++;
      continue;
    }

    // "1. **Kritik Bulgular**" gibi numaralı ana bölüm başlıkları (altında tireli maddeler varsa bölüm başlığı yap)
    const numSec = trimmed.match(/^(\d+)\.\s+\*\*([^*]+)\*\*:?\s*$/);
    if (numSec) {
      out.push(`<h3 class="md-h3"><span class="md-sec-num">${numSec[1]}</span> ${formatInline(numSec[2])}</h3>`);
      i++;
      continue;
    }

    // GFM Tablo
    if (trimmed.includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1])) {
      const headers = splitTableRow(trimmed);
      i += 2;
      const bodyRows = [];
      while (i < lines.length && lines[i].trim().includes('|') && lines[i].trim() !== '') {
        bodyRows.push(splitTableRow(lines[i]));
        i++;
      }
      out.push(
        `<div class="md-table-wrap"><table class="md-table"><thead><tr>${headers
          .map((h) => `<th>${h}</th>`)
          .join('')}</tr></thead><tbody>${bodyRows
          .map((r) => `<tr>${headers.map((_, ci) => `<td>${r[ci] ?? ''}</td>`).join('')}</tr>`)
          .join('')}</tbody></table></div>`
      );
      continue;
    }

    // Alıntı / Callout (&gt; ...)
    if (trimmed.startsWith('&gt;')) {
      const qLines = [];
      while (i < lines.length && lines[i].trim().startsWith('&gt;')) {
        qLines.push(formatInline(lines[i].trim().replace(/^&gt;\s?/, '')));
        i++;
      }
      out.push(`<blockquote class="md-quote">${qLines.join('<br />')}</blockquote>`);
      continue;
    }

    // Madde işaretli veya numaralı liste
    const ulMatch = line.match(/^(\s*)([-*•])\s+(.+)$/);
    const olMatch = !ulMatch ? line.match(/^(\s*)(\d+)[.)]\s+(.+)$/) : null;
    if (ulMatch || olMatch) {
      const isOrdered = Boolean(olMatch);
      const tag = isOrdered ? 'ol' : 'ul';
      const items = [];
      while (i < lines.length) {
        const cur = lines[i];
        const mUl = cur.match(/^(\s*)([-*•])\s+(.+)$/);
        const mOl = !mUl ? cur.match(/^(\s*)(\d+)[.)]\s+(.+)$/) : null;
        if (!mUl && !mOl) break;
        const indent = (mUl ? mUl[1] : mOl[1]).length;
        const content = formatInline(mUl ? mUl[3] : mOl[3]);
        items.push(`<li style="${indent >= 2 ? `margin-left:${Math.min(indent * 8, 32)}px` : ''}">${content}</li>`);
        i++;
      }
      out.push(`<${tag} class="md-list">${items.join('')}</${tag}>`);
      continue;
    }

    // Normal paragraf (ardışık düz satırları <br/> ile birleştir)
    const pLines = [];
    while (
      i < lines.length &&
      lines[i].trim() !== '' &&
      !/^(#{1,6}\s|[-*•]\s|\d+[.)]\s|(-{3,}|\*{3,})$|&gt;|\u0000CODEBLOCK_)/.test(lines[i].trim()) &&
      !(lines[i].includes('|') && i + 1 < lines.length && isTableSep(lines[i + 1]))
    ) {
      pLines.push(formatInline(lines[i]));
      i++;
    }
    if (pLines.length) {
      out.push(`<p class="md-p">${pLines.join('<br />')}</p>`);
    } else {
      out.push(`<p class="md-p">${formatInline(trimmed)}</p>`);
      i++;
    }
  }

  let html = out.join('\n');

  // Yer tutucuları geri yerleştir
  html = html.replace(/\u0000HTMLTAG_(\d+)\u0000/g, (_, idx) => htmlTags[Number(idx)] || '');
  html = html.replace(/\u0000INLINECODE_(\d+)\u0000/g, (_, idx) => inlineCodes[Number(idx)] || '');
  html = html.replace(/\u0000CODEBLOCK_(\d+)\u0000/g, (_, idx) => codeBlocks[Number(idx)] || '');

  // 5) DOMParser ile katı güvenlik ve font/stil temizliği
  if (typeof DOMParser === 'undefined') return html;
  const doc = new DOMParser().parseFromString(`<div>${html}</div>`, 'text/html');
  const root = doc.body.firstElementChild;
  if (!root) return html;

  function sanitizeNode(node) {
    const children = Array.from(node.childNodes);
    for (const ch of children) {
      if (ch.nodeType === 1) {
        const tag = ch.tagName.toUpperCase();
        if (!ALLOWED_TAGS.has(tag)) {
          const txt = doc.createTextNode(ch.textContent || '');
          node.replaceChild(txt, ch);
          continue;
        }
        // <font color="..." size="..." face="..."> etiketini modern stil kurallarına çevir
        if (tag === 'FONT') {
          const extra = [];
          const col = ch.getAttribute('color');
          const sz = ch.getAttribute('size');
          const face = ch.getAttribute('face');
          if (col && /^[#a-zA-Z0-9(),.\s%-]{1,40}$/.test(col)) extra.push(`color:${col}`);
          if (sz && FONT_SIZE_MAP[sz]) extra.push(`font-size:${FONT_SIZE_MAP[sz]}`);
          if (face && /^[a-zA-Z0-9,\s'"-]{1,60}$/.test(face)) extra.push(`font-family:${face}`);
          if (extra.length) {
            ch.setAttribute('style', `${ch.getAttribute('style') || ''};${extra.join(';')}`);
          }
        }
        // İzin verilmeyen attribute'ları sil (on*, href=javascript: vb.)
        for (const attr of Array.from(ch.attributes)) {
          const aname = attr.name.toLowerCase();
          if (aname === 'class') continue;
          if (aname === 'style') {
            const safeDecls = attr.value
              .split(';')
              .map((d) => d.trim())
              .filter(Boolean)
              .map((d) => {
                const colon = d.indexOf(':');
                if (colon <= 0) return null;
                const prop = d.slice(0, colon).trim().toLowerCase();
                const val = d.slice(colon + 1).trim();
                if (!ALLOWED_STYLE_PROPS.has(prop)) return null;
                if (/url\s*\(|expression\s*\(|javascript:/i.test(val)) return null;
                return `${prop}:${val}`;
              })
              .filter(Boolean);
            if (safeDecls.length) ch.setAttribute('style', safeDecls.join(';'));
            else ch.removeAttribute('style');
          } else if (tag === 'TD' || tag === 'TH') {
            if (aname !== 'colspan' && aname !== 'rowspan') ch.removeAttribute(attr.name);
          } else {
            ch.removeAttribute(attr.name);
          }
        }
        sanitizeNode(ch);
      }
    }
  }

  sanitizeNode(root);
  return root.innerHTML;
}

export default function RichAiMarkdown({ text, dark = false }) {
  const safeHtml = useMemo(() => markdownToSafeHtml(text), [text]);
  return (
    <div
      className={`ai-rich-card ${dark ? 'dark' : ''}`}
      dangerouslySetInnerHTML={{ __html: safeHtml }}
    />
  );
}
