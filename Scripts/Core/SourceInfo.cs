namespace Singulario.Core;

// A source is a simple fixed pool, not a regenerating/concentration-based
// resource: every capture subtracts a flat amount from Remaining, and the
// source disappears entirely once Remaining reaches 0 (see GridCell.Source
// being set back to null in Simulation's capture step).
public class SourceInfo
{
    public string Color; // energy color: "yellow" or "blue"
    public float Remaining;
    public float Total;
}
