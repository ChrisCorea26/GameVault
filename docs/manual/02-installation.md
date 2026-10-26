# Installing GameVault

## System requirements

| | Minimum |
|---|---|
| Operating system | Windows 10 version 1809 or later, or Windows 11 |
| Processor | x64 (recommended), ARM64 or 32-bit x86. A 4-core CPU from 2018 or later runs the 8- and 16-bit systems comfortably. PlayStation and Nintendo 64 need a faster CPU; heavy N64 3D scenes may run below full speed. |
| Memory | 4 GB. GameVault itself uses under 400 MB with the default rewind memory. |
| Graphics | Any GPU with Direct3D 11 |
| Audio | Any Windows audio device |
| Disk | About 300 MB for the app, plus your games |
| Optional | A USB or Bluetooth game controller; an internet connection for RetroAchievements and GameGenie.com codes; an INL Retro-Prog for dumping cartridges |

GameVault is a portable (unpackaged) app. It doesn't need administrator rights, doesn't use the Microsoft Store, and
keeps all its data in `%LOCALAPPDATA%\GameVault\`.

## Option A: a ready-made copy

If you were given a GameVault folder or `.zip`:

1. Extract it anywhere you like, for example `C:\Games\GameVault`.
2. Run `GameVault.exe`.
3. If Windows SmartScreen says *Windows protected your PC*, choose **More info → Run anyway**. This appears for
   apps that aren't code-signed.

To uninstall, delete the folder. To also remove your library, saves and settings, delete
`%LOCALAPPDATA%\GameVault\` (back it up first if you want to keep your saves).

## Option B: build it from source

You need:

| Tool | Where to get it | Needed for |
|---|---|---|
| **.NET 10 SDK** | <https://dotnet.microsoft.com/download> or `winget install Microsoft.DotNet.SDK.10` | Always |
| **Git** | `winget install Git.Git` | Getting the source |
| **CMake** and a C compiler | Visual Studio 2022 or later with the *Desktop development with C++* workload, or `winget install Kitware.CMake` plus the Visual Studio Build Tools | RetroAchievements only. Without them GameVault still builds and runs, with RetroAchievements turned off. |
| Visual Studio with the *Windows application development* workload | <https://visualstudio.microsoft.com> | Optional, for editing the app |

Then, in PowerShell:

```powershell
git clone https://github.com/ChrisCorea26/GameVault.git
cd GameVault
dotnet build GameVault.sln -c Release
dotnet run --project src/GameVault.App -c Release
```

To make a stand-alone folder you can copy to another PC (it doesn't need .NET installed):

```powershell
dotnet publish src/GameVault.App -c Release -r win-x64 --self-contained -o publish\GameVault
```

Then run `publish\GameVault\GameVault.exe`. Use `-r win-arm64` or `-r win-x86` for other processors.

> **Note:** The first build compiles the RetroAchievements component with CMake. If CMake or a C compiler is
> missing, you'll see a build warning and RetroAchievements will say *The RetroAchievements component is missing
> from this copy of GameVault*. Everything else works.

## Trying GameVault without any games

To look around before you have any games, start GameVault with sample data:

```powershell
GameVault.exe --sample-data
```

or, from the source folder, `dotnet run --project src/GameVault.App -- --sample-data`. This fills every screen
with a made-up library, save states and passwords, and shows a *Sample data* badge in the title bar. Nothing is
saved. `--sample-data=empty` shows the first-run screens with an empty library.

## Firewall prompt

The first time you host or join a netplay game, Windows Defender Firewall may ask whether GameVault can use the
network. Allow it on **Private networks** for netplay to work. See [Netplay](#netplay).
