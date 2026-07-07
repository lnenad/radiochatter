# Nuclear Option — Immersive Comms Mod ("RadioChatter")
## Implementation Plan / Handoff Document

> **Note:** this is the original planning document, kept for history. The implemented layout and
> behavior have drifted from it in places (e.g. there is no separate `TowerController`/`AwacsController`;
> that logic lives in `CommsDirector`). See `README.md` for the current state of the project.

**Goal:** A BepInEx mod for Nuclear Option that adds a TTS-voiced Tower and AWACS controller reacting to live game state: takeoff/landing clearances communicated based on player position, vectors to target when player has fixed a target, BRA calls, splash calls, missile warnings, RTB guidance when player is heading towards the airport for some time.

**Decided constraints (do not re-litigate):**
- TTS: **Pocket TTS sidecar** over localhost HTTP (dynamic text, not pre-baked WAVs), using `kyutai-labs/pocket-tts`.
- Default voice: **`eve`**.
- Scope v1: **Both Tower and AWACS** roles.
- **Singleplayer / host only.** No multiplayer-client support in v1. Detect MP client mode and disable gracefully.
- Target: BepInEx **5.x**, Mono Unity, plugin as **.NET Framework 4.7.2** class library.
- Game DLLs change frequently between game updates — isolate ALL game-specific access behind one adapter layer so rebuilds are cheap.

---

## 0. Ground rules for the executing agent

1. **Never invent game API names.** Class/method/field names of Nuclear Option's `Assembly-CSharp.dll` are NOT known to this plan. Phase 1 exists to discover them. Every game type reference in later phases is written against an adapter interface (`IGameAdapter`) — the concrete implementation is filled in only after Phase 1 verification.
2. Known-good facts from the modding community you can rely on:
   - The game is Mono (decompilable with ILSpy/dnSpy), moddable with BepInEx 5.4.23.x. A preconfigured pack exists on Thunderstore (`thunderstore.io/c/nuclear-option`).
   - `BepInEx.cfg` must have `[Chainloader] HideGameManagerObject = true` or the manager GameObject interferes with the game.
   - Existing open-source mods to mine for API knowledge (read their source before decompiling blind):
     - `github.com/qwerty1423/no-autopilot-mod` — reads flight state, waypoints, reuses AI landing logic, fuel/range display, GCAS. Best single reference for aircraft state access.
     - `github.com/9138noms/TargetCamControl` — target camera, `TargetScreenUI` fields (RNG/ALT/HDG/GRID), Rewired input registration under the Debug category.
     - `github.com/9138noms/NuclearOption-VehicleControl` — unit possession, distinguishing SP/host vs MP client.
     - `github.com/nikkorap/NuclearMods`, `github.com/mkualquiera/MKModsNO`, `github.com/Modzer0/ActualStealth` (aircraft internal names e.g. `SmallFighter1`, `Multirole1`, `EW1`), `github.com/AlEX-FRiT/My-NO-Mods` (build setup: standalone net472 csproj per mod, deploy DLL to `BepInEx/plugins/`).
3. Prefer **Harmony patches for discrete events** and a **polling loop for continuous state**. If a clean method to patch can't be found for some event, derive it from polled state (state-machine transitions) — this is an acceptable fallback and often more robust across game updates.
4. Every callout the mod makes must be **disable-able via config** individually.
5. Keep per-frame allocations near zero in the polling and audio paths.

---

## 1. Repository layout (deliverable)

```
parent-dir/
├── plan/                          # this file + discovery notes
│   └── GameApi.md                 # Phase 1 output: verified game API surface
├── src/
│   └── RadioChatter/
│       ├── RadioChatter.csproj    # net472, references via GameDir property
│       ├── Plugin.cs              # BepInEx entry point, config, wiring
│       ├── Game/
│       │   ├── IGameAdapter.cs    # ALL game access behind this interface
│       │   ├── GameAdapter.cs     # concrete impl (Phase 1 names go here)
│       │   ├── Snapshot.cs        # immutable per-tick state structs
│       │   └── Patches.cs         # Harmony patches (thin: raise events only)
│       ├── Comms/
│       │   ├── CommsDirector.cs   # priority queue, cooldowns, dedup
│       │   ├── TowerController.cs # tower state machine
│       │   ├── AwacsController.cs # AWACS logic (threats, vectors, splash)
│       │   └── Events.cs          # internal event definitions
│       ├── Speech/
│       │   ├── PhraseEngine.cs    # templates, slot filling, variation
│       │   ├── NumberSpeech.cs    # 045 -> "zero four five", etc.
│       │   ├── PocketTtsClient.cs # async HTTP to sidecar, cache
│       │   └── phrases.json       # embedded resource: phrase banks
│       └── Audio/
│           ├── RadioAudioPlayer.cs  # AudioSource + queue
│           └── RadioFilter.cs       # bandpass + noise + squelch clicks
├── sidecar/
│   ├── server.py                  # Pocket TTS HTTP wrapper/server
│   ├── requirements.txt
│   ├── voices.json                # role -> Pocket TTS voice mapping
│   ├── run_sidecar.bat
│   └── run_sidecar.sh
├── packaging/
│   └── thunderstore/README.md     # Thunderstore package README
├── installer/
│   ├── linux/install-radiochatter.sh
│   └── windows/RadioChatterInstaller.iss
├── build.ps1                      # build + copy DLL to game plugins dir
├── build.sh                       # Linux build + copy DLL to game plugins dir
├── tools/
│   ├── package_github_release.py  # GitHub release asset builder
│   ├── new_release_tag.ps1        # local build + prebuilt payload + tag helper
│   └── package_thunderstore.py    # optional Thunderstore/r2modman package builder
├── .github/workflows/release.yml  # tag-triggered installer/zip release workflow
├── release/payload/               # committed prebuilt DLL + sidecar for CI packaging
└── README.md                      # install, config reference, troubleshooting
```

---

## 2. Phase 0 — Environment & project setup

1. Verify game install path (Steam: `D:\SteamLibrary\steamapps\common\Nuclear Option`, executable `NuclearOption.exe`, managed DLLs in `NuclearOption_Data/Managed/`).
2. BepInEx 5.4.23.x (x64, Mono) present here: `C:\Users\nndlk\Downloads\BepInEx_win_x64_5.4.23.5`. Run game once to generate configs. Set `HideGameManagerObject = true`. Optionally enable console logging.
3. Install **BepInEx.ConfigurationManager** (F1 in-game menu) — the mod should expose its settings through it.
4. Install **UnityExplorer (BepInEx5 Mono build)** for Phase 1 runtime inspection. Remove/disable it for release builds.
5. Create `RadioChatter.csproj`:
   - `TargetFramework=net472`, `LangVersion=latest`, `AllowUnsafeBlocks=false`.
   - Reference from `$(GameDir)NuclearOption_Data/Managed/`: `Assembly-CSharp.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.AudioModule.dll`, `UnityEngine.UnityWebRequestModule.dll` (if used), plus `Rewired_Core.dll` if input binding is added. Reference `BepInEx.dll`, `0Harmony.dll` from `BepInEx/core/`.
   - Support a `Local.props` file to override `GameDir` (pattern used by the autopilot mod).
   - Consider **BepInEx.AssemblyPublicizer.MSBuild** to access private members without reflection; if used, document it in README. Reflection via Harmony `AccessTools` is the fallback.
6. `build.ps1` / `build.sh`: `dotnet build -c Release` then copy DLL and sidecar launch files to `BepInEx/plugins/RadioChatter/`.

**Exit criteria:** empty plugin loads, logs "RadioChatter loaded" in BepInEx console, F1 config menu shows the plugin section.

---

## 3. Phase 1 — Game API discovery (most important phase)

Decompile `Assembly-CSharp.dll` with ILSpy and inspect a running game with UnityExplorer. **Cross-check against the open-source mods listed in §0.2 first** — most of this is already discovered in their code.

Produce `plan/GameApi.md` documenting the verified names, signatures, and access paths for:

| # | Needed capability | What to find |
|---|---|---|
| D1 | Player aircraft object | How to get the local player's aircraft (singleton/manager? camera target?). Its Unity `Transform` for position; velocity vector; altitude AGL vs MSL; heading; indicated airspeed. |
| D2 | Unit registry | A list/manager of all active units. Per-unit: position, faction/allegiance, alive/dead, unit type/display name, whether airborne. |
| D3 | Detection/datalink state | Whether a given enemy unit is *known* to the player's faction (radar-detected / datalinked). AWACS must only call out detected contacts — no omniscient calls. If a per-faction "known contacts" collection exists, use it; otherwise find the radar track list. |
| D4 | Target selection | The player's currently selected target(s) (the targeting system mods interact with). |
| D5 | Unit destroyed | The method or event fired when a unit is destroyed/killed, ideally with attacker attribution (who scored the kill). Harmony-patch candidate. |
| D6 | Missile launch / RWR | How the game represents missiles in flight and RWR warnings targeting the player. Patch candidate or pollable list of missiles with `target == player`. |
| D7 | Weapon release by player | Event/method when the player fires a weapon (for optional "rifle"/"fox" self-calls — low priority). |
| D8 | Airbase objects | Airbase/runway objects: position, faction, runway heading if available. Needed to pick "home plate" (nearest friendly base) and drive Tower logic. |
| D9 | Landing gear & ground state | Player gear state, weight-on-wheels / grounded flag, on-runway detection (or approximate via proximity to airbase + low altitude). |
| D10 | Mission/game mode | How to detect: in-mission vs menu; singleplayer/host vs multiplayer client (VehicleControl mod distinguishes this); pause state. |
| D11 | Units setting | The game's metric/imperial preference (the autopilot mod reads it) so callouts match the HUD. |
| D12 | Mission clock / time | For "time on target"-style flavor, optional. |

For each item record: type name, member name(s), how obtained (static instance, FindObjectOfType, manager field), public/private, and a one-line code snippet. Then implement `GameAdapter` against these findings. **Nothing outside `Game/` may reference `Assembly-CSharp` types.**

**Exit criteria:** a debug overlay (IMGUI, toggled by config) showing live: player pos/alt/hdg/speed, nearest friendly airbase + distance, selected target BRA, count of detected enemy air contacts, gear/ground state. All values visibly correct in a test mission.

---

## 4. Phase 2 — Core plugin skeleton

- `Plugin.cs` (`[BepInPlugin("com.lnenad.radiochatter", "RadioChatter", "0.1.0")]`):
  - Bind all config entries (see §10) in `Awake`.
  - Create a persistent hidden GameObject hosting: `StatePoller` (MonoBehaviour), `RadioAudioPlayer`, `CommsDirector`.
  - Apply Harmony patches (`Harmony.CreateAndPatchAll`).
  - Start/verify sidecar (§8): health-check `GET /health`; if down and `AutoStartSidecar=true`, launch `run_sidecar.bat`, `run_sidecar.sh`, or a configured command via `Process.Start`, then poll health for up to 15 s. If unavailable, log a clear warning and run in **subtitles-only mode** (still show text callouts on screen) rather than dying.
- `StatePoller`: every `PollIntervalSeconds` (default 0.5), if in-mission and not MP client, build an immutable `Snapshot` (player state, contacts, airbases, missiles) via `IGameAdapter` and hand it to `CommsDirector.Tick(snapshot)`. Wrap in try/catch; one failing tick must never crash the game or spam-log (rate-limit error logging).
- Internal event bus: Harmony patches translate game callbacks into typed events (`UnitDestroyed{victim, attacker}`, `MissileLaunchedAtPlayer{...}`) pushed onto a thread-safe queue drained on the main thread in `Tick`.

**Exit criteria:** snapshots logged at debug level with plausible values; no GC pressure spikes (check with profiler or simply frame-time stability).

---

## 5. Phase 3 — Event detection

Implement in `Patches.cs` + derived detectors in the controllers:

**Harmony-patched (preferred where D5–D7 found clean targets):**
- Unit destroyed → `UnitDestroyed` event with victim + attacker if attributable.
- Missile launched at player / RWR lock → `ThreatMissile` event.

**Derived from polling (state-machine transitions; also the fallback if patches are brittle):**
- **Takeoff:** grounded→airborne transition while within R of a friendly airbase, gear up or climbing.
- **Landing:** airborne→grounded within R of friendly airbase, survived (not destroyed same tick).
- **Approach:** descending, distance to home plate < 15 km and closing, below ~2000 m AGL.
- **On final:** < 5 km, aligned within ±30° of the base bearing (or runway heading if D8 provides it), descending.
- **New contact:** enemy air unit enters the faction-detected set (D3) that wasn't in it last tick.
- **Contact update / picture:** periodic re-broadcast of nearest/priority threat.
- **RTB conditions:** player fuel low (if adapter exposes fuel; the autopilot mod computes fuel time/range) or winchester (no A2A weapons) — optional, config-gated.

All detectors must debounce (e.g., grounded flag flickering on bounce) — require state stable for N consecutive ticks before transition fires.

---

## 6. Phase 4 — Comms director (the brain)

`CommsDirector` owns a **priority queue of pending callouts**:

- Priorities (high→low): `ThreatMissile` > `ThreatContactClose` > `TowerClearance` > `SplashConfirm` > `NewContact` > `VectorToTarget` > `PictureUpdate` > `Flavor`.
- **Only one transmission at a time.** A higher-priority item may preempt: cut current audio with a squelch click, then play the urgent line. Only `ThreatMissile` preempts; everything else waits.
- **Cooldowns:** per-callout-type (e.g., picture update ≥ 45 s, vector ≥ 20 s, new-contact per-unit once, splash per-kill once). Global minimum gap between any two transmissions (default 3 s).
- **Deduplication:** identical text within 60 s is dropped.
- **Staleness:** items expire if their triggering condition no longer holds when they reach the head of the queue (e.g., don't announce a contact that died while queued).

**TowerController** state machine (per session, using home plate = nearest friendly airbase at spawn):
`OnRamp → TaxiTakeoff("cleared for takeoff, runway XX / winds calm") → Airborne("airborne, contact Overwatch on button two") → [gameplay] → InboundRTB(player < 25 km & descending: "report ready for approach") → Approach("cleared to land") → Landed("welcome home, taxi to ramp")`
Transitions come from Phase 3 detectors. Keep it forgiving — if states are skipped (player takes off vertically, crashes, respawns), reset to the appropriate state rather than getting stuck. Respawn/new-aircraft detection resets the machine.

**AwacsController** ("Overwatch" default callsign, configurable):
- On `NewContact`: BRA call relative to **player** position: `"<player callsign>, Overwatch, new contact, bearing 045 for 22, angels 8, hot/flanking/cold"` (aspect from contact heading vs bearing to player).
- On `UnitDestroyed` where attacker == player: air kill → "splash one, <type> down"; ground kill → "good effect on target, target destroyed".
- On `ThreatMissile`: "defend, defend, missile inbound bearing XXX" (this is the preempting call).
- `VectorToTarget`: when player has a selected target (D4), on request-interval: "target bearing XXX for YY, angels Z". Optional: bind a Rewired key ("Request vector", Debug category — same approach as TargetCamControl) so the player can ask on demand. Config-gated; keybind is a stretch goal, interval-based is the v1 baseline.
- Range/altitude units follow the game's metric/imperial setting (D11): "for 22" = km or nm accordingly, "angels" only in imperial mode (thousands of feet); in metric say "altitude 2500".

---

## 7. Phase 5 — Phrase engine

- `phrases.json`: per event type, an array of template variants: `"Overwatch to {callsign}, splash one, {type} destroyed"`, `"{callsign}, good kill, {type} down"`. Random pick, avoid repeating the last-used variant per event type.
- Slots: `{callsign}`, `{bearing3}` (three-digit spoken: "zero four five"), `{range}`, `{alt}`, `{aspect}`, `{type}`, `{runway}`, `{count}`.
- `NumberSpeech`: digits spoken individually for bearings/headings ("two seven zero"), natural numbers for ranges ("twenty-two"), "niner" for 9 in bearings. Pocket TTS handles plain words well — always expand numbers to words before sending; never send "045".
- Unit display names (D2) may be technical (`SmallFighter1`); map internal names → spoken names ("Vortex", "Ifrit") in a small dictionary with passthrough fallback.
- Player callsign: config string, default "Falcon 1".

---

## 8. Phase 6 — Pocket TTS sidecar

TTS backend: `kyutai-labs/pocket-tts` (`https://github.com/kyutai-labs/pocket-tts`). It runs on CPU, provides Python API + CLI, supports `pocket-tts serve`, and should keep the model and voice states resident for low latency.

`sidecar/server.py` — small Python HTTP wrapper/server (FastAPI or stdlib `http.server`, executor's choice; keep dependencies minimal):

- `GET /health` → 200 `{"status":"ok","voices":[...]}`.
- `POST /speak` JSON `{"text": "...", "voice": "awacs"}` → `audio/wav` bytes (22.05 kHz or 16 kHz mono, 16-bit PCM).
- `voices.json` maps roles → Pocket TTS voice ids or exported voice-state files. v1 default: `tower` → `eve`, `awacs` → `eve`. If role separation is desired later, add a second Pocket TTS voice after the audio path is stable.
- Prefer using the Pocket TTS Python API directly in `server.py`: `TTSModel.load_model()` once at startup, `get_state_for_audio_prompt("eve")` once at startup, then `generate_audio(...)` per request. If the built-in `pocket-tts serve` endpoint can satisfy the exact WAV-byte contract, wrapping/proxying it is also acceptable.
- Install with `pip install pocket-tts` or `uvx pocket-tts`; Pocket TTS requires PyTorch 2.5+ CPU builds and supports Python 3.10+.
- Bind to `127.0.0.1:5075` (configurable). No auth needed (localhost only) but reject non-loopback connections.

**Mod-side `PocketTtsClient`:**
- Async request (thread-pool or UnityWebRequest on a coroutine — must NOT block the main thread), timeout 4 s.
- Parse WAV → `AudioClip` (`AudioClip.Create` + `SetData`; handle sample-rate passthrough, Unity resamples on play).
- **LRU cache** (config cap ~100 clips) keyed by `voice + text` — many lines repeat verbatim.
- **Warm-up:** on mission start, pre-synthesize the most common static lines (tower clearances, "splash one" variants) in the background.
- On request failure: fall back to subtitle-only for that line; if 3 consecutive failures, re-run health check and log once.

---

## 9. Phase 7 — Audio playback & radio effect

- `RadioAudioPlayer`: dedicated `AudioSource` on the plugin GameObject, 2D (no spatialization), output not routed through game mixers if they duck/alter it (verify; plain AudioSource is fine v1). Volume from config, `ignoreListenerPause = false` so it pauses with the game.
- **Radio effect** — apply as **offline processing on the PCM samples before creating the AudioClip** (simpler and safer than `OnAudioFilterRead`):
  1. Band-pass ~300–3400 Hz (biquad HP at 300 Hz + LP at 3400 Hz).
  2. Soft clip / mild saturation (tanh at ~1.5× gain) for transmit crunch.
  3. Add low-level white noise (-30 dB-ish, config `NoiseLevel`).
  4. Prepend/append a short squelch click + 80 ms noise burst (generate procedurally; no asset files needed).
- Subtitles: config-gated on-screen text (IMGUI label, bottom-center, ~4 s or audio length) showing `[TWR]`/`[AWACS]` prefix + line. Always shown in subtitles-only fallback mode.

---

## 10. Phase 8 — Config & polish

BepInEx config entries (all live-editable via ConfigurationManager):

- `General`: `Enabled`, `PlayerCallsign` ("Falcon 1"), `AwacsCallsign` ("Overwatch"), `SubtitlesEnabled`, `PollIntervalSeconds` (0.5), `DebugOverlay` (false).
- `Sidecar`: `Url` (`http://127.0.0.1:5075`), `AutoStartSidecar` (true), `SidecarCommand` (path to a sidecar launcher script), `CacheSize` (100).
- `Audio`: `Volume` (0.8), `RadioEffectEnabled` (true), `NoiseLevel`, `TowerVoice` ("eve"), `AwacsVoice` ("eve").
- `Callouts` (each bool + cooldown float where relevant): `Takeoff`, `Landing`, `Approach`, `NewContact`, `PictureUpdate` + `PictureIntervalSeconds` (45), `VectorToTarget` + interval, `SplashCalls`, `MissileWarning`, `RtbCalls`.

Polish tasks: README (install steps incl. BepInEx + sidecar + voice downloads, config reference, troubleshooting table), maintain the GitHub release installer/script path, version-tolerance note (rebuild instructions after game updates).

---

## 11. Testing plan

Use the in-game **mission editor** to build deterministic test missions:

1. **Tower test:** spawn at friendly airbase → verify takeoff clearance on roll, "airborne" call, fly out 30 km, return → approach + cleared-to-land + welcome-home. Also: crash on runway (no landing call), respawn (state machine resets).
2. **AWACS contact test:** place 1 enemy fighter beyond detection range, fly toward it → exactly one "new contact" BRA when detected; verify bearing/range/aspect against the in-game map; verify units match HUD setting in both metric and imperial.
3. **Splash test:** kill air target → splash call once; kill ground target → "good effect"; let an AI ally kill something → no player-attributed splash.
4. **Missile test:** enemy SAM/fighter fires at player → "defend" call preempts any playing line; no duplicate warnings for the same missile.
5. **Saturation test:** mission with 10+ enemies detected simultaneously → queue doesn't flood; picture/contact cooldowns respected; global gap respected; no frame hitches (sidecar calls off-main-thread).
6. **Degradation test:** kill the sidecar mid-mission → subtitles-only continues, single warning logged; restart sidecar → audio resumes after health re-check.
7. **MP guard:** join any multiplayer session as client → mod logs "disabled (multiplayer client)" and does nothing.
8. **Menu/pause:** no callouts in menus; audio pauses with game pause.

---

## 12. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Game update renames/changes internals | All access via `GameAdapter`; `GameApi.md` documents each hook so re-verification after an update is a checklist, not archaeology. Reflection lookups fail soft (log + disable the dependent feature, not crash). |
| No clean Harmony target for kills/missiles | Derived detection from polling (unit disappears from alive set → destroyed near player's last target ⇒ probable player kill; degrade splash attribution honestly: "target destroyed" without "splash one"). |
| Pocket TTS latency spikes | Keep model + `eve` voice state resident, cache + warm-up common phrases, preemption only for missile warnings, 4 s timeout → subtitle fallback. |
| Audio filter artifacts | Offline PCM processing (deterministic), unit-test the biquads on a sine sweep. |
| Detection state (D3) not cleanly exposed | Fallback heuristic: only call contacts within X km of any friendly radar-equipped unit; document the compromise in README. |
| Rewired custom keybind friction | On-demand "request vector" is a stretch goal; interval-based vectors ship first. |

---

## 13. Milestone order & exit criteria summary

1. **M0** — plugin loads, config visible (Phase 0).
2. **M1** — `GameApi.md` complete, debug overlay shows correct live state (Phase 1). *Gate: do not proceed until overlay values are verified in-game.*
3. **M2** — events firing in log for takeoff/landing/kill/contact in a test mission (Phases 2–3).
4. **M3** — subtitles-only comms fully working: director + phrases render correct, well-timed text (Phases 4–5).
5. **M4** — sidecar + audio: voiced with radio effect, cached, resilient (Phases 6–7).
6. **M5** — config polish, README, full test pass (§11), tag v0.1.0 (Phase 8).

Milestone M3 before M4 is deliberate: all comms logic is testable and debuggable as text before audio enters the picture.

Important note:

DO NOT ALTER EXISTING GAME FILES TO AVOID CORRUPTING THE GAME
