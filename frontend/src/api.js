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
  va01Kaydet: (surecNo, overrideBody) =>
    request(`/talepler/${surecNo}/va01-kaydet`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(overrideBody),
    }),
  va01Ac: (surecNo, body) =>
    request(`/talepler/${surecNo}/va01-ac`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),
  va01VbsUrl: (surecNo) => `/api/talepler/${surecNo}/va01-script.vbs`,
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
  // ASELSAN LLM Suite Flow + Bağımsız MCP Sunucusu
  llmSuiteDurum: () => request('/llmsuite/durum'),
  llmSuiteAyarlarKaydet: (body) =>
    request('/llmsuite/ayarlar', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),
  llmSuiteTest: () => request('/llmsuite/test', { method: 'POST' }),
  llmSuiteAnaliz: (surecNo, body) =>
    request(`/talepler/${surecNo}/llmsuite-analiz`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {}),
    }),
  llmModeller: (apiUrl = '', apiKey = '') => {
    const q = new URLSearchParams(Object.entries({ apiUrl, apiKey }).filter(([, v]) => v));
    return request(`/llm/modeller${q.toString() ? `?${q}` : ''}`);
  },
  mcpAracDene: (name, argumentsObj = {}) =>
    request('/mcp/arac-dene', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, argumentsJson: JSON.stringify(argumentsObj) }),
    }),
  importKlasor: () => request('/import/klasor', { method: 'POST' }),
  importYukle: (files) => {
    const fd = new FormData();
    [...files].forEach((f) => fd.append('files', f));
    return request('/import/yukle', { method: 'POST', body: fd });
  },
};
