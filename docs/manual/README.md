# User manual

`GameVault-User-Manual.pdf` is the end-user manual and setup guide. It's built from the numbered Markdown chapters in
this folder, which are the source of truth. Edit the chapters, never the PDF.

## Building

Needs **pandoc** and a Chromium-based browser (Chrome, Chromium or Edge, version 131 or later).

```bash
docs/manual/build-manual.sh          # Linux/macOS (set BROWSER=/path/to/chrome if it isn't found)
```
```powershell
docs/manual/build-manual.ps1         # Windows (uses Edge or Chrome; -Browser <path> to choose)
```

Chapters are joined in file-name order. `template.html` holds the title page (its edition line comes from the
`edition:` field at the top of `01-introduction.md`) and `manual.css` the print styles. Commit the rebuilt PDF with
the Markdown changes.

## Keeping it up to date

Update the manual in the same change whenever a user-visible feature changes, and **always when a phase adds a new
system**. For a new system:

1. `01-introduction.md`: add a row to *Supported systems at a glance*; add any BIOS or system ROM to *What you must
   supply*; update the `edition:` line.
2. `04-systems.md`: add a section in the same shape as the others: a table with **Files**, **You supply** and
   **Saves**, then setup steps, controls, *Not supported yet*, and a table of the error messages a player can hit
   (copied from the code, word for word).
3. `07-controls.md` and `08-settings.md`: new keys, controller layouts and Settings sections, with their defaults.
4. `11-data-and-troubleshooting.md`: new save file types and common problems.
5. `12-edition-history.md`: a new edition row saying what changed.
6. Rebuild the PDF and check every internal link resolves and the new pages look right.

Describe what the code does, not what the plan intended: check labels, defaults and messages against the XAML,
ViewModels and `AppSettings`.
