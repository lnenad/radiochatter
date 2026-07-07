# RadioChatter

RadioChatter is a BepInEx 5 mod for **Nuclear Option** that adds spoken radio traffic for tower, AWACS, and mission-scripted in-game comms. It uses live game state from Nuclear Option plus a local Pocket TTS sidecar, so callouts are generated dynamically instead of being pre-rendered WAV files.

The current build targets singleplayer and host-side play. Multiplayer clients are detected and the mod disables itself to avoid reading or acting on state it should not own.

## Current Features

- Tower calls for takeoff, airborne handoff, inbound approach, game-provided landing clearance, and successful landing.
- AWACS calls for newly detected enemy aircraft, missile threats, player kill confirmations, selected-target vectors, and picture updates.
- AWACS RTB calls for bingo fuel and sustained inbound flight back to home plate.
- Player/pilot calls for weapon releases: `fox one!`, `fox two!`, `fox three!`, `rifle!`, `magnum!`, `pickle!`, and `guns! guns! guns!`.
- Player/pilot defensive calls for incoming missiles, such as `missile, break`.
- Player/pilot ejection call when the player ejects.
- Player readbacks for tower takeoff clearance, landing clearance, and AWACS handoff; otherwise varied acknowledgements after incoming comms finish. Player responses are labeled by source, such as `[PLAYER-TWR]`, `[PLAYER-FLIGHT]`, and `[PLAYER-AWACS]`.
- In-game mission comms capture through `MissionMessages.ShowMessage`, voiced separately for AI wingman-style chatter.
- Separate voice IDs for tower, AWACS, player/pilot, and wingman/in-game comms.
- Customizable phrase templates through `phrases.json`, with a drop-in override next to the plugin DLL that needs no rebuild.
- Same-channel audio serialization: tower will not overlap tower, AWACS will not overlap AWACS, player calls will not overlap player calls, and wingman comms will not overlap wingman comms. Different channels can still overlap.
- Subtitles with duration based on message length.
- Subtle radio processing: band-limited voice, mild saturation, and quiet configurable hiss.
- Subtitles-only fallback if the TTS sidecar is unavailable.
- Debug overlay for live player/airbase/contact state.

## Repository Layout

```text
repo-root/
+-- build.ps1
+-- build.sh
+-- PROJECT.md
+-- README.md
+-- packaging/
+|   +-- thunderstore/README.md
+-- installer/
+|   +-- linux/install-radiochatter.sh
+|   +-- windows/RadioChatterInstaller.iss
+-- plan/
|   +-- GameApi.md
|   +-- tools/dump-api.ps1
+-- sidecar/
|   +-- requirements.txt
|   +-- run_sidecar.bat
|   +-- run_sidecar.sh
|   +-- server.py
|   +-- voices.json
+-- src/RadioChatter/
    +-- RadioChatter.csproj
    +-- Plugin.cs              # BepInEx entry point + Config
    +-- RadioRuntime.cs        # static wiring between patches and services
    +-- Audio/
    |   +-- RadioAudioPlayer.cs  # playback, radio effect, acknowledgements, subtitles
    +-- Comms/
    |   +-- CommsDirector.cs     # event detection, priorities, cooldowns, queueing
    |   +-- RadioText.cs         # stateless text/formatting helpers
    |   +-- Events.cs            # roles, event types, thread-safe event bus
    +-- Game/
    |   +-- GameAdapter.cs       # all game API reads (behind IGameAdapter)
    |   +-- Patches.cs           # Harmony patches -> RadioEventBus events
    |   +-- Snapshot.cs          # per-tick game-state structs
    |   +-- StatePoller.cs       # polls adapter, drives CommsDirector
    +-- Speech/
        +-- PhraseEngine.cs      # loads phrases.json, slot filling, variation
        +-- phrases.json         # phrase banks (embedded; loose copy overrides)
        +-- NumberSpeech.cs      # numbers -> spoken words
        +-- PocketTtsClient.cs   # sidecar HTTP client, WAV parse, LRU cache
        +-- SidecarSupervisor.cs # health probes, auto-start, backoff
+-- tools/
    +-- package_github_release.py  # builds GitHub release assets
    +-- package_thunderstore.py    # optional Thunderstore/r2modman zip
```

## Requirements

- Nuclear Option installed locally.
- BepInEx 5.x for Mono Unity installed into the Nuclear Option folder.
- `.NET SDK` capable of building SDK-style projects.
- Python 3.10+ for the sidecar. The launchers create `sidecar/.venv` and install dependencies automatically when possible.
- Pocket TTS Python package and dependencies from `sidecar/requirements.txt` if you install the sidecar manually.

The project defaults to these game paths:

```text
Windows: D:/SteamLibrary/steamapps/common/Nuclear Option/
Linux:   ~/.steam/steam/steamapps/common/Nuclear Option/
```

Override that path through `build.ps1 -GameDir ...`, `build.sh --game-dir ...`, or `src/RadioChatter/Local.props`.

## BepInEx Setup

Install BepInEx 5 into the Nuclear Option game directory, then run the game once so BepInEx creates its folders and config files.

Recommended BepInEx setting:

```ini
[Chainloader]
HideGameManagerObject = true
```

The deployed plugin path is:

```text
<Nuclear Option>/BepInEx/plugins/RadioChatter/RadioChatter.dll
```

## Install From GitHub Releases

Download a release from:

```text
https://github.com/lnenad/radiochatter/releases
```

Windows:

1. Close Nuclear Option.
2. Run `RadioChatter-<version>-Setup.exe`.
3. Select the Nuclear Option game folder.
4. Leave `Prepare Pocket TTS sidecar now` checked if you want the installer to create the sidecar `.venv` immediately.

Linux:

```sh
unzip RadioChatter-<version>-linux.zip
sh install-radiochatter.sh --game-dir "$HOME/.steam/steam/steamapps/common/Nuclear Option" --yes
```

Both installers expect BepInEx 5 to already be installed in the game folder. The plugin auto-starts the sidecar by default. If the sidecar environment has not already been prepared, the launcher creates `BepInEx/plugins/RadioChatter/sidecar/.venv`, installs `sidecar/requirements.txt`, and downloads Pocket TTS model assets. That first startup can take several minutes and requires Python 3.10+.

## Build And Deploy

From the repo root on Windows:

```powershell
.\build.ps1
```

To use a different Windows game install:

```powershell
.\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Nuclear Option\"
```

From the repo root on Linux:

```sh
sh ./build.sh
```

To use a different Linux game install:

```sh
sh ./build.sh --game-dir "$HOME/.local/share/Steam/steamapps/common/Nuclear Option"
```

The scripts build:

```text
src/RadioChatter/bin/Release/RadioChatter.dll
```

Then copy the DLL and sidecar launcher files into:

```text
<GameDir>/BepInEx/plugins/RadioChatter/
```

If the copy fails with `user-mapped section open`, Nuclear Option is still running and has the DLL loaded. Close the game and run the deploy again.

## Pocket TTS Sidecar

RadioChatter talks to a local HTTP sidecar at:

```text
http://127.0.0.1:5075
```

The sidecar exposes:

- `GET /health` returns JSON status and loaded voice aliases.
- `POST /speak` accepts `{"text":"...", "voice":"..."}` and returns `audio/wav`.

### Install Sidecar Dependencies

The sidecar launchers install dependencies automatically into `sidecar/.venv` if no usable environment exists. To prepare the environment manually from the repo root:

Windows:

```powershell
py -3.12 -m venv .venv-sidecar312
.\.venv-sidecar312\Scripts\python.exe -m pip install --upgrade pip
.\.venv-sidecar312\Scripts\python.exe -m pip install -r sidecar\requirements.txt
```

Linux:

```sh
python3 -m venv .venv-sidecar312
./.venv-sidecar312/bin/python -m pip install --upgrade pip
./.venv-sidecar312/bin/python -m pip install -r sidecar/requirements.txt
```

The launchers prefer a local `sidecar/.venv`, then the repo-level `.venv-sidecar312`, then create `sidecar/.venv` from system Python if needed.

### Start The Sidecar

Windows:

```powershell
.\sidecar\run_sidecar.bat
```

Linux:

```sh
sh ./sidecar/run_sidecar.sh
```

Expected output:

```text
Loading Pocket TTS model and voices...
Loaded voices: awacs, default, eve, player, tower, wingman
RadioChatter Pocket TTS sidecar listening on http://127.0.0.1:5075
```

The first startup may take longer because Pocket TTS has to load or download model assets.

### Check Sidecar Health

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:5075/health
```

Shell:

```sh
curl http://127.0.0.1:5075/health
```

### Generate A Test WAV

PowerShell:

```powershell
$body = @{ text = "Falcon 1, cleared to land runway two seven"; voice = "tower" } | ConvertTo-Json
Invoke-WebRequest -Uri http://127.0.0.1:5075/speak -Method Post -ContentType "application/json" -Body $body -OutFile tower_test.wav
```

Shell:

```sh
curl -sS http://127.0.0.1:5075/speak \
  -H "Content-Type: application/json" \
  -d '{"text":"Falcon 1, cleared to land runway two seven","voice":"tower"}' \
  --output tower_test.wav
```

## Voices

Voice aliases are configured in [sidecar/voices.json](sidecar/voices.json):

```json
{
  "default": "eve",
  "eve": "eve",
  "tower": "eve",
  "awacs": "vera",
  "player": "george",
  "wingman": "paul"
}
```

The plugin requests these aliases through BepInEx config:

- `Audio.TowerVoice = tower`
- `Audio.AwacsVoice = awacs`
- `Audio.PlayerVoice = player`
- `Audio.WingmanVoice = wingman`

To change a role voice, edit `sidecar\voices.json` and restart the sidecar. If you change the BepInEx config voice ID, make sure the same alias exists in `voices.json`.

## Customizing Phrases

All tower/AWACS phrase templates live in [src\RadioChatter\Speech\phrases.json](src/RadioChatter/Speech/phrases.json), which is embedded into `RadioChatter.dll` at build time. At startup the plugin loads phrases from the first of:

1. A loose `phrases.json` next to the deployed DLL: `<Nuclear Option>\BepInEx\plugins\RadioChatter\phrases.json`.
2. The copy embedded in the DLL.

To customize callouts without rebuilding, copy `src\RadioChatter\Speech\phrases.json` next to the deployed DLL and edit it. Delete the loose file to revert to the built-in phrases.

The format is one array of template variants per event key:

```json
{
  "awacs_missile": [
    "defend, defend, missile inbound bearing {bearing}",
    "{callsign}, missile inbound, defend, bearing {bearing}"
  ]
}
```

A random variant is picked per call, avoiding the last-used one. `{slot}` placeholders are filled per event; the available slots are `{callsign}`, `{awacs}`, `{runway}`, `{bearing}`, `{bearing_clause}`, `{range}`, `{altitude}`, `{altitude_clause}`, `{aspect}`, and `{type}` (which keys receive which slots matches the built-in file). Write numbers as words — text is sent to TTS as-is.

The BepInEx log reports which source was loaded, e.g. `Loaded 13 phrase banks from embedded resource.` If the file is missing or malformed, an error is logged and radio calls fall back to speaking raw event keys such as `awacs_rtb_fuel`.

## Plugin Configuration

BepInEx writes config to:

```text
<Nuclear Option>\BepInEx\config\com.lnenad.radiochatter.cfg
```

### General

| Key | Default | Meaning |
|---|---:|---|
| `Enabled` | `true` | Master switch. |
| `PlayerCallsign` | `Falcon 1-1` | Callsign used by tower/AWACS. |
| `AwacsCallsign` | `Overwatch` | AWACS station callsign in generated phrases. |
| `SubtitlesEnabled` | `true` | Shows bottom-center radio subtitles. |
| `PollIntervalSeconds` | `0.5` | Game-state polling interval, clamped from `0.1` to `2`. |
| `DebugOverlay` | `false` | Shows live player/contact/airbase state. |

### Sidecar

| Key | Default | Meaning |
|---|---:|---|
| `Url` | `http://127.0.0.1:5075` | Base URL for the Pocket TTS sidecar. |
| `AutoStartSidecar` | `true` | If enabled, tries to launch the sidecar when `/health` is down. |
| `SidecarCommand` | empty | Path to a sidecar launcher script. |
| `CacheSize` | `100` | Max synthesized clips kept in the in-memory TTS cache. |

If `AutoStartSidecar` is enabled, either set `SidecarCommand` to this repo's `sidecar/run_sidecar.bat` or `sidecar/run_sidecar.sh`, or deploy the `sidecar` folder next to the plugin in `BepInEx/plugins/RadioChatter/sidecar`. The build scripts copy those sidecar files during deploy.

### Audio

| Key | Default | Meaning |
|---|---:|---|
| `Volume` | `0.8` | Voice playback volume. |
| `RadioEffectEnabled` | `true` | Applies the radio effect to generated clips. |
| `NoiseLevel` | `0.015` | Very light transmission hiss amount. |
| `MaxConcurrentTransmissions` | `3` | Max different radio channels that may overlap. |
| `TowerVoice` | `tower` | Sidecar voice alias for tower calls. |
| `AwacsVoice` | `awacs` | Sidecar voice alias for AWACS calls. |
| `PlayerVoice` | `player` | Sidecar voice alias for player/pilot calls. |
| `WingmanVoice` | `wingman` | Sidecar voice alias for captured mission comms. |

Same-channel chatter is serialized regardless of `MaxConcurrentTransmissions`.

### Callouts

| Key | Default | Meaning |
|---|---:|---|
| `Takeoff` | `true` | Tower takeoff and airborne calls. |
| `Landing` | `true` | Tower landing clearance and welcome-home calls. |
| `Approach` | `true` | Tower inbound approach call. |
| `NewContact` | `true` | AWACS new-contact BRA calls. |
| `ContactInfoCooldownSeconds` | `35` | Minimum seconds between AWACS calls about the same contact (new contact, picture, vector), unless its range or aspect changes significantly. |
| `PictureUpdate` | `true` | Periodic AWACS picture calls. |
| `PictureIntervalSeconds` | `45` | Minimum seconds between picture calls. |
| `VectorToTarget` | `true` | AWACS calls for the selected target, suppressed inside 4 km. |
| `VectorIntervalSeconds` | `20` | Minimum seconds between target vector calls. |
| `SplashCalls` | `true` | Player kill confirmations. |
| `MissileWarning` | `true` | Defend calls for missiles targeting the player. |
| `PlayerWeaponCalls` | `true` | Pilot weapon-release calls such as fox, rifle, magnum, pickle, and guns. |
| `PlayerDefensiveCalls` | `true` | Pilot defensive calls for incoming missiles. |
| `PlayerEjectionCalls` | `true` | Pilot mayday call when the player ejects. |
| `PlayerAcknowledgements` | `true` | Short varied pilot acknowledgements after incoming radio calls finish. |
| `InGameComms` | `true` | Reads mission-scripted comms through the wingman voice. |
| `RtbCalls` | `true` | Low-fuel and sustained inbound return-to-base advisories. |

## How Callouts Work

### Tower

- Takeoff clearance fires when the player is stably grounded near a friendly airbase at mission start.
- Airborne handoff fires after transitioning from grounded to airborne near the airbase.
- Approach call now requires sustained inbound flight:
  - within `35 km` of the home airfield,
  - closing on the airfield,
  - under `3500 m AGL`,
  - showing approach intent by heading generally toward the field, descending, gear down, or already inside `12 km`,
  - held for `22 seconds` with a short grace window for heading/altitude corrections,
  - and within `16 km` when the call plays.
- Final landing clearance uses the game's own UI message, such as `Cleared to land runway 27`, instead of trying to infer final approach.
- Runway numbers are parsed from the UI message, including `runway 27L`, `runway 27 L`, and `RWY 27`.
- Landing/welcome-home fires on successful sortie or stable landing near the friendly airbase.

### AWACS

- New contacts come from the local faction tracking database and only include currently observed enemy aircraft.
- Picture updates use the nearest observed enemy contact. While a target is selected (and vector calls are enabled), the picture skips that contact — the vector call owns it — and covers the next nearest threat instead.
- Target vectors use the player's currently selected target.
- New-contact, picture, and vector calls share a per-contact cooldown (`ContactInfoCooldownSeconds`), so AWACS does not repeat the same facts about one contact back to back. It breaks in early only when the situation changes: the contact turns hot, or its range moves by about a quarter.
- When a player kill is confirmed, queued and in-progress vector calls are cut before the splash call plays, so AWACS does not give a vector to a target that is already down.
- Informational calls are checked again the moment they would actually play: calls about a contact that has since been destroyed are dropped, once one call about a contact plays any queued repeats about it are discarded, and a new-contact/picture/vector clip that could not start within ~12 seconds (busy channel, slow TTS) is thrown away rather than played late.
- Missile warnings come from missiles locking the local aircraft.
- Splash/good-effect calls use the local kill display event.
- RTB calls include a one-time bingo-fuel advisory below about 18 percent fuel and a home-plate vector after roughly 18 seconds of sustained inbound flight from 18-90 km out.

### Player / Pilot Chatter

Player weapon calls are detected from local `WeaponManager.Fire()` events, with lower-level weapon hooks kept as fallback. The classifier uses Nuclear Option `WeaponInfo` role fields and weapon names:

- `fox one!`: semi-active radar-guided air-to-air missile.
- `fox two!`: infrared air-to-air missile.
- `fox three!`: active radar or otherwise radar/BVR-style air-to-air missile, including AAM Scythe.
- `rifle!`: air-to-ground missile.
- `magnum!`: anti-radar / anti-radiation missile.
- `pickle!`: bomb or glide-bomb release.
- `guns! guns! guns!`: gun or cannon fire.

Incoming missile events also add a pilot defensive call on the player channel: `missile, break`.

Player ejection is detected from the local aircraft ejected state and interrupts current chatter so the pilot call plays immediately: `mayday! mayday! ejecting!`. If the local aircraft is destroyed without an ejection, active and queued chatter is interrupted and AWACS calls the aircraft down, e.g. `Darkstar, Broadsword 1-1 is down, no chute`.

If a weapon maps incorrectly, enable BepInEx debug logging and check the `Player weapon call:` log entry for the exact weapon display name.

After incoming tower, AWACS, or wingman comms finish and the non-player radio queue is drained, the player channel may respond. Tower takeoff clearance, landing clearance, and AWACS handoff get specific readbacks; other comms get short varied acknowledgements. If overlapping tower/AWACS/wingman lines finish together, multiple player responses are queued and played sequentially on the shared player channel. Responses are coalesced by source while one is already pending or playing, so a batch of AWACS calls gets one `[PLAYER-AWACS]` response and a batch of flight chatter gets one `[PLAYER-FLIGHT]` response. Missile/defend calls are excluded so they do not get casual acknowledgements during defensive moments.

### In-Game / Wingman Comms

Mission-scripted messages shown through `MissionMessages.ShowMessage(...)` are captured and voiced through the wingman channel, unless they are recognized as tower landing clearance.

At mission start only, if wingman/in-game lines arrive while the player is still grounded at a friendly airbase and the tower takeoff clearance has not fired yet, RadioChatter holds those lines until after the takeoff clearance and player readback.

If startup mission comms arrive while the tower takeoff clearance or the player's takeoff readback is still pending or playing, those lines are held until the `[PLAYER-TWR]` readback has finished. This keeps the takeoff exchange together before AI wingman chatter starts.

Non-urgent AWACS lines generated during that same startup gate are also held. The intended first-airfield sequence is tower clearance, player readback, startup mission/wingman comms, then AWACS. After that startup sequence clears, AWACS and mission/wingman channels may overlap normally again.

The sanitizer:

- strips rich text tags,
- collapses whitespace,
- expands compact units such as `500m` to `500 meters`,
- expands `500ft` to `500 feet`,
- expands `120m/s` and `120 m/s` to `120 meters per second`.

## Audio Behavior

The audio path is:

1. `CommsDirector` decides which line to transmit.
2. `PocketTtsClient` checks the in-memory cache or requests WAV audio from the sidecar.
3. `RadioAudioPlayer` applies the radio effect offline to the generated PCM samples.
4. The clip is played through a temporary 2D Unity `AudioSource`.
5. The source and generated clip are destroyed after playback.

Different roles may overlap, but a role cannot overlap itself. For example, AWACS can talk over wingman, but two wingman lines or two player lines will play sequentially.

Subtitles are shown when audio playback begins, not when the line is first queued.

While the game is paused, the radio pipeline freezes with it: playing lines pause and resume, queued lines are held without expiring, and no new lines start until the game unpauses. Subtitles also stay on screen through a pause. Nothing is skipped or lost because of a pause.

## Debugging

Enable:

```ini
[General]
DebugOverlay = true
```

The overlay shows:

- game mode and mission state,
- player aircraft, position, heading, speed, altitude, gear, grounded state,
- nearest friendly airbase and runway,
- selected target bearing/range,
- counts for contacts, airbases, missiles, and tracked units.

Logs are written through BepInEx. Look in the BepInEx console or log output for messages prefixed with `RadioChatter`.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| No audio, subtitles still appear | Sidecar is not running or `/health` failed. | Start `sidecar/run_sidecar.bat` or `sidecar/run_sidecar.sh` and check `http://127.0.0.1:5075/health`. |
| No audio and no subtitles | Plugin disabled or not loaded. | Check `Enabled=true`, plugin DLL path, and BepInEx logs. |
| Deploy fails with `user-mapped section open` | Nuclear Option has the DLL loaded. | Close the game before copying the DLL. |
| Sidecar says `pocket-tts is not installed` | Python environment is missing dependencies. | Run `python -m pip install -r sidecar\requirements.txt` in the sidecar venv. |
| Sidecar starts slowly | Pocket TTS model is loading or downloading. | Wait for `RadioChatter Pocket TTS sidecar listening...`. |
| Auto-started sidecar never comes online (python running, no CPU) | A stalled HuggingFace connection hung the model load. | The sidecar launchers set `HF_HUB_ETAG_TIMEOUT`/`HF_HUB_DOWNLOAD_TIMEOUT` and `server.py` retries with `HF_HUB_OFFLINE=1`, so this should self-recover; if a python process is stuck from an older version, stop it and start the sidecar again. |
| Wrong voice for a role | Plugin voice alias and `voices.json` do not match. | Update `Audio.*Voice` config or `sidecar\voices.json`, then restart the sidecar. |
| Radio speaks raw keys like `awacs_rtb_fuel` | `phrases.json` failed to load, or a loose override is malformed or missing that key. | Check the BepInEx log for the phrase-bank error; fix or delete `BepInEx\plugins\RadioChatter\phrases.json`. |
| Player weapon call is wrong | Weapon classification heuristic needs tuning for that Nuclear Option weapon. | Check the `Player weapon call:` log entry and update the mapping in `WeaponFirePatch`. |
| New contact call when map looks empty | Contact filtering may need re-checking against game detection state. | Enable debug overlay and compare `Contacts:` count to the in-game map. |
| Landing clearance repeats or fires wrong | The UI landing message parser matched an unexpected message. | Check BepInEx logs for `[Tower]` lines and capture the exact UI text. |
| Inbound approach fires too early | Approach gate constants need tuning. | Current gate is 22 seconds of sustained closure and under 16 km before call. |
| Multiplayer client produces no calls | Intended behavior. | RadioChatter is singleplayer/host only. |

## Development Notes

Build only:

```powershell
dotnet build src\RadioChatter\RadioChatter.csproj -c Release
```

Build with explicit game path:

```powershell
dotnet build src\RadioChatter\RadioChatter.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Nuclear Option\"
```

```sh
dotnet build src/RadioChatter/RadioChatter.csproj -c Release -p:GameDir="$HOME/.steam/steam/steamapps/common/Nuclear Option"
```

Build GitHub release assets:

```powershell
python tools\package_github_release.py --game-dir "D:\SteamLibrary\steamapps\common\Nuclear Option\"
```

```sh
python3 tools/package_github_release.py --game-dir "$HOME/.steam/steam/steamapps/common/Nuclear Option"
```

The Linux asset is written to `dist/RadioChatter-<version>-linux.zip`. If Inno Setup 6 is installed, the Windows wizard is compiled to `dist/RadioChatter-<version>-Setup.exe`; otherwise the script stages the payload and prints the manual Inno compile step. Use `--skip-build` if the Release DLL is already current.

Automated tagged releases use a prebuilt payload because GitHub-hosted runners cannot legally build against Nuclear Option game assemblies. Create release tags from a local machine with the game installed:

```powershell
.\tools\new_release_tag.ps1 -Version 0.1.0 -GameDir "D:\SteamLibrary\steamapps\common\Nuclear Option\"
git push origin HEAD
git push origin v0.1.0
```

Push the branch and the tag separately: pushing both refs in one command can make GitHub drop the tag push event, and the release workflow never triggers. If a tag was already pushed without triggering a run, either re-push it (`git push origin :refs/tags/v0.1.0 && git push origin v0.1.0`) or run the **Release** workflow manually from the Actions tab with the tag name.

That script builds the DLL locally, copies `RadioChatter.dll` plus sidecar files into `release/payload`, commits that payload, and creates the annotated tag. The GitHub Actions workflow then packages the Windows installer `.exe` and Linux zip from `release/payload` without needing game files on the runner.

The old Thunderstore package helper is still available at `tools/package_thunderstore.py`, but the GitHub release installer/script path is the primary distribution flow.

The C# project references game assemblies from:

```text
<GameDir>/NuclearOption_Data/Managed/
<GameDir>/BepInEx/core/
```

You can add a local uncommitted override at:

```text
src\RadioChatter\Local.props
```

Example:

```xml
<Project>
  <PropertyGroup>
    <GameDir>D:/SteamLibrary/steamapps/common/Nuclear Option/</GameDir>
  </PropertyGroup>
</Project>
```

Linux example:

```xml
<Project>
  <PropertyGroup>
    <GameDir>/home/you/.steam/steam/steamapps/common/Nuclear Option/</GameDir>
  </PropertyGroup>
</Project>
```

All direct Nuclear Option API access should stay behind `src/RadioChatter/Game/GameAdapter.cs` or thin Harmony event patches in `src/RadioChatter/Game/Patches.cs`. This keeps game-update repairs localized.

Other layering conventions:

- `Comms/CommsDirector.cs` holds mission logic only (detectors, gating, queue); stateless string helpers belong in `Comms/RadioText.cs`.
- Phrase templates belong in `Speech/phrases.json`, not in code. `PhraseEngine` loads the embedded copy or a loose override next to the DLL (see Customizing Phrases).
- The polling and audio paths avoid steady-state allocations; prefer reusing lists/structs over LINQ or closures there.

## Test Checklist

- Start sidecar and verify `/health`.
- Launch a mission from a friendly airbase.
- Confirm takeoff clearance on ramp/start.
- Take off and confirm airborne handoff.
- Spawn or approach detected enemies and confirm one new-contact call per observed enemy aircraft.
- Select a target and confirm vector calls respect cooldown.
- Trigger a missile warning and confirm a defend call.
- Fire air-to-air, air-to-ground, anti-radar, bomb, and gun weapons; confirm player calls map to `fox three!`, `rifle!`, `magnum!`, `pickle!`, and `guns! guns! guns!`.
- Trigger an incoming missile and confirm the player defensive call does not spam.
- Eject and confirm `mayday! mayday! ejecting!` interrupts other chatter immediately.
- Destroy the player aircraft without ejection and confirm active/queued chatter is interrupted by the AWACS down call.
- Let a tower clearance/handoff finish and confirm the player gives a readback; let an AWACS/wingman line finish and confirm a short varied acknowledgement follows after the non-player radio queue drains.
- Kill an aircraft and confirm one splash call.
- Return to base, fly inbound for a sustained period, and confirm approach call timing.
- Return to base from more than 18 km out and confirm the RTB vector call after sustained inbound flight.
- Drop below bingo fuel and confirm one AWACS RTB fuel advisory.
- Wait for the game's `Cleared to land runway ...` UI message and confirm the tower reads the runway from it.
- Land successfully and confirm welcome-home call.
- Trigger multiple wingman messages and confirm they do not overlap with each other.

## Known Limitations

- The mod is not intended for multiplayer clients.
- Pocket TTS latency depends on machine speed and whether the line is already cached.
- Game API changes may require updating `GameAdapter` and `Patches`.
