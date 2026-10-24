# DPS meter
The main tab. It reads the game's own network packets (never writes to the game) and shows one row per player in the fight: name, class, DPS, total damage and share. The leader's bar is full and the rest are sized against it (or against the party total, see Settings).
## What you see
- The header shows the fight title (the boss or the first monster hit), the state (waiting, live, last fight) and the clock.
- The boss panel shows its HP and the "counted from" mark when the meter started watching mid-fight.
- On a crowded world boss the server stops sending most players' damage: the footer then says "N attacking · damage of M sent" instead of a party DPS, because the numbers would be wrong.
- The footer line shows the capture state ("No capture (Npcap missing)" on a PC without Npcap: Settings → Capture says what still works) and the "Open the fight" / "Fights" links; the pills below are the field-boss timer and the server events (Shugo, Invasion, Rift, Kaira, Siege, Reset = daily reset 16:00 Seoul, Weekly = the Wednesday reset at the same moment, shown as "Weekly 1d 22h" while more than a day away).
- After a dungeon, Nightmare, Transcendence or Ascension Trial boss kill, a gold line under the footer says for 60 s where your DPS sits among your class on that boss this week, from Abyss Logs: "p62 of Templars 50K–100K CP on Ferocious Horn Nuakum this week · median 9.7K/s" (p62 = better than 62 % of the logs; the CP bracket is used when it has enough logs). The fight's breakdown keeps it under its header. Field bosses and wipes get no line. Settings → Data turns it off.
## How to use it
- Start the meter before entering a dungeon: names arrive on the loading screen.
- Click a row for that player's breakdown (skills, accuracy, rotation, defense). "Open the fight" opens the current fight; "Fights" lists the saved ones.
- Right-click any pill to switch its sound or Windows notification. Notifications are off by default: a toast over the game costs FPS.
- Hotkeys: Ctrl+Shift+D hide/show, Ctrl+Shift+L click-through, Ctrl+Shift+R restart the meter, Ctrl+Shift+A atlas (all editable in Settings).
- Ctrl + mouse wheel over the rows changes their size.
## The fight breakdown
Clicking a row, "Open the fight", a row of the "Fights" chip inside the breakdown, or a "My fights" row in Top DPS opens the breakdown inside this card, in place of the rows: party list, PLACE / DMG / DPS / CONTRIB / TIME, then the DPS, Accuracy, Rotation and Defense tabs. A highlighted chart pill appears next to the tab icons while it is open. "Back" at the top left (or Esc after clicking inside it) returns to the DPS rows. On a narrow card the party list folds into a "Party ▾" button and the skill tables drop their less used columns (average and max; front, multi and damage); widen the card to see them. The pop-out icon opens the same fight in its own resizable window and puts the card back on the DPS rows; with the overlay hidden the breakdown always opens as a window. When the meter knows the selected player's official page (your own characters, or anyone opened from Top DPS in the last 12 h) the tabs Profile, Skills and Daevanion appear after Defense: gear, attributes, skill levels and Daevanion boards; they follow the player picked in the party list and disappear for players without one.
## Tabs
The icons at the top switch between DPS, Atlas, Top DPS, Prices, Craft, Build, Items and Boss timers. The pop-out icon opens the current tab in a separate window (close it with its X).

A character dropdown shows next to the tabs: pick whose bag, warehouses, builds and Craft counts to look at. It works while you play too: the logged-in character is marked "(current)" and keeps receiving the live data (deposits still update the shared account warehouse), while the tabs show the one you picked; an amber eye and an amber dropdown remind you that you are viewing someone else. Logging in with another character in the game switches the view to that character.
