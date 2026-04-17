# IL2Plugin Modifications for YawVR GameLink

Modified versions of the **IL-2 Sturmovik** plugin for **YawVR GameLink**, based on the public **Yaw-VR/GameLink-Plugins** project.

These files are intended as drop-in replacements for the upstream `IL2Plugin.cs` source file inside the original Yaw-VR solution.

## Upstream project

- Original project: `Yaw-VR/GameLink-Plugins`
- Original plugin: `IL2Plugin`

## What this repo contains

This repo contains **modified IL-2 plugin source files only**, rather than a full copy of the entire GameLink plugin solution.

Included versions may include:

- `IL2Plugin_v1.3.cs`
- `IL2Plugin_v1.4_dualstream.cs`
- `IL2Plugin_v1.5_forcecue.cs`
- `IL2Plugin_v1.6_bestofboth.cs`

These are experimental custom builds intended to improve motion cueing, add telemetry-driven haptics, and make the IL-2 experience more usable on a limited-travel YawVR platform.

## Why only one file is included

Only the IL-2 plugin source file was modified. All other files, dependencies, and projects come from the original Yaw-VR GameLink source.

That means you can:

1. Download or clone the original upstream repository
2. Replace the upstream `IL2Plugin/IL2Plugin.cs` file with one of the files from this repo
3. Build the `IL2Plugin` project in Visual Studio
4. Copy the resulting DLL into your YawVR GameLink installation

## Version summary

## v1.3

Compared to the vanilla YawVR IL-2 plugin, v1.3 adds:

- packet validation
- pause-aware behavior
- reduced pause/unpause jolts
- soft-limited pitch and roll cueing
- integrated yaw processing to avoid yaw wrap issues
- improved return-to-center behavior on exit or stream timeout

This version focuses on improving the core motion experience while still using the standard IL-2 motion output stream.

## v1.4

v1.4 includes everything from v1.3, and also adds support for IL-2's telemetry stream on UDP 4322.

Compared to the vanilla plugin, v1.4 adds:

- dual-stream support:
  - motion stream on UDP 4321
  - telemetry stream on UDP 4322
- telemetry-driven GameLink inputs for:
  - engine shake
  - cockpit shake
  - telemetry acceleration
  - airspeed
  - angle of attack
  - flaps
  - air brakes
  - landing gear pressure
- processed event-envelope inputs for:
  - gunfire
  - hits
  - damage
  - explosions
  - bomb drops
  - rocket launches

Compared to v1.3, v1.4 expands the plugin from motion-only improvements into a motion-plus-haptics plugin.

## v1.5

v1.5 builds on the v1.4 dual-stream plugin by moving further toward **force-based cueing** rather than simple aircraft-orientation mapping.

Compared to v1.4, v1.5 adds new derived motion channels intended to better represent what the pilot feels:

- `Roll_Force`
- `Pitch_Force`
- `Surge_Force`
- `Heave_Bump`
- `Inverted_Cue`
- `Buffet_Amp`
- `Ground_Rumble`

This version is inspired by the idea that a motion rig should simulate **apparent forces** rather than simply mirror the aircraft’s pitch, roll, and heading.

## v1.6

v1.6 is a best-of-both-worlds merge that combines the processed motion and force-cueing direction of the custom branch with practical telemetry and setup ideas inspired by **Erdős Zoltán’s** IL-2 plugin work.

Compared to v1.4, v1.6 adds:

- a cleaner and more usable naming scheme for GameLink’s alphabetical dropdowns:
  - `MOT_*` for motion cues
  - `FX_*` for continuous vibration / texture
  - `EVT_*` for transient event effects
  - `MOD_*` for useful modulators
- reduced input clutter by culling less useful raw channels
- a more curated set of motion, vibration, and event-driven inputs
- directional hit-response cues such as:
  - `EVT_HitYaw`
  - `EVT_HitPitch`
  - `EVT_HitRoll`
- a more practical “full motion + haptics” plugin structure
- a default profile direction based on processed cues rather than raw orientation

Special thanks to **Erdős Zoltán** for prior IL-2 plugin work that helped inform the v1.6 release, especially around:
- dual-stream motion + telemetry handling
- practical `startup.cfg` patching for IL-2
- exposing useful telemetry and event data structures
- groundwork around directional hit and damage telemetry handling

## Build instructions

### 1. Get the upstream project
Download or clone the original Yaw-VR GameLink plugins repository.

### 2. Replace the source file
Replace the upstream `IL2Plugin/IL2Plugin.cs` file with the version you want to build from this repo.

### 3. Open in Visual Studio
Open the upstream solution in **Visual Studio 2022**.

### 4. Restore packages
Make sure NuGet packages restore successfully. If needed, add the NuGet source:

```text
https://api.nuget.org/v3/index.json
```

### 5. Build the correct projects
Build:

- `YawGLAPI`
- `IL2Plugin`

You do not need to build every other plugin in the solution.

### 6. Find the built DLL
The compiled DLL should appear in a path similar to:

```text
IL2Plugin\bin\Release\net8.0\IL2Plugin.dll
```

## Installation

Before replacing anything, back up the original DLL.

Typical install location:

```text
C:\Program Files (x86)\Steam\steamapps\common\YawVR GameLink\Gameplugins\IL2Plugin.dll
```

Recommended backup naming:

- `IL2Plugin_stock.dll`
- `IL2Plugin_v1.3.dll`
- `IL2Plugin_v1.4.dll`
- `IL2Plugin_v1.5.dll`
- `IL2Plugin_v1.6.dll`

Then copy the newly built DLL into the Gameplugins folder and restart YawVR GameLink.

## IL-2 configuration

### Motion output
Enable IL-2 motion output on UDP **4321**.

### Telemetry output
For v1.4 and later, also enable IL-2 telemetry output on UDP **4322**.

Both are configured in IL-2's `startup.cfg`.

Recommended starting settings:

```ini
[KEY = motiondevice]
addr = "127.0.0.1"
decimation = 1
enable = true
port = 4321
[END]

[KEY = telemetrydevice]
addr = "127.0.0.1"
decimation = 1
enable = true
port = 4322
[END]
```

## Recommended GameLink profile setup

## v1.6 naming convention

The v1.6 branch is designed to be easier to work with inside GameLink’s alphabetical input dropdown.

- `MOT_*` = motion-driving cues
- `FX_*` = continuous vibration / texture sources
- `EVT_*` = transient event effects
- `MOD_*` = useful modulators for future profile logic

## Recommended core motion mapping

For a strong first-pass v1.6 profile:

- **YAW** → `MOT_YawIntegrated`
- **PITCH** → `MOT_PitchForce`
- **ROLL** → `MOT_RollForce`

These are the three most important rows to start with.

## Suggested full-suite profile approach

Set up the rows below, but enable and test them gradually.

### Core motion rows

```text
MOT_YawIntegrated  -> YAW    mult 1.00   smooth 0.05
MOT_PitchForce     -> PITCH  mult 0.75   smooth 0.08
MOT_RollForce      -> ROLL   mult 0.75   smooth 0.08
```

### Motion support rows

```text
MOT_PitchSoft      -> PITCH  mult 0.20   smooth 0.10
MOT_SurgeForce     -> PITCH  mult 0.25   smooth 0.04
MOT_HeaveBump      -> PITCH  mult 0.30   smooth 0.02
MOT_InvertedCue    -> PITCH  mult 0.35   smooth 0.10

MOT_RollProcessed  -> ROLL   mult 0.25   smooth 0.08
MOT_YawRate        -> YAW    mult 0.10   smooth 0.03
```

### Directional hit-motion rows

```text
EVT_HitYaw         -> YAW    mult 0.15   smooth 0.01
EVT_HitPitch       -> PITCH  mult 0.15   smooth 0.01
EVT_HitRoll        -> ROLL   mult 0.15   smooth 0.01
```

### Continuous vibration rows

```text
FX_EngShakeFreq        -> VIB_HZ   mult 1.00   smooth 0.10
FX_EngShakeAmp         -> VIB_AMP  mult 0.30   smooth 0.08
FX_CockpitShakeAmp     -> VIB_AMP  mult 0.20   smooth 0.05
FX_BuffetAmp           -> VIB_AMP  mult 0.25   smooth 0.04
FX_GroundRumble        -> VIB_AMP  mult 0.30   smooth 0.03
FX_GearPress           -> VIB_AMP  mult 0.15   smooth 0.04
```

### Event vibration rows

```text
EVT_GunLight           -> VIB_AMP  mult 0.18   smooth 0.01
EVT_GunHeavy           -> VIB_AMP  mult 0.35   smooth 0.01

EVT_HitShock           -> VIB_AMP  mult 0.25   smooth 0.01
EVT_DamageShock        -> VIB_AMP  mult 0.35   smooth 0.01
EVT_ExplosionShock     -> VIB_AMP  mult 0.55   smooth 0.01

EVT_BombKick           -> VIB_AMP  mult 0.35   smooth 0.01
EVT_RocketKick         -> VIB_AMP  mult 0.30   smooth 0.01
```

## Recommended testing order

Do not enable everything at once.

Test in this order:

1. `MOT_YawIntegrated`
2. `MOT_PitchForce`
3. `MOT_RollForce`
4. `MOT_PitchSoft`
5. `MOT_RollProcessed`
6. `MOT_SurgeForce`
7. `MOT_HeaveBump`
8. `MOT_InvertedCue`
9. vibration rows
10. weapon / damage event rows
11. directional hit-motion rows

This makes it much easier to identify which row is helping and which row is causing a bad sensation.

## Important GameLink note

In practice, GameLink often keeps previously saved profiles rather than regenerating the built-in plugin default profile every time a DLL changes.

That means your **exported `.yawglprofile` files** are often the real source of truth for your setup.

It is a good idea to version your profiles separately, for example:

- `IL2_v1.6_profile_baseline.yawglprofile`
- `IL2_v1.6_profile_fullfx.yawglprofile`

## Notes

- These are custom experimental modifications and are not official YawVR releases.
- Later versions are more ambitious and may require more tuning than v1.3.
- Steam installs are easier to detect automatically, but standalone IL-2 installs may require the correct install path to be known by GameLink before any automatic patching can work.
- If a newer build proves troublesome, revert by restoring an earlier DLL backup or rebuilding from an earlier source file.

## Credits

Original GameLink plugin framework and IL-2 plugin source:
- Yaw-VR / GameLink-Plugins

Additional inspiration and useful prior IL-2 plugin work:
- Erdős Zoltán

## Disclaimer

Use at your own risk.

These builds are experimental and may behave differently depending on:

- GameLink profile setup
- YawVR simulator settings
- IL-2 motion / telemetry configuration
- motion compensation workflow
- headset / OpenXR / Virtual Desktop environment
