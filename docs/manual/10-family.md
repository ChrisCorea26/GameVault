# Family profiles

Each person in your household can have their own profile, with their own library status, save states, battery
saves, timeline, journal, codes and settings.

## Setting up profiles

In **Settings → Family**:

1. Type a name under **New profile**.
2. Optionally set **Minutes a day** (5 to 1440) to limit daily play. Leave it empty for no limit.
3. Tick **Can change settings** if this person may change GameVault's settings.
4. Choose **Add**.

The first profile, *Player 1*, is the main profile and can't be removed. Neither can the profile that's in use.

## Parent PIN

Type a PIN of 4 to 12 digits and choose **Set parent PIN**. Once a PIN is set:

- Profiles without *Can change settings* see Settings locked. A parent types the PIN and chooses **Unlock**.
- **Lock** locks Settings again. **Remove PIN** removes it.

> **Forgot the PIN?** GameVault stores only a scrambled (hashed) copy, so it can't show it to you. To clear it,
> close GameVault, open `%LOCALAPPDATA%\GameVault\profiles.json` in Notepad, delete the `"pin": { … }` entry
> (including its trailing comma if one follows), and save. Profiles and saves are kept.

## Switching profiles

When there's more than one profile, a profile button appears at the bottom of the navigation pane. Choose it, then
choose a name under **Who's playing?**. Switching closes the running game (it's saved first) and reopens GameVault
with that person's games and saves.

## Daily play limits

For a profile with a daily limit:

- A notice appears 5 minutes before the end: *5 minutes of play left today.*
- At the limit, the game is saved and paused, and a *Time's up for today* card appears.
- A parent can type the **Parent PIN** and choose **Keep playing**. That profile then isn't stopped again for the rest of the day.

Each extra profile keeps its files in `%LOCALAPPDATA%\GameVault\Profiles\<profile id>\`; the main profile uses
`%LOCALAPPDATA%\GameVault\` itself.
