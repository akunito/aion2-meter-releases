# Craft
Recipes against your character's live inventory (bag and warehouses), and a profit calculator against the market.
## What you see
- The dropdown chooses the scope: "Only what I can craft now" (default), "Also short by 1–2 materials" (with what is missing and where it comes from: gather, craft, drop with its chance, NPC, achievement) or "All recipes".
- Each row: the result, profession and level, grade, materials; "×N" in green is how many times your stock allows it. The star pins the recipe at the top of every list; press it again to unpin.
- Typing in the search box searches every recipe by English or Spanish name regardless of the scope; the X or Escape clears it.
- The search also takes a grade word in English or Spanish: "legendary stone", "piedra legendaria", "unique", "épico" (4 letters or more, plurals fine).
- Several items share a name and differ only by grade (in English "Artisan's Refining Stone" is both the rare and the legendary one). A coloured tag after the name says the grade: common grey, rare green, legendary blue, unique gold, epic orange, special teal. Inside a list of materials the grade appears in brackets only when the name alone is ambiguous.
## Profit
- "Load market file…" picks the JSON the market script saved in Downloads (Prices → Copy market script). Tick "Profit" to list every recipe whose result sells, most profitable first: cheapest listing of the result × yield after the broker fee, minus the cheapest listings of the materials (unpriced materials count 0 and are flagged).
- Caveats: prices are the cheapest listing at the site's last hourly sample; the result's grade is random in the game and the calculator assumes the recipe's grade; a high margin with one or two listings may not be sellable.
