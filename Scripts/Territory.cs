using System.Collections.Generic;

// Открытая территория (T009, GDD «Прогрессия → Старт») — чистые данные без
// Godot: какие чанки игроку доступны. Экземпляр — NucleusLayer.Territory.
//
// AllOpen — вся карта открыта (запуск, старые сохранения без поля OpenChunks).
// Иначе открыты только чанки из набора. Песочница открывает всё поверх этого
// (правило — NucleusLayer.IsChunkOpen), сам набор при этом не меняется.
public sealed class Territory
{
	// Стартовая зона новой игры — 2×2 чанка вокруг начала координат.
	public static readonly (int cx, int cy)[] StartChunks = { (-1, -1), (0, -1), (-1, 0), (0, 0) };

	private readonly HashSet<(int cx, int cy)> _open = new();

	public bool AllOpen { get; private set; } = true;

	// Меняется при каждом изменении — для перерисовки и отладки.
	public int Version { get; private set; }

	public bool IsOpen(int cx, int cy) => AllOpen || _open.Contains((cx, cy));

	public int OpenCount => _open.Count;

	// Открыть всю карту (набор очищается).
	public void OpenAll()
	{
		AllOpen = true;
		_open.Clear();
		Version++;
	}

	// Закрыть всё, кроме chunks.
	public void Reset(IEnumerable<(int cx, int cy)> chunks)
	{
		AllOpen = false;
		_open.Clear();
		foreach (var c in chunks) _open.Add(c);
		Version++;
	}

	// Открыть чанки (расширение по награде заданий — T011). Возвращает, сколько новых.
	public int Open(IEnumerable<(int cx, int cy)> chunks)
	{
		int added = 0;
		foreach (var c in chunks)
			if (_open.Add(c)) added++;
		if (added > 0) Version++;
		return added;
	}

	// Прямоугольник открытых чанков включительно (T012 — камера на старте и при
	// расширении). false, если вся карта открыта или набор пуст.
	public bool TryGetBounds(out int x0, out int y0, out int x1, out int y1)
	{
		x0 = y0 = int.MaxValue;
		x1 = y1 = int.MinValue;
		if (AllOpen || _open.Count == 0) return false;
		foreach (var (cx, cy) in _open)
		{
			if (cx < x0) x0 = cx;
			if (cx > x1) x1 = cx;
			if (cy < y0) y0 = cy;
			if (cy > y1) y1 = cy;
		}
		return true;
	}

	// Следующее кольцо (T011): прямоугольник вокруг открытых чанков, расширенный
	// на 1 со всех сторон, без уже открытых (2×2 → 4×4 → 6×6 …). Порядок —
	// (cy, cx). Пусто, если вся карта открыта или набор пуст.
	public List<(int cx, int cy)> NextRing()
	{
		var ring = new List<(int cx, int cy)>();
		if (!TryGetBounds(out int x0, out int y0, out int x1, out int y1)) return ring;
		for (int cy = y0 - 1; cy <= y1 + 1; cy++)
			for (int cx = x0 - 1; cx <= x1 + 1; cx++)
				if (!_open.Contains((cx, cy))) ring.Add((cx, cy));
		return ring;
	}

	// Открытые чанки в детерминированном порядке (cy, cx) — для сохранения.
	public List<(int cx, int cy)> SortedOpenChunks()
	{
		var list = new List<(int cx, int cy)>(_open);
		list.Sort((a, b) => a.cy != b.cy ? a.cy.CompareTo(b.cy) : a.cx.CompareTo(b.cx));
		return list;
	}
}
