using System.Collections.Generic;

namespace Singulario.Core;

// A star is a dual-role map object: an energy SINK (absorbs recipe-relevant
// colors without ever touching SingularityState.TotalReceivedEver) and an
// item SOURCE (dispenses crafted nucleus-items to any adjacent nucleus hole,
// via the same capture principle as a plain energy source). It can span
// Diameter x Diameter cells (Row/Col is the top-left anchor).
public class Star
{
	public int Id;
	public int Row;
	public int Col;
	public int Diameter;

	// Star color -- currently only "blue" has a recipe (see
	// GameConfig.StarRecipes), but the field is a plain string so more
	// colors can be added later without touching the engine.
	public string Color;

	// Named per-color fields (e.g. {"yellow":8, "blue":4}), NOT one combined
	// number -- required amounts to craft one item.
	public Dictionary<string, float> Recipe;

	// Accumulated progress per recipe color. Crafting is repeatable: as soon
	// as every field in Progress >= the matching Recipe amount, one item is
	// crafted and the recipe amounts are subtracted (leftover progress
	// carries over to the next craft, it never resets to exactly 0).
	public Dictionary<string, float> Progress = new();

	// How many crafted items are currently waiting near the star, ready to
	// be picked up by a real nucleus. Purely a counter -- the visual "orbit"
	// of ready items is a rendering detail (see StarView), not simulation
	// state.
	public int ReadyCount;

	// Shared cooldown mechanism with energy capture -- see Nucleus's own
	// LastCaptureTick for the field that's actually checked (pickup uses the
	// PICKING-UP nucleus's cooldown, not the star's; this field on the star
	// itself is currently unused by Simulation but kept for parity/future
	// per-star throttling).
	public int? LastCaptureTick;
}
