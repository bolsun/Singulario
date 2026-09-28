// Атом — предмет слоя 2: до 8 частиц с сохранённым составом (цвет каждой
// частицы, в порядке поступления). Чистые данные без Godot: 8 байт цвета в
// одном ulong + счётчик — структура копируется целиком, без аллокаций.
//
// Один и тот же тип служит и готовым атомом (Count == Size), и буфером порта:
// выходной порт набирает в него частицы (Push), входной отдаёт их по одной
// в том же порядке (PopFront).
public struct Atom
{
	public const int Size = 8;

	public ulong Colors; // байт i — цвет i-й частицы (0..255)
	public int Count;    // сколько частиц сейчас внутри (0..Size)

	public readonly bool IsFull => Count >= Size;
	public readonly bool IsEmpty => Count <= 0;

	public readonly int ColorAt(int i) => (int)((Colors >> (8 * i)) & 0xFF);

	public void Push(int color)
	{
		if (IsFull) return;
		Colors |= (ulong)(byte)color << (8 * Count);
		Count++;
	}

	public int PopFront()
	{
		if (IsEmpty) return -1;
		int color = ColorAt(0);
		Colors >>= 8;
		Count--;
		return color;
	}
}
