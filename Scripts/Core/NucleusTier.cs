namespace Singulario.Core;

// Nucleus "color" is a first-class speed-tier identity, NOT an energy color
// (see EnergyColor-style string constants used for source/charge colors like
// "yellow"/"blue" elsewhere) -- keeping these two concepts in separate types
// is deliberate, per the port spec's warning about the name collision.
public enum NucleusTier
{
    Blue,
    Red,
    Green
}

public static class NucleusTierExtensions
{
    // Used to parse the "nucleus:<tier>" item-color tag back into a tier
    // (see RingSlot.Color / Simulation's delivery step). Deliberately a
    // plain switch instead of Enum.Parse so an unexpected tag never throws.
    public static NucleusTier ParseTier(string s)
    {
        return s?.ToLowerInvariant() switch
        {
            "red" => NucleusTier.Red,
            "green" => NucleusTier.Green,
            _ => NucleusTier.Blue,
        };
    }

    public static string ToKey(this NucleusTier tier)
    {
        return tier switch
        {
            NucleusTier.Red => "red",
            NucleusTier.Green => "green",
            _ => "blue",
        };
    }
}
