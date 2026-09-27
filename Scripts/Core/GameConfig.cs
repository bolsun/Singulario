using System.Collections.Generic;

namespace Singulario.Core;

// All simulation tunables in one place, matching the defaults from the
// porting spec's constants table. Every field here is meant to be editable
// at runtime from the UI (see Main.cs), not hardcoded.
public class GameConfig
{
    public float BaseYield = 1f;

    public float ColorUnlockCost = 12f;
    public float ColorRepeatCost = 4f;

    public float ExpansionBaseCost = 20f;
    public float ExpansionGrowth = 1.6f;
    public int ExpansionStep = 2;

    // The A/B toggle from the original design: false ("free"/cumulative) is
    // the default -- pools never decrease, thresholds keep climbing instead.
    public bool SingularitySpends = false;

    public bool CaptureCooldownEnabled = true;
    public int CaptureCooldownTicks = 8;

    public Dictionary<NucleusTier, int> TierTicks = new()
    {
        { NucleusTier.Blue, 8 },
        { NucleusTier.Red, 4 },
        { NucleusTier.Green, 1 },
    };

    // Craft recipe per star color -- see Star.cs. Only "blue" is defined for
    // now; add more entries here (and to Star placement in Main.cs) to
    // support other star colors later.
    public Dictionary<string, Dictionary<string, float>> StarRecipes = new()
    {
        { "blue", new Dictionary<string, float> { { "yellow", 8f }, { "blue", 4f } } },
    };

    public int MinFieldSize = 5;
    public int MaxFieldSize = 80;

    public int DefaultSourceTotal = 100;
}
