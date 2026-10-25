# UI screens

Screen notes for SPEC §9.2. The XAML pages are the source of truth. Preview them with
`dotnet run --project src/GameVault.App -- --sample-data`, XAML Hot Reload and the Live Visual Tree.

| Screen | Implemented |
|---|---|
| Library (home) | Phase 5: my games / complete checklist, search, region/genre/publisher/year/status filters, Add a ROM folder with scan progress, summary. `--sample-data=empty` previews first run |
| Game Details | Phase 5: cover picture, ownership, status, favorite, rating, notes, playtime, save-state timeline, properties, Play (`--page=gamedetails`) |
| Player | Phases 2–5: game view (D3D11 presets), pause/reset, save-state timeline flyout, resume prompt, rewind bar, Zapper crosshair, controller notices |
| Settings | Library folders, video (picture style, TV pixel shape, crop), audio, gameplay, rewind memory, controllers, advanced |
| Catalog Import wizard | Phase 5: choose a DAT → preview → import → done (`--page=catalogimport`) |
| Dump Cartridge wizard | — |
