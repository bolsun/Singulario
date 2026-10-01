using System;
using System.Collections.Generic;

// Звезда (T006, T027; GDD «Производство → Роли звёзд») — чистые данные без
// Godot. Звезда — объект слоя 1, квадрат Size×Size клеток (Size — из типа,
// нечётный), (Row, Col) — верхняя левая клетка. Тип (0 Ж печь, 1 К фабрика,
// 2 С сверхгигант) и рецепты — StarCatalog. Вокруг — 4 × Size клеток подвода
// (без углов): из атомов в них звезда берёт частицы и атомы-предметы (правила
// приёма — Accepts, обход — NucleusLayer, как у горизонта ЧД). Выход — средняя
// клетка одной из сторон (OutputSide).
//
// Буфер ингредиентов — счётчики по виду предмета (частица 0..2, атом 0..2),
// на 1 рецепт. Рецепт: у фабрики (Player) — выбранный игроком (RecipeId), у
// печи (Auto) — по входу (правило — Accepts/TryStart), у С (None) — нет.
// Набранный рецепт, пока звезда не занята, уходит в работу (ActiveRecipe);
// работа длится Ticks рецепта; готовый предмет — в выходной буфер (T008), если
// там есть место (ёмкость — из типа), иначе ждёт внутри — звезда стоит. Пока
// идёт работа, буфер набирается на следующий рецепт.

public sealed class Star
{
	// Стороны выхода: 0 N, 1 E, 2 S, 3 W (R — следующая по часовой).
	public const int SideCount = 4;
	public const int DefaultOutputSide = 2;

	public int Row { get; private set; }
	public int Col { get; private set; }
	public readonly int Type;
	public StarCatalog Catalog { get; private set; }
	// Выбранный рецепт фабрики (Player); у остальных типов — null.
	public string RecipeId { get; private set; }
	// Что звезда делает сейчас; null — не работает.
	public StarRecipe ActiveRecipe { get; private set; }
	public int OutputSide;
	// Набрано по ячейкам (StarCatalog.SlotOf: частица 0..2, атом 3..5).
	public readonly int[] Buffer = new int[StarCatalog.SlotCount];
	public int Elapsed; // тиков работы; == Duration — готово, ждёт места в выходном буфере
	// Выходной буфер (T008): коды готовых предметов (StarItem — атом или
	// звезда, T011), голова — самый старый. Смена рецепта его не сжигает.
	public readonly Queue<int> Output = new();

	public Star(int row, int col, int type, StarCatalog catalog, string recipeId)
	{
		Row = row; Col = col; Type = type; Catalog = catalog;
		OutputSide = DefaultOutputSide;
		RecipeId = Choice == StarChoice.Player ? recipeId : null;
	}

	public StarType TypeData => Catalog.TypeOf(Type);
	public int Size => TypeData.Size;
	public StarChoice Choice => TypeData.Choice;
	public int OutputCapacity => TypeData.OutputCapacity;
	public StarRecipe Recipe => RecipeId != null ? Catalog.Get(RecipeId) : null;
	public bool Producing => ActiveRecipe != null;
	public int Duration => ActiveRecipe?.Ticks ?? 1;

	// Смена рецепта фабрики сжигает буфер и работу. true — что-то сгорело.
	public bool SetRecipe(string id)
	{
		bool burned = Producing || HasBuffered;
		RecipeId = Choice == StarChoice.Player ? id : null;
		Burn();
		return burned;
	}

	// Очистка C (T035): входной буфер и текущая работа сгорают; выходной буфер,
	// рецепт и сторона выхода остаются. Атомы буфера вызывающий забирает до вызова.
	// true — было что очищать.
	public bool ClearInput()
	{
		bool had = Producing || HasBuffered;
		Burn();
		return had;
	}

	private void Burn()
	{
		Array.Clear(Buffer);
		ActiveRecipe = null;
		Elapsed = 0;
	}

	public bool HasBuffered
	{
		get
		{
			foreach (int b in Buffer) if (b > 0) return true;
			return false;
		}
	}

	// Примет ли звезда этот предмет (T027). Фабрика — если выбранному рецепту
	// (доступному) его не хватает. Печь — если есть доступный рецепт печи r, у
	// которого (буфер + предмет) ⊆ ингредиенты r и среди них есть стартовый предмет r
	// (атом-основа или частица рецепта без основы): при пустом буфере рецепт
	// начинает только его стартовый предмет. С — ничего.
	public bool Accepts(IngredientKind kind, int id, Func<StarRecipe, bool> available)
	{
		if (id < 0 || id >= StarCatalog.ColorCount) return false;
		int slot = StarCatalog.SlotOf(kind, id);
		switch (Choice)
		{
			case StarChoice.Player:
			{
				var r = Recipe;
				return r != null && available(r) && Buffer[slot] < r.Need[slot];
			}
			case StarChoice.Auto:
				foreach (var r in Catalog.RecipesOf(Type))
					if (available(r) && FitsWith(r, slot)) return true;
				return false;
			default:
				return false;
		}
	}

	private bool FitsWith(StarRecipe r, int slot)
	{
		for (int s = 0; s < Buffer.Length; s++)
			if (Buffer[s] + (s == slot ? 1 : 0) > r.Need[s]) return false;
		return r.StarterSlot >= 0 && (Buffer[r.StarterSlot] > 0 || r.StarterSlot == slot);
	}

	public void Put(IngredientKind kind, int id) => Buffer[StarCatalog.SlotOf(kind, id)]++;

	// Рецепт, которому хватает буфера, или null. Фабрика — выбранный; печь —
	// первый в порядке каталога, у которого буфер совпал с ингредиентами.
	private StarRecipe ReadyRecipe()
	{
		switch (Choice)
		{
			case StarChoice.Player:
			{
				var r = Recipe;
				return r != null && Covers(r) ? r : null;
			}
			case StarChoice.Auto:
				foreach (var r in Catalog.RecipesOf(Type))
					if (Covers(r)) return r;
				return null;
			default:
				return null;
		}
	}

	private bool Covers(StarRecipe r)
	{
		for (int s = 0; s < Buffer.Length; s++) if (Buffer[s] < r.Need[s]) return false;
		return true;
	}

	// Набранный рецепт уходит в работу, если звезда свободна.
	public bool TryStart()
	{
		if (Producing) return false;
		var r = ReadyRecipe();
		if (r == null) return false;
		for (int s = 0; s < Buffer.Length; s++) Buffer[s] -= r.Need[s];
		ActiveRecipe = r;
		Elapsed = 0;
		return true;
	}

	// Один тик. true — работа готова (ждёт выход).
	public bool Advance()
	{
		TryStart();
		if (!Producing) return false;
		if (Elapsed < Duration) Elapsed++;
		return Elapsed >= Duration;
	}

	// Готовая работа — в выходной буфер, если там есть место; звезда свободна,
	// набранный рецепт сразу уходит в работу. true — положено.
	public bool TryFinishToOutput()
	{
		if (!Producing || Elapsed < Duration || Output.Count >= OutputCapacity) return false;
		Output.Enqueue(StarItem.Of(ActiveRecipe));
		ActiveRecipe = null;
		Elapsed = 0;
		TryStart();
		return true;
	}

	// Загрузка сохранения: буфер (лишнее обрезается до 0..Need всех рецептов типа)
	// и работа (рецепт должен принадлежать типу).
	public void RestoreBuffer(IReadOnlyList<int> counts)
	{
		Array.Clear(Buffer);
		if (counts == null || Choice == StarChoice.None) return;
		for (int s = 0; s < Buffer.Length && s < counts.Count; s++)
		{
			int max = 0;
			foreach (var r in Catalog.RecipesOf(Type)) max = Math.Max(max, r.Need[s]);
			Buffer[s] = Math.Clamp(counts[s], 0, max);
		}
	}

	public void RestoreWork(StarRecipe active, int elapsed)
	{
		ActiveRecipe = active != null && active.Producer == Type ? active : null;
		Elapsed = ActiveRecipe != null ? Math.Clamp(elapsed, 0, ActiveRecipe.Ticks) : 0;
	}

	// Перезагрузка каталога (F9, отладка): буфер и работа сгорают, рецепт фабрики
	// — по Id (пропал — null, вызывающий ставит рецепт по умолчанию), выходной
	// буфер обрезается до новой ёмкости (старые остаются), звезда переезжает в
	// (row, col) (центр сохраняется вызывающим).
	public void Rebind(StarCatalog catalog, int row, int col)
	{
		Catalog = catalog;
		Row = row; Col = col;
		Burn();
		var r = Recipe;
		if (Choice != StarChoice.Player || r == null || r.Producer != Type) RecipeId = null;
		if (Output.Count > OutputCapacity)
		{
			var keep = Output.ToArray();
			Output.Clear();
			for (int i = 0; i < OutputCapacity; i++) Output.Enqueue(keep[i]);
		}
	}

	public bool ContainsCell(int row, int col) =>
		row >= Row && row < Row + Size && col >= Col && col < Col + Size;

	public bool Overlaps(int row, int col, int size) =>
		row < Row + Size && Row < row + size && col < Col + Size && Col < col + size;

	public (int row, int col) OutputCell => SideMiddle(OutputSide);

	public (int row, int col) SideMiddle(int side)
	{
		int size = Size;
		return side switch
		{
			0 => (Row - 1, Col + size / 2),
			1 => (Row + size / 2, Col + size),
			2 => (Row + size, Col + size / 2),
			_ => (Row + size / 2, Col - 1),
		};
	}

	// 4 × Size клеток подвода по порядку (N слева направо, E сверху вниз, S, W);
	// k — сторона света (компас Adj8 NucleusLayer) из клетки на звезду.
	public IEnumerable<(int row, int col, int k)> RingCells()
	{
		int size = Size;
		for (int i = 0; i < size; i++) yield return (Row - 1, Col + i, 4);
		for (int i = 0; i < size; i++) yield return (Row + i, Col + size, 6);
		for (int i = 0; i < size; i++) yield return (Row + size, Col + i, 0);
		for (int i = 0; i < size; i++) yield return (Row + i, Col - 1, 2);
	}

	public bool IsRingCell(int row, int col)
	{
		int size = Size;
		bool inRows = row >= Row && row < Row + size;
		bool inCols = col >= Col && col < Col + size;
		return (inRows && (col == Col - 1 || col == Col + size))
			|| (inCols && (row == Row - 1 || row == Row + size));
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
		if (Overlaps(star.Row, star.Col, star.Size)) return false;
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
