# Verified challenges (SPEC §7.9)

A challenge is a moment in a game plus a goal: "from here, get three crystals within a minute".
Players share two kinds of file:

| File | Made by | Holds |
|---|---|---|
| `.gvchallenge` | Whoever sets the challenge (Player → Challenges → *Save challenge…*) | The start state, the goal, an optional time limit |
| `.gvresult` | Whoever takes it (*Save result…* after finishing) | Every frame's buttons from the start state to the finish |

## How results are checked

*Check a result…* doesn't trust the frame count in the file. It loads the challenge's start state on a
fresh console, replays the recorded buttons frame by frame, and accepts the result only if the goal
first holds **exactly** on the last recorded frame. A result edited to claim a faster time, or recorded
on a different game, fails the check.

While an attempt runs, rewind, loading states and Game Genie codes are off, and starting netplay or
loading another game ends the attempt.

## Goals

Goals use the game's memory map (`docs/memory-maps.md`): pick a named value, a comparison and a
number. The values a goal uses are copied into the challenge file, so the person taking it doesn't
need the memory map. Games without a memory map can still *take* challenges, but can't make them.

## File format

Both files are indented JSON (web casing). A challenge:

```json
{
  "version": 1,
  "id": "6f0c…",
  "romSha1": "…",                 // headerless SHA-1; the challenge only runs on this game
  "gameTitle": "…",
  "name": "Crystal sprint",
  "note": "optional text",
  "goal": [ { "value": "crystals", "op": ">=", "target": 3 } ],
  "values": { "crystals": { "address": "0x0050" } },
  "timeLimitFrames": 3606,       // null for no limit; 60.0988 frames per second
  "startState": "base64 save state",
  "createdBy": null,
  "created": "2026-10-26T…"
}
```

A result: `challengeId`, `romSha1`, `player`, `frames`, `inputs` (base64 of a run-length list of
`(count, player-1 buttons, player-2 buttons)` varints) and `finished`.
