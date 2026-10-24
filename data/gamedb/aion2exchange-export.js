// Export the market of ONE server from aion2.exchange to a JSON file, from your own browser session.
// How: open https://aion2.exchange/ , pick your server in the page, press F12 → Console, paste this whole file, Enter.
// It walks the site's own API (the same calls the page makes), one request every 400 ms, and when done it downloads
// aion2exchange-<server>-<date>.json. Nothing runs outside your browser. Re-run on another day for history.
(async () => {
  const base = (window.AION_CONFIG && window.AION_CONFIG.apiBase) || '/api';
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const get = async (path) => {
    for (let attempt = 0; attempt < 4; attempt++) {
      const r = await fetch(base + path);
      if (r.status === 429) { console.log('rate limited, resting 30 s'); await sleep(30000); continue; }
      if (!r.ok) throw new Error(path + ' → ' + r.status);
      return r.json();
    }
    throw new Error('gave up on ' + path);
  };
  const servers = await get('/servers');
  const list = servers.servers || servers;
  // EU Vaizel: game_server_key 1303 (NA East also has a Vaizel, key 1103 — the first run picked that one).
  const server = list.find((s) => s.game_server_key === 1303) || list.find((s) => /vaizel/i.test(s.name) && /EU|Europe/i.test(s.region_name || s.region || ''));
  console.log('server', server);
  const out = { exportedAt: new Date().toISOString(), server, items: {}, prices: {}, volume: {} };
  // The item list comes in pieces: sections and/or pages. Walk whatever the API exposes until nothing new appears.
  const seen = new Set();
  const rows = [];
  const addAll = (res) => {
    const arr = res.items || res.rows || (Array.isArray(res) ? res : []);
    let added = 0;
    for (const it of arr) { const key = it.item_key ?? it.key ?? it.id; if (!seen.has(key)) { seen.add(key); rows.push(it); added++; } }
    return { added, res };
  };
  const first = await get('/items?server=' + server.server_id);
  console.log('items response keys:', Object.keys(first), 'sample:', JSON.stringify(first).slice(0, 300));
  addAll(first);
  const sections = first.sections || (await get('/sections').catch(() => null)) || [];
  for (const sec of Array.isArray(sections) ? sections : []) {
    const id = sec.id ?? sec.key ?? sec;
    for (let page = 1; page <= 50; page++) {
      await sleep(400);
      let res;
      try { res = await get(`/items?server=${server.server_id}&section=${encodeURIComponent(id)}&page=${page}`); } catch (e) { console.log('section', id, 'page', page, String(e)); break; }
      const { added } = addAll(res);
      console.log('section', id, 'page', page, '+', added);
      if (added === 0) break;
    }
  }
  if (!sections.length) {
    for (let page = 2; page <= 200; page++) {
      await sleep(400);
      let res;
      try { res = await get(`/items?server=${server.server_id}&page=${page}`); } catch (e) { console.log('page', page, String(e)); break; }
      const { added } = addAll(res);
      if (added === 0) { // try offset paging once
        try { const r2 = await get(`/items?server=${server.server_id}&offset=${rows.length}`); if (addAll(r2).added === 0) break; } catch { break; }
      }
    }
  }
  console.log(rows.length, 'items in total');
  let n = 0;
  for (const it of rows) {
    const key = it.item_key ?? it.key ?? it.id;
    out.items[key] = it;
    try {
      await sleep(400);
      out.prices[key] = await get(`/items/${key}/prices?server=${server.server_id}&window=28d`);
      await sleep(400);
      out.volume[key] = await get(`/items/${key}/volume?server=${server.server_id}`);
    } catch (e) { out.prices[key] = { error: String(e) }; }
    if (++n % 25 === 0) console.log(n, '/', rows.length);
  }
  window.__a2x = out; // kept in the page: if the download is blocked, run  copy(JSON.stringify(window.__a2x))  and paste into a file
  const blob = new Blob([JSON.stringify(out)], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `aion2exchange-${server.name}-${new Date().toISOString().slice(0, 10)}.json`;
  a.click();
  console.log('done:', a.download, '— if Brave blocked the download (icon in the address bar), allow it there, or run: copy(JSON.stringify(window.__a2x)) and paste into a file');
})();
