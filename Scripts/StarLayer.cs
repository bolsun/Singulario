using Godot;

// Звезда-сборщик (T006) — объект слоя 1, 3×3 клетки. Данные — StarSet
// (NucleusLayer.Stars), приём ингредиентов, производство и выход — NucleusLayer.
// Этот узел только:
//   - инструмент установки (кнопки ★Ж/★К/★С на панели слоя 1, SelectTool):
//     ЛКМ — поставить (клетка под курсором — центр), превью спрайта или
//     красный квадрат, если нельзя; удаление — ПКМ через NucleusLayer
//     (RemoveAllAtMouse → RemoveAt); Ctrl+ЛКМ / Shift+ЛКМ по звезде — весь
//     выходной буфер / один атом в инвентарь (T008, TakeToInventory);
//   - отрисовка: спрайт star_gray на 3×3 клетки, цвет тира — шейдер палитр
//     атомов (общий материал NucleusLayer, строка палитры в INSTANCE_CUSTOM.x);
//     во время производства звезда пульсирует ярче (INSTANCE_CUSTOM.y, яркость
//     с сохранением оттенка), в простое — ровно цвета палитры (приглушение
//     INSTANCE_CUSTOM.z = IdleDim, по умолчанию 0). Все звёзды — один MultiMesh.
//     Это режим «спрайт» (F6); по умолчанию — шейдер (T017, star_assembler.gdshader,
//     производная PixelPlanets): свой MultiMesh, тир/состояние/пиксели — в custom
//     data, фаза анимации — от тика симуляции (LoopTicks), все звёзды тира синхронны.
//     Состояния: работает — дрейф и «дыхание» короны; простаивает — замерла, на
//     ступень темнее, без короны; выход забит — замерла, красная дуга, после цикла
//     рецепта — пульс шеврона выхода. Ниже StarLodZoom — диск;
//   - эффект поглощения (T006c).
//
// Эффект поглощения — только визуал: частица или атом, которых забрала звезда
// (в симуляции — мгновенно), летит по прямой к центру с ускорением и на
// IntakeEndFraction радиуса диска исчезает (уменьшается и растворяется на
// последней IntakeFadeFraction пути). Цвет свой, без спирали и красного
// смещения. Идёт по времени кадра, в сохранение не попадает. Устройство как у
// падения в ЧД (BlackHoleLayer): узлов на объект нет, у звезды заранее
// выделенный массив из MaxIntakePerStar структур, всё летящее всех звёзд — один
// MultiMesh, буфер пишется одним вызовом за кадр. Сверх лимита — без анимации.
// Звезда вне экрана анимаций не заводит и не обновляет (текущие сбрасываются).
public partial class StarLayer : Node2D
{
	public const string TexturePath = "res://Resources/Textures/star_gray_96px.png";
	public const string ShaderPath = "res://Resources/Shaders/ThirdParty/PixelPlanets/star_assembler.gdshader";

	// --- звезда на шейдере (T017) ---
	// F6 — переключение «спрайт (как было) / шейдер»; стартовый режим — шейдер, не сохраняется.
	[Export] public bool UseShader = true;
	// Цикл анимации в тиках симуляции (кратен 256): фаза = (тик mod LoopTicks) / LoopTicks.
	[Export] public int LoopTicks = 512;
	// Диаметр тела в клетках; квад с короной — CoronaScale диаметров тела.
	[Export] public float BodyDiameterCells = 2.2f;
	[Export] public float CoronaScale = 2f;
	// Только вид (занимаемые клетки не меняются): 2 — посмотреть гиганта 6×6.
	[Export] public float DebugVisualScale = 1f;
	// Ниже этого зума — диск тона 3 с краем тона 1, без поверхности и короны.
	[Export] public float StarLodZoom = 0.15f;
	// Сид на тир (Ж, К, С, З), как в мастерской PixelPlanets.
	[Export] public int[] TierSeeds = { 753, 753, 753, 753 };
	// Шеврон выхода у давно забитой звезды: тиков на ступень пульса.
	[Export] public int ChevronStepTicks = 24;

	// Рампы тиров Singulario 32 (Resources/palettes/singulario-32.gpl): тир × тоны 0..4 (0 — темнее).
	private static readonly Color[] TierTones =
	{
		new("5e2a1e"), new("a8501c"), new("e8911f"), new("ffc93c"), new("fff09a"), // Ж
		new("4a1030"), new("8c1c3a"), new("d23a4a"), new("ff6e5e"), new("ffb09a"), // К
		new("1a1f5c"), new("26479e"), new("3a7fe0"), new("69b8ff"), new("c2ecff"), // С
		new("0d3b3f"), new("146e4e"), new("25a860"), new("6ddb5e"), new("b4f2a0"), // З
	};
	private static readonly Color BlockedArcColor = new("d23a4a");
	private static readonly Color[] ChevronPulse = { new("ff6e5e"), new("d23a4a"), new("8c1c3a"), new("d23a4a") };

	// Свечение во время производства — прибавка яркости (0.5 — в 1.5 раза ярче):
	// база и размах пульсации, частота (Гц).
	[Export] public float ProducingGlow = 0.3f;
	[Export] public float ProducingPulse = 0.2f;
	[Export] public float PulseHz = 1.2f;
	// Приглушение в простое (0..1); 0 — ровно цвета палитры.
	[Export] public float IdleDim = 0f;

	// Эффект поглощения.
	[Export] public int MaxIntakePerStar = 16;
	[Export] public float IntakeSeconds = 0.4f;
	// Радиус видимого диска спрайта в клетках (33.5 из 96 px на 3 клетки).
	[Export] public float DiskRadiusCells = 1.05f;
	// Где объект исчезает — доля радиуса диска от центра.
	[Export] public float IntakeEndFraction = 2f / 3f;
	// Доля пути в конце, на которой объект уменьшается и растворяется.
	[Export] public float IntakeFadeFraction = 0.35f;
	// Радиус атома на экране (px), ниже которого атом рисуется одним кружком.
	[Export] public float IntakeLodPixels = 5f;

	private static readonly Color BlockedColor = new Color(1f, 0.2f, 0.2f, 0.35f);
	private static readonly Color AtomBodyColor = new Color(0.08f, 0.08f, 0.1f, 0.9f);
	// Атом-предмет в эффекте (T007): доля диаметра, закрашенная тёмной серединой.
	private const float ItemHoleFraction = 0.55f;

	// MultiMesh 2D с цветом: 8 float трансформа (2 строки по 4) + 4 float цвета.
	private const int Stride = 12;

	private struct IntakeFx
	{
		public Vector2 Start;  // откуда пришёл, относительно центра звезды
		public float Age;      // секунд с начала
		public int Tier;       // тир атома; -1 — одиночная частица
		public int Color;      // цвет частицы или тир предмета (только для Tier == -1)
		public bool Item;      // атом-предмет из дырки (T007): полое кольцо размером с частицу
		public Atom Particles; // частицы в гнёздах атома (цвета)

		public readonly int Instances => Tier < 0 ? (Item ? 2 : 1) : 2 + Particles.Count;
	}

	private sealed class StarFx
	{
		public IntakeFx[] Items;
		public int Count;
	}

	// Только для звёзд, которые принимали на экране; чистится при удалении звезды.
	private readonly System.Collections.Generic.Dictionary<Star, StarFx> _fx = new();
	private readonly System.Collections.Generic.List<Star> _fxToRemove = new();
	private int _fxStarsVersion = -1;
	private float _atomRadius;
	private float _particleRadius;
	private MultiMesh _fxMesh;
	private float[] _fxBuffer = System.Array.Empty<float>();
	private int _fxCapacity;
	private Rect2 _viewRect;

	private NucleusLayer _nucleusLayer;
	private EnergyLayer _energyLayer;
	private readonly System.Collections.Generic.List<EnergyClusterLayer> _clusterLayers = new();
	private StarSet _stars;
	private Texture2D _texture;
	private float _cellSize;
	private bool _ready;

	private MultiMesh _mesh;
	private MultiMeshInstance2D _meshNode;
	private int _meshVersion = -1;
	private float _time;

	private MultiMesh _shaderMesh;
	private MultiMeshInstance2D _shaderNode;
	private ShaderMaterial _shaderMaterial;
	// Тик, с которого выход звезды забит (только вид: шеврон после одного цикла рецепта).
	private readonly System.Collections.Generic.Dictionary<Star, long> _blockedSince = new();
	private readonly System.Collections.Generic.List<Star> _blockedToRemove = new();
	private int _blockedVersion = -1;

	private Label _modeLabel;
	private float _modeLabelLeft;
	private const float ModeLabelSeconds = 1.5f;
	private const float ModeLabelFadeSeconds = 0.4f;

	private int? _toolTier;
	private bool _hadPreview;
	private bool _hadStars;
	private bool _hadPickups;

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		if (_nucleusLayer == null || !_nucleusLayer.IsReady || _nucleusLayer.Stars == null)
		{
			GD.PrintErr("[StarLayer] NucleusLayer не найден или не инициализирован — звёзды не работают.");
			return;
		}
		_energyLayer = GetNodeOrNull<EnergyLayer>("../TileMapLayer");
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer layer)
				_clusterLayers.Add(layer);

		_texture = GD.Load<Texture2D>(TexturePath);
		if (_texture == null) GD.PrintErr($"[StarLayer] не загрузился спрайт {TexturePath}.");

		_stars = _nucleusLayer.Stars;
		_cellSize = _nucleusLayer.CellSize;
		TextureFilter = TextureFilterEnum.Nearest;

		float side = Star.Size * _cellSize;
		_mesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = new QuadMesh { Size = new Vector2(side, side) },
			// Как у эффекта ЧД: границы на весь мир, иначе canvas item отсекает
			// MultiMesh по прямоугольнику, закэшированному, пока он был пуст.
			CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1f), new Vector3(2e7f, 2e7f, 2f)),
		};
		AddChild(_meshNode = new MultiMeshInstance2D
		{
			Name = "Stars",
			Multimesh = _mesh,
			Texture = _texture,
			Material = _nucleusLayer.PaletteMaterial,
			TextureFilter = TextureFilterEnum.Nearest,
			// Спрайты — под _Draw этого узла (рецепт, дуга, превью), а не поверх.
			ShowBehindParent = true,
		});
		CreateShaderMesh();
		CreateModeLabel();

		_atomRadius = _cellSize * 0.4f;
		_particleRadius = _cellSize * 0.1f;
		_fxMesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseColors = true,
			Mesh = new QuadMesh { Size = Vector2.One },
			CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1f), new Vector3(2e7f, 2e7f, 2f)),
		};
		// После спрайтов звёзд — поверх них, но тоже под _Draw (дуга, рецепт).
		AddChild(new MultiMeshInstance2D
		{
			Name = "StarIntake",
			Multimesh = _fxMesh,
			Texture = BlackHoleLayer.BuildDiscTexture(64),
			TextureFilter = TextureFilterEnum.Linear,
			ShowBehindParent = true,
		});
		SetProcessUnhandledInput(true);
		_ready = true;
	}

	// Звёзды на шейдере (T017): один MultiMesh, квад 1×1 масштабируется трансформом
	// инстанса (размер зависит от зума — пиксельная сетка), данные — в custom data.
	private void CreateShaderMesh()
	{
		var shader = GD.Load<Shader>(ShaderPath);
		if (shader == null) GD.PrintErr($"[StarLayer] не загрузился шейдер {ShaderPath}.");
		_shaderMaterial = new ShaderMaterial { Shader = shader };
		var tones = new Color[TierTones.Length];
		System.Array.Copy(TierTones, tones, tones.Length);
		_shaderMaterial.SetShaderParameter("tones", tones);
		var seeds = new float[4];
		for (int i = 0; i < seeds.Length; i++)
		{
			int sd = TierSeeds != null && i < TierSeeds.Length ? TierSeeds[i] : 753;
			seeds[i] = sd % 1000 / 100f; // как set_seed в PixelPlanets
		}
		_shaderMaterial.SetShaderParameter("seeds", seeds);

		_shaderMesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = new QuadMesh { Size = Vector2.One },
			CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1f), new Vector3(2e7f, 2e7f, 2f)),
		};
		AddChild(_shaderNode = new MultiMeshInstance2D
		{
			Name = "StarsShader",
			Multimesh = _shaderMesh,
			Material = _shaderMaterial,
			TextureFilter = TextureFilterEnum.Nearest,
			ShowBehindParent = true,
		});
	}

	// Надпись режима F6 — как у F4/F5 (NebulaBackground): свой слой поверх поля и HUD, под меню.
	private void CreateModeLabel()
	{
		var layer = new CanvasLayer { Name = "StarModeLabelLayer", Layer = 90 };
		AddChild(layer);
		_modeLabel = new Label
		{
			Name = "StarModeLabel",
			AutoTranslateMode = AutoTranslateModeEnum.Disabled,
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_modeLabel.AddThemeColorOverride("font_color", Colors.White);
		_modeLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
		_modeLabel.AddThemeConstantOverride("outline_size", 4);
		_modeLabel.AddThemeFontSizeOverride("font_size", 22);
		_modeLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
		_modeLabel.GrowHorizontal = Control.GrowDirection.Both;
		_modeLabel.OffsetTop = 64;
		layer.AddChild(_modeLabel);
	}

	private void UpdateModeLabel(float delta)
	{
		if (_modeLabelLeft <= 0f) return;
		_modeLabelLeft = Mathf.Max(0f, _modeLabelLeft - delta);
		_modeLabel.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_modeLabelLeft / ModeLabelFadeSeconds, 0f, 1f));
		_modeLabel.Visible = _modeLabelLeft > 0f;
	}

	// --- инструмент (панель слоя 1) ---

	public void SelectTool(int tier)
	{
		if (!_ready) return;
		_nucleusLayer.ClearSelection(); // сбрасывает и этот инструмент
		_energyLayer?.ClearSelection();
		foreach (var layer in _clusterLayers) layer.ClearSelection();
		_toolTier = tier;
		GD.Print($"[StarLayer] выбрана звезда тира {tier}: ЛКМ — поставить (центр под курсором), ПКМ — удалить.");
	}

	public void ClearTool() => _toolTier = null;
	public bool HasTool => _toolTier.HasValue;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_ready) return;
		// F6 — звёзды: спрайт (как было) / шейдер (T017), для сравнения. Не сохраняется.
		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.F6)
		{
			UseShader = !UseShader;
			_meshVersion = -1; // пересчитать число инстансов обоих MultiMesh
			_modeLabel.Text = string.Format(Tr("Звёзды: {0}"), Tr(UseShader ? "Шейдер" : "Спрайт"));
			_modeLabel.Visible = true;
			_modeLabelLeft = ModeLabelSeconds;
			GetViewport().SetInputAsHandled();
			return;
		}
		if (ViewLayer.IsLayer2) return;
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;
		var (row, col) = CellUnderMouse();
		// Ctrl+ЛКМ по звезде — весь выходной буфер в инвентарь, Shift+ЛКМ — один
		// атом (T008). Раньше смены рецепта, с любым инструментом.
		if ((mb.CtrlPressed || mb.ShiftPressed) && _stars.TryGetAt(row, col, out var source))
		{
			TakeToInventory(source, all: mb.CtrlPressed);
			GetViewport().SetInputAsHandled();
			return;
		}
		// ЛКМ по звезде — следующий доступный рецепт (в настоящем режиме —
		// только открытые заданиями, T011; в песочнице — все), с любым инструментом.
		if (_stars.TryGetAt(row, col, out var star))
		{
			int next = NextAvailableRecipe(star.Recipe);
			if (next < 0)
				GD.Print($"[StarLayer] звезда ({star.Row},{star.Col}): других открытых рецептов нет (текущий «{star.RecipeData.Name}»).");
			else
			{
				bool burned = star.SetRecipe(next);
				GD.Print($"[StarLayer] звезда ({star.Row},{star.Col}): рецепт «{star.RecipeData.Name}».{(burned ? " Ингредиенты в работе и в буфере сгорели." : "")}");
			}
			GetViewport().SetInputAsHandled();
			return;
		}
		if (_toolTier.HasValue)
		{
			PlaceFromTool(row - Star.Size / 2, col - Star.Size / 2, _toolTier.Value);
			GetViewport().SetInputAsHandled();
		}
	}

	// Установка инструментом (T011): в настоящем режиме тратит звезду тира из
	// инвентаря; нет звезды — отказ. Шаблоны и загрузка ставят бесплатно (TryPlace).
	private void PlaceFromTool(int row, int col, int tier)
	{
		var inventory = _nucleusLayer.Inventory;
		if (!inventory.CanAffordStar(tier))
		{
			GD.Print($"[StarLayer] нет звезды тира {tier} в инвентаре — не ставится.");
			return;
		}
		if (TryPlace(row, col, tier, log: true) != null) inventory.TrySpendStar(tier);
	}

	// Выходной буфер звезды → инвентарь (T008): все предметы или один, самый
	// старый (атомы и звёзды-предметы, T011). Ждущая готовая работа уйдёт в
	// освободившееся место на следующем тике.
	private void TakeToInventory(Star star, bool all)
	{
		var inventory = _nucleusLayer.Inventory;
		int taken = 0;
		while (star.Output.Count > 0 && (all || taken == 0))
		{
			int code = star.Output.Dequeue();
			inventory.AddItem(code);
			AddPickup(star, StarItem.Tier(code), taken);
			taken++;
		}
		GD.Print(taken > 0
			? $"[StarLayer] из звезды ({star.Row},{star.Col}) в инвентарь: {taken} предмет(ов)."
			: $"[StarLayer] выходной буфер звезды ({star.Row},{star.Col}) пуст.");
	}

	// --- отклик «забрал в инвентарь» (только визуал): полое кольцо цвета тира
	// летит из центра звезды к курсору и тает. Идёт по времени кадра.

	[Export] public float PickupSeconds = 0.35f;
	// Задержка между кольцами при заборе всего буфера (сек).
	[Export] public float PickupStagger = 0.04f;
	private const int MaxPickups = 32;

	private struct Pickup
	{
		public Vector2 From;
		public float Age; // < 0 — ещё не вылетело
		public int Tier;
	}

	private readonly System.Collections.Generic.List<Pickup> _pickups = new();

	private void AddPickup(Star star, int tier, int index)
	{
		if (_pickups.Count >= MaxPickups) return;
		_pickups.Add(new Pickup { From = StarCenter(star), Age = -index * PickupStagger, Tier = tier });
	}

	private void UpdatePickups(float delta)
	{
		for (int i = _pickups.Count - 1; i >= 0; i--)
		{
			var p = _pickups[i];
			p.Age += delta;
			if (p.Age >= PickupSeconds) _pickups.RemoveAt(i);
			else _pickups[i] = p;
		}
	}

	private void DrawPickups()
	{
		var mouse = GetGlobalMousePosition();
		float radius = Mathf.Max(_particleRadius * 1.5f, 1f);
		foreach (var p in _pickups)
		{
			if (p.Age < 0f) continue;
			float t = p.Age / PickupSeconds;
			float ease = t * t; // с ускорением к курсору
			var pos = p.From.Lerp(mouse, ease);
			var color = new Color(TierColor(p.Tier), 1f - t * t);
			DrawArc(pos, radius, 0f, Mathf.Tau, 16, color, radius * 0.6f);
		}
	}

	private (int row, int col) CellUnderMouse()
	{
		var p = GetGlobalMousePosition();
		return (Mathf.FloorToInt(p.Y / _cellSize), Mathf.FloorToInt(p.X / _cellSize));
	}

	// Причина, по которой звезду нельзя поставить (верхняя левая клетка), или null.
	private string PlaceBlockReason(int row, int col)
	{
		if (_stars.Overlaps(row, col, Star.Size)) return "пересекается с другой звездой";
		for (int r = row; r < row + Star.Size; r++)
			for (int c = col; c < col + Star.Size; c++)
				if (!_nucleusLayer.CanPlaceStarCell(r, c)) return $"клетка ({r},{c}) занята";
		return null;
	}

	// Установка звезды (инструмент и загрузка сохранения). Рецепт по умолчанию —
	// атом тира звезды (если такой рецепт есть).
	public Star TryPlace(int row, int col, int tier, bool log, int? recipe = null)
	{
		if (!_ready) return null;
		string reason = PlaceBlockReason(row, col);
		if (reason != null)
		{
			if (log) GD.Print($"[StarLayer] звезда в клетку ({row},{col}): {reason} — пропуск.");
			return null;
		}
		var star = new Star(row, col, tier, recipe ?? DefaultRecipe(tier));
		_stars.Add(star);
		if (log) GD.Print($"[StarLayer] установлена звезда тира {tier} в клетке ({row},{col}), рецепт «{star.RecipeData.Name}».");
		return star;
	}

	// Следующий после current доступный рецепт по кругу; -1 — кроме current доступных нет.
	private int NextAvailableRecipe(int current)
	{
		for (int step = 1; step < StarRecipes.Count; step++)
		{
			int i = (current + step) % StarRecipes.Count;
			if (_nucleusLayer.IsRecipeAvailable(i)) return i;
		}
		return -1;
	}

	// Рецепт новой звезды: доступный «атом тира звезды», иначе первый доступный,
	// иначе «атом тира звезды» (рецепты ещё не открыты — звезда ждёт).
	private int DefaultRecipe(int tier)
	{
		int own = -1, firstAvailable = -1;
		for (int i = 0; i < StarRecipes.Count; i++)
		{
			var r = StarRecipes.All[i];
			bool isOwn = r.Kind == RecipeResult.Atom && r.ResultTier == tier;
			if (isOwn && own < 0) own = i;
			if (!_nucleusLayer.IsRecipeAvailable(i)) continue;
			if (isOwn) return i;
			if (firstAvailable < 0) firstAvailable = i;
		}
		return firstAvailable >= 0 ? firstAvailable : System.Math.Max(0, own);
	}

	// ПКМ по любой клетке звезды (вызывает NucleusLayer.RemoveAllAtMouse).
	// Недособранные ингредиенты сгорают. Настоящий режим (T011, GDD «звезду
	// можно забрать ПКМ и переставить»): звезда и её выходной буфер — в инвентарь.
	public void RemoveAt(int row, int col)
	{
		if (!_ready || !_stars.TryGetAt(row, col, out var star)) return;
		_stars.Remove(star);
		string burned = star.Producing || star.HasBuffered ? " Ингредиенты в работе и в буфере сгорели." : "";
		string refunded = "";
		var inventory = _nucleusLayer.Inventory;
		if (!inventory.Sandbox)
		{
			int items = star.Output.Count;
			while (star.Output.Count > 0) inventory.AddItem(star.Output.Dequeue());
			inventory.AddStar(star.Tier);
			refunded = $" В инвентарь: звезда{(items > 0 ? $" и {items} предмет(ов) из буфера" : "")}.";
		}
		GD.Print($"[StarLayer] удалена звезда из клетки ({star.Row},{star.Col}).{burned}{refunded}");
	}

	// --- эффект поглощения (только визуал, на симуляцию не влияет) ---

	// Звезда забрала частицу из гнезда соседнего атома: from — мировая точка гнезда.
	public void OnParticleTaken(Star star, Vector2 from, int color) =>
		AddFx(star, new IntakeFx { Start = from - StarCenter(star), Tier = -1, Color = color });

	// Звезда забрала атом-предмет тира tier из гнезда соседнего атома (T007).
	public void OnItemTaken(Star star, Vector2 from, int tier) =>
		AddFx(star, new IntakeFx { Start = from - StarCenter(star), Tier = -1, Color = tier, Item = true });

	// Звезда поглотила атом-ингредиент: from — мировая точка, куда он приехал;
	// particles — цвета частиц в его гнёздах.
	public void OnAtomTaken(Star star, Vector2 from, int tier, Atom particles) =>
		AddFx(star, new IntakeFx { Start = from - StarCenter(star), Tier = System.Math.Max(0, tier), Particles = particles });

	private void AddFx(Star star, IntakeFx item)
	{
		if (!_ready || MaxIntakePerStar <= 0 || ViewLayer.IsLayer2) return;
		if (!_viewRect.Intersects(StarRect(star))) return;
		if (!_fx.TryGetValue(star, out var fx))
		{
			fx = new StarFx { Items = new IntakeFx[MaxIntakePerStar] };
			_fx[star] = fx;
		}
		if (fx.Count >= fx.Items.Length) return; // сверх лимита — без анимации
		fx.Items[fx.Count++] = item;
	}

	private Vector2 StarCenter(Star s) =>
		new Vector2((s.Col + Star.Size / 2f) * _cellSize, (s.Row + Star.Size / 2f) * _cellSize);

	private Rect2 StarRect(Star s) =>
		new Rect2(s.Col * _cellSize, s.Row * _cellSize, Star.Size * _cellSize, Star.Size * _cellSize);

	private void UpdateViewRect()
	{
		var cam = GetViewport().GetCamera2D();
		if (cam == null) { _viewRect = new Rect2(); return; }
		var size = GetViewportRect().Size / cam.Zoom;
		_viewRect = new Rect2(cam.GetScreenCenterPosition() - size / 2f, size);
	}

	private void UpdateEffects(float dt)
	{
		if (_fxStarsVersion != _stars.Version)
		{
			_fxStarsVersion = _stars.Version;
			_fxToRemove.Clear();
			var alive = new System.Collections.Generic.HashSet<Star>(_stars.All);
			foreach (var key in _fx.Keys)
				if (!alive.Contains(key)) _fxToRemove.Add(key);
			foreach (var key in _fxToRemove) _fx.Remove(key);
		}

		bool hidden = ViewLayer.IsLayer2;
		foreach (var pair in _fx)
		{
			var fx = pair.Value;
			if (fx.Count == 0) continue;
			if (hidden || !_viewRect.Intersects(StarRect(pair.Key)))
			{
				fx.Count = 0;
				continue;
			}
			for (int i = 0; i < fx.Count;)
			{
				fx.Items[i].Age += dt;
				if (fx.Items[i].Age < IntakeSeconds) { i++; continue; }
				fx.Items[i] = fx.Items[--fx.Count]; // порядок отрисовки не важен
			}
		}
	}

	// Всё летящее видимых звёзд → буфер MultiMesh (атом: обод, тело, точки поверх).
	private void FillEffectMesh()
	{
		int needed = 0;
		foreach (var fx in _fx.Values)
			for (int i = 0; i < fx.Count; i++) needed += fx.Items[i].Instances;
		if (needed > _fxCapacity)
		{
			_fxCapacity = Mathf.Max(needed, _fxCapacity * 2);
			_fxBuffer = new float[_fxCapacity * Stride];
			_fxMesh.InstanceCount = _fxCapacity; // растёт только до пика
		}
		if (needed == 0)
		{
			_fxMesh.VisibleInstanceCount = 0;
			return;
		}

		var colors = _nucleusLayer.TierPreviewColors;
		var cam = GetViewport().GetCamera2D();
		float zoom = cam != null ? cam.Zoom.X : 1f;
		float endR = DiskRadiusCells * _cellSize * IntakeEndFraction;
		float fade = Mathf.Clamp(IntakeFadeFraction, 0.01f, 1f);
		int n = 0;
		foreach (var pair in _fx)
		{
			var fx = pair.Value;
			if (fx.Count == 0) continue;
			var center = StarCenter(pair.Key);
			for (int i = 0; i < fx.Count; i++)
			{
				ref var f = ref fx.Items[i];
				float t = Mathf.Clamp(f.Age / IntakeSeconds, 0f, 1f);
				float e = t * t; // ускорение к центру
				float r0 = f.Start.Length();
				var dir = r0 > 0f ? f.Start / r0 : Vector2.Up;
				// Приехал уже внутрь 2/3 диска — только растворяется на месте.
				float r1 = Mathf.Min(endR, r0);
				var pos = center + dir * Mathf.Lerp(r0, r1, e);
				float k = Mathf.Clamp((e - (1f - fade)) / fade, 0f, 1f); // 0..1 на последнем участке
				float shrink = 1f - 0.85f * k;
				float alpha = 1f - k;

				if (f.Tier < 0)
				{
					float pr = _particleRadius * shrink;
					Put(ref n, pos, 2f * pr, new Color(FxColor(f.Color, colors), alpha));
					if (f.Item) Put(ref n, pos, 2f * pr * ItemHoleFraction, new Color(AtomBodyColor, AtomBodyColor.A * alpha));
					continue;
				}

				float radius = _atomRadius * shrink;
				Put(ref n, pos, 2f * radius, new Color(FxColor(f.Tier, colors), alpha));
				if (radius * zoom < IntakeLodPixels) continue; // мелко — один кружок
				Put(ref n, pos, 1.76f * radius, new Color(AtomBodyColor, AtomBodyColor.A * alpha));
				for (int p = 0; p < f.Particles.Count; p++)
				{
					float da = p * Mathf.Tau / Atom.Size - Mathf.Pi / 2f;
					var at = pos + radius * 0.6f * new Vector2(Mathf.Cos(da), Mathf.Sin(da));
					Put(ref n, at, 0.4f * radius, new Color(FxColor(f.Particles.ColorAt(p), colors), alpha));
				}
			}
		}

		RenderingServer.MultimeshSetBuffer(_fxMesh.GetRid(), _fxBuffer);
		_fxMesh.VisibleInstanceCount = n;
	}

	// Один кружок: центр pos, диаметр d, цвет c.
	private void Put(ref int n, Vector2 pos, float d, Color c)
	{
		int o = n * Stride;
		var b = _fxBuffer;
		b[o] = d; b[o + 1] = 0f; b[o + 2] = 0f; b[o + 3] = pos.X;
		b[o + 4] = 0f; b[o + 5] = d; b[o + 6] = 0f; b[o + 7] = pos.Y;
		b[o + 8] = c.R; b[o + 9] = c.G; b[o + 10] = c.B; b[o + 11] = c.A;
		n++;
	}

	private static Color FxColor(int color, Color[] tierColors) =>
		(tierColors != null && color >= 0 && color < tierColors.Length) ? tierColors[color] : Colors.White;

	// --- отрисовка ---

	public override void _Process(double delta)
	{
		if (!_ready) return;
		_time += (float)delta;
		UpdateViewRect();
		UpdateEffects((float)delta);
		UpdatePickups((float)delta);
		UpdateModeLabel((float)delta);
		FillEffectMesh();
		UpdateMesh();
		bool preview = _toolTier.HasValue && !ViewLayer.IsLayer2;
		// _hadStars — ещё один кадр после удаления последней звезды, чтобы стереть её рецепт.
		bool pickups = _pickups.Count > 0;
		if (_stars.Count > 0 || _hadStars || preview || _hadPreview || pickups || _hadPickups) QueueRedraw();
		_hadPreview = preview;
		_hadStars = _stars.Count > 0;
		_hadPickups = pickups;
	}

	// Звёзд мало — буфер пишется целиком каждый кадр (пульсация).
	private void UpdateMesh()
	{
		var stars = _stars.All;
		if (_meshVersion != _stars.Version)
		{
			_meshVersion = _stars.Version;
			_mesh.InstanceCount = UseShader ? 0 : stars.Count;
			_shaderMesh.InstanceCount = UseShader ? stars.Count : 0;
		}
		_meshNode.Visible = !ViewLayer.IsLayer2 && !UseShader;
		_shaderNode.Visible = !ViewLayer.IsLayer2 && UseShader;
		if (UseShader) UpdateShaderMesh(stars);
		else UpdateSpriteMesh(stars);
	}

	private void UpdateSpriteMesh(System.Collections.Generic.IReadOnlyList<Star> stars)
	{
		float pulse = 0.5f + 0.5f * Mathf.Sin(_time * Mathf.Tau * PulseHz);
		for (int i = 0; i < stars.Count; i++)
		{
			var s = stars[i];
			var center = new Vector2((s.Col + Star.Size / 2f) * _cellSize, (s.Row + Star.Size / 2f) * _cellSize);
			_mesh.SetInstanceTransform2D(i, new Transform2D(0f, center));
			bool working = s.Producing && s.Elapsed < _nucleusLayer.StarDuration(s);
			float rowUv = (s.Tier + 0.5f) / _nucleusLayer.TierCount;
			float glow = working ? ProducingGlow + ProducingPulse * pulse : 0f;
			float dim = working ? 0f : IdleDim;
			_mesh.SetInstanceCustomData(i, new Color(rowUv, glow, dim, 0f));
		}
	}

	public enum StarState { Working = 0, Idle = 1, Blocked = 2 }

	// Публично — для свечения звезды (T020).
	public StarState StateOf(Star s) =>
		!s.Producing ? StarState.Idle
		: s.Elapsed < _nucleusLayer.StarDuration(s) ? StarState.Working
		: StarState.Blocked;

	// Звезда на шейдере. Пиксель — 1 пиксель мира; при отдалении — не мельче пикселя
	// экрана, ступенями степени двойки (иначе сетка «плывёт» с зумом — рябь). Тело —
	// чётное число пикселей, квад — тело + целое число пикселей с каждой стороны.
	private void UpdateShaderMesh(System.Collections.Generic.IReadOnlyList<Star> stars)
	{
		var cam = GetViewport().GetCamera2D();
		float zoom = cam != null ? cam.Zoom.X : 1f;
		float pixel = 1f;
		if (zoom < 1f) pixel = Mathf.Pow(2f, Mathf.Ceil(Mathf.Log(1f / zoom) / Mathf.Log(2f) - 1e-4f));
		float diameter = BodyDiameterCells * _cellSize * Mathf.Max(DebugVisualScale, 0.01f);
		int bodyPx = Mathf.Max(2, 2 * Mathf.RoundToInt(diameter / pixel / 2f));
		int margin = Mathf.Max(0, Mathf.RoundToInt(bodyPx * (Mathf.Max(CoronaScale, 1f) - 1f) / 2f));
		int quadPx = bodyPx + 2 * margin;
		float side = quadPx * pixel;

		int loop = Mathf.Max(256, LoopTicks);
		long tick = _nucleusLayer.GlobalTick;
		float phase = ((float)(tick % loop) + _nucleusLayer.SubTickFraction) / loop;
		_shaderMaterial.SetShaderParameter("phase", phase);
		_shaderMaterial.SetShaderParameter("lod", zoom < StarLodZoom);

		for (int i = 0; i < stars.Count; i++)
		{
			var s = stars[i];
			_shaderMesh.SetInstanceTransform2D(i, new Transform2D(new Vector2(side, 0f), new Vector2(0f, side), StarCenter(s)));
			_shaderMesh.SetInstanceCustomData(i, new Color(s.Tier, (int)StateOf(s), bodyPx, quadPx));
		}
	}

	// Сколько тиков выход звезды уже забит (0 — не забит). Только вид.
	private long BlockedTicks(Star s)
	{
		if (_blockedVersion != _stars.Version)
		{
			_blockedVersion = _stars.Version;
			_blockedToRemove.Clear();
			var alive = new System.Collections.Generic.HashSet<Star>(_stars.All);
			foreach (var key in _blockedSince.Keys)
				if (!alive.Contains(key)) _blockedToRemove.Add(key);
			foreach (var key in _blockedToRemove) _blockedSince.Remove(key);
		}
		long now = _nucleusLayer.GlobalTick;
		if (StateOf(s) != StarState.Blocked)
		{
			_blockedSince.Remove(s);
			return 0;
		}
		if (!_blockedSince.TryGetValue(s, out long since)) _blockedSince[s] = since = now;
		return now - since;
	}

	// Шеврон стороны выхода: на краю звезды, остриём наружу.
	private void DrawOutputChevron(Star star, Vector2 center, Color color)
	{
		var (orow, ocol) = star.OutputCell;
		var outCenter = new Vector2((ocol + 0.5f) * _cellSize, (orow + 0.5f) * _cellSize);
		var dir = (outCenter - center).Normalized();
		var perp = new Vector2(-dir.Y, dir.X);
		float edge = Star.Size / 2f * _cellSize;
		float h = _cellSize * 0.22f;
		var tip = center + dir * (edge + h * 0.5f);
		var back = center + dir * (edge - h * 0.5f);
		DrawPolyline(new[] { back + perp * h, tip, back - perp * h }, color, _cellSize * 0.08f);
	}

	public override void _Draw()
	{
		if (!_ready || ViewLayer.IsLayer2) return;
		var hovered = StarUnderMouse();
		foreach (var star in _stars.All) DrawStarInfo(star, star == hovered);
		if (_toolTier.HasValue) DrawPreview();
		DrawPickups();
	}

	// Звезда, над одной из 9 клеток которой или над клеткой выхода которой курсор.
	private Star StarUnderMouse()
	{
		var (row, col) = CellUnderMouse();
		if (_stars.TryGetAt(row, col, out var star)) return star;
		foreach (var s in _stars.All)
			if (s.OutputCell == (row, col)) return s;
		return null;
	}

	private static Color TierTone(int tier, int tone) =>
		TierTones[System.Math.Clamp(tier, 0, TierTones.Length / 5 - 1) * 5 + System.Math.Clamp(tone, 0, 4)];

	private Color TierColor(int tier)
	{
		var colors = _nucleusLayer.TierPreviewColors;
		return colors != null && tier >= 0 && tier < colors.Length ? colors[tier] : Colors.White;
	}

	// Рецепт (атом-результат в центре), прогресс работы (дуга), буфер (подписи
	// «набрано/нужно» цветом ингредиента), выходной буфер (T008) и клетка выхода (рамка). Дуга — всегда
	// (статус), остальное — только у звезды под курсором (hovered).
	private void DrawStarInfo(Star star, bool hovered)
	{
		var center = new Vector2((star.Col + Star.Size / 2f) * _cellSize, (star.Row + Star.Size / 2f) * _cellSize);
		var recipe = star.RecipeData;

		if (star.Producing)
		{
			int duration = _nucleusLayer.StarDuration(star);
			float t = Mathf.Clamp((float)star.Elapsed / duration, 0f, 1f);
			var arcColor = t >= 1f ? new Color(1f, 0.35f, 0.3f, 0.9f) : new Color(1f, 1f, 1f, 0.85f); // красная — готово, выходной буфер полон
			// Шейдер (T017): цвета из палитры — тон 3 тира, забитый выход — #d23a4a.
			if (UseShader) arcColor = t >= 1f ? BlockedArcColor : TierTone(star.Tier, 3);
			DrawArc(center, _cellSize * 0.6f, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Mathf.Tau * t, 32, arcColor, _cellSize * 0.08f);
		}
		// Выход забит дольше одного цикла рецепта — шеврон выхода мягко пульсирует (без наведения).
		if (UseShader && BlockedTicks(star) > _nucleusLayer.StarDuration(star))
		{
			int step = System.Math.Max(1, ChevronStepTicks);
			int i = (int)(_nucleusLayer.GlobalTick / step % ChevronPulse.Length);
			DrawOutputChevron(star, center, ChevronPulse[i]);
		}
		if (!hovered) return;

		float icon = _cellSize * 0.8f;
		// Значок результата: атом — ядро цвета тира, звезда (T011) — спрайт звезды.
		var iconTex = recipe.Kind == RecipeResult.Star ? _texture : _nucleusLayer.CoreTexture;
		if (recipe.Kind == RecipeResult.Star) icon *= 1.4f;
		if (iconTex != null)
			DrawTextureRect(iconTex, new Rect2(center - new Vector2(icon, icon) / 2f, new Vector2(icon, icon)), false, TierColor(recipe.ResultTier));

		var font = ThemeDB.FallbackFont;
		int fontSize = Mathf.Max(6, Mathf.RoundToInt(_cellSize * 0.3f));
		float y = center.Y + _cellSize * 0.75f;
		for (int i = 0; i < recipe.Ingredients.Length; i++)
		{
			var ing = recipe.Ingredients[i];
			string text = ing.Kind == IngredientKind.Atom ? $"◯{star.Buffer[i]}/{ing.Count}" : $"•{star.Buffer[i]}/{ing.Count}";
			float x = center.X + (i - (recipe.Ingredients.Length - 1) / 2f) * _cellSize * 0.9f - _cellSize * 0.4f;
			DrawString(font, new Vector2(x, y + fontSize * 0.35f), text, HorizontalAlignment.Left, -1, fontSize, TierColor(ing.Id));
		}
		// Выходной буфер (T008) — строкой под ингредиентами.
		string output = string.Format(Tr("в буфере: {0} / {1}"), star.Output.Count, _nucleusLayer.StarOutputCapacity);
		var outSize = font.GetStringSize(output, HorizontalAlignment.Left, -1, fontSize);
		DrawString(font, new Vector2(center.X - outSize.X / 2f, y + fontSize * 1.6f), output, HorizontalAlignment.Left, -1, fontSize, Colors.White);

		var (orow, ocol) = star.OutputCell;
		var outRect = new Rect2(ocol * _cellSize, orow * _cellSize, _cellSize, _cellSize).Grow(-_cellSize * 0.06f);
		DrawRect(outRect, new Color(TierColor(star.Tier), 0.9f), false, _cellSize * 0.06f);
	}

	private void DrawPreview()
	{
		var (row, col) = CellUnderMouse();
		row -= Star.Size / 2;
		col -= Star.Size / 2;
		var rect = new Rect2(col * _cellSize, row * _cellSize, Star.Size * _cellSize, Star.Size * _cellSize);
		if (PlaceBlockReason(row, col) != null || !_nucleusLayer.Inventory.CanAffordStar(_toolTier.Value)) DrawRect(rect, BlockedColor);
		else if (_texture != null)
		{
			var colors = _nucleusLayer.TierPreviewColors;
			var tint = colors != null && _toolTier.Value < colors.Length ? colors[_toolTier.Value] : Colors.White;
			DrawTextureRect(_texture, rect, false, new Color(tint, 0.6f));
		}
	}
}
