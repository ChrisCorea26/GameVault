# UI screens

Screen notes for SPEC §9.2. The XAML pages are the source of truth. Preview them with
`dotnet run --project src/GameVault.App -- --sample-data`, XAML Hot Reload and the Live Visual Tree. Player-facing
descriptions of every screen are in the user manual (`docs/manual/`).

| Screen | Implemented |
|---|---|
| Shell | `MainWindow.xaml`: custom title bar with a *Sample data* badge, `NavigationView` with Library and Settings, a profile switcher ("Who's playing?") in the pane footer when there's more than one family profile (Phase 9). Title bar and pane hide in full screen |
| Library (home) | Phases 5, 11: my games / complete checklist, all / owned only, search, system/region/genre/publisher/year/status filters (System only with more than one system), grid/list toggle, Add a ROM folder with scan progress, Open game file, Import catalog, Dump cartridge, collection summary. `--sample-data=empty` previews first run |
| Game Details | Phases 5–8: cover picture, editable title for uncataloged games, ownership, status, favorite, rating, notes, playtime, save-state timeline, properties (Controls, Reduce input lag, Reduce flicker), passwords, journal (notes, screenshots, maps), codes, Play (`--page=gamedetails`) |
| Player | Phases 2–16: game view (D3D11 presets), back, pause/reset, Save states, Assist and Practice flyouts, disk side / disc picker (FDS, PlayStation `.m3u`), Apple II Disks menu, Codes (NES), Netplay, full screen, Stats overlay, Challenges, Achievements; F9 password panel, resume bar, "Previously on…" recap card, rewind bar, Zapper crosshair, controller notices, play-limit card, RetroAchievements notifications (`--page=player` shows a test pattern) |
| Settings | Family (profiles, parent PIN, daily limits), RetroAchievements (sign-in, hardcore), Library folders, Video (picture style, TV pixel shape, hide screen edges, full screen), Audio (volume), Gameplay (reduce input lag, pause in background, rewind memory), Controllers (port cards, pins, swap, two players one keyboard, turbo speed), Cartridge dumper path, Codes (code collections), Disk System BIOS, Apple II ROMs, PlayStation BIOS and fast boot, Nintendo 64 Expansion Pak, PlayStation 2 BIOS and fast boot, Advanced (allow opposite directions), Restore defaults (`--page=settings`) |
| Catalog Import wizard | Phase 5: choose a DAT → preview → import → done (`--page=catalogimport`). Any system's Logiqx DAT; the system is detected from the DAT name |
| Dump Cartridge wizard | Phase 6: pick game (catalog search) and board → insert → dump → result (`--page=dumpcartridge`, simulated dumper) |
