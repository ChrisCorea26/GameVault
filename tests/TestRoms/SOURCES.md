# Test ROM sources

Every file **committed** in this folder must be freely redistributable and recorded below (SPEC §11.3).

Suites whose license doesn't clearly permit redistribution are **downloaded at test time** instead.
`fetch-test-roms.sh` / `fetch-test-roms.ps1` read [`manifest.tsv`](manifest.tsv), download each file into
`.cache/` (git-ignored) and verify its SHA-256. CI runs the script before the tests.

**Never commit commercial ROMs, BIOS files, or dumps of licensed games, even "for testing."**
To test against your own dumps, set `GAMEVAULT_LOCAL_ROMS` to a local folder. Tests marked
`[LocalRomFact]` run only when it is set.

## Committed files

| File / folder | Suite | Author | Source URL | License / permission | Added in |
|---|---|---|---|---|---|
| _(none yet)_ | | | | | |

## Downloaded at test time

All downloads are pinned to commit `95d8f621ae55cee0d09b91519a8989ae0e64753b` of the community mirror
[christopherpow/nes-test-roms](https://github.com/christopherpow/nes-test-roms). Hashes are in `manifest.tsv`.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `nestest/nestest.nes`, `nestest/nestest.log` | nestest | Kevin Horton (kevtris) | No explicit redistribution license | Phase 1: CPU trace comparison |
| `instr_test-v5/*.nes` (16 single-test ROMs) | instr_test-v5 | Shay Green (blargg) | No explicit redistribution license | Phase 1: CPU instruction behavior |
