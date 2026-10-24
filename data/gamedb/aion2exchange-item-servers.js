// Watched items on EVERY server of aion2.exchange, from your own browser session: open https://aion2.exchange/ , F12 → Console, paste, Enter.
// Walks /servers and /items/<key>/prices?window=28d per server (one request every 400 ms, ~30 s), then downloads
// aion2exchange-item-<key>-<date>.json. Edit ITEMS for other item ids.
(async () => {
  const ITEMS = [610511013, 610530010]; // the meter's Prices window pastes its own list here
  const base = (window.AION_CONFIG && window.AION_CONFIG.apiBase) || '/api';
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const get = async (p) => { const r = await fetch(base + p); if (!r.ok) throw new Error(p + ' → ' + r.status); return r.json(); };
  const servers = (await get('/servers')); const list = servers.servers || servers;
  const out = { exportedAt: new Date().toISOString(), items: ITEMS, servers: {} };
  for (const s of list) {
    if (s.has_data === false) continue;
    out.servers[s.server_id] = { server: s, prices: {} };
    for (const item of ITEMS) {
      try { out.servers[s.server_id].prices[item] = await get(`/items/${item}/prices?server=${s.server_id}&window=28d`); }
      catch (e) { out.servers[s.server_id].prices[item] = { error: String(e) }; }
      await sleep(400);
    }
    console.log(s.region, s.name, s.faction, Object.values(out.servers[s.server_id].prices).map((p) => p.error || (p.points || []).length).join(' '));
  }
  window.__a2x_item = out;
  const name = `aion2exchange-items-${new Date().toISOString().slice(0, 10)}.json`;
  try { const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([JSON.stringify(out)], { type: 'application/json' })); a.download = name; a.click(); console.log('downloaded', name); }
  catch (e) { console.log('download blocked; copy(window.__a2x_item) in the console'); }
})();
