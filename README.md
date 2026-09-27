# Singulario

A Godot 4.7 (.NET/C#) port of the "atom factory" HTML prototype's core
mechanics: nuclei with rotating 8-slot rings that capture energy from
sources, transfer it hand-to-hand between neighbors, and deliver it to a
singularity (or a black hole), plus stars that consume energy by recipe and
dispense crafted "nucleus" items.

## What's implemented

- **Core simulation** (`Scripts/Core/`), plain C# with zero Godot
  dependencies: `GameWorld` (grid + registries + placement API),
  `Nucleus`/`RingSlot`/`Star`/`GridCell`/`SingularityState`/`GameConfig`,
  and `Simulation` (the tick: rotate -> capture -> transfer -> deliver).
- **Rendering** (`Scripts/Views/`): `GridView` (terrain + grid lines),
  `NucleusView` (core, direction dot, 8 ring slots -- holes, energy,
  crafted items), `StarView` (body + a shuffled "ready items" orbit).
  All flat-shape `_Draw()` calls, no sprites/textures.
- **Orchestration** (`Scripts/Main.cs`): tick loop driven from `_Process`
  (not `_PhysicsProcess`), Play/Pause/Step controls, an editable tick
  interval, left-click to place the selected tool (or flip an existing
  nucleus's direction) and right-click to erase, and a toolbar/config panel
  built entirely in code (tool buttons, tick-speed/cooldown/slot-count/
  star-diameter/per-tier-speed spin boxes, cooldown and "singularity
  spends" checkboxes, a status label).
- Nucleus speed tiers (Blue/Red/Green -- ticks-per-rotation, independent of
  energy color), orthogonal-only (N/E/S/W) capture, transfer and delivery,
  the `RingSlot.Locked` anti-oscillation rule, multi-cell stars with
  repeatable crafting and carried-over recipe progress, and the two
  deliberately separate counters (`ColorMilestoneCounts` vs.
  `CraftedNucleusInventory`) from the porting spec.

## What's deliberately NOT ported

Per the porting spec, none of these exist anywhere in this codebase, not
even as disabled options:

- total-charge comparison gate on transfer (`requireLowerTotal`)
- per-color comparison gate on transfer (`requireLowerColorTotal`)
- 45-degree same-ring diffusion (`intraRingDiffusion`)
- diagonal capture/transfer (`diagonalTransfer`) -- capture, transfer and
  the orthogonality check are hardcoded to N/E/S/W only

Without the two removed comparison gates, closed loops of nuclei *can*
circulate charge indefinitely without ever reaching the singularity --
that's an accepted simplification, not a bug.

## What's deferred (lower priority, per the spec's own list)

- The animated atom-symbol icon (glide + counter-rotation) for crafted
  items -- `NucleusView` currently draws a static flat "atom glyph"
  instead.
- Hole opacity/visual polish beyond a flat 0.5-alpha outline.
- Import/export of game state (the JSON save/load the HTML prototype had).
- Camera pan/zoom -- mouse-to-grid mapping currently assumes `Main` sits
  at the scene root with no camera transform.

## Opening the project

1. Open Godot 4.7 (the .NET/C# edition) and import this folder.
2. `Singulario.csproj` targets `Godot.NET.Sdk/4.7.0` and `net8.0`. If your
   installed Godot is a different 4.7.x patch version and the build
   complains about the SDK version, bump the `Sdk="Godot.NET.Sdk/4.7.0"`
   line in `Singulario.csproj` to match (Build > "Build Project" inside the
   editor, or `dotnet build`, will report the mismatch clearly if so).
3. Run the project (F5). The main scene is `Scenes/Main.tscn`.

## A note on how this was built

This project was written directly into this folder from a session with no
local Godot editor or `dotnet` CLI available to compile against -- every
file was hand-written and manually re-read for C# and Godot-API
correctness rather than verified with a real build. It's worth doing a
first build in the actual editor and treating any compiler errors it turns
up as expected teething trouble, not a sign something is deeply wrong.
