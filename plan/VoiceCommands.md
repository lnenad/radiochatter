# Voice Commands ("Push-to-Talk") — Design & Progress

> Handoff document. If you are picking this up mid-way, read **Progress** at the bottom first —
> it says exactly what is done, what is in flight, and how to verify each step.

## Goal

Let the player *speak* to Tower/AWACS over a push-to-talk key and get a spoken, in-fiction
response driven by live game state:

- "Tower, Falcon 1-1, request takeoff" → takeoff clearance (or "unable")
- "Request landing" / "inbound for landing" → landing clearance with runway
- "Overwatch, request picture" / "bogey dope" → BRA call on the nearest contact (or "picture clean")
- "Request vector to target" → vector to the sorted target (or nearest contact / "negative")
- "Vector to home plate" / "request RTB" → bearing/range to home base
- "Radio check" → "read you loud and clear"
- Anything unintelligible or unmatched → "say again" from the addressed station

**No LLM.** The command vocabulary is a small closed grammar; a deterministic keyword/intent
matcher is instant, testable, and never hallucinates. Speech-to-text runs locally in the
existing Python sidecar (faster-whisper on CPU), biased toward radio phraseology with an
`initial_prompt`.

## Architecture

```
[PTT key held]                                 (mod, main thread)
   └─ UnityEngine.Microphone records mono @16 kHz into an AudioClip
[PTT released]
   └─ samples pulled on main thread → background Task:
        WAV-encode → POST /transcribe {audio_b64, prompt} → sidecar
                                                              │
   sidecar (Python): faster-whisper base.en (CPU int8) ───────┘
        returns {"text": "tower falcon one one request takeoff"}
   └─ RadioEventBus.Enqueue(PlayerVoiceCommand, Text=transcript)   (thread-safe, existing bus)

CommsDirector.DrainPatchedEvents (main thread, next poll tick)
   └─ VoiceIntentParser.Parse(transcript) → intent + addressed station
   └─ HandleVoiceCommand: renders response through the EXISTING pipeline
        (PhraseEngine → Queue → Pocket TTS → RadioAudioPlayer, subtitles, radio filter)
```

Design decisions (do not re-litigate):
- **STT in the sidecar**, not in-process: keeps native deps out of the net472 plugin and
  reuses the sidecar's model download/offline-retry/venv machinery.
- **faster-whisper `base.en`, CPU, int8, beam_size=2, vad_filter=True.** ~75 MB download to
  the sidecar's existing HF cache. `initial_prompt` carries callsigns + brevity vocabulary.
- **Capture in-game with `UnityEngine.Microphone`** (AudioModule, already referenced): avoids
  mic-device contention and works on Windows and Linux without extra native code.
- **Whole-utterance, not streaming.** Record while held, transcribe on release. A ~0.5–1 s
  pause before the controller answers reads as realistic radio.
- **Intent parsing in C#** (`VoiceIntentParser`): normalized-token keyword rules with a few
  synonyms per intent. Unknown → "say again" (this is the "I don't understand" behavior).
- Player's own voice is **not** re-synthesized; the recognized transcript is shown as a
  `[PILOT]` subtitle, then the station responds via TTS.
- STT failure modes: sidecar down / model loading / empty transcript all resolve to a
  "say again" response (or subtitles-only if TTS is also down — existing fallback).

## Sidecar changes (`sidecar/server.py`)

- New engine class `WhisperSttEngine` (lazy import of `faster_whisper`), loaded on its own
  background thread at startup, with the same HF-offline retry pattern as the TTS engine.
- New endpoint `POST /transcribe`, JSON body `{"audio_b64": "<base64 WAV>", "prompt": "..."}`
  → `200 {"text": "..."}`, `503 {"error": ...}` while loading/failed, `400` on bad input.
- `GET /health` gains an `"stt"` field: `"ok" | "loading" | "error" | "disabled"`.
  The existing TTS `status` semantics are unchanged (the mod's SidecarSupervisor keys off
  `status`/`"loading"`/`"error"` in the 503 body only, so this is backward compatible).
- New args: `--stt-model` (default `base.en`; empty string disables STT).
- `requirements.txt`: add `faster-whisper`.
- `run_sidecar.bat` dependency probe extended to check `faster_whisper` (so existing installs
  pick up the new dep); `run_sidecar.sh` gets an equivalent check before `exec`.

## Mod changes (`src/RadioChatter/`)

New files:
- `Speech/VoiceCommandController.cs` — PTT state machine (Idle/Recording), Microphone
  start/stop, sample extraction, background WAV-encode + `/transcribe` POST, enqueues
  `PlayerVoiceCommand` on the RadioEventBus. Draws a small "● VOICE" recording /
  "· · · VOICE" transcribing indicator (OnGUI, above the sidecar status line).
- `Comms/VoiceIntentParser.cs` — transcript → `VoiceIntent { Kind, Station }`.
  Kinds: `RequestTakeoff, RequestLanding, RequestPicture, RequestVector, RequestVectorHome,
  RadioCheck, Unknown`. Station: `Tower | Awacs | Unspecified` from leading address
  ("tower ...", "overwatch ...", AWACS callsign).

Modified files:
- `Plugin.cs` — `[VoiceCommands]` config section: `Enabled` (true), `PushToTalkKey`
  (KeyCode, default `RightAlt`), `MicrophoneDevice` (empty = OS default),
  `MaxCommandSeconds` (8), `ShowRecognizedText` (true). Wires the controller.
- `RadioRuntime.cs` — ticks the controller.
- `Comms/Events.cs` — new `RadioEventType.PlayerVoiceCommand`; `IRadioOutput.ShowSubtitle`.
- `Audio/RadioAudioPlayer.cs` — implements `ShowSubtitle` (subtitle without TTS).
- `Comms/CommsDirector.cs` — `PlayerVoiceCommand` case in `DrainPatchedEvents` →
  `HandleVoiceCommand(snapshot, text, now)`. Responses reuse existing queue/dedup machinery
  with `cooldown=0`, short duplicate window, and a new `bypassStartupHold` flag so a
  player-requested AWACS answer is not parked by the startup radio gate. `QueueContact`
  gains a `force` flag (skips `ShouldConveyContactInfo`). Voice-granted clearances set the
  same flags as the automatic ones (`_takeoffClearanceAnnounced`, `_finalAnnounced`, …) so
  the state machine stays consistent.
- `Speech/phrases.json` (embedded) — new keys:
  `tower_unable`, `tower_continue_inbound`, `awacs_picture_clean`, `awacs_no_target`,
  `awacs_vector_home`, `radio_check_tower`, `radio_check_awacs`, `say_again_tower`,
  `say_again_awacs`.

### Intent → response mapping

| Intent | Condition | Response (role, phrase key) |
|---|---|---|
| RequestTakeoff | grounded near home base | Tower `tower_takeoff` (sets clearance flags) |
| RequestTakeoff | otherwise | Tower `tower_unable` |
| RequestLanding | within 35 km of home | Tower `tower_final` (sets `_finalAnnounced`) |
| RequestLanding | farther, home known | Tower `tower_continue_inbound` (with range) |
| RequestPicture | contacts on scope | AWACS `awacs_picture` (forced, nearest contact) |
| RequestPicture | no contacts | AWACS `awacs_picture_clean` |
| RequestVector | target sorted / any contact | AWACS `awacs_vector` (forced) |
| RequestVector | no contacts | AWACS `awacs_no_target` |
| RequestVectorHome | home known | AWACS `awacs_vector_home` |
| RadioCheck | addressed tower | Tower `radio_check_tower` |
| RadioCheck | otherwise | AWACS `radio_check_awacs` |
| Unknown/empty | addressed tower | Tower `say_again_tower` |
| Unknown/empty | otherwise | AWACS `say_again_awacs` |

## Testing

1. **Sidecar standalone:** install deps into the sidecar venv, run `server.py`, then
   `tools/test_transcribe.py` (added by this work) posts a WAV
   (`radiochatter_wingman_paul_test.wav` in repo root works) to `/transcribe` and prints the
   text. Verify `/health` shows `"stt":"ok"`.
2. **Build:** `./build.ps1` (needs the game at `D:\SteamLibrary\...\Nuclear Option\` or
   `-GameDir`). Deploys DLL + sidecar to `BepInEx/plugins/RadioChatter/`.
3. **In game:** hold PTT (default RightAlt), say "request takeoff" on the ramp → tower
   clearance; say gibberish → "say again"; "request picture" with a contact → BRA call.

## Progress

Update this checklist as you work. Statuses: `[ ]` todo, `[~]` in progress, `[x]` done+verified.

- [x] Design doc (this file)
- [x] Sidecar: `WhisperSttEngine`, `/transcribe`, `/health` stt field, `--stt-model` arg
- [x] Sidecar: `requirements.txt` + `run_sidecar.bat`/`.sh` dependency checks
- [x] Sidecar: standalone test — deps installed, server run, health shows `"stt":"ok"`, WAV transcribed OK (`tools/test_transcribe.py`)
- [x] Mod: config entries (`[VoiceCommands]` section in `Plugin.cs`)
- [x] Mod: `Comms/Events.cs` — `PlayerVoiceCommand` event type, `IRadioOutput.ShowSubtitle`
- [x] Mod: `Audio/RadioAudioPlayer.cs` — `ShowSubtitle` implementation
- [x] Mod: `Speech/VoiceCommandController.cs` (PTT capture, WAV encode, STT client, GUI indicator)
- [x] Mod: `Comms/VoiceIntentParser.cs`
- [x] Mod: `CommsDirector.HandleVoiceCommand` + `force`/`bypassStartupHold` plumbing
- [x] Mod: `phrases.json` new response phrase banks
- [x] Mod: wiring (`Plugin.cs` construct + OnGUI, `RadioRuntime.Tick`)
- [x] Build passes (`build.ps1`, 0 warnings 0 errors) — deployed to game plugins dir
- [ ] In-game smoke test (user): PTT → clearance; gibberish → "say again"
- [x] Docs: README section on voice commands + config reference (features list, `VoiceCommands`
      config table, sidecar HTTP contract)

### Verification evidence (2026-07-08)

- `build.ps1` → 0 warnings, 0 errors; DLL + sidecar deployed to the game plugins dir.
- Sidecar run with repo venv (`.venv-sidecar312`): `/health` →
  `{"status":"ok","voices":[...],"stt":"ok"}`.
- `tools/test_transcribe.py radiochatter_wingman_paul_test.wav` → "2. Tally Bandit. Engaging now." (0.61 s).
- Round trip (TTS `/speak` → `/transcribe`, player voice):
  "Tower Falcon 1-1 request takeoff." / "Overwatch. Request picture." /
  "Request vector to target." — all ~0.5 s, and all map to the correct intents in
  `VoiceIntentParser` (Takeoff/Tower, Picture/Awacs, Vector).
- No loose `phrases.json` in the deployed plugin dir, so the embedded bank (with the new
  voice-response keys) is active.

### Notes / gotchas discovered while implementing

- BepInEx `KeyboardShortcut.IsPressed()` requires *no other keys* to be held, which breaks
  push-to-talk while flying (W/A/S/D held). Use `ConfigEntry<KeyCode>` +
  `UnityInput.Current.GetKey(...)` instead.
- `Microphone` lives in `UnityEngine.AudioModule` (already referenced). All Microphone /
  AudioClip calls must stay on the main thread; only WAV encode + HTTP go to a Task.
- The mic AudioClip is recorded non-looping with length `MaxCommandSeconds`; on release we
  read `Microphone.GetPosition` *before* `Microphone.End` to know how many samples are valid.
- faster-whisper accepts a file-like WAV at any sample rate (PyAV decodes + resamples), so
  the mod writes whatever rate the mic actually delivered.
- The sidecar's HF cache is redirected by the launchers (Windows:
  `%LOCALAPPDATA%\RadioChatter\cache\huggingface` to stay under MAX_PATH; Linux/macOS:
  `sidecar/cache/huggingface`); the whisper model lands there too. Offline retry
  mirrors the TTS path.
- The repo-level `.venv-sidecar312` had no pip (uv-seeded); `python -m ensurepip --upgrade`
  first, then `pip install faster-whisper`. End users are covered by the launchers'
  dependency probes instead.
- **Import race (fixed):** the TTS and STT loader threads import overlapping dependency
  graphs (beartype → typing_extensions, huggingface_hub). Concurrent first-time imports from
  two threads can expose a partially initialized module ("NameError: name 'sys' is not
  defined" inside typing_extensions) and permanently poison sys.modules — every 60s retry
  then fails with a misleading "not installed" error until restart. `server.py` now
  serializes all heavy engine imports through a module-level `IMPORT_LOCK`. It is flaky:
  it loaded fine twice before failing twice in a row.

## Round 2 (2026-07-08): subject parsing, radio discipline, startup-gate fix

All implemented, built (0 warnings), deployed, and verified:

- **Callsign parsing** (`VoiceIntentParser`): "Tower, this is Broadsword 1-1, requesting
  takeoff" and "Tower, Falcon 1-1, request takeoff" both extract the spoken callsign.
  A callsign matching the configured one (ignoring spacing/hyphens/digit-words: "falcon one
  one" == "Falcon 1-1") resolves to the configured spelling; any other callsign is prettified
  ("broad sword 1 1" → "Broad Sword 1-1") and the stations answer to it.
- **Proper-call enforcement** (`Config.VoiceRequireProperCalls`, default true): format is
  "<station>, [this is] <callsign>, <request>", station = "tower"/"awacs"/AWACS callsign at
  the *start* of the utterance. Missing station → AWACS `radio_format_unaddressed`; missing
  callsign → `radio_format_no_callsign_tower`/`_awacs` from the addressed station. Off =
  round-1 loose behavior (station and callsign optional, mid-sentence station still routes).
- **Startup-gate regression fix** (`CommsDirector.TryReleaseHeldStartupAwacs`): previously
  AWACS released as soon as the takeoff exchange finished if the first scripted mission comm
  had not *arrived* yet — mission messages landing a few seconds later played after/over
  AWACS. Now, if no mission comm has been seen, the gate waits
  `StartupMissionCommsGraceSeconds` (15 s) after the takeoff sequence completes before
  releasing; a comm arriving in that window is then waited on via the existing Game-channel
  checks. Tracked by `_startupMissionCommsSeen` / `_startupTakeoffSequenceDoneAt`, reset per
  aircraft. The overall 90 s cap still applies. Trade-off: comm-less missions get 15 s of
  extra AWACS silence at startup.

- **Request-driven comms** (`Config.VoiceRequestDriven`, "RequestDriven", default true;
  superseded the short-lived `TowerWaitsForRequest` flag before any release): with voice
  commands enabled, clearances and AWACS info are pull, not push. Suppressed automatics
  (guarded by `RequestDrivenComms()`): tower takeoff clearance (`DetectTower`), approach
  call (`DetectApproach` — its distance/inbound state keeps updating for mid-flight
  toggles), landing clearance from the game's UI message (`TowerFinal` event case),
  AWACS picture (`DetectPicture`), vector to target (`DetectVector`), and the periodic
  "continue RTB" advisory (second half of `DetectRtb`). Still automatic: new-contact BRA
  calls, missile warnings, splashes, bingo fuel, airborne handoff, welcome-home, weapon and
  defensive player calls, wingman comms. The startup gate still holds AWACS while the
  player is a grounded takeoff candidate (90 s cap), wingman/mission comms release on
  their 10 s fallback, and `_takeoffClearanceAnnounced` stays false when suppressed so a
  later voice request works and gate logic sees takeoff as pending. Off (or voice commands
  disabled) = fully automatic pre-voice behavior.

Verification:
- Parser suite (scratchpad `parsertest`, compiles `VoiceIntentParser.cs` standalone):
  20/20 pass, covering both user phrasings, flags, prettifying, digit-word folding,
  mid-sentence station routing.
- Round trip TTS→`/transcribe`: "Tower, this is Broadsword 1-1, requesting takeoff." /
  "Overwatch, Falcon 1-1, request picture." / "Request takeoff." all transcribed
  word-perfect with the updated bias prompt.
- README updated (format requirement, `RequireProperCalls` row, callsign play-along).
- Note: user tweaked `awacs_no_target` wording in phrases.json — preserved.
- Windows launcher `run_sidecar.bat` line endings must stay CRLF (build.ps1 rewrites them).
- STT model load takes ~1–3 s (warm) in the background; a voice command issued before it is
  ready gets a 503 → resolves to "say again", which is acceptable UX.
- faster-whisper on CPU spawns OpenMP threads; `cpu_threads=4` keeps it from fighting the game.
- Multi-utterance protection: while a transcription is in flight the PTT key is ignored
  (single in-flight command; `_busy` flag).

## Round 3 — immersion marker hiding (2026-07-08)

`[Immersion]` config section, all default **false** (visual removals are opt-in; the mod
ships to other players):

- `HideObjectiveHudMarkers` — Harmony prefix on `ObjectiveOverlay.UpdateOverlay` calls
  `HideOverlay()` and skips the original (`Game/ImmersionPatches.cs`). The game has no
  native option for this overlay; `ObjectiveOverlayManager.UpdateOverlays` updates it
  unconditionally, hence the patch.
- `HideAirbaseHudMarkers` — Harmony prefix on `AirbaseOverlay.DisplayMarkers(bool show)`
  forces `show` false (`Game/ImmersionPatches.cs`). `AirbaseOverlay` is self-driven from its
  own `LateUpdate` (no manager, no `HideOverlay`); `LateUpdate` runs `PositionMarkers` then
  `DisplayMarkers(show)`, and `DisplayMarkers` is the only place that toggles the
  `airbaseMarker`/`airbaseLabel` images (verified at IL level). Forcing `show` false hides
  the nav marker + distance label through the game's own path while the runway borders /
  glideslope landing aids (drawn later in `LateUpdate`) and the taxi/landing clearance +
  takeoff-detection logic (in `UpdateNearestAirbase`) are untouched. Only ever shows the
  aircraft's own faction airbases (`NetworkHQ` / `FactionHQ`), so "friendly" is exact.
- `HideMapObjectiveMarkers` / `HideMapAirbaseIcons` — no patch needed. The game's
  `MapOptions` scene singleton (public fields `showObjectives` / `showAirbaseIcon`) is
  re-read every frame by `ObjectiveMarkerManager.UpdateObjectiveMarkers` and
  `AirbaseMapIcon.UpdateIcon` (verified at IL level). `ImmersionMapOptions.Tick` (called
  from `RadioRuntime.Tick`) forces the fields false while the flag is on and restores the
  player's previous value when turned off mid-session.

Deliberately out of scope: hiding `HUDUnitMarker`s (the unit boxes on the HUD).
`CombatHUD.TargetSelect` sorts those markers for target selection, so hiding them breaks
weapons employment, not just information. A selective version (keep locked target, hide
the rest) is future work.

Discovery notes: no decompiler is installed; game types were dumped with Mono.Cecil from
`BepInEx/core/Mono.Cecil.dll` via a scratchpad PowerShell script reading
`NuclearOption_Data/Managed/Assembly-CSharp.dll` (list types/members, dump IL bodies,
find field readers). `MapOptions` also has `showTargetInfo`, `showPilotIcons`,
`showJamming`, `showGridLabels` if more map toggles are ever wanted.

### Round 3 addendum — "vector to objective" command

New intent `RequestVectorObjective` ("objective"/"objectives" keyword, checked before the
generic vector/target rule so "vector to objective" does not fall into `RequestVector`;
both words added to `CallsignStopWords`). Distinct from `RequestVector`, which stays about
the locked/sorted target (nearest-contact fallback unchanged).

Data path: `GameAdapter.FillObjective` calls the game's public static
`MissionPosition.TryGetClosestObjectivePosition(Unit, out PositionResult)` each poll and
fills `Snapshot.HasObjective/ObjectiveName/ObjectivePosition` (name from
`result.Objective.SavedObjective.DisplayName`). `CommsDirector.RespondVectorObjective`
answers with `awacs_vector_objective` (slots: callsign/awacs/objective/bearing/range,
2-D range like home plate) or `awacs_no_objective` when the mission has no positioned
active objective. Whisper bias prompt gained "Vector to objective." Parser suite: 25/25.

### Round 3 addendum 2 — objective list + specific-objective vectors

- Snapshot now carries the whole board: `Snapshot.Objectives` (`ObjectiveInfo {Name,
  HasPosition, Position, DistanceM}`), filled by `GameAdapter.FillObjectives` from
  `MissionPosition.TryGetActiveObjectives(hq, out list)` (the runner's internal
  `activeByFaction` list — treat as read-only) + `MissionPosition.DistanceTo(objective,
  playerPos, out result)` per objective (false = no position, e.g. sortie/wait objectives;
  listed by name only, not vectorable). `SavedObjective.Hidden` objectives are skipped so
  AWACS cannot leak them. Replaces the short-lived single closest-objective snapshot fields.
- New intent `RequestObjectiveList`: "objectives" (plural) without a vector word, or any of
  list/all/current/active/available/tasking/status/what alongside objective(s). Those words
  are also callsign stop words now ("Falcon 1-1, list objectives" bug: "list" got sucked
  into the callsign). Response `awacs_objective_list` reads up to 5, closest first, each
  with bearing/range when positioned ("; and N more" past the cap).
- `VoiceIntent.ObjectiveQuery`: words after "objective(s)". Empty → closest positioned
  objective. Non-empty → `VoiceIntentParser.LooseNameMatch(query, name, out score)`:
  normalized tokens, filler words dropped (the/a/to/at/...), digit words folded, token
  match = exact or 4+ char prefix ("radars"→"radar", "kowal"→"kowalski"); STRICT majority
  (score*2 > tokenCount — exactly half is not enough, "destroy radar convoy escort" must
  not match "Escort the convoy"). Best score wins, distance breaks ties; no match →
  `awacs_objective_unknown` corrective, never a silent wrong-objective fallback.
- Parser suite: 39/39 (intent detection, query extraction, matcher table).
