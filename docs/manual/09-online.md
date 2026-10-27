# Online and multiplayer

GameVault works fully offline. These features use the network only when you ask them to.

## Netplay

Two people can play together, each on their own PC: on the **same local network** (home Wi-Fi or wired network) or
**over the internet**.

**What you need:**

- Both PCs on the same network, or both on the internet.
- **The same game file** on both PCs. GameVault checks this and never sends game data over the network.
- For Apple II games, the same Apple II ROM files on both PCs; for Disk System games, the same Disk System BIOS.
- RetroAchievements **Hardcore mode** off on both PCs.
- A game for any system except the PlayStation 2, whose console is too large for netplay's rollback.

**Hosting:**

1. Start the game.
2. Open **Netplay** and choose **Host a game on this network**.
3. Read the invite code to the other player, or choose **Copy code** and send it. It looks like `ABCDE-12345`.

**Joining:**

1. Start the same game.
2. Open **Netplay**, type the code under *Or join with the other player's code*, and choose **Join**.

**Over the internet:**

1. The host starts the game, opens **Netplay** and chooses **Host a game over the internet**, then sends the code to
   the other player (a message, a chat app).
2. The other player starts the same game and joins with the code, as above. They get a **reply code** and send it back.
3. The host types the reply code and chooses **Connect**.

GameVault asks a public STUN server (Google's or Cloudflare's) what address your PC has on the internet; nothing else
is sent to it. Most home routers then let the two PCs reach each other directly. If yours can't (some mobile and
university networks), one of you can run **GameVault's relay** on a server and enter its address in **Settings →
Netplay → Relay server**: hosting over the internet then goes through it, the code is 16 characters long, and no reply
code is needed.

**Running the relay:** `GameVault.Relay` is a small program that runs on Windows, Linux or macOS with .NET. Start it
on a server the internet can reach (`GameVault.Relay` listens on UDP port 47801, or give another port as its only
argument), open that UDP port in the server's firewall, and enter `server-address:47801` in Settings. It only passes
the two players' netplay packets to each other; it never sees game files.

Both games restart from the title screen, without battery saves or codes. The Netplay panel shows whether you're Player 1 or Player 2. Progress made
during netplay isn't written to the game's save file. Choose **Leave netplay** to stop.

**Firewall:** the first time, Windows may ask whether GameVault can use the network. Allow it on **Private
networks**. If you clicked *Cancel*, allow GameVault in *Windows Security → Firewall & network protection → Allow an
app through firewall*. Netplay uses UDP port 47800, or another free port if that one is busy. Your network must be
set to *Private* in Windows network settings; public networks (cafés, hotels, many university networks) often block
devices from reaching each other.

**Messages:**

| Message | What to do |
|---|---|
| *That invite code isn't right.* | Codes have 10 letters and digits (16 through a relay). Case, spaces and dashes don't matter. |
| *That reply code isn't right.* | Type the reply code the other player got when they joined, not your own code. |
| *GameVault couldn't find this PC's internet address.* | Check the internet connection. Networks that block UDP need a relay, or play on the same network. |
| *The relay server chosen in Settings → Netplay didn't answer.* | Check the relay's address and that it's running with its UDP port open, or clear the setting to connect directly. |
| *The relay server in that code didn't answer, or the game there already has two players.* | Ask the host to host again. |
| *The other player has a different game or version.* | Both players need the identical file. Compare the files or copy yours to them. |
| *Lost the connection to the other player.* | Check both PCs are on the same network (or online) and the firewall allows GameVault. Over the internet, if you never connect after the reply code, your routers can't reach each other: use a relay. |
| *The two games stopped matching, so netplay stopped.* | Start netplay again. |
| *This PC doesn't seem to be on a network.* | Connect to the network first. |
| *Netplay is off in RetroAchievements hardcore mode.* | Turn Hardcore off in Settings → RetroAchievements. |

Famicom Disk System games play too. Either player can change the disk side from the disk menu, and both PCs change it
on the same frame.

## RetroAchievements

[RetroAchievements](https://retroachievements.org) is a free community service that adds achievements and
leaderboards to classic games.

**Setup:**

1. Create a free account at <https://retroachievements.org>.
2. In GameVault, open **Settings → RetroAchievements**, type your **Username** and **Password**, and choose
   **Sign in**. It shows *Signed in as …* with your points.

Your password is sent only to RetroAchievements. GameVault keeps an encrypted sign-in token instead, readable only
by your Windows account. Each family profile signs in separately.

**Playing:** when a game RetroAchievements recognises starts, the **Achievements** button appears in the Player.
Unlocks pop up in the bottom-right corner, and leaderboard attempts are announced as they start and finish.

**Hardcore mode** (on by default) follows RetroAchievements' rules: achievements count double, and loading save
states, rewind, slow motion, codes and netplay are off. Saving states still works. Turning it on restarts the running
game. Turn it off in Settings if you'd rather keep those features.

> **Note:** RetroAchievements hasn't yet approved GameVault for hardcore unlocks, so they may be recorded as
> softcore or refused until it does. If your copy says *The RetroAchievements component is missing*, it was built
> without CMake; see [Installing GameVault](#installing-gamevault).

| Message | What to do |
|---|---|
| *RetroAchievements didn't accept that username and password.* | Check them on the RetroAchievements website. |
| *Your RetroAchievements sign-in has expired.* | Sign in again. |
| *RetroAchievements didn't answer.* | Check your internet connection. |

## GameGenie.com codes

In the **Codes** panel of an NES game, **Get codes from GameGenie.com** downloads that game's codes from
GameGenie.com (© Danworld, Inc.) and shows them with a link to the page. Nothing is downloaded until you choose it.
Pages are kept for up to 30 days so they work offline afterwards.

## Code collections

If you have your own list of Game Genie codes in a spreadsheet, import it in **Settings → Codes → Import a code
collection…**. It's copied into GameVault's data folder.

The file must be `.xlsx` or `.csv` with a header row containing these columns (other columns are ignored):

| Column | Header text GameVault looks for |
|---|---|
| Game | `Game` |
| Code | `Code`, `Codes` or `Key in` |
| What it does | `Effect`, `Effects` or `Description` |
| Source (optional) | `Source` or `Code Source` |

In a spreadsheet, GameVault reads the sheet named **NES**, or the first sheet. Rows whose code isn't valid are kept as
notes for that game. A collection's codes then appear in the Codes panel for matching games.

## Cartridge dumper (INL Retro-Prog)

GameVault can dump NES cartridges with an **INL Retro-Prog**, an open-source USB cartridge reader. This is optional.

**Setup:**

1. Install the INL Retro-Prog software and its **USB driver** from the INL website, following its instructions. Note
   the folder that contains `inlretro.exe`; GameVault runs it from that folder and uses its `scripts\inlretro2.lua`.
2. Plug in the INL Retro-Prog.
3. In **Settings → Cartridge dumper**, choose **Choose…** and pick `inlretro.exe`, or paste its full path, for example
   `C:\INL-retro\inlretro.exe`.
4. **Restart GameVault.**

Then use **Library → Dump cartridge**; see [Dumping a cartridge](#dumping-a-cartridge). Supported boards: NROM,
MMC1, UxROM, CNROM, MMC3, AxROM, MMC2, MMC4, Color Dreams, BNROM, GxROM and Sunsoft FME-7.

| Message | What to do |
|---|---|
| *GameVault can't find the INL Retro-Prog software.* | Check the path in Settings and restart GameVault. |
| *GameVault can't find the INL Retro-Prog. Check that it's plugged in…* | Reconnect the USB cable. |
| *Windows didn't let GameVault use the dumper.* | Install the INL Retro-Prog USB driver. |
| *The reads didn't match. Clean the cartridge contacts and try again.* | Clean the contacts with isopropyl alcohol and reseat the cartridge. |
| *The INL Retro-Prog can't read this kind of cartridge yet.* | That board isn't supported. |
