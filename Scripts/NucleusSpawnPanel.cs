using Godot;
using System.Collections.Generic;

// Debug-панель 3x3 в правом верхнем углу — по одной кнопке на комбинацию
// (тир x количество дырок в кольце из 8). Нажатие кнопки только ВЫБИРАЕТ
// пресет (см. NucleusLayer.SelectSpawnPreset) — само создание NucleusEntity
// и установка в клетку под курсором происходят по следующему ЛКМ по полю
// (см. NucleusLayer._UnhandledInput/TryPlaceNucleus).
//
// Строки — количество дырок (2/4/8 из 8 слотов кольца), колонки — тир
// (синий/красный/жёлтый, тот же порядок, что и PalettePaths в NucleusLayer).
public partial class NucleusSpawnPanel : Control
{
	private const int ButtonSize = 64;

	private NucleusLayer _nucleusLayer;
	private EnergyLayer _energyLayer;
	// На каждый тир скопления частиц — свой отдельный узел TileMapLayer со
	// скриптом EnergyClusterLayer (см. шапку того файла) и своим Tier; в
	// отличие от _nucleusLayer/_energyLayer их находим не по жёстко зашитому
	// имени узла, а по типу скрипта среди детей Main (см. LoadParticleLayers),
	// и сортируем по Tier, чтобы порядок кнопок не зависел от порядка узлов
	// в дереве сцены.
	private List<EnergyClusterLayer> _particleLayers = new();
	// Слой 2 (см. MoleculeLayer): на нём панель показывает свою сетку кнопок
	// молекул вместо кнопок слоя 1 (см. ApplyViewLayer). Выбранный инструмент
	// каждый слой хранит у себя, поэтому при переключении он не теряется.
	private MoleculeLayer _moleculeLayer;
	private GridContainer _layer1Grid;
	private GridContainer _layer2Grid;

	// label — однобуквенная метка тира для подписи кнопки, tier — индекс в
	// NucleusLayer.PalettePaths (0=синий,1=красный,2=жёлтый).
	private static readonly (string label, int tier)[] Tiers =
	{
		("Ж", 0), // жёлтый
		("К", 1), // красный
		("С", 2), // синий
	};

	private static readonly int[] HoleCounts = { 2, 4, 8 };

	// Запасной цвет подписи серой кнопки на случай, если GrayCoreTier почему-то
	// не попадает в диапазон _tierColors (не должно случаться в норме — теперь
	// у серого тира есть настоящая палитра, и цвет обычно берётся прямо из неё,
	// см. использование ниже).
	private static readonly Color GrayButtonColor = new Color(0.7f, 0.7f, 0.7f);

	// Пока нет отдельных спрайтов-иконок на тир — красим текст подписи в цвет
	// тира, чтобы кнопки хоть как-то отличались визуально. Цвет берём НЕ
	// произвольный, а реально сэмплируем из той же палитровой текстуры
	// (PalettePaths), которой красится сама частица/ядро в NucleusLayer —
	// так цвет кнопки будет совпадать с тем, что игрок увидит на поле.
	private Color[] _tierColors;

	// Пересчитывает геометрию панели — вызывается один раз из _Ready и затем
	// каждый раз при изменении размера окна (см. подписку на
	// GetTree().Root.SizeChanged в _Ready). TopRight — угловой (точечный)
	// анкор, а не анкор на весь прямоугольник, так что Position/Size у
	// Control в этом случае ведут себя неочевидно — задаём геометрию через
	// однозначные Offset* (пиксельный отступ ОТ анкорной точки), тот же
	// приём, что уже используется у FpsLabel в сцене.
	//
	// ВАЖНО: прямоугольник — widthPx x heightPx, а НЕ квадрат. Раньше здесь
	// по ошибке ширина считалась равной высоте (обеим — "side" = ButtonSize *
	// totalRows), хотя грид на самом деле узкий (Columns=3, т.е. реальная
	// ширина = ButtonSize*3), а не квадратный. Из-за этого прямоугольник
	// панели был в разы шире, чем видимые кнопки, а лишняя (невидимая) часть
	// слева всё равно ловила клики мышью (обычный Control блокирует мышь по
	// всему своему Rect, а не только там, где есть дочерний элемент) —
	// отсюда и жалоба "нельзя ставить/копировать ядра рядом с панелью на
	// каком-то расстоянии". См. также MouseFilter=Ignore в _Ready — вторая,
	// независимая подстраховка от той же проблемы.
	private void RecomputeLayout()
	{
		SetAnchorsPreset(LayoutPreset.TopRight);

		// Строк: HoleCounts.Length (комбинации ядер) + HoleCounts.Length (та
		// же линейка для серого ядра) + HoleCounts.Length (линейка для
		// поворачивателя, см. RotatorCoreTier) + 1 (типы энергии основного
		// слоя) + сколько нужно строк под кнопки тиров скопления частиц —
		// считаем по факту найденных узлов (см.
		// LoadParticleLayers), а не жёстко "1", чтобы панель не обрезала
		// кнопки, если тиров вдруг станет больше 3 (GridContainer при этом
		// сам переносит лишние кнопки на новую строку).
		int extraButtons = _particleLayers.Count;
		int clusterRows = extraButtons > 0 ? Mathf.CeilToInt(extraButtons / 3f) : 0;
		// HoleCounts.Length * 4 — линейка "количество дырок" повторена 4 раза:
		// обычные тиры, серое ядро, поворачиватель И бросатель (см.
		// ThrowerCoreTier в NucleusLayer.cs).
		int totalRows = HoleCounts.Length * 4 + 1 + clusterRows;
		// Слой 2: Ж/К/С x 2/4/8, строка серых Сер2/Сер4/Сер8 и строка с ЧД.
		if (ViewLayer.IsLayer2) totalRows = HoleCounts.Length + 2;

		int widthPx = ButtonSize * 3; // ровно столько колонок в GridContainer
		int heightPx = ButtonSize * totalRows;
		const int margin = 16;
		OffsetLeft = -widthPx - margin;
		OffsetTop = margin;
		OffsetRight = -margin;
		OffsetBottom = margin + heightPx;
	}

	// Отписка от сигнала окна — без неё при выгрузке сцены (например, при
	// смене сцены или выходе из игры) подписка осталась бы висеть на
	// уничтоженном узле.
	public override void _ExitTree()
	{
		var tree = GetTree();
		if (tree?.Root != null) tree.Root.SizeChanged -= RecomputeLayout;
		ViewLayer.Changed -= OnViewLayerChanged;
	}

	public override void _Ready()
	{
		// Абсолютный путь от корня сцены — не зависит от того, где именно
		// в дереве лежит сама панель (см. тот же приём в FpsLabel).
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		_energyLayer = GetNodeOrNull<EnergyLayer>("/root/Main/TileMapLayer");
		_moleculeLayer = GetNodeOrNull<MoleculeLayer>("/root/Main/MoleculeLayer");
		_particleLayers = LoadParticleLayers();
		_tierColors = LoadTierColors();

		// ВАЖНО (было багом): панель сама (этот Control) — обычный Control с
		// mouse_filter по умолчанию Stop, а он "съедает" клики по ВСЕМУ своему
		// прямоугольнику, а не только там, где реально есть кнопка. Ниже был
		// баг, из-за которого этот прямоугольник получался квадратным (сторона
		// = высота грида в ButtonSize*totalRows), хотя грид на самом деле узкий
		// (3 колонки), а не квадратный, — то есть добрая половина мнимого
		// "квадрата" была невидимой мёртвой зоной, блокирующей ЛКМ/ПКМ/пипетку
		// по игровому полю рядом с панелью (см. RecomputeLayout — там же
		// исправлен сам размер). Плюс, отдельно от размера, Ignore тут —
		// подстраховка на будущее: даже если размер опять посчитается неверно
		// (например, добавят ещё кнопок), сама панель больше не будет
		// блокировать клики там, где нет кнопки — их продолжат ловить только
		// дочерние Button (у них свой mouse_filter=Stop, не трогаем).
		MouseFilter = MouseFilterEnum.Ignore;

		RecomputeLayout();
		// TopRight (см. RecomputeLayout) — угловой (точечный) анкор, поэтому
		// сам по себе должен переезжать при изменении размера окна. На деле
		// (см. баг-репорт) через анкоры одни это не всегда происходит
		// надёжно — например, при разворачивании/сворачивании окна панель
		// оставалась на прежнем месте. Поэтому пересчитываем офсеты явно при
		// каждом изменении размера окна, а не полагаемся только на анкоры.
		GetTree().Root.SizeChanged += RecomputeLayout;

		var grid = new GridContainer { Columns = 3 };
		grid.AddThemeConstantOverride("h_separation", 0);
		grid.AddThemeConstantOverride("v_separation", 0);
		AddChild(grid);
		_layer1Grid = grid;

		foreach (int holeCount in HoleCounts)
		{
			foreach (var (label, tier) in Tiers)
			{
				var button = new Button
				{
					Text = $"{label}{holeCount}",
					CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
				};
				if (tier >= 0 && tier < _tierColors.Length)
				{
					var c = _tierColors[tier];
					button.AddThemeColorOverride("font_color", c);
					button.AddThemeColorOverride("font_hover_color", c);
					button.AddThemeColorOverride("font_pressed_color", c);
					button.AddThemeColorOverride("font_focus_color", c);
				}
				int capturedTier = tier;
				int capturedHoles = holeCount;
				button.Pressed += () => OnSpawnPressed(capturedTier, capturedHoles);
				grid.AddChild(button);
			}
		}

		// Отдельная строка — серое (экспериментальное) ядро (см.
		// NucleusLayer.GrayCoreTier/[Export] RequireOwnColorTier в
		// NucleusLayer.cs): та же линейка "количество дырок", что и у обычных
		// тиров, но принимает любую частицу всегда. Теперь это просто ещё один
		// обычный тир (PalettePaths[GrayCoreTier] — настоящая серая палитра),
		// поэтому цвет подписи сэмплируется из _tierColors точно так же, как у
		// остальных тиров, а не берётся отдельной константой.
		int grayTier = _nucleusLayer?.GrayCoreTier ?? 3;
		Color grayColor = (grayTier >= 0 && grayTier < _tierColors.Length) ? _tierColors[grayTier] : GrayButtonColor;
		foreach (int holeCount in HoleCounts)
		{
			var button = new Button
			{
				Text = $"Сер{holeCount}",
				CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
			};
			button.AddThemeColorOverride("font_color", grayColor);
			button.AddThemeColorOverride("font_hover_color", grayColor);
			button.AddThemeColorOverride("font_pressed_color", grayColor);
			button.AddThemeColorOverride("font_focus_color", grayColor);
			int capturedHoles = holeCount;
			button.Pressed += () => OnGraySpawnPressed(capturedHoles);
			grid.AddChild(button);
		}

		// Ещё одна строка — экспериментальный "поворачиватель" (клеточный
		// автомат, см. NucleusLayer.RotatorCoreTier/TriggerRotatorRotation):
		// тоже просто ещё один тир со своей палитрой (palette_green.png), цвет
		// подписи сэмплируется точно так же, как у остальных.
		int rotatorTier = _nucleusLayer?.RotatorCoreTier ?? 4;
		Color rotatorColor = (rotatorTier >= 0 && rotatorTier < _tierColors.Length) ? _tierColors[rotatorTier] : Colors.White;
		foreach (int holeCount in HoleCounts)
		{
			var button = new Button
			{
				Text = $"Пов{holeCount}",
				CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
			};
			button.AddThemeColorOverride("font_color", rotatorColor);
			button.AddThemeColorOverride("font_hover_color", rotatorColor);
			button.AddThemeColorOverride("font_pressed_color", rotatorColor);
			button.AddThemeColorOverride("font_focus_color", rotatorColor);
			int capturedHoles = holeCount;
			button.Pressed += () => OnRotatorSpawnPressed(capturedHoles);
			grid.AddChild(button);
		}

		// Ещё одна строка — экспериментальный "бросатель" (см.
		// NucleusLayer.ThrowerCoreTier/EvaluateFlightStep): тоже просто ещё
		// один тир со своей палитрой (palette_violet.png), цвет подписи
		// сэмплируется точно так же, как у остальных.
		int throwerTier = _nucleusLayer?.ThrowerCoreTier ?? 5;
		Color throwerColor = (throwerTier >= 0 && throwerTier < _tierColors.Length) ? _tierColors[throwerTier] : Colors.White;
		foreach (int holeCount in HoleCounts)
		{
			var button = new Button
			{
				Text = $"Бр{holeCount}",
				CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
			};
			button.AddThemeColorOverride("font_color", throwerColor);
			button.AddThemeColorOverride("font_hover_color", throwerColor);
			button.AddThemeColorOverride("font_pressed_color", throwerColor);
			button.AddThemeColorOverride("font_focus_color", throwerColor);
			int capturedHoles = holeCount;
			button.Pressed += () => OnThrowerSpawnPressed(capturedHoles);
			grid.AddChild(button);
		}

		// 4-я строка — кнопки выбора типа энергии (С/К/Ж). Пресетов "количество
		// дырок" тут нет (энергия — не ядро), поэтому строка всего одна и в ней
		// ровно 3 кнопки — как раз заполняет 3 колонки того же GridContainer.
		// Подпись начинается с "Э", чтобы не путать с кнопками ядер (у тех —
		// просто "С2"/"К4"/"Ж8" и т.п.).
		foreach (var (label, tier) in Tiers)
		{
			var button = new Button
			{
				Text = $"Э{label}",
				CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
			};
			if (tier >= 0 && tier < _tierColors.Length)
			{
				var c = _tierColors[tier];
				button.AddThemeColorOverride("font_color", c);
				button.AddThemeColorOverride("font_hover_color", c);
				button.AddThemeColorOverride("font_pressed_color", c);
				button.AddThemeColorOverride("font_focus_color", c);
			}
			int capturedTier = tier;
			button.Pressed += () => OnEnergyPressed(capturedTier);
			grid.AddChild(button);
		}

		// Следующие строки — по одной кнопке на каждый найденный тир-слой
		// "скопление частиц" (EnergyClusterLayer, см. шапку того файла) — свой
		// независимый, не-Terrain способ раскладки энергии со своей сеткой
		// 96px, взаимоисключающий и с ядрами, и с основным слоем энергии, и
		// друг с другом (сброс выбора — внутри EnergyClusterLayer.SelectClusterMode()).
		// Подпись — своя буква на тир (см. ClusterTierLabel), но нумерация тиров
		// у слоёв частиц теперь СОВПАДАЕТ с Tiers выше (0=жёлтый/1=красный/
		// 2=синий на обеих сторонах, см. NucleusLayer.PalettePaths) — поэтому
		// цвет кнопки берём прямо по индексу layer.Tier, без разворота.
		foreach (var layer in _particleLayers)
		{
			var button = new Button
			{
				Text = $"Ч{ClusterTierLabel(layer.Tier)}",
				CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
			};
			int colorIndex = layer.Tier;
			if (colorIndex >= 0 && colorIndex < _tierColors.Length)
			{
				var c = _tierColors[colorIndex];
				button.AddThemeColorOverride("font_color", c);
				button.AddThemeColorOverride("font_hover_color", c);
				button.AddThemeColorOverride("font_pressed_color", c);
				button.AddThemeColorOverride("font_focus_color", c);
			}
			var capturedLayer = layer;
			button.Pressed += () => OnClusterPressed(capturedLayer);
			grid.AddChild(button);
		}

		// Кнопки молекул — только при включённом слое 2 (T005).
		if (ViewLayer.Layer2Enabled) BuildLayer2Grid();
		ViewLayer.Changed += OnViewLayerChanged;
		ApplyViewLayer();
	}

	// Кнопки молекул слоя 2: те же подписи и цвета, что у ядер слоя 1
	// (Ж/К/С x 2/4/8 и Сер2/4/8), и кнопка чёрной дыры. Поворачиватель и
	// бросатель на слое 2 пока не поддерживаются (T001).
	private void BuildLayer2Grid()
	{
		_layer2Grid = new GridContainer { Columns = 3 };
		_layer2Grid.AddThemeConstantOverride("h_separation", 0);
		_layer2Grid.AddThemeConstantOverride("v_separation", 0);
		AddChild(_layer2Grid);

		int grayTier = _nucleusLayer?.GrayCoreTier ?? 3;
		foreach (int holeCount in HoleCounts)
		{
			foreach (var (label, tier) in Tiers)
				AddMoleculeButton($"{label}{holeCount}", tier, holeCount);
		}
		foreach (int holeCount in HoleCounts)
			AddMoleculeButton($"Сер{holeCount}", grayTier, holeCount);

		// Чёрная дыра (T003) — встроенный объект слоя 2, ставится только здесь.
		var holeButton = new Button
		{
			Text = "ЧД",
			CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
		};
		holeButton.Pressed += () => _moleculeLayer?.SelectBlackHoleTool();
		_layer2Grid.AddChild(holeButton);
	}

	private void AddMoleculeButton(string text, int tier, int holeCount)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(ButtonSize, ButtonSize)
		};
		Color c = (tier >= 0 && tier < _tierColors.Length) ? _tierColors[tier] : GrayButtonColor;
		button.AddThemeColorOverride("font_color", c);
		button.AddThemeColorOverride("font_hover_color", c);
		button.AddThemeColorOverride("font_pressed_color", c);
		button.AddThemeColorOverride("font_focus_color", c);
		button.Pressed += () => _moleculeLayer?.SelectPreset(tier, holeCount);
		_layer2Grid.AddChild(button);
	}

	private void OnViewLayerChanged(int layer) => ApplyViewLayer();

	private void ApplyViewLayer()
	{
		bool layer2 = ViewLayer.IsLayer2;
		if (_layer1Grid != null) _layer1Grid.Visible = !layer2;
		if (_layer2Grid != null) _layer2Grid.Visible = layer2;
		RecomputeLayout();
	}

	// Находит все узлы со скриптом EnergyClusterLayer среди детей Main (по
	// типу, а не по жёстко зашитым именам узлов — устойчиво к переименованию)
	// и сортирует по Tier, чтобы порядок кнопок был стабильным и предсказуемым
	// независимо от порядка узлов в дереве сцены.
	private List<EnergyClusterLayer> LoadParticleLayers()
	{
		var result = new List<EnergyClusterLayer>();
		var main = GetNodeOrNull<Node>("/root/Main");
		if (main == null)
		{
			GD.PrintErr("[NucleusSpawnPanel] не найден узел Main — кнопки скопления частиц недоступны.");
			return result;
		}
		foreach (var child in main.GetChildren())
			if (child is EnergyClusterLayer layer)
				result.Add(layer);
		result.Sort((a, b) => a.Tier.CompareTo(b.Tier));
		return result;
	}

	// Своя буква на тир скопления частиц (нумерация теперь совпадает с Tiers
	// выше, см. комментарий у цикла построения кнопок). Для тира вне 0..2
	// (если добавится четвёртый) просто печатаем номер, чтобы кнопка не
	// осталась без подписи.
	private static string ClusterTierLabel(int tier) => tier switch
	{
		0 => "Ж", // жёлтый
		1 => "К", // красный
		2 => "С", // синий
		_ => tier.ToString(),
	};

	// Сэмплирует по одному представительному цвету из каждой палитровой
	// текстуры тира (PalettePaths — узкие 1-строчные полоски-градиенты, см.
	// NucleusLayer.BuildPaletteAtlas) — берём пиксель из середины полоски.
	// Если NucleusLayer ещё не найден или палитра не загрузилась — тихо
	// откатываемся на путь по умолчанию/белый цвет, чтобы панель не падала.
	private Color[] LoadTierColors()
	{
		string[] paths = _nucleusLayer?.PalettePaths ?? new string[]
		{
			"res://Resources/Textures/palette_yellow.png",
			"res://Resources/Textures/palette_blue.png",
			"res://Resources/Textures/palette_red.png",
		};

		var colors = new Color[paths.Length];
		for (int i = 0; i < paths.Length; i++)
		{
			var tex = GD.Load<Texture2D>(paths[i]);
			if (tex == null)
			{
				GD.PrintErr($"[NucleusSpawnPanel] не загрузилась палитра {paths[i]} — цвет кнопки будет белым.");
				colors[i] = Colors.White;
				continue;
			}
			var img = tex.GetImage();
			img.Convert(Image.Format.Rgba8);
			int midX = img.GetWidth() / 2;
			colors[i] = img.GetPixel(midX, 0);
		}
		return colors;
	}

	private void OnSpawnPressed(int tier, int holeCount)
	{
		// Сам спавн (создание NucleusEntity и т.д.) — в NucleusLayer, эта
		// кнопка только выбирает пресет, а установка на поле — по ЛКМ
		// (см. NucleusLayer._UnhandledInput/TryPlaceNucleus).
		if (_nucleusLayer == null)
		{
			GD.PrintErr("[NucleusSpawnPanel] не найден NucleusLayer — выбор пресета невозможен.");
			return;
		}
		_nucleusLayer.SelectSpawnPreset(tier, holeCount);
	}

	private void OnGraySpawnPressed(int holeCount)
	{
		// Аналогично OnSpawnPressed — серое ядро теперь просто ещё один тир
		// (GrayCoreTier), без отдельного флага: рендер и скорость вращения
		// берутся из PalettePaths/TierTicks по этому индексу как обычно, а
		// особое игровое правило "принимает любой цвет" живёт в
		// NucleusLayer.ColorAccepted, не здесь.
		if (_nucleusLayer == null)
		{
			GD.PrintErr("[NucleusSpawnPanel] не найден NucleusLayer — выбор пресета невозможен.");
			return;
		}
		_nucleusLayer.SelectSpawnPreset(_nucleusLayer.GrayCoreTier, holeCount);
	}

	private void OnRotatorSpawnPressed(int holeCount)
	{
		// Аналогично OnGraySpawnPressed — поворачиватель тоже просто ещё один
		// тир (RotatorCoreTier), особое поведение (проворачивает соседей по
		// часовой стрелке) живёт в NucleusLayer.SimTick/TriggerRotatorRotation.
		if (_nucleusLayer == null)
		{
			GD.PrintErr("[NucleusSpawnPanel] не найден NucleusLayer — выбор пресета невозможен.");
			return;
		}
		_nucleusLayer.SelectSpawnPreset(_nucleusLayer.RotatorCoreTier, holeCount);
	}

	private void OnThrowerSpawnPressed(int holeCount)
	{
		// Аналогично OnRotatorSpawnPressed — бросатель тоже просто ещё один
		// тир (ThrowerCoreTier), особое поведение (толкает соседей и потом
		// ещё запускает их в полёт) живёт в NucleusLayer.SimTick/
		// TriggerRotatorRotation/EvaluateFlightStep.
		if (_nucleusLayer == null)
		{
			GD.PrintErr("[NucleusSpawnPanel] не найден NucleusLayer — выбор пресета невозможен.");
			return;
		}
		_nucleusLayer.SelectSpawnPreset(_nucleusLayer.ThrowerCoreTier, holeCount);
	}

	private void OnEnergyPressed(int tier)
	{
		// Аналогично OnSpawnPressed — сама установка энергии на поле в
		// EnergyLayer, эта кнопка только выбирает тип (см.
		// EnergyLayer._UnhandledInput/TryPlaceAtMouseIfSelected).
		if (_energyLayer == null)
		{
			GD.PrintErr("[NucleusSpawnPanel] не найден EnergyLayer — выбор типа энергии невозможен.");
			return;
		}
		_energyLayer.SelectEnergyType(tier);
	}

	private void OnClusterPressed(EnergyClusterLayer layer)
	{
		// Аналогично OnEnergyPressed — сама установка на поле в конкретном
		// тир-слое EnergyClusterLayer, эта кнопка только включает его режим
		// (см. EnergyClusterLayer._UnhandledInput/TryPlaceAtMouseIfSelected).
		// Ссылка на layer гарантированно не null — кнопка создаётся только
		// для уже найденных узлов (см. LoadParticleLayers).
		layer.SelectClusterMode();
	}
}
