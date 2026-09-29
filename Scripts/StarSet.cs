using System;
using System.Collections.Generic;

// Звезда-сборщик (T006, GDD «Производство → Звезда — сборщик») — чистые данные
// без Godot. Звезда — объект слоя 1, квадрат Size×Size клеток, (Row, Col) —
// верхняя левая клетка. Вокруг — 12 клеток подвода (по 3 на сторону, без
// углов): из атомов в них звезда берёт частицы, в них же ей доставляют
// атомы-ингредиенты (правила — в NucleusLayer, как у горизонта ЧД). Выход —
// средняя клетка одной из сторон (OutputSide), туда кладётся готовый атом грузом.
//
// Производство: ингредиенты набираются в буфер (на 1 рецепт); полный буфер,
// пока звезда не занята, уходит в работу; работа длится Duration тиков; готовый
// атом ждёт внутри, пока клетка выхода занята. Пока идёт работа (или ждёт выход),
// буфер набирается на следующий рецепт.

public enum IngredientKind { Particle, Atom }

// Particle: Id — цвет частицы (RingSlot.ColorTier: 0 Ж, 1 К, 2 С).
// Atom: Id — тир атома-переносчика (CoreTier).
public readonly record struct Ingredient(IngredientKind Kind, int Id, int Count);

public sealed record StarRecipe(string Name, int ResultTier, int ResultHoles, Ingredient[] Ingredients);

// Рецепты — данные в одном месте. Любая звезда может делать любой рецепт.
public static class StarRecipes
{
	public static readonly StarRecipe[] All =
	{
		new("атом Ж", 0, 8, new[] { new Ingredient(IngredientKind.Particle, 0, 8) }),
		new("атом К", 1, 8, new[] { new Ingredient(IngredientKind.Atom, 0, 1), new Ingredient(IngredientKind.Particle, 1, 8) }),
		new("атом С", 2, 8, new[] { new Ingredient(IngredientKind.Atom, 0, 1), new Ingredient(IngredientKind.Particle, 2, 8) }),
	};

	public static int Count => All.Length;
}

public sealed class Star
{
	public const int Size = 3;
	// Стороны выхода: 0 N, 1 E, 2 S, 3 W (R — следующая по часовой).
	public const int SideCount = 4;
	public const int DefaultOutputSide = 2;

	public readonly int Row, Col, Tier;
	public int Recipe { get; private set; }
	public int OutputSide;
	// Набрано в буфер по каждому ингредиенту рецепта (индексы — как в Ingredients).
	public int[] Buffer { get; private set; }
	public bool Producing;
	public int Elapsed; // тиков работы; == Duration — готово, ждёт выход

	public Star(int row, int col, int tier, int recipe)
	{
		Row = row; Col = col; Tier = tier;
		SetRecipe(recipe);
		OutputSide = DefaultOutputSide;
	}

	public StarRecipe RecipeData => StarRecipes.All[Recipe];

	// Смена рецепта сжигает буфер и работу. true — что-то сгорело.
	public bool SetRecipe(int recipe)
	{
		bool burned = Producing || HasBuffered;
		Recipe = ((recipe % StarRecipes.Count) + StarRecipes.Count) % StarRecipes.Count;
		Buffer = new int[RecipeData.Ingredients.Length];
		Producing = false;
		Elapsed = 0;
		return burned;
	}

	public bool HasBuffered
	{
		get
		{
			if (Buffer == null) return false;
			foreach (int b in Buffer) if (b > 0) return true;
			return false;
		}
	}

	public bool BufferFull
	{
		get
		{
			var ing = RecipeData.Ingredients;
			for (int i = 0; i < ing.Length; i++) if (Buffer[i] < ing[i].Count) return false;
			return true;
		}
	}

	// Индекс ингредиента, которому нужен ещё один такой предмет, или -1.
	public int NeedIndex(IngredientKind kind, int id)
	{
		var ing = RecipeData.Ingredients;
		for (int i = 0; i < ing.Length; i++)
			if (ing[i].Kind == kind && ing[i].Id == id && Buffer[i] < ing[i].Count) return i;
		return -1;
	}

	public void Put(int index) => Buffer[index]++;

	// Для загрузки: восстановить буфер (лишнее обрезается по рецепту).
	public void RestoreBuffer(IReadOnlyList<int> counts)
	{
		var ing = RecipeData.Ingredients;
		for (int i = 0; i < ing.Length && counts != null && i < counts.Count; i++)
			Buffer[i] = Math.Clamp(counts[i], 0, ing[i].Count);
	}

	public int Duration(int recipeTicks, int[] speedByTier)
	{
		int speed = Tier >= 0 && Tier < speedByTier.Length ? speedByTier[Tier] : 1;
		return Math.Max(1, recipeTicks / Math.Max(1, speed));
	}

	// Полный буфер уходит в работу, если звезда свободна.
	public bool TryStart()
	{
		if (Producing || !BufferFull) return false;
		Array.Clear(Buffer);
		Producing = true;
		Elapsed = 0;
		return true;
	}

	// Один тик. true — работа готова (ждёт выход).
	public bool Advance(int duration)
	{
		TryStart();
		if (!Producing) return false;
		if (Elapsed < duration) Elapsed++;
		return Elapsed >= duration;
	}

	// Готовый атом выложен — звезда свободна, буфер сразу уходит в работу.
	public void FinishOutput()
	{
		Producing = false;
		Elapsed = 0;
		TryStart();
	}

	public bool ContainsCell(int row, int col) =>
		row >= Row && row < Row + Size && col >= Col && col < Col + Size;

	public bool Overlaps(int row, int col, int size) =>
		row < Row + Size && Row < row + size && col < Col + Size && Col < col + size;

	public (int row, int col) OutputCell => SideMiddle(OutputSide);

	public (int row, int col) SideMiddle(int side) => side switch
	{
		0 => (Row - 1, Col + Size / 2),
		1 => (Row + Size / 2, Col + Size),
		2 => (Row + Size, Col + Size / 2),
		_ => (Row + Size / 2, Col - 1),
	};

	// 12 клеток подвода по порядку (N слева направо, E сверху вниз, S, W);
	// k — сторона света (компас Adj8 NucleusLayer) из клетки на звезду.
	public IEnumerable<(int row, int col, int k)> RingCells()
	{
		for (int i = 0; i < Size; i++) yield return (Row - 1, Col + i, 4);
		for (int i = 0; i < Size; i++) yield return (Row + i, Col + Size, 6);
		for (int i = 0; i < Size; i++) yield return (Row + Size, Col + i, 0);
		for (int i = 0; i < Size; i++) yield return (Row + i, Col - 1, 2);
	}

	public bool IsRingCell(int row, int col)
	{
		bool inRows = row >= Row && row < Row + Size;
		bool inCols = col >= Col && col < Col + Size;
		return (inRows && (col == Col - 1 || col == Col + Size))
			|| (inCols && (row == Row - 1 || row == Row + Size));
	}
}

// Все звёзды поля в порядке (Row, Col) — обход всегда детерминирован.
public sealed class StarSet
{
	private readonly List<Star> _stars = new();

	// Меняется при установке/удалении — для кэшей отрисовки.
	public int Version { get; private set; }
	public int Count => _stars.Count;
	public IReadOnlyList<Star> All => _stars;

	public bool Overlaps(int row, int col, int size)
	{
		foreach (var s in _stars) if (s.Overlaps(row, col, size)) return true;
		return false;
	}

	public bool TryGetAt(int row, int col, out Star star)
	{
		foreach (var s in _stars)
			if (s.ContainsCell(row, col)) { star = s; return true; }
		star = null;
		return false;
	}

	public bool Add(Star star)
	{
		if (Overlaps(star.Row, star.Col, Star.Size)) return false;
		int i = 0;
		while (i < _stars.Count && (_stars[i].Row < star.Row || (_stars[i].Row == star.Row && _stars[i].Col < star.Col))) i++;
		_stars.Insert(i, star);
		Version++;
		return true;
	}

	public bool Remove(Star star)
	{
		if (!_stars.Remove(star)) return false;
		Version++;
		return true;
	}

	public void Clear()
	{
		_stars.Clear();
		Version++;
	}
}
