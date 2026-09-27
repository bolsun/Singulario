using System;
using System.Collections.Generic;
using System.Linq;

namespace Singulario.Core;

// All simulation state, deliberately with zero Godot dependencies -- this
// is plain data plus the placement/removal operations on it. Simulation.cs
// contains the actual per-tick logic; the Views/ scripts read this state to
// render it. This split mirrors the original prototype's separation between
// its JS state objects (grid/nuclei/stars/singularity) and its DOM renderer.
public class GameWorld
{
	public int Rows;
	public int Cols;

	public GridCell[,] Grid;

	// Cell -> nucleus id. Exactly one nucleus per cell.
	public int?[,] EntityAt;

	// Cell -> star id, set on EVERY cell a star's footprint covers (not just
	// its top-left anchor) -- this is what lets a multi-cell star be found
	// from any of its cells in O(1).
	public int?[,] StarAt;

	public Dictionary<int, Nucleus> Nuclei = new();
	public Dictionary<int, Star> Stars = new();

	public (int Row, int Col)? SingularityPos;
	public SingularityState Singularity = new();

	// Old color-milestone reward counter (color -> how many "star" milestones
	// have fired for it). Deliberately named and typed distinctly from
	// CraftedNucleusInventory below -- these are two unrelated concepts that
	// happen to both involve the word "star" in the original prototype.
	public Dictionary<string, int> ColorMilestoneCounts = new();

	// The player's inventory of crafted nucleus-items actually delivered to
	// the singularity (tier key -> count). See Star.cs / Simulation's
	// delivery step.
	public Dictionary<string, int> CraftedNucleusInventory = new();

	public int TickCounter;

	public GameConfig Config;

	int _nextNucleusId = 1;
	int _nextStarId = 1;

	public GameWorld(int rows, int cols)
	{
		Config = new GameConfig();
		Rows = rows;
		Cols = cols;
		AllocateGrids();
	}

	void AllocateGrids()
	{
		Grid = new GridCell[Rows, Cols];
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				Grid[r, c] = new GridCell();
		EntityAt = new int?[Rows, Cols];
		StarAt = new int?[Rows, Cols];
	}

	public bool InBounds(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Cols;

	// A cell is free only if NONE of the layers (terrain, nucleus, star)
	// occupy it -- matches the prototype's cellIsEmpty exactly.
	public bool CellIsEmpty(int r, int c)
	{
		if (!InBounds(r, c)) return false;
		var cell = Grid[r, c];
		return !EntityAt[r, c].HasValue
			&& !StarAt[r, c].HasValue
			&& cell.Source == null
			&& !cell.IsBlackhole
			&& !cell.IsSingularity;
	}

	// Every cell of a Diameter x Diameter footprint anchored at (r,c) must
	// be free -- used before placing a star.
	public bool FootprintIsFree(int r, int c, int diameter)
	{
		for (int rr = r; rr < r + diameter; rr++)
			for (int cc = c; cc < c + diameter; cc++)
				if (!CellIsEmpty(rr, cc)) return false;
		return true;
	}

	// Evenly spaces `slotCount` holes around the 8-position ring:
	// idx = floor(k * 8 / slotCount) % 8 for k in [0, slotCount). Every
	// other index is left as "no slot" (null), never a hole.
	public RingSlot[] MakeRing(int slotCount)
	{
		var ring = new RingSlot[8];
		for (int k = 0; k < slotCount; k++)
		{
			int idx = (int)Math.Floor(k * 8.0 / slotCount) % 8;
			ring[idx] = new RingSlot(true);
		}
		return ring;
	}

	public int NucleusTicks(Nucleus n) =>
		Config.TierTicks.TryGetValue(n.Tier, out var t) ? t : 8;

	public int AddNucleus(int r, int c, NucleusTier tier, int slotCount, int dir = 1)
	{
		int id = _nextNucleusId++;
		var n = new Nucleus
		{
			Id = id,
			Row = r,
			Col = c,
			Tier = tier,
			Dir = dir,
			Ring = MakeRing(slotCount),
			SlotCount = slotCount,
			LastCaptureTick = null,
		};
		Nuclei[id] = n;
		EntityAt[r, c] = id;
		return id;
	}

	public void RemoveNucleusAt(int r, int c)
	{
		var id = EntityAt[r, c];
		if (id.HasValue)
		{
			Nuclei.Remove(id.Value);
			EntityAt[r, c] = null;
		}
	}

	public int AddStar(int r, int c, int diameter, string color)
	{
		int id = _nextStarId++;
		var recipe = Config.StarRecipes.TryGetValue(color, out var rec)
			? rec
			: new Dictionary<string, float>();
		var star = new Star
		{
			Id = id,
			Row = r,
			Col = c,
			Diameter = diameter,
			Color = color,
			Recipe = recipe,
			Progress = recipe.Keys.ToDictionary(k => k, _ => 0f),
			ReadyCount = 0,
			LastCaptureTick = null,
		};
		Stars[id] = star;
		for (int rr = r; rr < r + diameter; rr++)
			for (int cc = c; cc < c + diameter; cc++)
				if (InBounds(rr, cc)) StarAt[rr, cc] = id;
		return id;
	}

	public void RemoveStarAt(int r, int c)
	{
		var idOpt = StarAt[r, c];
		if (!idOpt.HasValue) return;
		var star = Stars[idOpt.Value];
		for (int rr = star.Row; rr < star.Row + star.Diameter; rr++)
			for (int cc = star.Col; cc < star.Col + star.Diameter; cc++)
				if (InBounds(rr, cc) && StarAt[rr, cc] == idOpt) StarAt[rr, cc] = null;
		Stars.Remove(idOpt.Value);
	}

	public void PlaceSource(int r, int c, string color, float total = -1f)
	{
		float amount = total > 0f ? total : Config.DefaultSourceTotal;
		Grid[r, c].Source = new SourceInfo { Color = color, Remaining = amount, Total = amount };
	}

	public void PlaceBlackhole(int r, int c)
	{
		Grid[r, c].IsBlackhole = true;
	}

	public void PlaceSingularity(int r, int c)
	{
		if (SingularityPos.HasValue)
		{
			var (pr, pc) = SingularityPos.Value;
			Grid[pr, pc].IsSingularity = false;
		}
		Grid[r, c].IsSingularity = true;
		SingularityPos = (r, c);
	}

	// Removes whatever occupies a single cell: a nucleus, a whole star (if
	// any of its footprint cells is this one), or terrain. Mirrors the
	// prototype's eraseCell.
	public void EraseCell(int r, int c)
	{
		if (EntityAt[r, c].HasValue) RemoveNucleusAt(r, c);
		if (StarAt[r, c].HasValue) RemoveStarAt(r, c);
		if (Grid[r, c].IsSingularity && SingularityPos.HasValue && SingularityPos.Value == (r, c))
			SingularityPos = null;
		Grid[r, c] = new GridCell();
	}

	// Grows/shrinks the field, carrying over whatever still fully fits in
	// BOTH the old and the new bounds; drops anything else. Called
	// automatically when a field-expansion milestone fires (see
	// Simulation.CheckExpansionMilestone).
	public void Resize(int newRows, int newCols)
	{
		newRows = Math.Clamp(newRows, Config.MinFieldSize, Config.MaxFieldSize);
		newCols = Math.Clamp(newCols, Config.MinFieldSize, Config.MaxFieldSize);

		var oldGrid = Grid;
		var oldEntityAt = EntityAt;
		var oldNuclei = Nuclei;
		var oldStarAt = StarAt;
		var oldStars = Stars;
		int oldRows = Rows, oldCols = Cols;

		Rows = newRows;
		Cols = newCols;
		AllocateGrids();
		Nuclei = new Dictionary<int, Nucleus>();
		Stars = new Dictionary<int, Star>();
		SingularityPos = null;

		int minRows = Math.Min(oldRows, Rows);
		int minCols = Math.Min(oldCols, Cols);

		for (int r = 0; r < minRows; r++)
		{
			for (int c = 0; c < minCols; c++)
			{
				Grid[r, c] = oldGrid[r, c];
				if (Grid[r, c].IsSingularity) SingularityPos = (r, c);
				var oid = oldEntityAt[r, c];
				if (oid.HasValue)
				{
					var n = oldNuclei[oid.Value];
					n.Row = r;
					n.Col = c;
					Nuclei[oid.Value] = n;
					EntityAt[r, c] = oid;
				}
			}
		}

		var seenStars = new HashSet<int>();
		for (int r = 0; r < minRows; r++)
		{
			for (int c = 0; c < minCols; c++)
			{
				var osid = oldStarAt[r, c];
				if (!osid.HasValue || !seenStars.Add(osid.Value)) continue;
				var os = oldStars[osid.Value];
				bool fitsOld = os.Row + os.Diameter <= oldRows && os.Col + os.Diameter <= oldCols;
				bool fitsNew = os.Row + os.Diameter <= Rows && os.Col + os.Diameter <= Cols;
				if (fitsOld && fitsNew)
				{
					int nsid = AddStar(os.Row, os.Col, os.Diameter, os.Color);
					Stars[nsid].Progress = os.Progress;
					Stars[nsid].ReadyCount = os.ReadyCount;
				}
			}
		}
	}
}
