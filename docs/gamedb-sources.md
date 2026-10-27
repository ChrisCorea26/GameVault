# Where the bundled game data comes from

GV-006 and GV-007 (`docs/bugs.md`). Every file in `data/gamedb/` holds facts about a game (its hash, which accessory it
uses, where it keeps values in RAM), never data from the game. This page says where each fact comes from, so the owner
can check them against real cartridges (owner checklists in `docs/plans/phase-6.md` and `docs/plans/phase-9.md`).

## Hashes
Every SHA-1 is the headerless hash No-Intro lists, taken from the `.unh` (unheadered) entries of the No-Intro NES DAT
in libretro-database (`metadat/no-intro/Nintendo - Nintendo Entertainment System.dat`, version 2026.10.07). They match
what GameVault computes (`RomHasher`): for example Super Mario Bros. (World) is 40,960 bytes headerless with SHA-1
`facee9c577a5262dbe33ac4930bb0b58c8c037f7`.

## Accessory profiles (`input-*.json`)
| File | Games | Source |
|---|---|---|
| `input-zapper.json` | 19 dumps of Zapper-only games (Duck Hunt, Hogan's Alley, Wild Gunman, Gumshoe, Gotcha!, Freedom Force, Operation Wolf, Barker Bill's Trick Shooting, To the Earth, Shooting Range, Mechanized Attack, Laser Invasion, Chiller, Baby Boomer) | Games that need the Zapper to play. Games that also play with a controller (The Lone Ranger, The Adventures of Bayou Billy) and the Super Mario Bros. combo carts keep the default, since a Zapper on port 2 would leave Player 2 without a controller. |
| `input-four-score.json` | 36 dumps (Gauntlet II, Super Spike V'Ball, Nintendo World Cup, Bomberman II, R.C. Pro-Am II, Smash T.V., M.U.L.E., …) | NESdev Wiki, "Four player adapters" (the NES Four Score list; Japan-only titles left out). Super Off Road, Spot and Ultimate Basketball were not on that list and are left out until checked. |
| `input-power-pad.json` | 10 dumps (World Class Track Meet, Stadium Events, Dance Aerobics, Short Order + Eggsplode!, Street Cop, Athletic World, Super Team Games) | The Power Pad / Family Fun Fitness releases. The Super Mario Bros. + Duck Hunt + World Class Track Meet cart keeps the default. |
| `input-arkanoid.json` | Arkanoid (USA) | The only Western release for the Vaus paddle. |

Players can still pick other controls in Game Details → Properties.

## Memory maps (`memory/`)
| File | Facts | Source |
|---|---|---|
| `super-mario-bros-world.json` | `OperMode` $0770 (1 = game), `NumberofLives` $075A (lives − 1), `WorldNumber` $075F, `LevelNumber` $075C, `CoinTally` $075E, `PlayerStatus` $0756 | doppelganger's SMBDIS.ASM disassembly (mirror: https://gist.github.com/1wErt3r/4048722) |
| `legend-of-zelda-usa.json` | `GameMode` $12 (5 = play), `CurLevel` $10, `RoomId` $EB (16 columns), `HeartValues` $066F (containers − 1 in the high nibble, full hearts − 1 in the low), `InvRupees` $066D, `InvBombs` $0658 | aldonunez, zelda1-disassembly (https://github.com/aldonunez/zelda1-disassembly), which builds the "(U) (PRG0)" ROM, so only The Legend of Zelda (USA), not Rev 1 |
| `metroid-usa.json` | `GameMode` $1D (0 = playing), `MainRoutine` $1E (3 = game engine), `InArea` $74, energy digits in `HealthHi` $0107 / `HealthLo` $0106, `MissileCount` $6879 | Nick Mikstas, metroid-disassembly (https://github.com/nmikstas/metroid-disassembly), `Metroid_Defines.asm` |

Zelda and Metroid have no lives counter, so F2 (before you lost a life) doesn't apply to them; their maps give the
explored map (Zelda) and the stat overlay. The SMB score (six bytes, one digit each) can't be described by the format
yet, so the overlay leaves it out.

Not shipped, because the only source found is a community wiki summary: Mega Man 2 (lives $A8, health $06C0),
Castlevania (state $18, lives $2A, health $45), Kid Icarus (health $A6). They are on the owner checklist.

## Password definitions (`passwords/`)
| File | Facts | Source |
|---|---|---|
| `metroid-usa.json` | Shown while `GameMode` $1D = 1 and `TitleRoutine` $1F = $1A (`WaitForSTART`, entry 26 of `MainTitleRoutine`, which runs only while the finished password is on screen after GAME OVER); 24 characters in `PasswordChar00`–`PasswordChar17` ($699A–$69B1); values 0–63 are `0-9`, `A-Z`, `a-z`, `?`, `-` (`PasswordRow0`–`4`) | nmikstas/metroid-disassembly, `Metroid_Defines.asm` and `Bank00.asm` |

`tests/GameVault.Library.Tests/BundledGameDataTests.cs` loads every file and checks these facts against memory the test
writes.
