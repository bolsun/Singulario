namespace Singulario.Core;

// One of a nucleus's 8 ring positions. `null` at a given index (in
// Nucleus.Ring) means "no slot at all" there -- a position never touched
// because slotCount < 8 -- which is different from a RingSlot with
// IsHole == true (an actual empty slot that can capture/receive).
//
// Color holds either a real energy color ("yellow"/"blue") or a crafted
// nucleus-item tag ("nucleus:blue", "nucleus:red", ...) when IsHole is
// false. These two kinds of "color" are intentionally never distinguished
// by type -- only by the "nucleus:" string prefix -- exactly mirroring the
// original prototype, so a nucleus-item can ride the same transfer/delivery
// code path as real energy with zero special-casing there.
public class RingSlot
{
    public bool IsHole;
    public string Color;
    public float Charge;

    // A slot that JUST received a charge this tick (from a source, a star,
    // or a neighboring nucleus) is locked until this nucleus's ring
    // physically rotates at least once. This is the ONLY anti-oscillation
    // rule in the port (see Simulation.Tick's transfer step) -- without it
    // a charge would bounce back and forth between two neighbors forever.
    public bool Locked;

    public RingSlot(bool isHole, string color = null, float charge = 0f, bool locked = false)
    {
        IsHole = isHole;
        Color = color;
        Charge = charge;
        Locked = locked;
    }
}
