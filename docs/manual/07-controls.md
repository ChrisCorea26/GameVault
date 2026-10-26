# Controls

GameVault needs no setup for controls. The keyboard always works as Player 1, and controllers are detected and
mapped automatically when you plug them in, including Xbox, PlayStation, Switch Pro and 8BitDo pads.

> **Note:** Buttons can't be remapped yet. The layouts below are fixed. You can choose which controller is which
> player, the turbo speed, and a two-player keyboard layout.

## Keyboard

### Player 1 (default)

| Console button | Key |
|---|---|
| D-pad | Arrow keys |
| B | <kbd>Z</kbd> |
| A | <kbd>X</kbd> |
| Select | <kbd>Right Shift</kbd> |
| Start | <kbd>Enter</kbd> |
| Turbo B / Turbo A (NES) | <kbd>A</kbd> / <kbd>S</kbd> |
| C (Genesis) | <kbd>C</kbd> |
| X, Y, Z (Genesis) | <kbd>Q</kbd>, <kbd>W</kbd>, <kbd>E</kbd> |
| L, R (SNES; PlayStation and PlayStation 2 L1, R1; N64 L, R) | <kbd>1</kbd>, <kbd>2</kbd> |
| L2, R2 (PlayStation and PlayStation 2); Z (N64) | <kbd>3</kbd>, <kbd>4</kbd> |
| L3, R3 (PlayStation 2) | <kbd>5</kbd>, <kbd>6</kbd> |
| Right stick (PlayStation 2; N64 C buttons) | <kbd>I</kbd> <kbd>J</kbd> <kbd>K</kbd> <kbd>L</kbd> |

Each system's own layout is described in [Supported systems](#supported-systems).

Keyboard input reaches the game only while the game has focus. When you switch away from GameVault, every held
key is released so nothing stays stuck down.

### Two players, one keyboard

Player 2 has no keys by default. Turn on **Settings → Controllers → Two players, one keyboard** to split the
keyboard:

| Console button | Player 1 (right half) | Player 2 (left half) |
|---|---|---|
| D-pad | Arrow keys | <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> |
| B | <kbd>,</kbd> (comma) | <kbd>F</kbd> |
| A | <kbd>.</kbd> (period) | <kbd>G</kbd> |
| Select | <kbd>Right Shift</kbd> | <kbd>Q</kbd> |
| Start | <kbd>Enter</kbd> | <kbd>E</kbd> |

There are no turbo keys in this layout. Some keyboards can't register many keys at once and may drop presses when
both players hold several buttons.

## Controllers

Buttons are mapped by **position**, so the NES layout (B on the left, A on the right) is the same on every pad.

| Console button | Pad position | Xbox label | PlayStation label |
|---|---|---|---|
| D-pad | D-pad and left stick | | |
| B | Bottom | A | Cross |
| A | Right | B | Circle |
| Turbo B | Left | X | Square |
| Turbo A | Top | Y | Triangle |
| Select | Back | View | Share / Create |
| Start | Start | Menu | Options |
| Rewind (hold) | Left shoulder | LB | L1 |
| Fast-forward (hold) | Right shoulder | RB | R1 |
| Pause / overlay | Guide, or hold Back + Start for 1 second | | |

Systems with more buttons (Genesis, SNES, PlayStation, N64) switch to their own layout while their games run; see
[Supported systems](#supported-systems).

Turbo buttons press and release the button repeatedly while held. Choose the rate in **Settings → Controllers →
Turbo speed** (15 presses per second by default).

By default a pad can't press Left and Right (or Up and Down) at once, like a real NES pad; the most recent
direction wins. **Settings → Advanced → Allow opposite directions** turns this off.

### Which controller is which player

- The keyboard is always on Player 1.
- Controllers join in the order you connect them: the first joins the keyboard on Player 1, the second becomes
  Player 2. A short notice says, for example, *8BitDo SN30 Pro is now Player 2*.
- **Settings → Controllers** shows a card per player. **Swap players** swaps them. **Pin to this player** makes a
  controller always take that player when it connects. Extra controllers are listed under **Other controllers**
  with **Use for Player 1** / **Use for Player 2**.

### If a controller disconnects

The game pauses and says which player's controller was lost. Reconnect it (it gets the same player back), or press
A or Start on another controller to take over. Resume from the toolbar.

### Using a controller in menus

Every connected controller can drive the Library and Settings: the D-pad or stick moves between items, the bottom
button selects, and the right button goes back or closes a menu.

## Mouse

NES Zapper games use the mouse: aim with the pointer and click to fire. GameVault sets this up automatically for
games its database knows; otherwise set **Game Details → Properties → Controls** to *Zapper*.

## Hotkeys

These keys never reach the game.

| Action | Keyboard | Controller |
|---|---|---|
| Pause / resume (in full screen, first leaves full screen) | <kbd>Esc</kbd> | Guide, or hold Back + Start |
| Full screen on/off | <kbd>F11</kbd> or <kbd>Alt</kbd>+<kbd>Enter</kbd> | |
| Rewind (hold) | <kbd>Backspace</kbd> (<kbd>Shift</kbd>+<kbd>Backspace</kbd> on Apple II) | Left shoulder |
| Fast-forward (hold) | <kbd>Tab</kbd> | Right shoulder |
| Back to before the last life lost | <kbd>F2</kbd> | |
| All codes off | <kbd>F3</kbd> | |
| Next disk side / next disc | <kbd>F4</kbd> | |
| Quick save / quick load | <kbd>F5</kbd> / <kbd>F8</kbd> | |
| Practice: retry / split | <kbd>F6</kbd> / <kbd>F7</kbd> | |
| Write down a password | <kbd>F9</kbd> | |
| Screenshot | <kbd>F12</kbd> | |
| Apple II Reset | <kbd>Ctrl</kbd>+<kbd>F12</kbd> | |

Controller hotkeys work only from controllers assigned to a player.

### Access keys

Press <kbd>Alt</kbd> to show letters on buttons, then press the letter.

| Screen | Keys |
|---|---|
| Anywhere | **L** Library |
| Library | **A** Add a ROM folder, **O** Open game file, **I** Import catalog, **D** Dump cartridge, **S** Search, **V** List/grid view |
| Game Details | **P** Play |
| Player | **B** Back, **P** Pause, **R** Reset, **S** Save states, **A** Assist, **D** Disks, **C** Codes, **N** Netplay, **F** Full screen, **T** Stats, **H** Challenges, **E** Achievements |
