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

	// Открыть чанки (T010 — расширение по награде). Возвращает, сколько новых.
	public int Open(IEnumerable<(int cx, int cy)> chunks)
	{
		int added = 0;
		foreach (var c in chunks)
			if (_open.Add(c)) added++;
		if (added > 0) Version++;
		return added;
	}

	// Открытые чанки в детерминированном порядке (cy, cx) — для сохранения.
	public List<(int cx, int cy)> SortedOpenChunks()
	{
		var list = new List<(int cx, int cy)>(_open);
		list.Sort((a, b) => a.cy != b.cy ? a.cy.CompareTo(b.cy) : a.cx.CompareTo(b.cx));
		return list;
	}
}
