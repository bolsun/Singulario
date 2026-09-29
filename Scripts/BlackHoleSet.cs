using System;
using System.Collections.Generic;

// Чёрные дыры (ЧД) — чистые данные без Godot (T005, GDD «Производство»,
// «Встроенные объекты»). ЧД — объект слоя 1: квадрат Size×Size клеток,
// (Row, Col) — верхняя левая клетка. Размер хранится у каждой ЧД (настройка —
// BlackHoleLayer.BlackHoleSize), поэтому смена настройки не ломает сохранения.
//
// Горизонт событий — клетки, примыкающие к сторонам ЧД (4 × Size клеток,
// углы по диагонали не считаются). Правила захвата (атом доставлен на
// горизонт → поглощён целиком; атом, стоящий там, отдаёт частицы) живут в
// NucleusLayer, здесь — только геометрия и счётчики поглощённого (общие на
// все ЧД).
//
// ЧД — в SortedSet по (Row, Col): обход всегда в одном порядке.
public readonly record struct BlackHole(int Row, int Col, int Size) : IComparable<BlackHole>
{
	public int CompareTo(BlackHole other)
	{
		int c = Row.CompareTo(other.Row);
		return c != 0 ? c : Col.CompareTo(other.Col);
	}

	public bool ContainsCell(int row, int col) =>
		row >= Row && row < Row + Size && col >= Col && col < Col + Size;

	// Клетка на горизонте этой ЧД: (dr, dc) — шаг из клетки в сторону ЧД.
	public bool IsHorizonCell(int row, int col, out int dr, out int dc)
	{
		dr = 0; dc = 0;
		bool inRows = row >= Row && row < Row + Size;
		bool inCols = col >= Col && col < Col + Size;
		if (inRows && col == Col - 1) { dc = 1; return true; }
		if (inRows && col == Col + Size) { dc = -1; return true; }
		if (inCols && row == Row - 1) { dr = 1; return true; }
		if (inCols && row == Row + Size) { dr = -1; return true; }
		return false;
	}

	public bool Overlaps(int row, int col, int size) =>
		row < Row + Size && Row < row + size && col < Col + Size && Col < col + size;
}

public sealed class BlackHoleSet
{
	// Цвет частицы — байт (как в Atom), поэтому 256 счётчиков.
	public const int ColorCount = 256;
	// Тиры атомов (CoreTier 0..5) с запасом.
	public const int TierCount = 8;

	private readonly SortedSet<BlackHole> _holes = new();

	public readonly long[] AtomsAbsorbed = new long[TierCount];
	public readonly long[] ParticlesAbsorbed = new long[ColorCount];

	// Меняется при каждой установке/удалении — для кэшей отрисовки.
	public int Version { get; private set; }

	public int Count => _holes.Count;

	public IEnumerable<BlackHole> Enumerate() => _holes;

	public long TotalAtomsAbsorbed
	{
		get
		{
			long sum = 0;
			foreach (long n in AtomsAbsorbed) sum += n;
			return sum;
		}
	}

	public bool Overlaps(int row, int col, int size)
	{
		foreach (var h in _holes)
			if (h.Overlaps(row, col, size)) return true;
		return false;
	}

	// ЧД, накрывающая клетку.
	public bool TryGetAt(int row, int col, out BlackHole hole)
	{
		foreach (var h in _holes)
			if (h.ContainsCell(row, col)) { hole = h; return true; }
		hole = default;
		return false;
	}

	// Клетка на горизонте какой-либо ЧД (первой в порядке обхода).
	public bool TryGetHorizon(int row, int col, out BlackHole hole, out int dr, out int dc)
	{
		foreach (var h in _holes)
			if (h.IsHorizonCell(row, col, out dr, out dc)) { hole = h; return true; }
		hole = default; dr = 0; dc = 0;
		return false;
	}

	// Добавить ЧД; false — если размер неверный или она пересекается с другой.
	public bool Add(int row, int col, int size)
	{
		if (size <= 0 || Overlaps(row, col, size)) return false;
		_holes.Add(new BlackHole(row, col, size));
		Version++;
		return true;
	}

	public bool Remove(BlackHole hole)
	{
		if (!_holes.Remove(hole)) return false;
		Version++;
		return true;
	}

	public void AbsorbAtom(int tier)
	{
		if (tier >= 0 && tier < TierCount) AtomsAbsorbed[tier]++;
	}

	public void AbsorbParticle(int color)
	{
		if (color >= 0 && color < ColorCount) ParticlesAbsorbed[color]++;
	}

	public void Clear()
	{
		_holes.Clear();
		ResetCounters();
		Version++;
	}

	public void ResetCounters()
	{
		Array.Clear(AtomsAbsorbed);
		Array.Clear(ParticlesAbsorbed);
	}
}
