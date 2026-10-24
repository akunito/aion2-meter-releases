# Prices
Watch items on the market (aion2.exchange) and get a sound when the cheapest listing reaches your price.
## What you see
- One row per watched item: name (Spanish name on hover), id, the site's sample time and when the meter last read it; your "units held" and "paid each" and the value of your stock; the ≤ / ≥ button (buy below / sell above) with your limit; the last price, its 24 h trend and the number of listings; a globe to open the item on the site and a bin to stop watching.
- Click a row to open it: on the left the 5 cheapest current listings (shown as "not configured" until the site's listings call is plugged in), on the right the price chart for 24 h / 7 days / 30 days with min, median, max and your buy/sell line dashed; hover the line for the time and price of each hourly sample, and "collecting since" says how far back the meter's own history reaches.
- The site samples each item about once an hour. "Check now" re-reads what the site has; it cannot force a new sample, and it waits 5 minutes between presses.
## How to use it
- Search an item by name in the box at the bottom, pick it, set a max price, tick "sell above" for a sell watch, Add. The EN / ES switch next to the box picks the language of the search and of the item names; it is Settings → Names, so it changes every game name in the other tabs too.
- The item search also takes a grade word in English or Spanish: "legendary stone", "piedra legendaria", "unique", "épico" (4 letters or more, plurals fine). Each result shows its grade tag, and picking one repeats it next to the id, so you know which stone you are adding.
- Several items share a name and differ only by grade (in English "Artisan's Refining Stone" is both the rare and the legendary one). A coloured tag after the name says the grade: common grey, rare green, legendary blue, unique gold, epic orange, special teal. Inside a list of materials the grade appears in brackets only when the name alone is ambiguous.
- "Copy market script" and "Copy watched-items script" copy a browser-console script: open aion2.exchange in your browser, F12 → Console, paste, Enter. The JSON lands in Downloads and feeds the Craft profit calculator.
- A hit (price at or under your maximum, or at or over your sell price) plays a chime; the Windows notification is off by default.
