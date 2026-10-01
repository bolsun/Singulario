using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// Типы звёзд и рецепты (T027, GDD «Производство → Роли звёзд») — чистые данные
// без Godot. У звезды нет тира: цвет = тип = размер (0 Ж печь, 1 К фабрика,
// 2 С сверхгигант). Таблица читается из JSON (res://Data/recipes.json, читает
// NucleusLayer, F9 — перечитать); ошибка — встроенная таблица DefaultJson.
//
// Выбор рецепта (StarChoice): Auto — звезда сама по входу (печь, правило — Star),
// Player — игрок (ЛКМ перебирает доступные рецепты своего типа), None — рецептов нет.
// Порядок рецептов в файле — приоритет печи и порядок перебора у фабрики.

public enum IngredientKind { Particle, Atom }

// Particle: Id — цвет частицы (RingSlot.ColorTier: 0 Ж, 1 К, 2 С).
// Atom: Id — тир атома-предмета (CoreTier 0..2).
public readonly record struct Ingredient(IngredientKind Kind, int Id, int Count)
{
	// Ячейка буфера звезды: частица 0..2, атом 3..5.
	public int Slot => StarCatalog.SlotOf(Kind, Id);
}

// Результат рецепта: атом-предмет (едет по линии) или звезда-предмет (T011) —
// по дыркам не едет, только в выходной буфер и оттуда в инвентарь.
public enum RecipeResult { Atom, Star }

public enum StarChoice { Auto, Player, None }

public sealed record StarType(int Type, string Name, int Size, int OutputCapacity, StarChoice Choice);

// Id — стабильное имя (по нему рецепты открываются заданиями и сохраняются).
// ResultId — тир атома (Atom) или тип звезды (Star).
public sealed class StarRecipe
{
	public string Id { get; }
	public string Name { get; }
	public int Producer { get; }
	public int Ticks { get; }
	public RecipeResult Kind { get; }
	public int ResultId { get; }
	public int ResultHoles { get; }
	public Ingredient[] Ingredients { get; }
	// Сколько нужно по ячейкам буфера (StarCatalog.SlotCount).
	public int[] Need { get; }
	// Стартовый предмет (для Auto): атом-основа, иначе частица; -1 — нет.
	public int StarterSlot { get; }

	public StarRecipe(string id, string name, int producer, int ticks, RecipeResult kind, int resultId, int resultHoles, Ingredient[] ingredients)
	{
		Id = id; Name = name; Producer = producer; Ticks = ticks;
		Kind = kind; ResultId = resultId; ResultHoles = resultHoles; Ingredients = ingredients;
		Need = new int[StarCatalog.SlotCount];
		StarterSlot = -1;
		foreach (var ing in ingredients)
		{
			Need[ing.Slot] += ing.Count;
			if (ing.Kind == IngredientKind.Atom) StarterSlot = ing.Slot;
		}
		if (StarterSlot < 0 && ingredients.Length > 0) StarterSlot = ingredients[0].Slot;
	}
}

// Предмет выходного буфера звезды — целый код (так он и сохраняется):
// 0..StarBase-1 — атом тира, StarBase + тип — звезда типа (T011).
public static class StarItem
{
	public const int StarBase = 16;

	public static int Of(StarRecipe recipe) =>
		recipe.Kind == RecipeResult.Star ? StarBase + recipe.ResultId : recipe.ResultId;
	public static bool IsStar(int code) => code >= StarBase;
	public static int Tier(int code) => IsStar(code) ? code - StarBase : code;
}

public sealed class StarCatalog
{
	// Типы звёзд 0 Ж, 1 К, 2 С — все три обязательны (инвентарь, кнопки).
	public const int TypeCount = 3;
	// Цвета частиц и тиры атомов-предметов в ингредиентах: 0..2.
	public const int ColorCount = 3;
	public const int SlotCount = 2 * ColorCount;
	public const int MaxSize = 9;

	public static int SlotOf(IngredientKind kind, int id) => (int)kind * ColorCount + id;

	public IReadOnlyList<StarType> Types { get; }
	public IReadOnlyList<StarRecipe> Recipes { get; }

	private StarCatalog(StarType[] types, StarRecipe[] recipes)
	{
		Types = types;
		Recipes = recipes;
	}

	public StarType TypeOf(int type) => Types[Math.Clamp(type, 0, TypeCount - 1)];
	public static bool IsType(int type) => type >= 0 && type < TypeCount;

	public int IndexOf(string id)
	{
		if (id == null) return -1;
		for (int i = 0; i < Recipes.Count; i++) if (Recipes[i].Id == id) return i;
		return -1;
	}

	public StarRecipe Get(string id)
	{
		int i = IndexOf(id);
		return i >= 0 ? Recipes[i] : null;
	}

	public IEnumerable<StarRecipe> RecipesOf(int type)
	{
		foreach (var r in Recipes) if (r.Producer == type) yield return r;
	}

	// Рецепт новой фабрики: первый доступный своего типа, иначе первый своего
	// типа (рецепты ещё не открыты — ждёт); у остальных типов — null.
	public string DefaultRecipe(int type, Func<StarRecipe, bool> available)
	{
		if (TypeOf(type).Choice != StarChoice.Player) return null;
		string first = null;
		foreach (var r in RecipesOf(type))
		{
			first ??= r.Id;
			if (available(r)) return r.Id;
		}
		return first;
	}

	// Следующий после current доступный рецепт своего типа по кругу; null — других нет.
	public string NextRecipe(int type, string current, Func<StarRecipe, bool> available)
	{
		var own = new List<StarRecipe>(RecipesOf(type));
		int at = own.FindIndex(r => r.Id == current);
		for (int step = 1; step <= own.Count; step++)
		{
			var r = own[((at + step) % own.Count + own.Count) % own.Count];
			if (r.Id != current && available(r)) return r.Id;
		}
		return null;
	}

	// Старый формат сохранения (до T027): индекс рецепта в прежней таблице.
	public static readonly string[] LegacyRecipeIds = { "atom_y", "atom_r", "atom_b", "star_y" };

	// --- разбор ---

	private static StarCatalog _default;
	public static StarCatalog Default => _default ??= Parse(DefaultJson, out _);

	// null — ошибка в error.
	public static StarCatalog Parse(string json, out string error)
	{
		FileData data;
		try
		{
			data = JsonSerializer.Deserialize<FileData>(json, Options);
		}
		catch (Exception e)
		{
			error = e.Message;
			return null;
		}
		if (data == null) { error = "пустой JSON."; return null; }
		return Build(data, out error);
	}

	private static StarCatalog Build(FileData data, out string error)
	{
		error = null;
		var types = new StarType[TypeCount];
		foreach (var t in data.StarTypes ?? new List<TypeData>())
		{
			if (t == null) continue;
			if (!IsType(t.Type)) { error = $"тип звезды {t.Type} вне 0..{TypeCount - 1}."; return null; }
			if (types[t.Type] != null) { error = $"тип звезды {t.Type} задан дважды."; return null; }
			if (t.Size < 1 || t.Size > MaxSize || t.Size % 2 == 0) { error = $"тип {t.Type}: Size {t.Size} — нужен нечётный 1..{MaxSize}."; return null; }
			if (t.OutputCapacity < 1) { error = $"тип {t.Type}: OutputCapacity {t.OutputCapacity} < 1."; return null; }
			types[t.Type] = new StarType(t.Type, t.Name ?? "", t.Size, t.OutputCapacity, t.Choice);
		}
		for (int i = 0; i < TypeCount; i++)
			if (types[i] == null) { error = $"нет типа звезды {i}."; return null; }

		var recipes = new List<StarRecipe>();
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var r in data.Recipes ?? new List<RecipeData>())
		{
			if (r == null) continue;
			string where = $"рецепт «{r.Id}»";
			if (string.IsNullOrEmpty(r.Id)) { error = "рецепт без Id."; return null; }
			if (!ids.Add(r.Id)) { error = $"{where} задан дважды."; return null; }
			if (!IsType(r.Producer)) { error = $"{where}: Producer {r.Producer} вне 0..{TypeCount - 1}."; return null; }
			var choice = types[r.Producer].Choice;
			if (choice == StarChoice.None) { error = $"{where}: тип {r.Producer} рецептов не делает (Choice None)."; return null; }
			if (r.Ticks < 1) { error = $"{where}: Ticks {r.Ticks} < 1."; return null; }
			if (r.Result == null) { error = $"{where}: нет Result."; return null; }
			int resultId;
			int holes = 0;
			if (r.Result.Kind == RecipeResult.Atom)
			{
				resultId = r.Result.Tier;
				if (resultId < 0 || resultId >= ColorCount) { error = $"{where}: тир атома {resultId} вне 0..{ColorCount - 1}."; return null; }
				holes = r.Result.Holes;
				if (holes != 2 && holes != 4 && holes != 8) { error = $"{where}: Holes {holes} — нужно 2, 4 или 8."; return null; }
			}
			else
			{
				resultId = r.Result.Type;
				if (!IsType(resultId)) { error = $"{where}: тип звезды {resultId} вне 0..{TypeCount - 1}."; return null; }
			}
			var list = r.Ingredients ?? new List<IngredientData>();
			if (list.Count == 0) { error = $"{where}: нет ингредиентов."; return null; }
			var ings = new Ingredient[list.Count];
			var seen = new bool[SlotCount];
			int atomKinds = 0, particleKinds = 0;
			for (int i = 0; i < list.Count; i++)
			{
				var g = list[i];
				if (g == null) { error = $"{where}: пустой ингредиент."; return null; }
				if (g.Id < 0 || g.Id >= ColorCount) { error = $"{where}: ингредиент {g.Kind} {g.Id} вне 0..{ColorCount - 1}."; return null; }
				if (g.Count < 1) { error = $"{where}: Count {g.Count} < 1."; return null; }
				var ing = new Ingredient(g.Kind, g.Id, g.Count);
				if (seen[ing.Slot]) { error = $"{where}: ингредиент {g.Kind} {g.Id} указан дважды."; return null; }
				seen[ing.Slot] = true;
				if (g.Kind == IngredientKind.Atom) atomKinds++; else particleKinds++;
				if (choice == StarChoice.Auto && g.Kind == IngredientKind.Atom && g.Count != 1)
				{ error = $"{where}: у печи атом-основа только один (Count 1)."; return null; }
				ings[i] = ing;
			}
			if (choice == StarChoice.Auto && atomKinds > 1) { error = $"{where}: у печи не больше одного атома-основы."; return null; }
			if (choice == StarChoice.Auto && atomKinds == 0 && particleKinds != 1)
			{ error = $"{where}: у рецепта печи без атома-основы — ровно одна частица (стартовый предмет)."; return null; }
			if (choice == StarChoice.Player && particleKinds > 0) { error = $"{where}: фабрика частиц не берёт."; return null; }
			recipes.Add(new StarRecipe(r.Id, r.Name ?? r.Id, r.Producer, r.Ticks, r.Result.Kind, resultId, holes, ings));
		}
		foreach (var t in types)
		{
			if (t.Choice == StarChoice.None) continue;
			bool any = false;
			foreach (var r in recipes) if (r.Producer == t.Type) { any = true; break; }
			if (!any) { error = $"тип {t.Type} ({t.Choice}) без рецептов."; return null; }
		}
		return new StarCatalog(types, recipes.ToArray());
	}

	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		Converters = { new JsonStringEnumConverter() },
	};

	private sealed class FileData
	{
		public List<TypeData> StarTypes { get; set; }
		public List<RecipeData> Recipes { get; set; }
	}

	private sealed class TypeData
	{
		public int Type { get; set; }
		public string Name { get; set; }
		public int Size { get; set; }
		public int OutputCapacity { get; set; }
		public StarChoice Choice { get; set; }
	}

	private sealed class RecipeData
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public int Producer { get; set; }
		public int Ticks { get; set; }
		public ResultData Result { get; set; }
		public List<IngredientData> Ingredients { get; set; }
	}

	private sealed class ResultData
	{
		public RecipeResult Kind { get; set; }
		public int Tier { get; set; }
		public int Holes { get; set; }
		public int Type { get; set; }
	}

	private sealed class IngredientData
	{
		public IngredientKind Kind { get; set; }
		public int Id { get; set; }
		public int Count { get; set; }
	}

	// Встроенная таблица (= Data/recipes.json на момент T027) — образец формата.
	// Составы звёзд К и С — заглушки до решения геймдизайнера.
	public const string DefaultJson = """
{
  "StarTypes": [
    { "Type": 0, "Name": "печь",        "Size": 3, "OutputCapacity": 10, "Choice": "Auto"   },
    { "Type": 1, "Name": "фабрика",     "Size": 5, "OutputCapacity": 10, "Choice": "Player" },
    { "Type": 2, "Name": "сверхгигант", "Size": 7, "OutputCapacity": 10, "Choice": "None"   }
  ],
  "Recipes": [
    { "Id": "atom_y", "Name": "атом Ж", "Producer": 0, "Ticks": 256,
      "Result": { "Kind": "Atom", "Tier": 0, "Holes": 8 },
      "Ingredients": [ { "Kind": "Particle", "Id": 0, "Count": 8 } ] },
    { "Id": "atom_r", "Name": "атом К", "Producer": 0, "Ticks": 256,
      "Result": { "Kind": "Atom", "Tier": 1, "Holes": 8 },
      "Ingredients": [ { "Kind": "Atom", "Id": 0, "Count": 1 }, { "Kind": "Particle", "Id": 1, "Count": 8 } ] },
    { "Id": "atom_b", "Name": "атом С", "Producer": 0, "Ticks": 256,
      "Result": { "Kind": "Atom", "Tier": 2, "Holes": 8 },
      "Ingredients": [ { "Kind": "Atom", "Id": 0, "Count": 1 }, { "Kind": "Particle", "Id": 2, "Count": 8 } ] },
    { "Id": "star_y", "Name": "звезда Ж", "Producer": 1, "Ticks": 256,
      "Result": { "Kind": "Star", "Type": 0 },
      "Ingredients": [ { "Kind": "Atom", "Id": 0, "Count": 8 } ] },
    { "Id": "star_r", "Name": "звезда К", "Producer": 1, "Ticks": 256,
      "Result": { "Kind": "Star", "Type": 1 },
      "Ingredients": [ { "Kind": "Atom", "Id": 0, "Count": 4 }, { "Kind": "Atom", "Id": 1, "Count": 4 } ] },
    { "Id": "star_b", "Name": "звезда С", "Producer": 1, "Ticks": 256,
      "Result": { "Kind": "Star", "Type": 2 },
      "Ingredients": [ { "Kind": "Atom", "Id": 0, "Count": 4 }, { "Kind": "Atom", "Id": 1, "Count": 4 }, { "Kind": "Atom", "Id": 2, "Count": 4 } ] }
  ]
}
""";
}
