namespace Singulario.Core;

// Per-color accumulator inside the singularity (see SingularityState).
// Pool never decreases in "free" mode (GameConfig.SingularitySpends ==
// false, the default) -- StarsAwarded then just counts how many rungs of an
// ever-rising threshold have been crossed. In "spends" mode Pool actually
// decreases by the threshold each time a milestone fires.
public class ColorPoolState
{
    public float Pool;
    public int StarsAwarded;
}
