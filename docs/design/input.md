# Input — Ports, Assignment and Keyboard

**Status:** Accepted. Expands SPEC §5.4; cited by `docs/plans/phase-2.md` and `docs/plans/phase-3.md`.

SPEC §5.4 sets the zero-config goal and the player-1 keyboard defaults. It does not say how devices
are given to the two NES ports, what player 2 uses on a keyboard, or what happens on hot-plug. This
document fills those gaps.

## 1. Model

- **Ports.** The console has two controller ports, **Player 1** and **Player 2**. The Four Score
  (Phase 5) adds Player 3 and Player 4; everything below extends to four ports unchanged.
- **Sources.** An input source is either **the keyboard** or **one SDL game controller**.
- **Many-to-one.** A port can have several sources. Their buttons are OR-ed together, so a player can
  switch between keyboard and pad at any time without changing settings.
- **One-to-one for pads.** A game controller drives at most one port. The keyboard can drive two
  ports only when the two-player keyboard preset is on (§4).
- **Unassigned pads** still navigate the UI (§7); they just don't reach the game.

## 2. Core contract

- Once per frame, before `RunFrame()`, the platform layer samples every source, merges them per port
  and posts one `ControllerState` byte per port through the command queue (SPEC §5.1).
- Bit order matches the order the standard controller shifts bits out through `$4016`/`$4017`:
  A, B, Select, Start, Up, Down, Left, Right
  ([NESdev Wiki: Standard controller](https://www.nesdev.org/wiki/Standard_controller)).
- The core only ever sees these bytes. Device identity, turbo, remapping and the opposite-direction
  rule (§6) all live in the platform layer. That keeps the core deterministic and makes the per-frame
  input stream recordable for rewind, run-ahead and netplay.
- Sampling happens as late as possible before the frame starts, to meet the latency target (SPEC §10).

## 3. Default port assignment (zero-config)

1. **The keyboard is always on Player 1** by default, even when controllers are connected.
2. **Controllers fill ports in connection order.** Each newly connected pad goes to the lowest free
   port: the first pad joins the keyboard on Player 1, the second gets Player 2.
3. **At startup**, pads that are already connected are assigned in SDL device-index order. On
   Windows this follows the order in which the OS enumerated them, which is stable across launches in
   practice but not guaranteed.
4. **Extra pads** (a third pad with no Four Score) stay unassigned until a port frees up or the user
   assigns them.
5. **Pinned assignment (optional).** In *Settings → Input* the user can choose *Always use this
   controller for Player N*. Pins are stored by SDL controller GUID plus serial number when the device
   reports one (`SDL_GameControllerGetSerial`), otherwise by GUID plus name. A pinned pad takes its
   port when it connects and pushes an unpinned pad to the next free port. Without pins, assignment is
   purely by connection order and is not saved between sessions.

Each assignment change shows a short, plain-language notice, for example *"8BitDo SN30 Pro is now
Player 2"*.

## 4. Keyboard

### 4.1 Player 1 (default, SPEC §5.4)

| NES | Key |
|---|---|
| D-pad | Arrow keys |
| B | Z |
| A | X |
| Select | Right Shift |
| Start | Enter |
| Turbo B | A |
| Turbo A | S |
| L, R (SNES; PlayStation L1, R1; added in Phase 15) | 1, 2 |
| PlayStation L2, R2 (Phase 15) | 3, 4 |

The turbo keys sit beside Z/X in the same order (B on the left). They repeat at the turbo speed set in
Settings → Input (§6). The two-player preset (§4.3) uses A and S for Player 2's D-pad, so it has no turbo keys.

### 4.2 Player 2: no default binding

Player 2 has no keyboard binding out of the box. Two players on a single keyboard is uncommon, and a
second default layout competes with the hotkeys (§8) and with key rollover limits on cheap keyboards.

### 4.3 Two-player keyboard preset (opt-in, Phase 3)

Settings → Input offers a **Two players, one keyboard** preset. P1's default puts a hand on Z/X at the
far left, so the preset moves both players to opposite halves of the keyboard:

| NES | Player 1 (right half) | Player 2 (left half) |
|---|---|---|
| D-pad | Arrow keys | W A S D |
| B | `,` (comma) | F |
| A | `.` (period) | G |
| Select | Right Shift | Q |
| Start | Enter | E |

Turning the preset off restores §4.1. The settings page warns that some keyboards can't register
many keys at once and may drop presses when both players hold several buttons.

### 4.4 Focus

- Keyboard input reaches the game only while the game view has focus.
- When the game view loses focus or the window is deactivated (Alt+Tab, a dialog), **all held keys
  are released** for every port, so no button stays stuck down.
- Coming back to the window gives the keyboard back to the game view. If "Pause when inactive" paused the
  game, it resumes; a game the player paused stays paused.
- Clicking Pause, Reset or Resume doesn't take focus from the game, and closing a Player flyout (Save
  states, Assist, Practice) returns focus to it.

## 5. Hot-plug

- **Connect:** the pad gets its reserved port if it has one (see below), else its pinned port, else
  the lowest free port (§3).
- **Disconnect during play:**
  - Every button the pad was holding is released immediately.
  - If a game is running and the pad was assigned to a port, the game **pauses** and the overlay
    shows *"Player 1's controller was disconnected. Reconnect it, or press a button on another
    controller to take over."*
  - The port stays **reserved** for that physical pad until the game closes or the user reassigns
    the port, so a pad whose battery dies comes back to the same player instead of the lowest free port.
  - The game does not resume on its own after reconnecting; the player resumes from the overlay.
- **Take over:** while a port is waiting for its pad, pressing A or Start on an unassigned pad gives
  it that port.

## 6. Controller mapping defaults

SDL's GameController API reports buttons by **position**, so the defaults are positional:

| NES | Pad (positional) | Xbox label | PlayStation label |
|---|---|---|---|
| D-pad | D-pad and left stick | | |
| B | South | A | Cross |
| A | East | B | Circle |
| Select | Back | View | Share / Create |
| Start | Start | Menu | Options |
| Turbo B | West | X | Square |
| Turbo A | North | Y | Triangle |

- South/east keeps the NES layout (B on the left, A on the right) on every pad. Set
  `SDL_HINT_GAMECONTROLLER_USE_BUTTON_LABELS=0` so Nintendo-layout pads are also positional.
- **West and North are turbo B / turbo A** by default (SPEC §5.4, §7.6). While held, turbo presses
  the button for 2 frames and releases it for 2 frames (15 presses per second at 60 Hz). The rate is
  derived from the frame count, so it is deterministic. *Settings → Input → Turbo speed* offers 30, 20,
  15 (default), 12, 10 and 7.5 presses per second (a cycle of 2-8 frames, pressed for the first half,
  rounded up). The same speed applies to the keyboard turbo keys (§4.1).
- **Left stick** drives the D-pad as 8-way input with a radial dead zone of 50%.
- **Opposite directions.** A real NES pad can't press Left+Right or Up+Down at the same time, and some
  games misbehave when they see it (see the Standard controller page above). By default the most
  recently pressed direction on each axis wins. *Settings → Advanced → Allow opposite directions*
  turns this off.
- Remaps are stored per controller model (SDL GUID) and per player. Per-game overrides arrive with
  game profiles (Phase 5, SPEC §7.5).

### 6.1 Other systems (Phase 11)
Sega SG-1000 and Master System pads use the same bindings. **B** (keyboard Z) is **Button 1**, **A**
(keyboard X) is **Button 2**, and **Start** (keyboard Enter) is the console's **Pause** button, which is
an NMI on both consoles. Select does nothing on these systems.

### 6.2 Genesis / Mega Drive (Phase 13)
The controller state is 16 bits: the NES pad's 8 buttons, then C, X, Y, Z (and L, R for later systems).
On the Genesis, **B is the pad's A**, **A is its B** and **Select is Mode**, so the keyboard keeps the pad's
order: Z, X, C are A, B, C, and Q, W, E are X, Y, Z (new defaults; the NES ignores them).

While a Genesis game runs, controllers switch to the 6-button layout (positional):

| Genesis | Pad | Xbox label |
|---|---|---|
| A, B, C | West, South, East | X, A, B |
| X, Y, Z | Left shoulder, North, right shoulder | LB, Y, RB |
| Mode | Back | View |
| Start | Start | Menu |

The 6-button protocol is the default; Game properties' `pad: "3"` gives a game the 3-button pad. Netplay
(protocol 2) and challenge recordings (version 2) carry all 16 bits.

### 6.3 Super Nintendo (Phase 14)
The SNES pad's buttons are the controller state's own: B, A, Y, X, L, R, Select and Start. Controllers use the
pad's layout while an SNES game runs:

| SNES | Pad | Xbox label |
|---|---|---|
| B, A | South, East | A, B |
| Y, X | West, North | X, Y |
| L, R | Left shoulder, right shoulder | LB, RB |

On the keyboard, Z and X stay B and A, and the turbo keys A and S become Y and X (SNES games have no turbo);
Q/W/E (X, Y, Z for the Genesis) are unused. Since Phase 15, 1 and 2 are L and R.

### 6.4 PlayStation (Phase 15)
The digital pad (SCPH-1080) maps onto the controller state by position, like the SNES pad, with two new bits
(14 and 15) for L2 and R2. Netplay and challenge recordings already carry all 16 bits.

| PlayStation | Controller state | Pad | Xbox label | Keyboard |
|---|---|---|---|---|
| Cross, Circle | B, A | South, East | A, B | Z, X |
| Square, Triangle | Y, X | West, North | X, Y | A, S (no turbo, as for the SNES) |
| L1, R1 | L, R | Left shoulder, right shoulder | LB, RB | 1, 2 |
| L2, R2 | L2, R2 | Left trigger, right trigger | LT, RT | 3, 4 |
| Select, Start | Select, Start | Back, Start | View, Menu | Right Shift, Enter |

A trigger counts as pressed past half way and released below a quarter, so a resting trigger never flickers.
The DualShock's sticks and vibration are left for later.

## 7. Game input vs. UI navigation

- **In the Library and other pages:** every connected pad navigates the UI (D-pad and stick move XY
  focus, South = activate, East = back), whether or not it is assigned to a port (SPEC §9.3).
- **While a game is running and the game view has focus:** assigned pads go to the game. Unassigned
  pads are ignored, except for the take-over press in §5.
- **Opening the overlay:** the Guide/Home button on any assigned pad. Some pads don't expose Guide, or
  another app (for example Steam or Game Bar) captures it, so holding **Back + Start for 1 second**
  also opens the overlay. On the keyboard, **Esc** opens it.
- **Background:** pad input is ignored while the window is not in the foreground
  (`SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS=0`).

## 8. Hotkeys

These keys never reach the game, so game bindings can't use them:

| Action | Keyboard | Controller |
|---|---|---|
| Overlay / pause (in full screen, Esc first leaves full screen) | Esc | Guide, or hold Back + Start |
| Full screen on/off (added after Phase 8) | F11 or Alt+Enter | — |
| Rewind (hold) | Backspace | Left shoulder |
| Fast-forward (hold) | Tab | Right shoulder |
| Back to before the last life lost (SPEC §7.9, Phase 9) | F2 | — |
| All codes off (SPEC §7.8, added in Phase 8) | F3 | — |
| Next disk side (Famicom Disk System, added in Phase 7); next disc (PlayStation .m3u games, Phase 15) | F4 | — |
| Quick save / quick load | F5 / F8 | — |
| Practice: retry / split (SPEC §7.6, added in Phase 6) | F6 / F7 | — |
| Capture password (SPEC §7.3, added in Phase 6) | F9 | — |
| Screenshot | F12 | — |

Controller hotkeys work only from pads assigned to a port.

## 9. Settings → Input

- One card per port listing its sources (*Keyboard*, *Xbox Wireless Controller*), with
  **Swap players**, **Remap…**, **Pin to this player**, and **Two players, one keyboard**.
- A list of unassigned controllers, each with **Use for Player N**.
- The same port cards appear in the in-game overlay, so a player can swap without leaving the game.
- Under `--sample-data`: the keyboard and an *Xbox Wireless Controller* on Player 1, an *8BitDo SN30 Pro*
  on Player 2, and one unassigned *DualSense Wireless Controller*.

## 10. Where it lives

- **Assignment policy** (§3, §5) is a pure class with no SDL or WinUI types. It takes connect,
  disconnect and button events and returns port assignments. It lives in a `net10.0` assembly so it
  runs in the Linux CI job, alongside the service interfaces in `GameVault.ViewModels/Services`, which
  follows the Phase 0 decision.
- **SDL device handling, keyboard sampling, turbo and merging** live in `GameVault.Platform/Input`.
- **Keyboard events** come from the `GameView` control's `KeyDown`/`KeyUp` handlers, which forward
  to the platform input service. The handlers contain no logic.

## 11. Phasing

| Phase | Scope |
|---|---|
| 2 | `ControllerState` and port merging; keyboard on Player 1 (§4.1); focus release (§4.4); opposite-direction rule (§6) |
| 3 | SDL controllers, default mapping, assignment policy and hot-plug (§3, §5); UI navigation (§7); Settings → Input with sample data (§9); two-player keyboard preset (§4.3) |
| 5 | Four Score (Players 3–4); per-game overrides; Zapper input type from game profiles |

## 12. Tests

- **Assignment policy:** connection order, lowest free port, third pad left unassigned, pins
  displacing unpinned pads, reservation on disconnect, take-over, swap.
- **Merging:** OR across sources; the opposite-direction rule, with and without the Advanced setting;
  turbo timing derived from the frame count, never from wall-clock time.
- **Focus:** losing focus releases every held key.
- **Core:** a `$4016` strobe/read test checks the bit order in §2.
- **ViewModels:** the Settings → Input ViewModel shows the sample devices and applies swaps through a
  fake input service.
