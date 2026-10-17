# RepairKit — Vehicle Repair Mod for GHPC

Press a key to put your vehicle into a repair state. After a set duration, damaged
components are restored to a configurable health (70% by default) while **all
functionality is fully restored** — engine, transmission, turret, fire control,
weapons, optics and ammo feed.

Appearance damage, crew casualties and ammo count are not repaired.

## Requirements

- *Gunner, HEAT, PC!*
- **MelonLoader

## Installation

1. Copy `RepairKit.dll` into:

   ```
   <Game Folder>\Bin\Mods\RepairKit.dll
   ```

2. Launch the game. MelonLoader loads the mod automatically and prints
   `RepairKit 1.0.0` to the console.
3. On first run a `[RepairKit]` section is created in `UserData/MelonPreferences.cfg`.

## Configuration

| Key | Default | Description |
|---|---|---|
| `RepairKey` | `J` | Key used to start/cancel repair. Any Unity `KeyCode` name, e.g. `J`, `K`, `F5`, `Keypad0`. |
| `RepairDuration` | `30` | Repair duration in seconds. Minimum 1. |
| `RepairHealthPercent` | `70` | Component health after repair, 1–100. Components already above this value are not lowered. |
| `ShowHud` | `true` | Show the repair progress bar and result messages. |

Settings are read each time the key is pressed, so edits to the config file take
effect **without restarting the game** (the remaining timer of an in-progress
repair is not changed).

## Behavior

- **Start**: bring the vehicle to a stop, then press `RepairKey` while inside it. A
  progress bar appears at the bottom of the screen; repair completes automatically
  when the timer ends.
- **Complete**: every damaged component is raised to `RepairHealthPercent`, then all
  "damaged" state flags are cleared and the affected systems are re-enabled.
- **Cancel**: pressing the same key again during repair aborts it immediately with
  **no repair effect**; the vehicle's previous state (parking brake, broken tracks,
  etc.) is restored exactly.
- **Damage during repair**: if any non-crew component of the vehicle loses health
  during the repair, the repair is aborted immediately with no effect.
- **No repair limit, no cooldown**: repair as often as you like.
- **Cannot repair when**:
  - the vehicle is considered destroyed,
  - the vehicle has been abandoned,
  - the entire crew is incapacitated,
  - the vehicle is still moving (horizontal speed above ~0.5 m/s / 1.8 km/h),
  - you are not in a vehicle.

While repairing:

- the vehicle cannot move (driving input is blocked, the parking brake is applied,
  cruise control is disabled);
- the vehicle cannot fire (main gun, missiles and the generic weapon system are all
  blocked);
- the turret can still be traversed normally.

## Source layout

| File | Responsibility |
|---|---|
| `Main.cs` | MelonMod entry point, preferences, scene callbacks. |
| `RepairSession.cs` | State machine: input, timer, movement lock, damage detection, cancel/restore, HUD. |
| `RepairApplier.cs` | The actual repair: health, chassis/transmission/turret/fire control/weapons/optics/ammo feed flags, engine audio. |
| `Patches.cs` | Harmony patches: fire blocking, plus capturing turret/transmission baseline values after `Awake`. |

### Implementation notes

1. **Health must be written before damage flags are cleared.** `SetHealthPercent`
   fires `HealthChanged`, which makes `ChassisDamageManager` set `_engineDamaged`
   and friends back to `true`.
2. **`DrivableChassis` holds the persistent damage state.** Clearing only
   `NwhChassis` flags is not enough: `SetEngineOn(true)` returns early while
   `_disabledEngine`/`_killedEngine` is set, and the vehicle would stay immobile.
   Likewise `SlewableTurret.KillPower` overwrites the traverse rates with "no power"
   values and `Disable` sets the turret rigidbody drag to 0.8, so both are captured
   after `Awake` and restored on repair.
3. **Engine audio is recreated explicitly.** Destroying the engine fires
   `NwhChassis.EngineStopped`; `GHPC.Audio.VehicleAudioController` subscribes its
   `StopEngine` to that event, which destroys the FMOD engine instance. Nothing in
   the game recreates it outside `OnEnable`, so the repair calls `StartEngine()`
   again when the instance is missing.

## Known limitations

- Appearance is not repaired: hull dents, textures, a popped turret and ammo rack
  detonation are permanent consequences.
- Crew members are not revived and ammo is not replenished (only the feed mechanism
  itself is repaired).
- `_loaderDamaged` (slower reload caused by an injured loader) is a crew injury and
  is intentionally left alone.
- Only the vehicle the player is currently in is repaired; friendly AI vehicles are
  not auto-repaired.

## Building

Requires the .NET SDK and a local game installation:

```bash
dotnet build -c Release
```

The game path defaults to `E:\SteamLibrary\steamapps\common\Gunner HEAT PC` and can
be overridden with `/p:GHPC_DIR="..."`. The project uses
`BepInEx.AssemblyPublicizer` to expose private members of `Assembly-CSharp` for
direct access to game internals.
