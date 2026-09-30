using System.Collections.Generic;

// Состав чанков для дальнего зума (T015, GDD «Визуал → Ступени детализации по
// зуму») — чистые данные без Godot. Экземпляр — NucleusLayer.Composition.
//
// У чанка два набора счётчиков по тиру (0 = Ж, 1 = К, 2 = С, 3 = серое): рабочие
// атомы и клетки источников. Атомы пересчитываются целиком при пересборке чанка
// (SetAtoms), источники — при изменении месторождений (ClearSources + AddSource).
// Вес клетки источника относительно атома — SourceWeight (в процентах, целое).
public sealed class ChunkComposition
{
	public const int TierCount = 4;

	private sealed class Entry
	{
		public readonly int[] Atoms = new int[TierCount];
		public readonly int[] Sources = new int[TierCount];
		public int AtomTotal, SourceTotal;
	}

	private readonly Dictionary<(int cx, int cy), Entry> _chunks = new();

	// Вес клетки источника, % от атома (100 — как один атом).
	public int SourceWeightPercent = 100;

	// Меняется при каждом изменении — для перерисовки.
	public int Version { get; private set; }

	public static bool IsCountedTier(int tier) => tier >= 0 && tier < TierCount;

	// Атомы чанка заново: counts[tier] — число рабочих атомов тира (длина TierCount).
	public void SetAtoms(int cx, int cy, int[] counts)
	{
		var e = GetOrAdd(cx, cy);
		int total = 0;
		bool same = true;
		for (int t = 0; t < TierCount; t++)
		{
			if (e.Atoms[t] != counts[t]) same = false;
			e.Atoms[t] = counts[t];
			total += counts[t];
		}
		e.AtomTotal = total;
		if (!same) Version++;
		RemoveIfEmpty(cx, cy, e);
	}

	// Все источники забыть (перед полным пересчётом месторождений).
	public void ClearSources()
	{
		var empty = new List<(int cx, int cy)>();
		foreach (var (key, e) in _chunks)
		{
			System.Array.Clear(e.Sources);
			e.SourceTotal = 0;
			if (e.AtomTotal == 0) empty.Add(key);
		}
		foreach (var key in empty) _chunks.Remove(key);
		Version++;
	}

	public void AddSource(int cx, int cy, int tier)
	{
		if (!IsCountedTier(tier)) return;
		var e = GetOrAdd(cx, cy);
		e.Sources[tier]++;
		e.SourceTotal++;
		Version++;
	}

	public void Clear()
	{
		_chunks.Clear();
		Version++;
	}

	// Вес чанка (в сотых долях атома): атомы × 100 + клетки источников × вес.
	public int Weight100(int cx, int cy) =>
		_chunks.TryGetValue((cx, cy), out var e) ? e.AtomTotal * 100 + e.SourceTotal * SourceWeightPercent : 0;

	// Преобладающий тир по весу; при равенстве — меньший номер тира. -1 — чанк пуст.
	public int Dominant(int cx, int cy)
	{
		if (!_chunks.TryGetValue((cx, cy), out var e)) return -1;
		int best = -1, bestWeight = 0;
		for (int t = 0; t < TierCount; t++)
		{
			int w = e.Atoms[t] * 100 + e.Sources[t] * SourceWeightPercent;
			if (w > bestWeight) { best = t; bestWeight = w; }
		}
		return best;
	}

	private Entry GetOrAdd(int cx, int cy)
	{
		if (!_chunks.TryGetValue((cx, cy), out var e)) _chunks[(cx, cy)] = e = new Entry();
		return e;
	}

	private void RemoveIfEmpty(int cx, int cy, Entry e)
	{
		if (e.AtomTotal == 0 && e.SourceTotal == 0) _chunks.Remove((cx, cy));
	}
}
