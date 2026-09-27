using System;

// Общие чистые функции кольца ядра — одни и те же для ядер слоя 1
// (NucleusLayer) и молекул слоя 2 (MoleculeLayer). Только целая арифметика,
// без Godot и без состояния: фаза поворота — функция (глобальный тик,
// период шага, направление), поэтому все ядра одного тира и спина всегда
// синхронны, когда бы они ни появились на поле.
public static class RingMath
{
	public const int Positions = 8;

	// Порядок специально подобран так, чтобы N штук гнёзд ложились симметрично:
	// первые 2 (0,4) — противоположная пара (N/S, "напротив друг друга" для С2);
	// первые 4 (0,4,2,6) — все 4 стороны света под 90° (С4); все 8 — просто всё
	// кольцо (С8). Раскладки вложены: 2 ⊂ 4 ⊂ 8.
	public static readonly int[] HolePriority = { 0, 4, 2, 6, 1, 5, 3, 7 };

	// Сколько шагов по 45° провернулось кольцо к тику tick (0..7) при периоде
	// шага periodTicks и направлении dir (+1/-1). periodTicks <= 0 — кольцо
	// не вращается.
	public static int RotationStep(long tick, int periodTicks, int dir)
	{
		if (periodTicks <= 0) return 0;
		long steps = (tick / periodTicks) * dir;
		return (int)(((steps % Positions) + Positions) % Positions);
	}

	// Сколько тиков прошло с последнего шага поворота (0..periodTicks-1) —
	// нужно только рендеру для плавной интерполяции между шагами.
	public static long TicksIntoStep(long tick, int periodTicks) =>
		periodTicks > 0 ? tick % periodTicks : 0;

	// Какие из 8 физических позиций кольца существуют при holeCount гнёздах
	// (см. HolePriority).
	public static bool[] SlotMask(int holeCount)
	{
		holeCount = Math.Clamp(holeCount, 0, Positions);
		var exists = new bool[Positions];
		for (int i = 0; i < holeCount; i++) exists[HolePriority[i]] = true;
		return exists;
	}
}
