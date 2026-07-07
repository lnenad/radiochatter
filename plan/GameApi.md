# Nuclear Option — Verified Game API Surface (Phase 1)

Sources: reflection dump of `Assembly-CSharp.dll` (game install, 2026-07-06, 1937 types) cross-checked
against 7 open-source mods (no-autopilot-mod, TargetCamControl, VehicleControl, NuclearMods, MKModsNO,
ActualStealth, My-NO-Mods). Game engine: Unity 2022.3 (Mono), networking: Mirage, input: Rewired.

Legend: ✅ verified in assembly dump • 🧩 verified in mod source (compiles against the game) • ⚠️ inferred, verify in-game.

---

## D1 — Local player aircraft & flight state

| What | How | Status |
|---|---|---|
| Local aircraft | `GameManager.GetLocalAircraft(out Aircraft a)` (static) | ✅ |
| Local player | `GameManager.GetLocalPlayer<Player>(out p)`; `GameManager.GetLocalHQ(out FactionHQ)`; `GameManager.GetLocalFaction(out Faction)` | ✅ |
| Aircraft-changed hook | Harmony postfix `FlightHud.SetAircraft(Aircraft)` / `CombatHUD.SetAircraft(Aircraft)` | ✅🧩 |
| Position (absolute) | `GlobalPositionExtensions.GlobalPosition(unit)` → `GlobalPosition {x,y,z}` (floating origin — never store raw `transform.position` across frames) | ✅🧩 |
| Altitude MSL | `unit.GlobalPosition().y` | ✅🧩 |
| Altitude AGL | `Unit.radarAlt` (public float) | ✅🧩 |
| Velocity | `Aircraft.rb.velocity` (`Unit.rb` Rigidbody property) | ✅🧩 |
| Speed (TAS m/s) | `Unit.speed` (public float) | ✅ |
| Heading | `aircraft.transform.eulerAngles.y`; course from velocity via `Vector3.ProjectOnPlane(v, up)` | 🧩 |
| Fuel | `Aircraft.GetFuelLevel()` → 0..1 fraction; `Aircraft.fuelLevel` public field | ✅🧩 |
| Ejected | `Aircraft.HasEjected()` | ✅ |

`GlobalPosition` is a struct `{float x,y,z}` with `AsVector3()`. Distances: `FastMath.Distance(GlobalPosition, GlobalPosition)` (✅).

## D2 — Unit registry

- `UnitRegistry.allUnits` — `static List<Unit>` of every active unit. ✅
- Per unit: `unitName` (string), `definition` (`UnitDefinition`: `unitName` display, `bogeyName`, `code`, `typeIdentity`, `roleIdentity`), `disabled` (public bool = dead/knocked out), `unitState` (enum `Active, Damaged, Abandoned, Destroyed, Returned`), `HQ` via property `NetworkHQ` → `FactionHQ`, `MapHQ` field. ✅
- Faction test: reference-compare `FactionHQ` (or `hq.faction` `Faction` SO). 🧩
- `Aircraft : Unit`, `Missile : Unit`, plus `Ship`, `GroundVehicle`, `Building`. ✅
- Airborne (aircraft): private field `Aircraft.airborne` (reflection) or derive from gear/AGL (see D9). ✅

## D3 — Detection / datalink (per-faction known contacts)

- **`FactionHQ.trackingDatabase`** — public `Dictionary<PersistentID, TrackingInfo>` (key type ⚠️ — access as non-generic `IDictionary` to be safe). ✅
- `TrackingInfo` (public): `lastKnownPosition` (GlobalPosition), `lastSpottedTime` (float), `id` (PersistentID), `GetUnit()`, `TryGetUnit(out Unit)`, `Observed()` (bool = currently spotted). ✅
- New-contact / lost-contact hooks exist (`FactionHQ.onDiscoverUnit` / `onForgetUnit`, private `Action<PersistentID>`) — we poll + diff the database instead (robust, no private-delegate hacking). ✅
- AWACS must only announce contacts present in the local faction's `trackingDatabase`. No omniscience.

## D4 — Player's selected target

- `Aircraft.weaponManager` → `WeaponManager.GetTargetList()` → `List<Unit>` (last element = most recent selection). ✅🧩
- HUD equivalent: `CombatHUD.GetTargetList()`; `SceneSingleton<CombatHUD>.i.aircraft`. ✅🧩

## D5 — Unit destroyed / kill attribution

- **Any unit died:** poll `UnitRegistry.allUnits` for `disabled` false→true transitions (also catches despawn removal). ✅
  Patch candidate: `Unit.ReportKilled()` — **no attacker parameter**; `Unit.UnitDisabled(bool,bool)` syncvar hook. ✅
- **Player kill credit (local):** Harmony postfix **`KillDisplay.DisplayKill(PersistentUnit killedUnit, float creditGiven, RewardType actionType)`** — fires on local player's kill confirmations. `PersistentUnit`: `unit`, `id`, `unitName`, `player`, `definition`, `GetFaction()`. ✅
- **Host-side any-player credit:** `FactionHQ.RewardPlayer(Player, Unit target, float, float, RewardType)` postfix. ✅
- `KillType` enum: `Aircraft, Vehicle, Building, Missile, Ship`. ✅

## D6 — Missiles / RWR

- Missile threat to player: **`MissileWarning`** component on player aircraft (`Aircraft.GetMissileWarningSystem()`):
  `knownMissiles` (public `List<Missile>`), `IsWarning()`, `TryGetNearestIncoming(out Missile)`. ✅
- Event hook: patch `Aircraft.LockedByMissile(Missile)` or `ThreatList.ThreatList_OnMissileWarning(MissileWarning.OnMissileWarning e)` (`e.missile`). ✅🧩
- `Missile : Unit` — `target` (private Unit; property `targetID` → `PersistentID.TryGetUnit`), `owner`, `seekerMode` (`activeLock, activeSearch, passive`), `GetSeekerType()` (string). ✅
- RWR spikes (radar lock, pre-launch): `Aircraft.onRadarWarning` private `Action<OnRadarWarning>` — struct `{Radar radar, Unit emitter, float power, bool detected, bool isTarget}`; patch `Aircraft.RpcGetRadarWarning(Unit radarSource)` instead if needed. ✅

## D7 — Weapon release by player

- Patch `WeaponStation.LaunchMount(Unit owner, Unit target, GlobalPosition aimpoint)` (per-launch, has owner+target). ✅🧩
- `WeaponManager.Fire()` = trigger press for current station. ✅🧩 (v1: low priority, config-gated off)

## D8 — Airbases & runways

- Friendly bases: `FactionHQ.GetAirbases()` (iterator) or filter `MissionManager.GetAllAirbase()` (static `Dictionary`). ✅🧩
- `Airbase`: `CurrentHQ` (property → FactionHQ), `runways` (`Runway[]`), `center` (Transform), `GetRadius()`, `disabled`, `networkUniqueName`. ✅
- `Airbase.Runway`: `Start`/`End` (Transforms), `GetDirection(bool reverse)` (Vector3), `GetName(bool reverse)`, `GetDistance(Transform)` → `{Distance, Reverse}`, `GetAngle(Transform)` → `{Angle, Reverse}`, `AircraftOnRunway(Aircraft)`, `AircraftOnApproach(Aircraft, range, bool)`, `ClearForTakeoff(Aircraft, bool)`, `occupied`, `airbase` (back-ref), `Length` property. ✅ — runway heading = `Quaternion.LookRotation(GetDirection(false)).eulerAngles.y`.
- Carriers/destroyers are also `Airbase` objects (name contains "Carrier"/"Destroyer"). 🧩

## D9 — Gear & ground state

- `Aircraft.gearDeployed` (public bool syncvar), `Aircraft.gearState` (public `LandingGear.GearState`: `Uninitialized, LockedRetracted, LockedExtended, Retracting, Extending`). ✅
- Grounded heuristic (no-autopilot pattern): `gearState != LockedRetracted && radarAlt < ~1 m` (they use 0.1; we use small threshold + debounce). 🧩
- True WoW available per-gear: `LandingGear.WeightOnWheel(float threshold)` (component under aircraft). ✅
- Landed-successfully event: postfix `Aircraft.SuccessfulSortie(float score)` (private). ✅🧩

## D10 — Game mode / host / pause

- `GameManager.gameState` (static property) — enum `GameState`: `Uninitialized, SinglePlayer, Multiplayer, Editor, Encyclopedia, Menu, ServerWaiting`. ✅
- In mission: `MissionManager.IsRunning` (static bool property) + `gameState` is `SinglePlayer`/`Multiplayer`/`Editor`; scene name == "GameWorld". ✅🧩
- **MP-client detection** (disable mod): `gameState == Multiplayer` and local `Player.IsHostPlayer == false`. Cross-check via Mirage `NetworkClient.Active && !IsHost` (mod pattern). ✅🧩
- Pause: `Time.timeScale == 0` locally; `NuclearOption.Networking.NetworkPause` (`serverPaused`/`localPaused` private). Audio pause: `AudioSource.ignoreListenerPause = false` handles it. ✅
- Mission identity: `MissionManager.CurrentMission` (static). ✅🧩

## D11 — Units setting

- `PlayerSettings.unitSystem` (static field) vs enum `PlayerSettings.UnitSystem { Metric, Imperial }`. ✅🧩
- Game formatters (already unit-aware): `UnitConverter.AltitudeReading/DistanceReading/SpeedReading(float)`. ✅

## D12 — Mission clock

- `MissionManager.MissionTime` (static-instance property, float seconds); fallback `Time.timeSinceLevelLoad`. ✅

## Misc verified extras

- Singletons: `SceneSingleton<T>.i` (e.g. `CombatHUD`, `FlightHud`, `KillDisplay`, `AircraftActionsReport`, `DynamicMap`).
- On-screen text: `SceneSingleton<AircraftActionsReport>.i.ReportText(string, float seconds)`; `MissionMessages.ShowMessage(string, bool sound, FactionHQ faction, bool sendToClients)` (static).
- Player: `NuclearOption.Networking.Player` — `PlayerName`, `HQ`, `Aircraft` (property), `IsHostPlayer`, `PlayerRef`.
- Aircraft display names: `UnitDefinition.unitName` is already the display name ("FS-20B Vortex" style); `bogeyName` for unknown-contact phrasing. Internal prefab names: SmallFighter1=Vortex, Multirole1=Ifrit, EW1=Medusa, Darkreach.
- Speed of sound: `LevelInfo.GetSpeedOfSound(altitude)`; wind: `LevelInfo.GetWind()`.
- The game ships `WindowsTTS` (SAPI) for chat TTS — unrelated to our Piper sidecar, but proves audio-over-game works.
- BepInEx: game confirmed working with 5.4.23.x; `HideGameManagerObject = true` required.

## Harmony patch surface chosen for RadioChatter (thin, events only)

| Event | Patch | Fallback |
|---|---|---|
| Player got kill credit | postfix `KillDisplay.DisplayKill` | poll `disabled` transitions near player's target |
| Missile launched at player | postfix `Aircraft.LockedByMissile(Missile)` | poll `MissileWarning.knownMissiles` |
| Player aircraft changed | postfix `FlightHud.SetAircraft` | poll `GameManager.GetLocalAircraft` |
| Landed successfully | postfix `Aircraft.SuccessfulSortie` | grounded-transition detector |

Everything else (takeoff, landing, approach, contacts, RTB) is polled state-machine detection per plan §5.

## Version-tolerance notes

All access above goes through `GameAdapter` only. On game update, re-run
`scratchpad/dump-api.ps1` (kept in repo under `plan/tools/`) and re-verify this table.
Reflection-based reads (private `Aircraft.airborne`, `Missile.target`) fail soft.
