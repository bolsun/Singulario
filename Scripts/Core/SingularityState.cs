using System.Collections.Generic;

namespace Singulario.Core;

public class SingularityState
{
    // The true lifetime total -- always visible, never decremented. A
    // delivered crafted nucleus-item does NOT add to this (see
    // Simulation.Tick's delivery step) -- only real energy does.
    public float TotalReceivedEver;

    public Dictionary<string, ColorPoolState> ColorPools = new();

    public float ExpansionPool;
    public int ExpansionsSoFar;

    // Energy only -- a nucleus-item lost to a black hole does not add here.
    public float LostToBlackHoles;
}
