async function request(path, options = {}) {
  const res = await fetch(`/api${path}`, options);
  const text = await res.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = { mesaj: text };
  }
  if (!res.ok) throw new Error(data?.hata || `İstek başarısız (${res.status})`);
  return data;
}

export const api = {
  health: () => request('/health'),
  ozet: () => request('/ozet'),
  talepler: (params = {}) => {
    const q = new URLSearchParams(Object.entries(params).filter(([, v]) => v));
    return request(`/talepler${q.toString() ? `?${q}` : ''}`);
  },
  talep: (surecNo) => request(`/talepler/${surecNo}`),
  onayla: (surecNo, body) =>
    request(`/talepler/${surecNo}/onayla`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),
  va01Ac: (surecNo, body) =>
    request(`/talepler/${surecNo}/va01-ac`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),
  va01VbsUrl: (surecNo, bolum = '10', siparisVeren = '') => {
    const q = new URLSearchParams({ bolum, siparisVeren });
    return `/api/talepler/${surecNo}/va01-script.vbs?${q}`;
  },
  aiDenetim: (surecNo) => request(`/talepler/${surecNo}/ai-denetim`, { method: 'POST' }),
  aiSozlesmeKiyas: (surecNo, { file, metin }) => {
    const fd = new FormData();
    if (file) fd.append('file', file);
    if (metin) fd.append('metin', metin);
    return request(`/talepler/${surecNo}/ai-sozlesme-kiyas`, { method: 'POST', body: fd });
  },
  aiMailTaslak: (surecNo, body) =>
    request(`/talepler/${surecNo}/ai-mail-taslak`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),
  importKlasor: () => request('/import/klasor', { method: 'POST' }),
  importYukle: (files) => {
    const fd = new FormData();
    [...files].forEach((f) => fd.append('files', f));
    return request('/import/yukle', { method: 'POST', body: fd });
  },
};
