// Publish a build from the AION2 DPS Meter to questlog.gg, from your own browser session (A2M-21, optional step).
// How: 1) in questlog.gg, logged in, open Skill Builder → Create Build, give it a name and open it (the URL ends in ?build-id=N);
//      2) in the meter, Build tab → "Copy for questlog" (the build goes to the clipboard as JSON);
//      3) press F12 → Console on that questlog page, paste this whole file, Enter, and paste the clipboard when asked.
// It calls the same saveBuild endpoint the page uses (same-origin cookies), once. Nothing runs outside your browser.
(async () => {
  const m = location.search.match(/build-id=(\d+)/);
  if (!m) { console.error('Open the build you created first: the URL must end in ?build-id=N'); return; }
  const id = Number(m[1]);
  const text = prompt('Paste the build JSON copied from the meter');
  if (!text) return;
  let exp;
  try { exp = JSON.parse(text); } catch (e) { console.error('That is not the meter JSON', e); return; }
  // skills map {id: {id, lvl, slotId "key.row"|null, specializations [ids]}}, priority [{id}], macros [{id, name, steps [{delayMs, skillId}]}]
  const skills = {};
  for (const s of exp.skills) skills[String(s.skillId)] = { id: String(s.skillId), lvl: s.level, slotId: s.slot ?? null, specializations: s.specs ?? [] };
  const build = {
    id, name: exp.name, classId: exp.classId, publisher: 'global', skills,
    priority: (exp.priority ?? []).map((x) => ({ id: String(x) })),
    macros: (exp.macros ?? []).map((mc, i) => ({ id: crypto.randomUUID ? crypto.randomUUID() : String(Date.now() + i), name: mc.name, steps: (mc.steps ?? []).map((sid) => ({ delayMs: 0, skillId: String(sid) })) })),
    tags: exp.tags ?? [], order: 0, isPublic: true,
    description: exp.description ?? '',
  };
  const r = await fetch('/aion-2/api/trpc/skillBuilder.saveBuild', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ build }) });
  console.log('saveBuild →', r.status, await r.text().catch(() => ''));
  if (r.ok) console.log('Saved. Reload the page to see it.'); else console.error('questlog refused the save; the page format may have changed');
})();
