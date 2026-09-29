using System;
using System.Collections.Generic;

// Порты чанков — чистые данные и правила без Godot (T002, GDD «Порты и блоки»).
//
// У каждого чанка 4 порта, по одному в центре стороны. Порт — дырка 2×2 на
// границе двух чанков; чанку принадлежит половина: клетки FirstCell и
// FirstCell+1 (7 и 8 при ChunkSize 16) крайнего ряда/столбца своей стороны.
// Эти клетки — отдельный тип объекта: ядра и источники на них не ставятся.
// Порт держит один атом (Atom): выход набирает в него частицы, вход отдаёт.
//
// Хранятся только порты, отличные от «закрыт и пуст», в SortedDictionary —
// обход всегда в одном порядке (cy, cx, сторона), от хеша ничего не зависит.
public enum PortSide { N = 0, E = 1, S = 2, W = 3 }

public enum PortMode { Closed = 0, Output = 1, Input = 2 }

public readonly record struct PortKey(int Cx, int Cy, int Side) : IComparable<PortKey>
{
	public int CompareTo(PortKey other)
	{
		int c = Cy.CompareTo(other.Cy);
		if (c != 0) return c;
		c = Cx.CompareTo(other.Cx);
		return c != 0 ? c : Side.CompareTo(other.Side);
	}
}

public struct PortState
{
	public PortMode Mode;
	public Atom Atom;
}

public sealed class PortSet
{
	public const int SideCount = 4;

	public readonly int ChunkSize;
	// Первая из двух клеток порта вдоль стороны (7 при ChunkSize 16).
	public int FirstCell => ChunkSize / 2 - 1;

	private readonly SortedDictionary<PortKey, PortState> _ports = new();

	// Отладочные счётчики (HUD). На замкнутой схеме: Packed = 8 × Created,
	// Emitted + (частицы во входных буферах) = 8 × Unpacked.
	public long ParticlesPacked;   // частиц принято выходными портами
	public long AtomsCreated;      // атомов собрано на выходах
	public long AtomsUnpacked;     // атомов принято входами на распаковку
	public long ParticlesEmitted;  // частиц отдано входами в ядра
	public long ParticlesDiscarded; // частиц сброшено при смене режима

	public PortSet(int chunkSize)
	{
		ChunkSize = chunkSize;
	}

	// --- геометрия ---

	// Сторона света порта → компас-индекс кольца (0=N, 2=E, 4=S, 6=W, см. RingMath).
	public static int SideToCompass(int side) => side * 2;
	public static int OppositeSide(int side) => (side + 2) % SideCount;

	public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

	// Клетка (row, col) — клетка порта? Тогда key — её порт (чанк и сторона).
	public bool TryGetPortAtCell(int row, int col, out PortKey key)
	{
		key = default;
		if (ChunkSize < 4) return false;
		int cx = FloorDiv(col, ChunkSize), cy = FloorDiv(row, ChunkSize);
		int r = row - cy * ChunkSize, c = col - cx * ChunkSize;
		int last = ChunkSize - 1, a = FirstCell, b = FirstCell + 1;

		if (r == 0 && (c == a || c == b)) key = new PortKey(cx, cy, (int)PortSide.N);
		else if (r == last && (c == a || c == b)) key = new PortKey(cx, cy, (int)PortSide.S);
		else if (c == 0 && (r == a || r == b)) key = new PortKey(cx, cy, (int)PortSide.W);
		else if (c == last && (r == a || r == b)) key = new PortKey(cx, cy, (int)PortSide.E);
		else return false;
		return true;
	}

	public bool IsPortCell(int row, int col) => TryGetPortAtCell(row, col, out _);

	// i-я (0 или 1) клетка порта в мировых координатах клеток слоя 1.
	public (int row, int col) Cell(PortKey key, int i)
	{
		int row0 = key.Cy * ChunkSize, col0 = key.Cx * ChunkSize;
		int along = FirstCell + i, last = ChunkSize - 1;
		return (PortSide)key.Side switch
		{
			PortSide.N => (row0, col0 + along),
			PortSide.S => (row0 + last, col0 + along),
			PortSide.W => (row0 + along, col0),
			_ => (row0 + along, col0 + last),
		};
	}

	public bool SameChunk(PortKey key, int row, int col) =>
		FloorDiv(col, ChunkSize) == key.Cx && FloorDiv(row, ChunkSize) == key.Cy;

	// --- состояние ---

	public PortState Get(PortKey key) => _ports.TryGetValue(key, out var s) ? s : default;

	public PortMode ModeOf(PortKey key) => Get(key).Mode;

	private void Set(PortKey key, PortState state)
	{
		if (state.Mode == PortMode.Closed && state.Atom.IsEmpty) _ports.Remove(key);
		else _ports[key] = state;
	}

	// Порты, отличные от «закрыт и пуст», в детерминированном порядке.
	public IEnumerable<KeyValuePair<PortKey, PortState>> Enumerate() => _ports;

	// Есть ли у чанка открытый порт. Это одна из двух причин, по которым чанк —
	// блок; полное правило — NucleusLayer.IsBlock (T004).
	public bool HasOpenPort(int cx, int cy)
	{
		for (int side = 0; side < SideCount; side++)
			if (ModeOf(new PortKey(cx, cy, side)) != PortMode.Closed) return true;
		return false;
	}

	public IEnumerable<(int cx, int cy)> EnumerateOpenPortChunks()
	{
		(int cx, int cy)? last = null;
		foreach (var pair in _ports)
		{
			if (pair.Value.Mode == PortMode.Closed) continue;
			var chunk = (pair.Key.Cx, pair.Key.Cy);
			if (last == chunk) continue;
			last = chunk;
			yield return chunk;
		}
	}

	// Закрыт → выход → вход → закрыт. Содержимое при смене режима сбрасывается
	// (ParticlesDiscarded) — иначе недобранный выход нельзя было бы закрыть.
	public PortMode CycleMode(PortKey key)
	{
		var s = Get(key);
		ParticlesDiscarded += s.Atom.Count;
		s.Atom = default;
		s.Mode = s.Mode switch
		{
			PortMode.Closed => PortMode.Output,
			PortMode.Output => PortMode.Input,
			_ => PortMode.Closed,
		};
		Set(key, s);
		return s.Mode;
	}

	// Для загрузки: режим и содержимое как есть, без счётчиков.
	public void Restore(PortKey key, PortMode mode, Atom atom) =>
		Set(key, new PortState { Mode = mode, Atom = atom });

	public void Clear()
	{
		_ports.Clear();
		ParticlesPacked = AtomsCreated = AtomsUnpacked = ParticlesEmitted = ParticlesDiscarded = 0;
	}

	// --- обмен частицами (слой 1) ---

	// Выход принимает частицы, пока атом не собран (обратное давление).
	public bool CanAcceptParticle(PortKey key)
	{
		var s = Get(key);
		return s.Mode == PortMode.Output && !s.Atom.IsFull;
	}

	public void AcceptParticle(PortKey key, int color)
	{
		var s = Get(key);
		s.Atom.Push(color);
		ParticlesPacked++;
		if (s.Atom.IsFull) AtomsCreated++;
		Set(key, s);
	}

	// Вход отдаёт частицы распакованного атома по одной; -1 — отдавать нечего.
	public int PeekParticle(PortKey key)
	{
		var s = Get(key);
		return s.Mode == PortMode.Input && !s.Atom.IsEmpty ? s.Atom.ColorAt(0) : -1;
	}

	public int EmitParticle(PortKey key)
	{
		var s = Get(key);
		int color = s.Atom.PopFront();
		if (color >= 0) ParticlesEmitted++;
		Set(key, s);
		return color;
	}

	// --- обмен атомами (слой 2) ---

	public bool HasReadyAtom(PortKey key)
	{
		var s = Get(key);
		return s.Mode == PortMode.Output && s.Atom.IsFull;
	}

	public Atom TakeAtom(PortKey key)
	{
		var s = Get(key);
		var atom = s.Atom;
		s.Atom = default;
		Set(key, s);
		return atom;
	}

	// Вход берёт следующий атом, только когда отдал все частицы предыдущего.
	public bool CanAcceptAtom(PortKey key)
	{
		var s = Get(key);
		return s.Mode == PortMode.Input && s.Atom.IsEmpty;
	}

	public void AcceptAtom(PortKey key, Atom atom)
	{
		var s = Get(key);
		s.Atom = atom;
		AtomsUnpacked++;
		Set(key, s);
	}
}
