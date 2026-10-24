# Build
Your character's skill build in questlog.gg's planner layout, checked against your character: what to level next and what is still missing. Levels and open Daevanion nodes come from the game itself when you log in with the meter running (newer than the official site, which can lag by days); otherwise from the official profile.
## What you see
- The picker holds this character's builds (saved on this PC, one file each). "New from profile" makes a build of what the character has now: every learned skill at its current level (the official site says nothing about key slots, specs or priority). "Import…" opens the questlog.gg import: paste a skill-builder link (…/skill-builder/<user>?build-id=<n>) and press Import, or search the public builds (your class by default, "All classes" for every class) and press Add. The build is saved as this character's active build; importing the same questlog build again refreshes it and keeps your notes and Daevanion plan.
- With no build chosen, the tab shows the five most-liked public builds of your class on questlog.gg (refreshed every 6 hours): Add one, Search… for another, or start "New from profile".
- When the build came from questlog, the card header has "Open on questlog.gg".
- Library: every class skill. Coloured = in the build, grey = not. Bottom-right number = the build's level; bottom-left = the character's level now (green at target, amber within 4 levels, red further or not learned). Blue diamonds = chosen specializations.
- "Show skills already at the build level": off hides the finished skills from the library and dims them on the bar, so only the work left stands out. The amber line counts what is missing; "refresh" fetches the official profile again.
- Hover any icon for its description at the build level, what the character has now, the specs, your note and the skill's relations.
## Your questlog builds
Without any login the meter lists your own builds from questlog.gg: Import… → "My builds" shows the skill builds and Daevanion plans of your questlog user for this class ("All classes" for every class, the arrow button fetches them again). The user is learnt from the first questlog link you import, or type it in the "questlog user" box (the code after /skill-builder/ in your links, or paste any of your links). Add on a skill build imports it and, when a plan of the same class starts with the same character name before the dash ("Irin — Ranger PvE 45" ↔ "Irin — Daevanion Ranger"), that plan too; Add on a plan puts it into the active build (one is made from the profile when there is none). With no build chosen, "Your questlog builds" come first in the suggestions. On an imported build, "Check questlog" next to "Open on questlog.gg" compares it with questlog (only when you click) and offers "Update" when questlog has a newer version: your notes stay, and your plan stays unless the plan is the newer one.
## Editing (saved by itself half a second after each change)
- Click a skill or a slot to open it below the bar. Double-click a library skill to put it on the highlighted key (click a key number to highlight it; key 1 when none); double-click or right-click a slot to take the skill off; with a skill open, click an empty slot to move it there. Passives never go on the bar.
- The slider sets the build level (the description follows it); click a specialization to choose it or drop it: up to 3 on actives and passives, 4 on stigmas, only from the level each one needs.
- Skill Priority: right-click an icon to move it or remove it, "+" adds a build skill of that kind. Macros: "+ Add macro", name it, add steps, Save.
- "Names: English / Español" changes skill names and descriptions, and every other game name too: it is Settings → Names (also the switch in Items and Prices).
- "When to use · combos" under the specializations: your own note for the open skill (up to 600 characters: when to use it, what it combos with, the order). It is saved with the build like any other edit, shows in the skill's tooltip, and emptying the box deletes it. Notes stay on this PC: "Copy for questlog" does not send them (questlog has no place for them).
- The slider runs from 1 to 20 like questlog's planner.
## Relations
Under the note, and at the bottom of the tooltip, the meter lists how the skill connects to the rest of the class, read from the English skill texts: → green = what this skill gives, ← blue = what it takes, · grey = your build.
- Buffs: "Enables Deadshot: grants Precision" on Marking Shot, "Stronger with Precision from Marking Shot" on Deadshot.
- States: "Applies Slow for Burst Arrow" on Snare Shot, "Needs Slow or Root: Snare Shot applies Slow" on Burst Arrow ("Bonus vs" when the state only adds damage). A state that only a specialization inflicts counts once that specialization is chosen.
- Specializations that name another skill: "Shortens Deadshot cooldown -1s (spec)", "Boosts Focused Eye x1.5 (spec)", "Chain skill Tempest Arrow (spec)".
- Your build: the chain of the skill's key in order ("E chain: Supporting Fire → Vaizel's Authority → Bow of Blessing → Marking Shot") and its place in Skill Priority ("#3 in your priority", counted inside actives, stigmas or passives).
Skill names follow "Names: English / Español"; buff and state names stay as the English texts write them.
## Publishing to questlog
The meter never logs in to questlog.gg; a build goes there from your own browser session in three steps:
1. On questlog.gg, logged in, open Skill Builder → Create Build, give it a name and open it (the address ends in ?build-id=N).
2. In the meter, Build tab → "Copy for questlog": the build goes to the clipboard as JSON.
3. On that questlog page press F12 → Console, paste the whole script questlog-publish.js (it is in the meter folder, data\gamedb\questlog-publish.js; open it with Notepad), press Enter and paste the clipboard when it asks. Reload the page to see the build.
