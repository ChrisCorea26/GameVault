# Phase 10 — RetroAchievements (outline)

**Goal (SPEC §7.10):** achievements, leaderboards and rich presence from
[RetroAchievements](https://retroachievements.org), with their hardcore rules.

The full plan is written when Phase 9 is done. Known so far:
- **Account:** the player signs in with their own RetroAchievements account. GameVault stores only the
  login token, encrypted for the Windows user (DPAPI), never the password.
- **Hash:** the NES hash follows RetroAchievements' published method; tests use their reference values.
- **Runtime:**
  - Achievement conditions are evaluated after each frame, as the rcheevos reference implementation does.
  - The options are a port to C#, or binding to rcheevos (MIT licence) through native interop. The choice
    is made at plan time, based on rcheevos' test suite.
- **Hardcore mode** turns off what RetroAchievements forbids, including save-state loading, rewind, slow
  motion, codes and netplay.
- **Owner tasks:**
  - Register GameVault with RetroAchievements.
  - Get the emulator approved for hardcore unlocks.
  - Supply an account to test with.
