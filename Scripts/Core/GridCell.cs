namespace Singulario.Core;

// The terrain layer of one grid cell. A cell can be a source, a black hole,
// or the singularity -- at most one of these at a time. Stars are NOT
// stored here because a star can span multiple cells (see GameWorld.StarAt,
// a separate registry keyed by every cell the star's footprint covers).
public class GridCell
{
    public SourceInfo Source;
    public bool IsBlackhole;
    public bool IsSingularity;
}
