# Input — Ports, Assignment and Keyboard

**Status:** Draft. Proposed expansion of SPEC §5.4, to be cited by `docs/plans/phase-2.md` and
`docs/plans/phase-3.md`. Decisions marked **(open)** need sign-off before the phase that uses them.

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

### 4.2 Player 2: no default binding

Player 2 has no keyboard binding out of the box. Two players on a single keyboard is uncommon, and a
second default layout competes with the hotkeys (§8) and with key rollover limits on cheap keyboards.

### 4.3 Two-player keyboard preset (opt-in) **(open)**

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

## 5. Hot-plug

- **Connect:** the pad gets its reserved port if it has one (see below), else its pinned port, else
  the lowest free port (§3).
- **Disconnect during play:**
  - Every button the pad was holding is released immediately.
  - If a game is running and the pad was assigned to a port, the game **pauses** and the overlay
    shows *"Player 1's controller was disconnected. Reconnect it, or press a button on another
    controller to take over."* **(open: pause automatically, or keep running?)**
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
| — | West, North | X, Y | Square, Triangle |

- South/east keeps the NES layout (B on the left, A on the right) on every pad. Set
  `SDL_HINT_GAMECONTROLLER_USE_BUTTON_LABELS=0` so Nintendo-layout pads are also positional.
- **West and North are left unbound** by default. They are the natural turbo B / turbo A slots when the
  user turns turbo on (SPEC §5.4, §7.6) **(open: bind them to plain B/A instead?)**.
- **Left stick** drives the D-pad as 8-way input with a radial dead zone of 50%.
- **Opposite directions.** A real NES pad can't press Left+Right or Up+Down at the same time, and some
  games misbehave when they see it (see the Standard controller page above). By default the most
  recently pressed direction on each axis wins. *Settings → Advanced → Allow opposite directions*
  turns this off.
- Remaps are stored per controller model (SDL GUID) and per player. Per-game overrides arrive with
  game profiles (Phase 5, SPEC §7.5).

## 7. Game input vs. UI navigation

- **In the Library and other pages:** every connected pad navigates the UI (D-pad and stick move XY
  focus, South = activate, East = back), whether or not it is assigned to a port (SPEC §9.3).
- **While a game is running and the game view has focus:** assigned pads go to the game. Unassigned
  pads are ignored, except for the take-over press in §5.
- **Opening the overlay:** the Guide/Home button on any assigned pad. Some pads don't expose Guide, or
  another app (for example Steam or Game Bar) captures it, so holding **Back + Start for 1 second**
  also opens the overlay. On the keyboard, **Esc** opens it **(open)**.
- **Background:** pad input is ignored while the window is not in the foreground
  (`SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS=0`).

## 8. Hotkeys

These keys never reach the game, so game bindings can't use them:

| Action | Keyboard | Controller |
|---|---|---|
| Overlay / pause | Esc **(open)** | Guide, or hold Back + Start |
| Rewind (hold) | Backspace | Left shoulder |
| Fast-forward (hold) | Tab | Right shoulder |
| Quick save / quick load | F5 / F8 | — |
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

## Open questions

1. Ship the two-player keyboard preset in Phase 3, and are the keys in §4.3 right?
2. Pause automatically when an assigned controller disconnects?
3. Esc for the overlay, or leave Esc to exit fullscreen only?
4. West/North: unbound by default (turbo slots), or extra B/A?
