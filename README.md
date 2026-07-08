<div align="center">

# RadioChatter

**Immersive, dynamically voiced radio comms for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/).**

Tower, AWACS, your own pilot, and mission chatter — spoken live from real game state,
not pre-recorded WAVs.

[![Latest release](https://img.shields.io/github/v/release/lnenad/radiochatter)](https://github.com/lnenad/radiochatter/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/lnenad/radiochatter/total)](https://github.com/lnenad/radiochatter/releases)
[![Release pipeline](https://github.com/lnenad/radiochatter/actions/workflows/release.yml/badge.svg)](https://github.com/lnenad/radiochatter/actions/workflows/release.yml)
![BepInEx 5](https://img.shields.io/badge/BepInEx-5.x-green)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20Linux-blue)

</div>

```text
[TWR]     Falcon 1-1, winds calm, runway two seven, cleared for takeoff.
[PLAYER]  Cleared for takeoff runway two seven, Falcon 1-1.
[AWACS]   Falcon 1-1, Overwatch, new contact, bearing zero four five, forty kilometers, hot.
[PLAYER]  Fox three!
[AWACS]   Splash one. Good kill, Falcon 1-1.
```

RadioChatter is a [BepInEx 5](https://github.com/BepInEx/BepInEx) mod that watches live game
state and generates spoken radio traffic through a local
[Pocket TTS](https://github.com/kyutai-labs/pocket-tts) sidecar. Because lines are synthesized
on the fly, callouts carry real bearings, ranges, altitudes, runways, and callsigns from your
current mission.

The mod targets **singleplayer and host-side play**. Multiplayer clients are detected and the
mod disables itself rather than read state it does not own.

## Features

**Tower**
- Takeoff clearance, airborne handoff, inbound approach, landing clearance (read from the
  game's own UI message, including the runway number), and welcome-home calls.

**AWACS**
- New-contact BRA calls for freshly detected enemy aircraft.
- Picture updates for the nearest observed threat.
- Vectors to your currently selected target.
- Missile-threat warnings, kill confirmations, bingo-fuel and RTB advisories.
- Smart de-duplication: calls about the same contact share a cooldown, stale calls are dropped
  the moment they would play (destroyed target, expired info, already-covered contact).

**Player / pilot**
- Weapon-release calls: `fox one!`, `fox two!`, `fox three!`, `rifle!`, `magnum!`, `pickle!`,
  and `guns! guns! guns!`, classified from the actual weapon fired.
- Defensive calls for incoming missiles (`missile, break`) and an ejection mayday that
  interrupts everything else.
- Readbacks for tower clearances and AWACS handoffs; short varied acknowledgements after other
  incoming comms, labeled by source (`[PLAYER-TWR]`, `[PLAYER-FLIGHT]`, `[PLAYER-AWACS]`).

**Mission / wingman comms**
- In-game scripted messages (`MissionMessages.ShowMessage`) are captured, cleaned up, and
  voiced on a separate wingman channel.

**Presentation**
- Distinct configurable voice per role (tower, AWACS, player, wingman).
- Radio effect: band-limited voice, mild saturation, quiet configurable hiss.
- Same-channel serialization — tower never talks over tower — while different channels may
  overlap naturally after the initial takeoff exchange.
- Subtitles timed to audio playback, with a subtitles-only fallback when TTS is unavailable.
- Pause-safe: pausing the game freezes the whole radio pipeline; nothing is skipped or lost.
- Fully customizable phrase templates via a drop-in `phrases.json` — no rebuild needed.

## Installation

### Prerequisites

1. [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/) installed.
2. [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (Mono build) installed into the
   game folder — run the game once afterwards so BepInEx creates its folders. Recommended
   setting in `BepInEx/config/BepInEx.cfg`:

   ```ini
   [Chainloader]
   HideGameManagerObject = true
   ```

3. Python 3.10+ for the TTS sidecar (voice synthesis). Without it the mod still works in
   subtitles-only mode.

### Windows

1. Download `RadioChatter-<version>-Setup.exe` from the
   [latest release](https://github.com/lnenad/radiochatter/releases/latest).
2. Close Nuclear Option and run the installer.
3. Select your Nuclear Option game folder.
4. Leave *Prepare Pocket TTS sidecar now* checked to set up the voice environment immediately
   (needs internet for the first model download).

### Linux

Download `RadioChatter-<version>-linux.zip` from the
[latest release](https://github.com/lnenad/radiochatter/releases/latest), then:

```sh
unzip RadioChatter-<version>-linux.zip
sh install-radiochatter.sh --game-dir "$HOME/.steam/steam/steamapps/common/Nuclear Option" --yes
```

### First run

The plugin auto-starts the sidecar. If the sidecar environment was not prepared during
install, the launcher creates `BepInEx/plugins/RadioChatter/sidecar/.venv`, installs the
Python dependencies, and downloads the Pocket TTS model — the first startup can take several
minutes. Subsequent startups take a few seconds. Until the sidecar is up, the mod shows
subtitles only.

To install from source instead, see [Building from source](#building-from-source).

## Configuration

BepInEx writes the config to `<Nuclear Option>/BepInEx/config/com.lnenad.radiochatter.cfg`
after the first launch. Highlights: set `PlayerCallsign` to your preferred callsign, and use
the `Callouts` section to switch individual call types on or off.

<details>
<summary><strong>General</strong></summary>

| Key | Default | Meaning |
|---|---:|---|
| `Enabled` | `true` | Master switch. |
| `PlayerCallsign` | `Falcon 1-1` | Callsign used by tower/AWACS. |
| `AwacsCallsign` | `Overwatch` | AWACS station callsign in generated phrases. |
| `SubtitlesEnabled` | `true` | Shows bottom-center radio subtitles. |
| `PollIntervalSeconds` | `0.5` | Game-state polling interval, clamped from `0.1` to `2`. |
| `DebugOverlay` | `false` | Shows live player/contact/airbase state. |

</details>

<details>
<summary><strong>Sidecar</strong></summary>

| Key | Default | Meaning |
|---|---:|---|
| `Url` | `http://127.0.0.1:5075` | Base URL for the Pocket TTS sidecar. |
| `AutoStartSidecar` | `true` | If enabled, tries to launch the sidecar when `/health` is down. |
| `SidecarCommand` | empty | Path to a sidecar launcher script. |
| `CacheSize` | `100` | Max synthesized clips kept in the in-memory TTS cache. |

If `AutoStartSidecar` is enabled, either set `SidecarCommand` to a launcher script, or keep
the `sidecar` folder next to the plugin in `BepInEx/plugins/RadioChatter/sidecar` (the
installers and build scripts put it there).

</details>

<details>
<summary><strong>Audio</strong></summary>

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

</details>

<details>
<summary><strong>Callouts</strong></summary>

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

</details>

## Customizing voices

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

The plugin requests these aliases through the `Audio.*Voice` config keys. To change a role's
voice, edit `voices.json` and restart the sidecar. If you change a voice ID in the BepInEx
config, make sure the same alias exists in `voices.json`.

## Customizing phrases

All tower/AWACS phrase templates live in
[src/RadioChatter/Speech/phrases.json](src/RadioChatter/Speech/phrases.json), embedded into
the DLL at build time. At startup the plugin loads phrases from the first of:

1. A loose `phrases.json` next to the deployed DLL:
   `<Nuclear Option>/BepInEx/plugins/RadioChatter/phrases.json`
2. The copy embedded in the DLL.

To customize callouts without rebuilding, copy `phrases.json` next to the deployed DLL and
edit it. Delete the loose file to revert to the built-in phrases.

The format is one array of template variants per event key:

```json
{
  "awacs_missile": [
    "defend, defend, missile inbound bearing {bearing}",
    "{callsign}, missile inbound, defend, bearing {bearing}"
  ]
}
```

A random variant is picked per call, avoiding the last-used one. `{slot}` placeholders are
filled per event; the available slots are `{callsign}`, `{awacs}`, `{runway}`, `{bearing}`,
`{bearing_clause}`, `{range}`, `{altitude}`, `{altitude_clause}`, `{aspect}`, and `{type}`
(which keys receive which slots matches the built-in file). Write numbers as words — text is
sent to TTS as-is.

The BepInEx log reports which source was loaded, e.g.
`Loaded 13 phrase banks from embedded resource.` If the file is missing or malformed, an error
is logged and radio calls fall back to speaking raw event keys such as `awacs_rtb_fuel`.

## How it works

```text
game state ────► GameAdapter ──► StatePoller ──► CommsDirector ──► RadioAudioPlayer
Harmony patches ──► RadioEventBus ────────────────┘                      │
                                                                Pocket TTS sidecar
                                                               (localhost HTTP, WAV)
```

1. `CommsDirector` watches polled game state and Harmony events, decides which line to say,
   and manages priorities, cooldowns, and the transmission queue.
2. `PocketTtsClient` checks an in-memory cache or requests WAV audio from the local sidecar.
3. `RadioAudioPlayer` applies the radio effect offline to the PCM samples and plays the clip
   through a temporary 2D Unity `AudioSource`.

Different roles may overlap, but a role never overlaps itself: AWACS can talk over the
wingman, but two wingman lines play sequentially. Subtitles appear when audio playback
begins, not when a line is queued. While the game is paused the whole radio pipeline freezes:
playing lines pause and resume, queued lines are held without expiring, and subtitles stay on
screen. Nothing is skipped or lost because of a pause.

<details>
<summary><strong>Tower callout logic</strong></summary>

- Takeoff clearance fires when the player is stably grounded near a friendly airbase at
  mission start.
- Airborne handoff fires after transitioning from grounded to airborne near the airbase.
- The approach call requires sustained inbound flight:
  - within `35 km` of the home airfield,
  - closing on the airfield,
  - under `3500 m AGL`,
  - showing approach intent by heading generally toward the field, descending, gear down, or
    already inside `12 km`,
  - held for `22 seconds` with a short grace window for heading/altitude corrections,
  - and within `16 km` when the call plays.
- Final landing clearance uses the game's own UI message, such as `Cleared to land runway 27`,
  instead of trying to infer final approach. Runway numbers are parsed from the UI message,
  including `runway 27L`, `runway 27 L`, and `RWY 27`.
- Landing/welcome-home fires on successful sortie or stable landing near the friendly airbase.

</details>

<details>
<summary><strong>AWACS callout logic</strong></summary>

- New contacts come from the local faction tracking database and only include currently
  observed enemy aircraft.
- Picture updates use the nearest observed enemy contact. While a target is selected (and
  vector calls are enabled), the picture skips that contact — the vector call owns it — and
  covers the next nearest threat instead.
- Target vectors use the player's currently selected target.
- New-contact, picture, and vector calls share a per-contact cooldown
  (`ContactInfoCooldownSeconds`), so AWACS does not repeat the same facts about one contact
  back to back. It breaks in early only when the situation changes: the contact turns hot, or
  its range moves by about a quarter.
- When a player kill is confirmed, queued and in-progress vector calls are cut before the
  splash call plays, so AWACS does not give a vector to a target that is already down.
- Informational calls are re-checked the moment they would actually play: calls about a
  contact that has since been destroyed are dropped, once one call about a contact plays any
  queued repeats about it are discarded, and a new-contact/picture/vector clip that could not
  start within ~12 seconds (busy channel, slow TTS) is thrown away rather than played late.
- Missile warnings come from missiles locking the local aircraft.
- Splash/good-effect calls use the local kill display event.
- RTB calls include a one-time bingo-fuel advisory below about 18 percent fuel and a
  home-plate vector after roughly 18 seconds of sustained inbound flight from 18-90 km out.

</details>

<details>
<summary><strong>Player / pilot chatter logic</strong></summary>

Player weapon calls are detected from local `WeaponManager.Fire()` events, with lower-level
weapon hooks kept as fallback. The classifier uses Nuclear Option `WeaponInfo` role fields and
weapon names:

- `fox one!`: semi-active radar-guided air-to-air missile.
- `fox two!`: infrared air-to-air missile.
- `fox three!`: active radar or otherwise radar/BVR-style air-to-air missile, including AAM
  Scythe.
- `rifle!`: air-to-ground missile.
- `magnum!`: anti-radar / anti-radiation missile.
- `pickle!`: bomb or glide-bomb release.
- `guns! guns! guns!`: gun or cannon fire.

Incoming missile events also add a pilot defensive call on the player channel:
`missile, break`.

Player ejection is detected from the local aircraft ejected state and interrupts current
chatter so the pilot call plays immediately: `mayday! mayday! ejecting!`. If the local
aircraft is destroyed without an ejection, active and queued chatter is interrupted and AWACS
calls the aircraft down, e.g. `Darkstar, Broadsword 1-1 is down, no chute`.

If a weapon maps incorrectly, enable BepInEx debug logging and check the
`Player weapon call:` log entry for the exact weapon display name.

After incoming tower, AWACS, or wingman comms finish and the non-player radio queue is
drained, the player channel may respond. Tower takeoff clearance, landing clearance, and
AWACS handoff get specific readbacks; other comms get short varied acknowledgements. If
overlapping tower/AWACS/wingman lines finish together, multiple player responses are queued
and played sequentially on the shared player channel. Responses are coalesced by source while
one is already pending or playing, so a batch of AWACS calls gets one `[PLAYER-AWACS]`
response and a batch of flight chatter gets one `[PLAYER-FLIGHT]` response. Missile/defend
calls are excluded so they do not get casual acknowledgements during defensive moments.

</details>

<details>
<summary><strong>Mission / wingman comms logic</strong></summary>

Mission-scripted messages shown through `MissionMessages.ShowMessage(...)` are captured and
voiced through the wingman channel, unless they are recognized as tower landing clearance.

At mission start only, RadioChatter keeps the takeoff exchange together: if wingman/in-game
lines arrive while the player is still grounded at a friendly airbase and the tower takeoff
clearance (or the player's readback) is still pending or playing, those lines are held until
the `[PLAYER-TWR]` readback has finished. Non-urgent AWACS lines generated during that same
startup gate are also held. The intended first-airfield sequence is tower clearance, player
readback, startup mission/wingman comms, then AWACS. After that startup sequence clears, the
channels may overlap normally again.

The sanitizer strips rich text tags, collapses whitespace, and expands compact units — `500m`
to `500 meters`, `500ft` to `500 feet`, `120m/s` to `120 meters per second`.

</details>

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

Logs are written through BepInEx — look for messages prefixed with `RadioChatter`. For live
state, enable the debug overlay (`[General] DebugOverlay = true`); it shows game mode and
mission state, player position/heading/speed/altitude/gear, the nearest friendly airbase and
runway, the selected target's bearing/range, and counts for contacts, airbases, missiles, and
tracked units.

## Building from source

### Requirements

- Nuclear Option installed locally (the project references its game assemblies).
- BepInEx 5.x (Mono) installed into the game folder.
- A .NET SDK capable of building SDK-style projects.
- Python 3.10+ for the sidecar.

The project defaults to these game paths:

```text
Windows: D:/SteamLibrary/steamapps/common/Nuclear Option/
Linux:   ~/.steam/steam/steamapps/common/Nuclear Option/
```

Override with `build.ps1 -GameDir ...`, `build.sh --game-dir ...`, or an uncommitted
`src/RadioChatter/Local.props`:

```xml
<Project>
  <PropertyGroup>
    <GameDir>D:/SteamLibrary/steamapps/common/Nuclear Option/</GameDir>
  </PropertyGroup>
</Project>
```

### Build and deploy

```powershell
# Windows
.\build.ps1
.\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Nuclear Option\"
```

```sh
# Linux
sh ./build.sh
sh ./build.sh --game-dir "$HOME/.local/share/Steam/steamapps/common/Nuclear Option"
```

The scripts build `src/RadioChatter/bin/Release/RadioChatter.dll` and copy the DLL plus the
sidecar launcher files into `<GameDir>/BepInEx/plugins/RadioChatter/`. To build without
deploying:

```powershell
dotnet build src\RadioChatter\RadioChatter.csproj -c Release
```

The C# project references game assemblies from `<GameDir>/NuclearOption_Data/Managed/` and
`<GameDir>/BepInEx/core/`.

### The Pocket TTS sidecar

RadioChatter talks to a local HTTP sidecar at `http://127.0.0.1:5075`:

- `GET /health` returns JSON status and loaded voice aliases.
- `POST /speak` accepts `{"text":"...", "voice":"..."}` and returns `audio/wav`.

The launchers (`sidecar/run_sidecar.bat`, `sidecar/run_sidecar.sh`) install dependencies
automatically into `sidecar/.venv` if no usable environment exists; they prefer a local
`sidecar/.venv`, then a repo-level `.venv-sidecar312`, then create `sidecar/.venv` from system
Python. To prepare the environment manually from the repo root:

```powershell
# Windows
py -3.12 -m venv .venv-sidecar312
.\.venv-sidecar312\Scripts\python.exe -m pip install --upgrade pip
.\.venv-sidecar312\Scripts\python.exe -m pip install -r sidecar\requirements.txt
```

```sh
# Linux
python3 -m venv .venv-sidecar312
./.venv-sidecar312/bin/python -m pip install --upgrade pip
./.venv-sidecar312/bin/python -m pip install -r sidecar/requirements.txt
```

Start it with `.\sidecar\run_sidecar.bat` (Windows) or `sh ./sidecar/run_sidecar.sh` (Linux)
and expect:

```text
Loading Pocket TTS model and voices...
Loaded voices: awacs, default, eve, player, tower, wingman
RadioChatter Pocket TTS sidecar listening on http://127.0.0.1:5075
```

Check health and synthesize a test WAV:

```powershell
Invoke-RestMethod http://127.0.0.1:5075/health
$body = @{ text = "Falcon 1, cleared to land runway two seven"; voice = "tower" } | ConvertTo-Json
Invoke-WebRequest -Uri http://127.0.0.1:5075/speak -Method Post -ContentType "application/json" -Body $body -OutFile tower_test.wav
```

```sh
curl http://127.0.0.1:5075/health
curl -sS http://127.0.0.1:5075/speak \
  -H "Content-Type: application/json" \
  -d '{"text":"Falcon 1, cleared to land runway two seven","voice":"tower"}' \
  --output tower_test.wav
```

### Repository layout

```text
build.ps1, build.sh          build + deploy into the local game install
installer/
  windows/RadioChatterInstaller.iss   Inno Setup wizard script
  linux/install-radiochatter.sh       POSIX install script
sidecar/                     Pocket TTS HTTP sidecar (server.py, launchers, voices.json)
plan/                        original design notes and game API dump tooling
tools/
  new_release_tag.ps1        builds the DLL and stages/commits/tags a release payload
  package_github_release.py  builds GitHub release assets (installer .exe + linux zip)
  package_thunderstore.py    optional Thunderstore/r2modman zip
src/RadioChatter/
  Plugin.cs                  BepInEx entry point + config
  RadioRuntime.cs            static wiring between patches and services
  Audio/
    RadioAudioPlayer.cs      playback, radio effect, acknowledgements, subtitles
  Comms/
    CommsDirector.cs         event detection, priorities, cooldowns, queueing
    RadioText.cs             stateless text/formatting helpers
    Events.cs                roles, event types, thread-safe event bus
  Game/
    GameAdapter.cs           all game API reads (behind IGameAdapter)
    Patches.cs               Harmony patches -> RadioEventBus events
    Snapshot.cs              per-tick game-state structs
    StatePoller.cs           polls adapter, drives CommsDirector
  Speech/
    PhraseEngine.cs          loads phrases.json, slot filling, variation
    phrases.json             phrase banks (embedded; loose copy overrides)
    NumberSpeech.cs          numbers -> spoken words
    PocketTtsClient.cs       sidecar HTTP client, WAV parse, LRU cache
    SidecarSupervisor.cs     health probes, auto-start, backoff
```

### Conventions

- All direct Nuclear Option API access stays behind `Game/GameAdapter.cs` or thin Harmony
  event patches in `Game/Patches.cs`, so game-update repairs stay localized.
- `Comms/CommsDirector.cs` holds mission logic only (detectors, gating, queue); stateless
  string helpers belong in `Comms/RadioText.cs`.
- Phrase templates belong in `Speech/phrases.json`, not in code.
- The polling and audio paths avoid steady-state allocations; prefer reusing lists/structs
  over LINQ or closures there.

### Releasing

Tagged releases are packaged by GitHub Actions from a **prebuilt payload**, because
GitHub-hosted runners cannot legally build against Nuclear Option game assemblies. Create
release tags from a machine with the game installed:

```powershell
.\tools\new_release_tag.ps1 -Version 0.1.0 -GameDir "D:\SteamLibrary\steamapps\common\Nuclear Option\"
git push origin HEAD
git push origin v0.1.0
```

The script builds the DLL, copies it plus the sidecar files into `release/payload`, commits
the payload, and creates the annotated tag. The `Release` workflow then compiles the Windows
installer and the Linux zip from that payload and attaches both to the GitHub release.

Push the branch and the tag **separately**: pushing both refs in one command can make GitHub
drop the tag push event, and the release workflow never triggers. If a tag was pushed without
triggering a run, either re-push it
(`git push origin :refs/tags/v0.1.0 && git push origin v0.1.0`) or run the **Release**
workflow manually from the Actions tab with the tag name.

To build release assets locally, run `python tools/package_github_release.py` — the Linux zip
is written to `dist/`, and the Windows wizard is compiled there too when Inno Setup 6 is
installed. Use `--skip-build` if the Release DLL is already current.

<details>
<summary><strong>Manual test checklist</strong></summary>

- Start sidecar and verify `/health`.
- Launch a mission from a friendly airbase.
- Confirm takeoff clearance on ramp/start.
- Take off and confirm airborne handoff.
- Spawn or approach detected enemies and confirm one new-contact call per observed enemy
  aircraft.
- Select a target and confirm vector calls respect cooldown.
- Trigger a missile warning and confirm a defend call.
- Fire air-to-air, air-to-ground, anti-radar, bomb, and gun weapons; confirm player calls map
  to `fox three!`, `rifle!`, `magnum!`, `pickle!`, and `guns! guns! guns!`.
- Trigger an incoming missile and confirm the player defensive call does not spam.
- Eject and confirm `mayday! mayday! ejecting!` interrupts other chatter immediately.
- Destroy the player aircraft without ejection and confirm active/queued chatter is
  interrupted by the AWACS down call.
- Let a tower clearance/handoff finish and confirm the player gives a readback; let an
  AWACS/wingman line finish and confirm a short varied acknowledgement follows after the
  non-player radio queue drains.
- Kill an aircraft and confirm one splash call.
- Return to base, fly inbound for a sustained period, and confirm approach call timing.
- Return to base from more than 18 km out and confirm the RTB vector call after sustained
  inbound flight.
- Drop below bingo fuel and confirm one AWACS RTB fuel advisory.
- Wait for the game's `Cleared to land runway ...` UI message and confirm the tower reads the
  runway from it.
- Land successfully and confirm welcome-home call.
- Trigger multiple wingman messages and confirm they do not overlap with each other.

</details>

## Known limitations

- The mod is not intended for multiplayer clients.
- Pocket TTS latency depends on machine speed and whether the line is already cached.
- Game API changes may require updating `GameAdapter` and `Patches`.

## Contributing

Issues and pull requests are welcome. Bug reports are most useful with the BepInEx log
(`BepInEx/LogOutput.log`) and, for callout problems, the exact in-game situation. If you touch
the code, please follow the [conventions](#conventions) above — in particular, keep all game
API access behind `GameAdapter`/`Patches`.

## Credits

- [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/) by Shockfront
  Studios — this is an unofficial fan-made mod, not affiliated with or endorsed by the
  developers.
- [Pocket TTS](https://github.com/kyutai-labs/pocket-tts) by Kyutai Labs powers the voices.
- [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony)
  make the modding possible.
