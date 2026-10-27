---
edition: "Edition 11 · October 2026 · Covers NES and Famicom Disk System, SG-1000 and Master System, Apple II, Genesis / Mega Drive, Super Nintendo, PlayStation, Nintendo 64 and PlayStation 2"
---

# Introduction

GameVault is a Windows app for playing and collecting classic console and computer games. It emulates the original
hardware accurately, sets itself up with no configuration, and keeps a library of your collection: what you own,
what you've beaten, your playtime, notes, passwords and save states.

GameVault plays games **you supply yourself**, such as dumps of cartridges and discs you own, or freely licensed
homebrew. It never includes, downloads or links to commercial games, BIOS files or system ROMs. It does come with
four freely licensed NES homebrew games, so there is something to play the first time you start it (see
[Bundled homebrew](#bundled-homebrew)).

## Supported systems at a glance

| System | Game files | Needs your own BIOS or system ROM? |
|---|---|---|
| Nintendo Entertainment System (NES) / Famicom | `.nes` | No |
| Famicom Disk System | `.fds` | **Yes**: `disksys.rom` (8 KB) |
| Sega SG-1000 / SC-3000 | `.sg`, `.sc` | No |
| Sega Master System | `.sms` | No |
| Apple II, II+, IIe | `.dsk`, `.do`, `.po`, `.nib`, `.woz` | **Yes**: system ROM (12 or 16 KB) and Disk II boot ROM (256 bytes) |
| Sega Genesis / Mega Drive | `.md`, `.gen`, `.smd`, `.bin` | No |
| Super Nintendo / Super Famicom | `.sfc`, `.smc` | Only for DSP and ST01x chip games: that chip's program ROM, such as `dsp1b.rom` (8 KB) |
| Sony PlayStation | `.cue` + `.bin`, `.chd`, `.m3u`, `.exe` | **Yes**: a 512 KB console BIOS such as SCPH-1001 |
| Nintendo 64 | `.z64`, `.v64`, `.n64` | No |
| Sony PlayStation 2 | `.iso`, `.chd`, `.cue` + `.bin`, `.elf` | **Yes, for discs**: a 4 MB console BIOS such as SCPH-39001. Homebrew `.elf` programs run without it. |

Cartridge games can also be inside a `.zip` file. Details for each system, including what isn't supported yet, are
in [Supported systems](#supported-systems).

## What you must supply

GameVault works out of the box, but some things only you can provide. This checklist covers everything.

| Item | Required? | Where it goes | More |
|---|---|---|---|
| **Your games** (ROM files and disc images) | Yes, beyond the bundled homebrew | Any folder; add it in the Library | [Quick start](#quick-start) |
| **Famicom Disk System BIOS** (`disksys.rom`, 8 KB) | Only for `.fds` games | Settings → Disk System | [Famicom Disk System](#famicom-disk-system) |
| **Apple II system ROM** (12 KB for II/II+, 16 KB for IIe) and **Disk II boot ROM** (256 bytes) | Only for Apple II | Settings → Apple II | [Apple II](#apple-ii-ii-and-iie) |
| **Super Nintendo DSP chip ROMs** (`dsp1b.rom` and the others, 8 KB each; `st010.rom` and `st011.rom`, 52 KB) | Only for games with a DSP or ST01x chip, such as *Super Mario Kart* | Settings → Super Nintendo | [Super Nintendo](#super-nintendo) |
| **PlayStation BIOS** (512 KB, e.g. SCPH-1001) | Only for PlayStation discs | Settings → PlayStation | [PlayStation](#sony-playstation) |
| **PlayStation 2 BIOS** (4 MB, e.g. SCPH-39001) | Only for PlayStation 2 discs | Settings → PlayStation 2 | [PlayStation 2](#sony-playstation-2) |
| **Catalog DAT files** (Logiqx XML, such as No-Intro or Redump) | Optional: names your games and gives a complete checklist | Library → Import catalog | [Importing a catalog](#importing-a-catalog) |
| **Cover pictures** | Optional | Game Details → Choose a picture… | [Game Details](#game-details) |
| **Game controller** | Optional; the keyboard works | Plug it in | [Controls](#controls) |
| **RetroAchievements account** | Optional | Settings → RetroAchievements | [RetroAchievements](#retroachievements) |
| **Code collection** (`.xlsx` or `.csv`) | Optional, NES codes | Settings → Codes | [Code collections](#code-collections) |
| **INL Retro-Prog dumper**, its software and USB driver | Optional, for dumping NES cartridges | Settings → Cartridge dumper | [Cartridge dumper](#cartridge-dumper-inl-retro-prog) |
| **A second PC on the same network** with the same game file | Optional, for netplay | Player → Netplay | [Netplay](#netplay) |

> **Make your own dumps.** BIOS files, system ROMs and games are copyrighted. Dump them from hardware you own. The
> NESdev, Apple II and PlayStation communities document how to do this for each system.

## About this manual

This manual describes GameVault as of the edition on the title page. Keys are shown like <kbd>F5</kbd>, and screen
labels in **bold**. Paths like `%LOCALAPPDATA%\GameVault\` can be pasted into the File Explorer address bar.
