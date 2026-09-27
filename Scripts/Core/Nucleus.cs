namespace Singulario.Core;

public class Nucleus
{
	public int Id;
	public int Row;
	public int Col;

	// Speed tier -- see NucleusTier.cs. Determines how many ticks pass
	// between one 45-degree ring rotation and the next, via
	// GameWorld.Config.TierTicks[Tier].
	public NucleusTier Tier;

	// +1 or -1 -- toggled by left-clicking an already-placed nucleus.
	public int Dir = 1;

	// 8 ring positions, index 0..7 = N, NE, E, SE, S, SW, W, NW (45 deg
	// steps clockwise from north). See Simulation.Adj8 for the matching
	// offsets. A null entry means "no slot" -- see RingSlot.cs.
	public RingSlot[] Ring = new RingSlot[8];

	public int SlotCount;

	// Null means "never captured yet" (always allowed). Shared by BOTH
	// energy capture from a source AND item pickup from a star -- they
	// draw on the same per-nucleus cooldown, not two separate ones.
	public int? LastCaptureTick;
}
