# IL2Plugin Modifications for YawVR GameLink

Modified versions of the **IL-2 Sturmovik** plugin for **YawVR GameLink**, based on the original source from the public Yaw-VR GameLink plugin repository.

These files are intended as drop-in replacements for the upstream `IL2Plugin.cs` source file in the original project:

- Upstream project: `Yaw-VR/GameLink-Plugins`
- Original plugin: `IL2Plugin`

## What this repo contains

This repo contains modified versions of the IL-2 plugin source file only, rather than a full copy of the entire GameLink plugin solution.

Included versions:

- `IL2Plugin_v1.3.cs`
- `IL2Plugin_v1.4_dualstream.cs`

These are custom experimental builds intended to improve motion cueing and add extra telemetry-driven effects for IL-2.

## Why only one file is included

Only the IL-2 plugin source file was modified. All other files, dependencies, and projects come from the original Yaw-VR GameLink plugin source.

That means you can:

1. Download or clone the original upstream repository
2. Replace the upstream `IL2Plugin.cs` with one of the files from this repo
3. Build the `IL2Plugin` project in Visual Studio
4. Copy the resulting DLL into your YawVR GameLink installation

## Versions

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

## Recommended GameLink mappings

For the motion channels:

- **YAW** → `Yaw_Integrated`
- **PITCH** → `Pitch_Soft`
- **ROLL** → `Roll_Processed`

For vibration and haptic experimentation in v1.4, useful starting inputs include:

- `Eng_Shake_Amp`
- `Cockpit_Shake_Amp`
- `Gun_Fire_Light`
- `Gun_Fire_Heavy`
- `Damage_Shock`
- `Explosion_Shock`

## Build instructions

### 1. Get the upstream project
Download or clone the original Yaw-VR GameLink plugins repository.

### 2. Replace the source file
Replace the upstream `IL2Plugin/IL2Plugin.cs` file with the version you want to build from this repo.

### 3. Open in Visual Studio
Open the upstream solution in **Visual Studio 2022**.

### 4. Restore packages
Make sure NuGet packages restore successfully.

### 5. Build the correct projects
Build:

- `YawGLAPI`
- `IL2Plugin`

You do not need to build every other plugin in the solution.

### 6. Find the built DLL
The compiled DLL should appear in a path similar to:

```text
IL2Plugin\bin\Release\net8.0\IL2Plugin.dll
