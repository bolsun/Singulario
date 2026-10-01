// Перекрёсток (T010, механика T032 «тик-так», GDD «Элементы набора → Перекрёсток»):
// режим атома-переносчика. Вместо кольца — две оси: E–W (Axis 0) и N–S (Axis 1).
// Оси не смешиваются.
//
// Выходы постоянные и задаются игроком (OutRot): две соседние стороны, по одной на ось.
// OutRot 0 = E,S; 1 = S,W; 2 = W,N; 3 = N,E. Противоположная выходу сторона оси — вход.
// Вход только принимает, выход только отдаёт. R (RotateOutputs) — оба выхода по часовой;
// за одно нажатие роли сторон меняются ровно у одной оси.
//
// Частица хранится на физической стороне, где лежит (не «вход/выход»), поэтому R ничего
// не переносит: частица бывшего выхода оказывается на входе и перейдёт при следующем
// перевороте, частица бывшего входа — на выходе и готова уйти. Без потерь и дублей.
//
// На оси 2 позиции — вход и выход. Переворот оси (Flip) раз в P = 8·T / дырки тиков
// (T — период поворота тира; 2/4/8 дырок → 4T/2T/T): частица входа переходит на выход,
// если выход пуст, иначе ничего. Поток оси = поток обычной линии с теми же дырками
// (дырки / (8T) частиц за тик). Оси в противофазе: E–W при tick mod P == 0, N–S при
// tick mod P == P/2 (AxisDue). Фаза не хранится — чистая функция глобального тика.
//
// Чистые данные без Godot, только целые числа. EnterTick и OutFreedTick — только для
// вида (анимация «тик-так»), на законы не влияют, в хеш и сохранение не входят.
//
// Стороны: 0 N, 1 E, 2 S, 3 W (компас Adj8 / 2).
public struct CrossParticle
{
	public int ColorTier;  // цвет частицы или тир атома-предмета
	public bool IsItem;    // атом-предмет (T007)
	public byte Variant;   // T024: только вид (форма осколка), на законы не влияет
	public long EnterTick; // T032: только вид — тик, с которого частица стоит на этой стороне
}

public sealed class Crossroad
{
	public const int SideCount = 4;

	public readonly int HoleCount;

	// Ориентация выходов 0..3 (см. шапку).
	public int OutRot;

	// Частица на стороне side — если бит side в OccupiedMask.
	public readonly CrossParticle[] Sides = new CrossParticle[SideCount];
	public int OccupiedMask;

	// T032: только вид — тик, когда выход оси освободился (начало движения частицы входа).
	public readonly long[] OutFreedTick = new long[2];

	public Crossroad(int holeCount, int outRot = 0)
	{
		HoleCount = holeCount;
		OutRot = outRot & 3;
	}

	public static int AxisOf(int side) => side % 2 == 0 ? 1 : 0;

	// Период переворота оси: 8·T / дырки.
	public static int Period(int tierTicks, int holeCount) =>
		holeCount > 0 ? 8 * tierTicks / holeCount : 8 * tierTicks;

	// Ось, которая переворачивается на тике globalTick: 0 (E–W), 1 (N–S) или -1.
	public static int AxisDue(long globalTick, int period)
	{
		if (period <= 0) return -1;
		long m = globalTick % period;
		if (m < 0) m += period;
		if (m == 0) return 0;
		if (period % 2 == 0 && m == period / 2) return 1;
		return -1;
	}

	// Сдвиг событий оси внутри периода: E–W — 0, N–S — P/2.
	public static int AxisOffset(int axis, int period) => axis == 0 ? 0 : period / 2;

	// Маска сторон-выходов (бит side): OutRot 0 → E,S, далее по часовой.
	public static int OutputMaskOf(int outRot) => (1 << ((1 + outRot) & 3)) | (1 << ((2 + outRot) & 3));
	public int OutputMask => OutputMaskOf(OutRot);

	public bool IsOutput(int side) => (OutputMask & (1 << side)) != 0;

	// Сторона-выход и сторона-вход оси.
	public int OutputSide(int axis)
	{
		int a = axis == 0 ? 1 : 0; // E или N
		return IsOutput(a) ? a : a + 2;
	}
	public int InputSide(int axis) => (OutputSide(axis) + 2) & 3;

	public bool Has(int side) => (OccupiedMask & (1 << side)) != 0;
	public bool IsEmpty => OccupiedMask == 0;

	// Можно ли принять частицу со стороны side: это вход и он пуст.
	public bool CanEnter(int side) => !IsOutput(side) && !Has(side);

	public void Enter(int side, int colorTier, bool isItem, byte variant, long enterTick)
	{
		Sides[side] = new CrossParticle { ColorTier = colorTier, IsItem = isItem, Variant = variant, EnterTick = enterTick };
		OccupiedMask |= 1 << side;
	}

	// Частица на выходе side, готовая уйти.
	public bool TryPeekOut(int side, out CrossParticle particle)
	{
		if (IsOutput(side) && Has(side))
		{
			particle = Sides[side];
			return true;
		}
		particle = default;
		return false;
	}

	public void TakeOut(int side, long tick)
	{
		OccupiedMask &= ~(1 << side);
		OutFreedTick[AxisOf(side)] = tick;
	}

	// Переворот оси: частица входа → на выход, если выход пуст. true — что-то перешло.
	public bool Flip(int axis, long tick)
	{
		int input = InputSide(axis), output = OutputSide(axis);
		if (!Has(input) || Has(output)) return false;
		var p = Sides[input];
		p.EnterTick = tick;
		Sides[output] = p;
		OccupiedMask = (OccupiedMask & ~(1 << input)) | (1 << output);
		return true;
	}

	// R: оба выхода по часовой. Частицы остаются на своих сторонах; у оси, сменившей
	// роли, сбрасываются поля вида (частица нового входа стартует с места).
	public void RotateOutputs(long tick)
	{
		int before = OutputMask;
		OutRot = (OutRot + 1) & 3;
		for (int axis = 0; axis < 2; axis++)
		{
			if ((before & (1 << OutputSide(axis))) != 0) continue; // роли оси не поменялись
			OutFreedTick[axis] = tick;
			for (int s = axis == 0 ? 1 : 0; s < SideCount; s += 2)
				Sides[s].EnterTick = tick;
		}
	}

	public void Clear() => OccupiedMask = 0;
}
