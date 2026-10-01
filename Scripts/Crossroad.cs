// Перекрёсток (T010, GDD «Элементы набора → Перекрёсток»): режим атома-переносчика
// «орбитали». Вместо кольца в плоскости экрана — два вертикальных кольца: ось E–W
// (Axis 0) и ось N–S (Axis 1). Частица, вошедшая с одной стороны оси, перелетает
// над центром на противоположную сторону за полоборота (ExitPos = 4 шага тира) и
// там ждёт, пока её заберут. Оси не смешиваются.
//
// Чистые данные без Godot, только целые числа. Шаги (Step) вызывает владелец на
// тике поворота тира атома — как снятие блокировки у обычного кольца.
//
// Стороны: 0 N, 1 E, 2 S, 3 W (компас Adj8 / 2).
public struct CrossParticle
{
	public int ColorTier; // цвет частицы или тир атома-предмета
	public bool IsItem;   // атом-предмет (T007)
	public int Pos;       // 0 — только вошла (вход занят), ExitPos — у выхода, можно отдавать
	public byte Variant;  // T024: только вид (форма осколка), на законы не влияет
}

public sealed class CrossAxis
{
	public const int MaxCapacity = 4;

	// Направление движения, пока на оси есть частицы: +1 — от W к E (ось 0) или
	// от N к S (ось 1), -1 — обратно. Встречная частица не входит, пока ось не
	// опустеет (встречный поток игрок разруливает сам).
	public int Dir;
	// Очередь: [0] — голова (ближе всех к выходу), [Count-1] — хвост.
	public readonly CrossParticle[] Items = new CrossParticle[MaxCapacity];
	public int Count;
}

public sealed class Crossroad
{
	// Полоборота кольца: вход (0) → выход (4) за 4 шага тира.
	public const int ExitPos = 4;

	public readonly CrossAxis[] Axes = { new CrossAxis(), new CrossAxis() };

	// Сколько частиц одновременно на оси — дырки атома / 2 (1/2/4): поток оси
	// равен потоку обычного атома с теми же дырками.
	public readonly int Capacity;

	public Crossroad(int holeCount)
	{
		Capacity = System.Math.Clamp(holeCount / 2, 1, CrossAxis.MaxCapacity);
	}

	public static int AxisOf(int side) => side % 2 == 0 ? 1 : 0;

	// Направление оси для частицы, вошедшей со стороны side.
	private static int DirFromEntry(int side) => side switch
	{
		0 => 1,  // с севера — на юг
		1 => -1, // с востока — на запад
		2 => -1, // с юга — на север
		_ => 1,  // с запада — на восток
	};

	// Сторона выхода оси axis при направлении dir.
	public static int ExitSide(int axis, int dir) =>
		axis == 0 ? (dir > 0 ? 1 : 3) : (dir > 0 ? 2 : 0);

	public bool IsEmpty => Axes[0].Count == 0 && Axes[1].Count == 0;

	// Есть ли место для частицы, входящей со стороны side.
	public bool CanEnter(int side)
	{
		var axis = Axes[AxisOf(side)];
		if (axis.Count == 0) return true;
		if (axis.Count >= Capacity || axis.Dir != DirFromEntry(side)) return false;
		return axis.Items[axis.Count - 1].Pos > 0; // вход ещё занят вошедшей на этом шаге
	}

	public void Enter(int side, int colorTier, bool isItem, byte variant = 0)
	{
		var axis = Axes[AxisOf(side)];
		if (axis.Count == 0) axis.Dir = DirFromEntry(side);
		axis.Items[axis.Count++] = new CrossParticle { ColorTier = colorTier, IsItem = isItem, Pos = 0, Variant = variant };
	}

	// Частица у выхода на стороне side, готовая уйти.
	public bool TryPeekExit(int side, out CrossParticle particle)
	{
		var axis = Axes[AxisOf(side)];
		if (axis.Count > 0 && ExitSide(AxisOf(side), axis.Dir) == side && axis.Items[0].Pos == ExitPos)
		{
			particle = axis.Items[0];
			return true;
		}
		particle = default;
		return false;
	}

	public void TakeExit(int side)
	{
		var axis = Axes[AxisOf(side)];
		for (int i = 1; i < axis.Count; i++) axis.Items[i - 1] = axis.Items[i];
		axis.Count--;
		if (axis.Count == 0) axis.Dir = 0;
	}

	// Шаг тира: каждая частица продвигается на позицию, если следующая свободна
	// (голова — до выхода, остальные — до позиции перед идущей впереди).
	public void Step()
	{
		foreach (var axis in Axes)
		{
			for (int i = 0; i < axis.Count; i++)
			{
				int limit = i == 0 ? ExitPos : axis.Items[i - 1].Pos - 1;
				if (axis.Items[i].Pos < limit) axis.Items[i].Pos++;
			}
		}
	}

	public void Clear()
	{
		foreach (var axis in Axes) { axis.Count = 0; axis.Dir = 0; }
	}
}
