# Playing games

## The Player screen

The Player shows the game with a toolbar above it. Press <kbd>Alt</kbd> to see each button's access key.

| Button | Key | What it does |
|---|---|---|
| **Back to library** | <kbd>Alt</kbd>+<kbd>B</kbd> | Saves a resume point and closes the game. |
| **Pause / Resume** | <kbd>Alt</kbd>+<kbd>P</kbd>, <kbd>Esc</kbd> | Pauses the game. |
| **Reset** | <kbd>Alt</kbd>+<kbd>R</kbd> | Presses the console's reset button. |
| **Save states** | <kbd>Alt</kbd>+<kbd>S</kbd> | The save-state timeline. |
| **Assist** | <kbd>Alt</kbd>+<kbd>A</kbd> | Slow motion, color-vision palettes and more. |
| **Practice** | | Practice points and a segment timer. |
| **Disk side / disc** | <kbd>F4</kbd> | Famicom Disk System sides and multi-disc PlayStation games. |
| **Disks** | <kbd>Alt</kbd>+<kbd>D</kbd> | Apple II drives 1 and 2. |
| **Codes** | <kbd>Alt</kbd>+<kbd>C</kbd> | Game Genie and RAM codes (NES). |
| **Netplay** | <kbd>Alt</kbd>+<kbd>N</kbd> | Two players on the same network. |
| **Full screen** | <kbd>F11</kbd>, <kbd>Alt</kbd>+<kbd>Enter</kbd> | <kbd>Esc</kbd> leaves full screen. |
| **Stats** | <kbd>Alt</kbd>+<kbd>T</kbd> | Shows values from the game, such as exact health. Only for games with a memory map. |
| **Challenges** | <kbd>Alt</kbd>+<kbd>H</kbd> | Make and take verified challenges. |
| **Achievements** | <kbd>Alt</kbd>+<kbd>E</kbd> | RetroAchievements progress, for recognised games. |

Buttons that don't apply to the running game are hidden. The toolbar is hidden in full screen.

When you come back to a game, a bar offers **Resume** to pick up where you stopped. Close the bar to start fresh.
After a break of a day or more, a **Previously on…** card shows how long you last played, your newest
screenshot, map, note and password. Choose **Resume where I left off** or **Continue**.

If GameVault loses focus (for example you <kbd>Alt</kbd>+<kbd>Tab</kbd> away), the game pauses and resumes when
you come back. You can turn this off in **Settings → Gameplay**.

## Save states and the timeline

A save state captures the whole console at one moment. GameVault keeps as many as you like per game, each with a
thumbnail, the date, and your playtime.

- <kbd>F5</kbd> saves a state. <kbd>F8</kbd> loads the newest one.
- **Save states → Save now** does the same as <kbd>F5</kbd>.
- Click a thumbnail to load it. Right-click to **Pin**, rename or **Delete**. Deleting can be undone.
- When you leave a game, GameVault saves a **Where you left off** state automatically.

Save states are separate from the game's own battery save (the in-game "save" of games like *The Legend of Zelda*).
Battery saves are written automatically and kept in GameVault's data folder, never in your ROM folders.

> **Note:** A save state stores which codes were on. Save states can't be loaded in RetroAchievements hardcore
> mode or during a challenge attempt.

## Rewind and fast-forward

- Hold <kbd>Backspace</kbd> or the controller's **left shoulder** button to rewind. A bar shows how far back you
  are. Release to carry on playing from there.
- Hold <kbd>Tab</kbd> or the **right shoulder** button to fast-forward.
- <kbd>F2</kbd> goes back to about 5 seconds before you last lost a life, for games with a memory map.
- How far back you can rewind depends on **Settings → Gameplay → Rewind memory** (256 MB by default, at least a
  minute of play).
- On the Apple II every key types into the computer, so use <kbd>Shift</kbd>+<kbd>Backspace</kbd> to rewind.

## Assist mode

**Assist** applies to the current session only and is always off when a game starts.

| Option | Values |
|---|---|
| **Assist mode** | Turns the options below on. |
| **Speed** | Normal speed, Half speed, Quarter speed |
| **Colors** | Standard colors, Red-green (deuteranopia), Red-green (protanopia), Blue-yellow (tritanopia) |
| **Unlimited rewind** | Removes the rewind memory limit. |
| **Reduce flicker** | Draws every sprite on a line. |

## Practice mode

1. Open **Practice** and choose **Set practice point** where you want to start a run.
2. Press <kbd>F6</kbd> (**Retry**) to go back to that point instantly.
3. Press <kbd>F7</kbd> (**Split**) to mark the end of a segment.

The timer counts emulated frames, so it's exact even when the game slows down. GameVault keeps your personal best
for each segment. Personal bests aren't recorded while codes are on.

## Passwords

Many older games save progress with a password.

- **Automatic capture.** For games GameVault has a password definition for, passwords are saved automatically
  when the password screen appears, and a notice says *Password saved*.
- **By hand.** Press <kbd>F9</kbd> on the password screen. The game pauses; type the **Password** and an optional
  **Note**, and choose **Save**.

Saved passwords are listed in **Game Details → Passwords** with a **Copy** button, and the newest one is shown on
the *Previously on…* card.

## Journal and maps

Each game has a journal in Game Details.

- **New note** creates a note with a rich-text editor.
- <kbd>F12</kbd> takes a screenshot and adds it to the journal. **Attach to note** links a picture to the selected note.
- **Add a map…** imports an image, for example a map you drew or scanned.
- For games with a memory map, GameVault records each room you visit and builds a map of where you've been
  automatically.

## Game Genie and RAM codes

The **Codes** panel (NES games) is in the Player and in Game Details.

- Type a 6- or 8-letter **Game Genie** code, such as `SXIOPO`, or a **RAM code** such as `075A:09`
  (address:value, written every frame). The panel shows what it decodes to as you type. Add a description and
  choose **Add**.
- Switch codes on and off individually. <kbd>F3</kbd> turns them all off. Codes you've switched on are remembered
  for the game.
- **Import…** reads `.txt` lists (`CODE  description` per line) and `.cht` cheat files from other emulators.
  **Export…** writes a `.txt` list.
- **Codes from GameGenie.com**: choose **Get codes from GameGenie.com** to download that game's codes from
  GameGenie.com (© Danworld, Inc.). Nothing is downloaded until you ask. This needs an internet connection.
- **Code collections** you imported in **Settings → Codes** show the codes they have for the game. If the game
  isn't matched, search for it in your collection; GameVault remembers your choice.

> **Note:** Practice personal bests aren't recorded while codes are on, codes are off in hardcore mode and
> during challenges, and netplay starts with codes off.

## Challenges

A challenge is a file you share with a friend: a starting save state, a goal, and an optional time limit. The other
player's attempt is recorded and can be replayed to prove the result. No server is involved.

**Make a challenge** (games with a memory map): open **Challenges**, give it a **Name**, pick a **Goal** (for
example *Boss HP = 0*), optionally a **Time limit in seconds**, and choose **Save challenge…**. The challenge
starts from this moment.

**Take a challenge**: choose **Open challenge…**, then **Start**. Rewind, loading states and codes are off until
the attempt ends. When you finish, choose **Save result…** to send it back. **Check a result…** replays someone
else's result file and confirms it.

## Stat overlays

For games with a memory map, the **Stats** button draws values from the game, such as exact health or a boss's
hit points, in the top-left corner. It's remembered per game and isn't included in screenshots.

## Taking screenshots

<kbd>F12</kbd> saves a screenshot to the game's journal.
