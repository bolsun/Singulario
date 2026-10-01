using System;
using System.Collections.Generic;

// Раскладка вида месторождения (T022) — чистые данные и функции, без Godot (по образцу
// ChunkComposition). Вход — клетки источников одного тира, стадия запаса скопления и
// «связаны ли две клетки» (одно скопление); выход — пятна облака (центр, радиус, сила) и
// частицы (у ядра клетки и на мостиках между соседями по 4 сторонам). Всё — чистая функция
// входа: детерминированный целочисленный хеш (SplitMix64), без Random и без зависимости от
// порядка словарей, поэтому после перезагрузки сохранения рисунок тот же. Числа — в клетках
// (float допустим: это только вид, не симуляция). Отбор частиц по стадии запаса — здесь, при
// перестройке, а не в шейдере. Таблицы и формулы — Docs/Art/deposit-visual-spec.md.
public sealed class DepositLayout
{
	public struct Spot
	{
		public float X, Y;          // центр «ядра» клетки, клетки мира
		public float Radius;        // клеток
		public float Strength;
	}

	// Kind 0 — у ядра: A — ядро, Angle/Dist — орбита. Kind 1 — мостик: A — начало,
	// (Dx, Dy) — вектор к другому ядру, Bend — изгиб (клетки).
	public struct Particle
	{
		public int Kind;
		public float X, Y;
		public float Angle, Dist;
		public float Dx, Dy, Bend;
		public int SizeClass;       // 0 — 16 px, 1 — 8 px, 2 — 4 px, 3 — 2 px
		public int Variant;         // кадр в полосе своего размера (T024), 0..Frames[SizeClass]−1
		public bool Dark;           // глубина (T024): на тон темнее
		public float Phase;         // 0..1
		public int PeriodK;         // период = 256 · PeriodK тиков
	}

	// Стадия запаса 0..3 (>75 / 50–75 / 25–50 / <25%).
	public float[] StageRadius = { 1.0f, 0.85f, 0.7f, 0.42f };
	public float[] StageStrength = { 0.07f, 0.05f, 0.04f, 0.012f };
	// Видна частица с приоритетом < порога (у стадии 0 — все).
	public float[] StageThreshold = { 1.01f, 0.72f, 0.46f, 0.30f };

	public float NeighborPull = 0.1f;     // смещение ядра на соседа, клеток
	public float NeighborPullMax = 0.2f;  // не больше
	public float CoreJitter = 0.06f;      // ± разброс из хеша
	public int CoreCount = 20;            // частиц на клетку
	public float CoreDistScale = 0.55f;   // √r · scale — почти равномерно по кругу клетки
	public int BridgeCount = 5;           // частиц на пару
	public float BridgeBend = 0.12f;
	// Доли размеров у ядра 16 / 8 / 4 px, остаток — 2 px (T024).
	public float ShareLarge = 0.10f;
	public float ShareMedium = 0.25f;
	public float ShareSmall = 0.40f;
	// Мостик тоньше ядра: без 16 px — 8 / 4 px, остаток — 2 px.
	public float BridgeShareMedium = 0.35f;
	public float BridgeShareSmall = 0.40f;
	public float DarkShare = 0.3f;        // доля частиц на тон темнее (глубина)
	// Кадров в полосе каждого размера (16/8/4/2 px); задаёт DepositLayer по картинкам.
	public int[] Frames = { 3, 3, 3, 1 };
	public int CorePeriodMin = 2, CorePeriodMax = 4;       // × 256 тиков
	public int BridgePeriodMin = 4, BridgePeriodMax = 8;

	// Максимальное расстояние частицы от ядра (3 равномерных: |сумма − 1,5| ≤ 1,5).
	private float MaxDist => CoreDistScale;

	// stageAt(row, col) — стадия клетки (<0 — нет клетки); linked(r1, c1, r2, c2) — обе клетки
	// есть и принадлежат одному скоплению. Собирает раскладку для клеток cells.
	public void Build(IEnumerable<(int row, int col)> cells, Func<int, int, int> stageAt,
		Func<int, int, int, int, bool> linked, List<Spot> spots, List<Particle> particles)
	{
		spots.Clear();
		particles.Clear();
		foreach (var (row, col) in cells)
		{
			int stage = Math.Clamp(stageAt(row, col), 0, 3);
			var (cx, cy) = Core(row, col, linked);
			spots.Add(new Spot { X = cx, Y = cy, Radius = StageRadius[stage], Strength = StageStrength[stage] });

			ulong cellHash = CellHash(row, col);
			float threshold = StageThreshold[stage];
			for (int i = 0; i < CoreCount; i++)
			{
				ulong h = Mix(cellHash + (ulong)(i + 1) * 0x9E3779B97F4A7C15UL);
				float angle = U(ref h) * MathF.Tau;
				float dist = MathF.Sqrt(U(ref h)) * CoreDistScale;
				float size = U(ref h), phase = U(ref h), period = U(ref h), rnd = U(ref h);
				// T024: новые вызовы — после прежних, чтобы раскладка остального не сдвинулась.
				float variant = U(ref h), dark = U(ref h);
				float priority = 0.65f * (dist / MaxDist) + 0.35f * rnd;
				if (priority >= threshold) continue;
				int sizeClass = SizeClassOf(size);
				particles.Add(new Particle
				{
					Kind = 0, X = cx, Y = cy, Angle = angle, Dist = dist, Phase = phase,
					SizeClass = sizeClass, Variant = VariantOf(variant, sizeClass), Dark = dark < DarkShare,
					PeriodK = PeriodOf(period, CorePeriodMin, CorePeriodMax),
				});
			}

			// Мостики: пару считает клетка слева/сверху (вправо и вниз) — без повторов.
			for (int d = 0; d < 2; d++)
			{
				int nr = row + d, nc = col + (1 - d);
				if (!linked(row, col, nr, nc)) continue;
				var (bx, by) = Core(nr, nc, linked);
				ulong pairHash = Mix(cellHash ^ (d == 0 ? 0xA5A5A5A5UL : 0x5A5A5A5AUL));
				for (int i = 0; i < BridgeCount; i++)
				{
					ulong h = Mix(pairHash + (ulong)(i + 1) * 0x9E3779B97F4A7C15UL);
					float size = U(ref h), phase = U(ref h), period = U(ref h), rnd = U(ref h), bend = U(ref h);
					float variant = U(ref h), dark = U(ref h);
					float priority = 0.3f + 0.6f * rnd;
					if (priority >= threshold) continue;
					int sizeClass = size < BridgeShareMedium ? 1 : size < BridgeShareMedium + BridgeShareSmall ? 2 : 3;
					particles.Add(new Particle
					{
						Kind = 1, X = cx, Y = cy, Dx = bx - cx, Dy = by - cy,
						Bend = (bend * 2f - 1f) * BridgeBend, Phase = phase,
						SizeClass = sizeClass, Variant = VariantOf(variant, sizeClass), Dark = dark < DarkShare,
						PeriodK = PeriodOf(period, BridgePeriodMin, BridgePeriodMax),
					});
				}
			}
		}
	}

	// Центр «ядра»: центр клетки + смещение к соседям того же скопления (по 4 сторонам) +
	// постоянный разброс из хеша клетки.
	public (float x, float y) Core(int row, int col, Func<int, int, int, int, bool> linked)
	{
		float ox = 0f, oy = 0f;
		if (linked(row, col, row, col - 1)) ox -= NeighborPull;
		if (linked(row, col, row, col + 1)) ox += NeighborPull;
		if (linked(row, col, row - 1, col)) oy -= NeighborPull;
		if (linked(row, col, row + 1, col)) oy += NeighborPull;
		float len = MathF.Sqrt(ox * ox + oy * oy);
		if (len > NeighborPullMax) { ox *= NeighborPullMax / len; oy *= NeighborPullMax / len; }
		ulong h = Mix(CellHash(row, col) ^ 0xC0FFEEUL);
		ox += (U(ref h) * 2f - 1f) * CoreJitter;
		oy += (U(ref h) * 2f - 1f) * CoreJitter;
		return (col + 0.5f + ox, row + 0.5f + oy);
	}

	private int SizeClassOf(float u) =>
		u < ShareLarge ? 0 : u < ShareLarge + ShareMedium ? 1 : u < ShareLarge + ShareMedium + ShareSmall ? 2 : 3;

	private int VariantOf(float u, int sizeClass)
	{
		int frames = Math.Max(1, Frames[sizeClass]);
		return Math.Min(frames - 1, (int)(u * frames));
	}

	private static int PeriodOf(float u, int min, int max) => Math.Min(max, min + (int)(u * (max - min + 1)));

	private static ulong CellHash(int row, int col) => Mix(((ulong)(uint)row << 32) | (uint)col);

	// SplitMix64 (как Goals.Rng): финализатор.
	private static ulong Mix(ulong x)
	{
		x += 0x9E3779B97F4A7C15UL;
		x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
		x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
		return x ^ (x >> 31);
	}

	// Следующее равномерное число 0..1 из потока хеша (состояние продвигается).
	private static float U(ref ulong state)
	{
		state = Mix(state);
		return (state >> 40) / (float)(1UL << 24);
	}
}
