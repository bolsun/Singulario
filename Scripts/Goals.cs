using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// Задания ЧД (T011, GDD «Прогрессия → Старт», «Задания ЧД и расширение») —
// чистые данные без Godot. Цепочка этапов читается из JSON (res://Data/goals.json,
// читает NucleusLayer), здесь — формат, состояние и счёт прогресса.
//
// Этап: требования (частицы цвета Id × Count или атомы тира Id × Count — все
// пункты должны быть выполнены) и награда (кольцо чанков, шаблоны в новом
// кольце, открытые рецепты). Прогресс считается от счётчиков ЧД (BlackHoleSet)
// с начала этапа: при старте этапа запоминаются текущие значения (Baseline),
// прогресс = счётчик − Baseline. Лишнее ЧД принимает как обычно.
// Награды выдаёт NucleusLayer (CompleteGoalStage), здесь только состояние.

public enum GoalKind { Particle, Atom }

public sealed class GoalRequirement
{
	public GoalKind Kind { get; set; }
	// Particle — цвет частицы (0 Ж, 1 К, 2 С); Atom — тир атома (CoreTier).
	public int Id { get; set; }
	public long Count { get; set; }
}

public sealed class GoalReward
{
	// Открыть следующее кольцо чанков вокруг открытой зоны.
	public bool Ring { get; set; } = true;
	// Шаблоны (пути res://) — в случайные свободные чанки нового кольца.
	public List<string> Templates { get; set; } = new();
	// Открываемые рецепты звезды (StarRecipe.Id).
	public List<string> Recipes { get; set; } = new();
}

public sealed class GoalStage
{
	public string Name { get; set; } = "";
	public List<GoalRequirement> Requirements { get; set; } = new();
	public GoalReward Reward { get; set; } = new();
}

public sealed class GoalChainData
{
	// Seed новой игры (размещение шаблонов наград). Сохраняется в игре.
	public ulong Seed { get; set; } = 1;
	public List<GoalStage> Stages { get; set; } = new();

	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		Converters = { new JsonStringEnumConverter() },
	};

	// null — ошибка в error.
	public static GoalChainData Parse(string json, out string error)
	{
		try
		{
			var data = JsonSerializer.Deserialize<GoalChainData>(json, Options);
			if (data == null) { error = "пустой JSON."; return null; }
			data.Stages ??= new List<GoalStage>();
			foreach (var st in data.Stages)
			{
				st.Requirements ??= new List<GoalRequirement>();
				st.Reward ??= new GoalReward();
				st.Reward.Templates ??= new List<string>();
				st.Reward.Recipes ??= new List<string>();
			}
			error = null;
			return data;
		}
		catch (Exception e)
		{
			error = e.Message;
			return null;
		}
	}
}

public sealed class GoalChain
{
	public readonly GoalChainData Data;

	// Индекс текущего этапа; == Stages.Count — все этапы пройдены.
	public int Stage { get; private set; }
	// Значения счётчиков ЧД на начало этапа — по пунктам требований.
	public long[] Baseline { get; private set; } = Array.Empty<long>();
	public ulong Seed;

	// Открытые рецепты (StarRecipe.Id). Порядок не важен (только проверка).
	private readonly HashSet<string> _recipes = new(StringComparer.Ordinal);

	// Меняется при смене этапа и открытии рецептов — для отклика в HUD.
	public int Version { get; private set; }

	public GoalChain(GoalChainData data)
	{
		Data = data ?? new GoalChainData();
		Seed = Data.Seed;
	}

	public int StageCount => Data.Stages.Count;
	public bool AllDone => Stage >= StageCount;
	public GoalStage Current => AllDone ? null : Data.Stages[Stage];

	public static long Counter(GoalRequirement req, BlackHoleSet holes)
	{
		if (req.Kind == GoalKind.Particle)
			return req.Id >= 0 && req.Id < BlackHoleSet.ColorCount ? holes.ParticlesAbsorbed[req.Id] : 0;
		return req.Id >= 0 && req.Id < BlackHoleSet.TierCount ? holes.AtomsAbsorbed[req.Id] : 0;
	}

	// Засчитано по пункту i текущего этапа (0..Count).
	public long Progress(int i, BlackHoleSet holes)
	{
		var st = Current;
		if (st == null || i < 0 || i >= st.Requirements.Count) return 0;
		var req = st.Requirements[i];
		long b = i < Baseline.Length ? Baseline[i] : 0;
		return Math.Clamp(Counter(req, holes) - b, 0, Math.Max(0, req.Count));
	}

	public bool IsStageComplete(BlackHoleSet holes)
	{
		var st = Current;
		if (st == null) return false;
		for (int i = 0; i < st.Requirements.Count; i++)
			if (Progress(i, holes) < st.Requirements[i].Count) return false;
		return true;
	}

	// Этап stage начинается сейчас: Baseline — текущие счётчики ЧД.
	public void BeginStage(int stage, BlackHoleSet holes)
	{
		Stage = Math.Clamp(stage, 0, StageCount);
		var st = Current;
		Baseline = new long[st?.Requirements.Count ?? 0];
		for (int i = 0; i < Baseline.Length; i++) Baseline[i] = Counter(st.Requirements[i], holes);
		Version++;
	}

	// Для загрузки: этап и Baseline как в сохранении (недостающее — 0).
	public void Restore(int stage, IReadOnlyList<long> baseline)
	{
		Stage = Math.Clamp(stage, 0, StageCount);
		Baseline = new long[Current?.Requirements.Count ?? 0];
		for (int i = 0; i < Baseline.Length && baseline != null && i < baseline.Count; i++) Baseline[i] = baseline[i];
		Version++;
	}

	// --- рецепты ---

	public bool IsRecipeOpen(string id) => _recipes.Contains(id);

	public bool OpenRecipe(string id)
	{
		if (!_recipes.Add(id)) return false;
		Version++;
		return true;
	}

	public void OpenAllRecipes()
	{
		foreach (var r in StarRecipes.All) _recipes.Add(r.Id);
		Version++;
	}

	public void ClearRecipes()
	{
		_recipes.Clear();
		Version++;
	}

	// Открытые рецепты в порядке таблицы StarRecipes — для сохранения.
	public List<string> SortedRecipes()
	{
		var list = new List<string>();
		foreach (var r in StarRecipes.All) if (_recipes.Contains(r.Id)) list.Add(r.Id);
		return list;
	}

	// Зерно размещения наград этапа stage — чистая функция (seed, этап),
	// отдельного состояния генератора нет.
	public ulong PlacementSeed(int stage) => Rng.Mix(Seed ^ ((ulong)(stage + 1) * 0x9E3779B97F4A7C15UL));
}

// Детерминированный генератор (SplitMix64) — одинаковый на всех платформах.
public struct Rng
{
	private ulong _state;

	public Rng(ulong seed) { _state = seed; }

	public static ulong Mix(ulong z)
	{
		z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
		z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
		return z ^ (z >> 31);
	}

	public ulong Next()
	{
		_state += 0x9E3779B97F4A7C15UL;
		return Mix(_state);
	}

	// Равномерно в [0, n), n > 0.
	public int NextInt(int n) => n <= 1 ? 0 : (int)(Next() % (ulong)n);
}
