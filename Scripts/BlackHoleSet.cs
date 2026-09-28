using System;
using System.Collections.Generic;

// Чёрные дыры (ЧД) — чистые данные без Godot (T003, GDD «Производство»,
// «Прогрессия → Старт»). ЧД — встроенный объект слоя 2 на всю клетку
// (= чанк слоя 1). Её 4 порта — всегда входы: атом, поданный в порт,
// засчитывается в тот же тик и исчезает, дырка сразу свободна. ЧД ничего не
// хранит, кроме счётчиков поглощённого (общие на все ЧД — основа целей
// «доставить N атомов Ж»).
//
// Клетки — в SortedSet: обход всегда в одном порядке (cy, cx).
public readonly record struct ChunkKey(int Cx, int Cy) : IComparable<ChunkKey>
{
	public int CompareTo(ChunkKey other)
	{
		int c = Cy.CompareTo(other.Cy);
		return c != 0 ? c : Cx.CompareTo(other.Cx);
	}
}

public readonly record struct PortAbsorb(ChunkKey Hole, PortKey From, Atom Atom);

public sealed class BlackHoleSet
{
	// Цвет частицы — байт атома (Atom.ColorAt), поэтому 256 счётчиков.
	public const int ColorCount = 256;

	private readonly SortedSet<ChunkKey> _holes = new();

	public long AtomsAbsorbed;
	public readonly long[] ParticlesAbsorbed = new long[ColorCount];

	// Меняется при каждой установке/удалении — для кэшей отрисовки.
	public int Version { get; private set; }

	public int Count => _holes.Count;

	public bool Contains(int cx, int cy) => _holes.Contains(new ChunkKey(cx, cy));

	public IEnumerable<ChunkKey> Enumerate() => _holes;

	public bool Add(int cx, int cy)
	{
		if (!_holes.Add(new ChunkKey(cx, cy))) return false;
		Version++;
		return true;
	}

	public bool Remove(int cx, int cy)
	{
		if (!_holes.Remove(new ChunkKey(cx, cy))) return false;
		Version++;
		return true;
	}

	// Засчитать поглощённый атом: +1 атом и его частицы по цветам.
	public void Absorb(Atom atom)
	{
		AtomsAbsorbed++;
		for (int i = 0; i < atom.Count; i++) ParticlesAbsorbed[atom.ColorAt(i)]++;
	}

	// Сторона ЧД → соседний чанк: N, E, S, W (как PortSide).
	private static readonly (int dx, int dy)[] Compass4 = { (0, -1), (1, 0), (0, 1), (-1, 0) };

	// Порт → ЧД: выходной порт соседнего чанка, стоящий против стороны ЧД (две
	// половины одной дырки), отдаёт готовый атом прямо в ЧД. Каждый тик слоя 1;
	// скорость ограничена только подающим портом. events (может быть null) —
	// для визуального эффекта, на симуляцию не влияет.
	public void AbsorbFromPorts(PortSet ports, List<PortAbsorb> events)
	{
		if (ports == null) return;
		foreach (var hole in _holes)
		{
			for (int side = 0; side < PortSet.SideCount; side++)
			{
				var (dx, dy) = Compass4[side];
				var key = new PortKey(hole.Cx + dx, hole.Cy + dy, PortSet.OppositeSide(side));
				if (!ports.HasReadyAtom(key)) continue;
				var atom = ports.TakeAtom(key);
				Absorb(atom);
				events?.Add(new PortAbsorb(hole, key, atom));
			}
		}
	}

	public void Clear()
	{
		_holes.Clear();
		ResetCounters();
		Version++;
	}

	public void ResetCounters()
	{
		AtomsAbsorbed = 0;
		Array.Clear(ParticlesAbsorbed);
	}
}
