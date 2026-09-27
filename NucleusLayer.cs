using Godot;
using System.Collections.Generic;

// Мир — не заранее заданное поле Rows x Cols, а разреженная сетка чанков:
// Dictionary<(cx,cy), WorldChunk>. Чанк создаётся ЛЕНИВО — в первый момент,
// когда его прямоугольник впервые пересёкся с видимой областью камеры (см.
// GetOrCreateChunk, вызывается только из _Process).
//
// На чанк — три MultiMeshInstance2D:
//   Node          — тела ядер (палитра через custom data, см. BuildPaletteAtlas)
//   HoleNode      — "дырки" кольца: обычная полупрозрачная текстура, без шейдера
//   ParticleNode  — "частицы" кольца: та же палитровая перекраска, что и ядро
//
// У каждого ядра — НАСТОЯЩЕЕ кольцо из 8 ФИЗИЧЕСКИХ слотов (Ring[8]) — индекс
// слота совпадает с индексом рендера (слот k всегда рисуется под углом
// baseAngle+k*45° и физически никогда не переставляется — только реальные
// передачи меняют содержимое конкретного физического слота). Каждый слот —
// либо дырка, либо частица определённого цвета (RingSlot).
//
// Поворот кольца (тики TierTicks[tier]) не переставляет Ring — вместо этого
// какой физический слот сейчас "смотрит" на какую сторону света вычисляется
// напрямую из (_globalTick, тир) в DiscreteRotationOffset, БЕЗ отдельного
// состояния на ядро (раньше был накопительный счётчик NucleusEntity.
// RotationOffset, стартующий с 0 в момент спавна — из-за этого ядра одного
// тира, появившиеся в разные моменты, расходились по фазе). Разделение угла
// рендера и физического индекса слота принципиально по другой причине: если
// бы поворот переставлял сам массив, слот одновременно получал бы и
// непрерывный сдвиг угла от рендера, и мгновенный скачок индекса — визуально
// это выглядело как частица, телепортирующаяся в соседнюю дырку (реальный
// баг, который тут был и почему сделано именно так).
//
// Раз в тик (PrototypeTickMs) вызывается SimTick(): сначала граница поворота
// (снятие блокировки "остывания" у ядер, чей тир как раз "щёлкает" —
// TierTicks[tier], см. OnRotationTick), потом передача частиц между соседними
// ядрами по умолчательным правилам прототипа:
// только по ортогоналям (без диагоналей, см. Adj8/OrthogonalSlots — это уже
// индексы сторон света, компас, а не физические слоты), получатель должен
// вращаться в ту же сторону, что и отдающий (requireMatchingSpin), только что
// полученная частица на тик "заблокирована" и разблокируется при следующем
// повороте этого же ядра — так же, как в JS-прототипе.
//
// Рендер слота — плавно вращающаяся точка на орбите, angle = baseAngle(t) +
// k*45°, где k — физический индекс в Ring; что показывать в каждом слоте
// (дырку или частицу нужного цвета) решается каждый кадр по live-данным Ring.
public partial class NucleusLayer : Node2D
{
	// CellSize — не свой экспорт, единственный источник истины — GridDraw.
	public int CellSize { get; private set; }

	[Export] public int SpriteSize = 32; // нативный размер спрайта ядра
	[Export] public int ChunkSize = 16; // сторона чанка в клетках
	[Export] public float CullMargin = 128f; // запас в пикселях вокруг видимой области камеры
	[Export] public float FillDensity = 0.5f; // доля клеток чанка, получающих ядро при генерации (тест)
	// T — переключает это в рантайме (см. _Input). По умолчанию выключено:
	// новые чанки генерируются ПУСТЫМИ, чтобы поле заполнялось только вручную
	// через панель спавна (см. TryPlaceNucleus) — раньше это было единственным
	// режимом и мешало тестировать конкретные расстановки.
	[Export] public bool RandomFillEnabled = false;

	[Export] public string CoreSpritePath = "res://nuclear_core_gray_32px.png";
	[Export] public string ShaderPath = "res://nucleus_palette.gdshader";
	[Export] public string[] PalettePaths = new string[]
	{
		"res://palette_yellow.png",
		"res://palette_red.png",
		"res://palette_blue.png",
		"res://palette_gray.png", // тир 3 — экспериментальное серое ядро, см. GrayCoreTier
	};

	[Export] public string HoleSpritePath = "res://hole2_16px.png";
	[Export] public int HoleSpriteSize = 16;
	[Export] public float HoleOpacity = 0.5f;
	[Export] public float OrbitDiameterCoef = 1f; // диаметр орбиты кольца = CellSize * этот коэффициент
	[Export] public int SpinDirection = 1;
	[Export] public int[] TierTicks = new int[] { 16, 8, 4, 16 }; // тики на шаг поворота (45°) по тирам — значения по вашему заданию (было 8,4,1 из дефолтного прототипа); 4-е значение — тир GrayCoreTier, та же скорость, что у тира 0
	// Экспериментальное серое ядро (см. обсуждение в чате) — не отдельный
	// ортогональный флаг на NucleusEntity, а просто ЕЩЁ ОДИН тир: 4-я
	// палитра (PalettePaths[3] = palette_gray.png, реально серая картинка) и
	// 4-е значение TierTicks (та же скорость, что у тира 0). Рендер тела и
	// частиц не знает о "серости" вообще — CoreTier=GrayCoreTier просто
	// выбирает нужную строку в атласе палитр, как и любой другой тир, без
	// какого-либо ветвления в шейдере или в C#. "Принимает любой цвет" —
	// единственное, что у него особенное, и это чисто игровое правило (см.
	// ColorAccepted), не имеющее отношения к рендеру.
	[Export] public int GrayCoreTier = 3;
	[Export] public float PrototypeTickMs = 16f;
	// Дырки менее информативны, чем частицы, и их визуально намного больше —
	// поэтому свой порог отключения: слой дырок гаснет раньше (при более
	// сильном отдалении камеры), чем слой частиц (см. ParticleHideZoom).
	[Export] public float HoleHideZoom = 0.25f;

	// --- частицы ---
	[Export] public string ParticleSpritePath = "res://particle_grаy_16px.png";
	[Export] public int ParticleSpriteSize = 16;
	[Export] public float ParticleFillChance = 0.5f; // доля слотов кольца (из 8), становящихся частицей вместо дырки при генерации
	// При Zoom.X меньше этого значения слой частиц вообще не считается и не
	// рисуется (на таком отдалении это всё равно неразличимые точки, а
	// чанков видно много — экономим CPU/GPU). Было 0.1 (частицы гасли позже
	// дырок) — поднято до 0.25, вровень с HoleHideZoom: раз симуляция ядер
	// теперь не завязана на видимость камеры (см. _activeSet), при большом
	// количестве ядер именно рендер частиц/дырок остаётся единственным, что
	// ещё можно дёшево срезать по зуму, не трогая саму симуляцию.
	[Export] public float ParticleHideZoom = 0.25f;

	// --- симуляция передачи частиц (правила из JS-прототипа) ---
	// requireMatchingSpin=true, diagonalTransfer=false, requireLowerTotal=false,
	// requireLowerColorTotal=false — дефолтные значения CONFIG прототипа, жёстко
	// зашиты, других профилей пока нет. RequireColorMatch — единственное
	// исключение: в прототипе по умолчанию OFF, но по вашему запросу включено
	// здесь (см. [Export] ниже) — ядро, уже держащее один цвет, отказывается
	// принимать другой, пока не опустеет полностью.
	[Export] public bool RequireColorMatch = true;

	// ЭКСПЕРИМЕНТАЛЬНЫЙ флаг (одна настройка, один переключатель — по
	// заданию): когда включён, обычное (не серое) ядро принимает только
	// частицы СВОЕГО цвета — то есть цвет частицы должен совпадать с
	// CoreTier этого ядра (0=Ж/1=К/2=С, та же нумерация, что и у
	// RingSlot.ColorTier — см. NucleusSpawnPanel.Tiers/PalettePaths), а не с
	// тем, что ядро уже держит в кольце (это отдельная, уже существующая
	// проверка — RequireColorMatch, см. выше). Проверки независимы и
	// действуют одновременно: RequireColorMatch не даёт держать в кольце два
	// разных цвета сразу, RequireOwnColorTier — не даёт держать цвет,
	// отличный от собственного тира ядра. Серое ядро (CoreTier==GrayCoreTier)
	// — единственное исключение из ОБЕИХ проверок сразу, см. ColorAccepted.
	[Export] public bool RequireOwnColorTier = false;

	// --- захват энергии из источников частиц (EnergyClusterLayer) ---
	// Интервал захвата ОДИНАКОВ для любого тира ядра (в отличие от
	// TierTicks[CoreTier], который управляет только поворотом кольца этого
	// конкретного тира) — по заданию, TierTicks_уровня_0 * 8. Считается один
	// раз в _Ready из ТЕКУЩЕГО TierTicks[0] (после того, как экспортируемые
	// поля уже применены редактором), а не хранится как независимая
	// константа — так если TierTicks[0] поменяют, интервал захвата само
	// собой пересчитается при следующем запуске.
	private int _energyCaptureTicks;
	// Сколько частиц забирается из кластера-источника за одну успешную
	// попытку захвата (см. _energyCaptureTicks) — отдельный, самостоятельный
	// баланс-параметр, не завязанный на InitialAmount у EnergyClusterLayer.
	[Export] public int EnergyCaptureAmount = 1;

	private static readonly (int dr, int dc)[] Adj8 =
	{
		(-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1)
	};
	private static readonly int[] OrthogonalSlots = { 0, 2, 4, 6 }; // N,E,S,W — диагонали (нечётные) выключены (diagonalTransfer=false)
	private static int Opposite(int k) => (k + 4) % 8;

	// Для HUD/отладки. TotalChunkCount — сколько чанков сгенерировано ЗА ВСЁ
	// ВРЕМЯ (растёт по мере исследования мира, не размер мира целиком).
	public int VisibleChunkCount => _visible.Count;
	public int TotalChunkCount => _chunks.Count;
	// Сколько ядер сейчас участвует в симуляции (SimTick бежит по _activeSet —
	// см. комментарий там же) — теперь это ВСЕ живые ядра, а не только
	// видимые, так что в норме совпадает с TotalChunkCount-суммой ядер по
	// всем чанкам. Название и HUD (см. FpsLabel) намеренно не переименовывал —
	// после разделения "симулируется"/"видимо" это то же самое число, что и
	// "живо", просто с исторически прежним именем.
	public int ActiveNucleusCount => _activeSet.Count;
	// UPS — сколько раз в секунду реально отработал SimTick() (не путать с
	// FPS кадров рендера): считается скользящим окном ~1 секунда, а не просто
	// 1000/PrototypeTickMs, чтобы было видно, если тики начинают "тормозить"
	// и досрочно упираются в guard в _Process (см. цикл там же).
	public float CurrentUPS { get; private set; }

	private struct RingSlot
	{
		public bool Exists;   // false — физического слота тут вообще нет (не рисуется ни дыркой,
		                      // ни частицей, не участвует в передаче). Используется ядрами со
		                      // спавн-панели с числом гнёзд меньше 8 (см. BuildFixedRing) — у
		                      // обычных (случайно сгенерированных) ядер всегда true на все 8.
		public bool IsHole;   // значим, только если Exists
		public int ColorTier; // значим, только если Exists и !IsHole
		public bool Locked;   // только что принятая частица — нельзя отдать дальше до следующего поворота этого ядра
	}

	private class NucleusEntity
	{
		public int Row, Col; // мировые координаты клетки — ключ в _entAt
		public Vector2 Center;
		// CoreTier == GrayCoreTier — экспериментальное "серое" ядро (см.
		// [Export] GrayCoreTier в шапке файла): для рендера и скорости
		// вращения это просто ещё один обычный тир (своя строка в атласе
		// палитр — уже серая картинка, PalettePaths[GrayCoreTier], и своя
		// запись в TierTicks) — никакого отдельного флага/ветвления в рендере
		// не нужно. Единственное, что у него особенное, — игровое правило в
		// ColorAccepted: он принимает частицу любого цвета безусловно,
		// независимо от RequireColorMatch/RequireOwnColorTier.
		public int CoreTier;
		public int Dir; // направление вращения (для requireMatchingSpin); пока = SpinDirection у всех
		// Ring индексирован ФИЗИЧЕСКИМ слотом (k=0..7 — тот же индекс, что и в
		// рендере: физический слот k всегда рисуется под углом baseAngle+k*45°
		// и НИКОГДА не переставляется поворотом — иначе, помимо непрерывного
		// вращения угла, содержимое ещё и прыгало бы между индексами, что и
		// давало эффект "телепортации" частицы в соседнюю дырку). Ориентация
		// кольца (какой физический слот сейчас смотрит на какую сторону света)
		// НЕ хранится тут как отдельное состояние — раньше это было
		// накопительное поле RotationOffset, стартующее с 0 в момент спавна, и
		// из-за этого ядра одного тира, появившиеся на поле в разные моменты,
		// расходились по фазе (см. DiscreteRotationOffset). Теперь ориентация
		// — чистая функция (_globalTick, CoreTier), одинаковая для всех ядер
		// тира одновременно, независимо от истории конкретного ядра.
		public RingSlot[] Ring; // 8 физических слотов
		// Тик, начиная с которого ЭТОМУ ядру снова можно захватывать энергию из
		// источника (см. Шаг 3 в SimTick). Раньше вместо этого поля вся попытка
		// захвата была завёрнута в общее условие "_globalTick % _energyCaptureTicks
		// == 0" — но т.к. _energyCaptureTicks кратен полному периоду вращения
		// кольца ЛЮБОГО тира (128 = 8*16 = 8*8*2 = 8*4*4), проверка каждый раз
		// приходилась на ОДНУ И ТУ ЖЕ фазу поворота — ядро вечно "видело" только
		// одну и ту же дырку в нужном направлении и захват срабатывал максимум
		// один раз (стробоскопический эффект). Индивидуальный кулдаун на ядро
		// (а не глобальный тик-модуль на всех сразу) убирает эту привязку к фазе.
		public long NextCaptureTick;
		public int LocalIndex; // индекс в WorldChunk.Nuclei — для адресации инстансов MultiMesh (localIndex*8+slot)
	}

	private class WorldChunk
	{
		public MultiMeshInstance2D Node;
		public MultiMeshInstance2D HoleNode;
		public MultiMeshInstance2D ParticleNode;
		public Rect2 WorldRect;
		public List<NucleusEntity> Nuclei;
	}

	private readonly Dictionary<(int cx, int cy), WorldChunk> _chunks = new();
	private readonly HashSet<(int cx, int cy)> _visible = new();

	// Настоящая модель поля: что физически существует в каждой клетке —
	// нужно для поиска соседей при передаче частиц (независимо от чанков).
	private readonly Dictionary<(int row, int col), NucleusEntity> _entAt = new();
	// ВСЕ живые ядра — участвуют в симуляции (SimTick: поворот, передача,
	// захват энергии, поглощение чёрной дырой) независимо от того, виден ли
	// их чанк камере. Раньше это множество наполнялось/чистилось по
	// видимости чанка в _Process (ядра за кадром экрана были полностью
	// "заморожены") — по факту это был баг, а не намеренное поведение:
	// _activeSet отвечал одновременно и за "что рисовать", и за "что
	// тикать", хотя это два разных вопроса. Теперь наполняется/чистится
	// только в местах создания/удаления ядра (PlaceNucleusAt,
	// RemoveNucleusAt, генерация чанка) — видимость (_visible) осталась
	// чисто рендерным понятием и на это множество больше не влияет.
	private readonly HashSet<NucleusEntity> _activeSet = new();

	private Texture2D _coreTexture;
	private Texture2D _holeTexture;
	private Texture2D _particleTexture;
	private ShaderMaterial _material; // общий и для ядра, и для частиц — один и тот же шейдер/атлас палитр
	private QuadMesh _coreQuad;
	private QuadMesh _holeQuad;
	private QuadMesh _particleQuad;
	private RandomNumberGenerator _rng;
	private float _orbitRadius;
	private int _tierCount;
	private float _chunkWorldSize;
	private bool _ready;

	// Глобальные часы симуляции — независимы от FPS, шаг ровно PrototypeTickMs.
	private double _tickAccumulatorMs;
	private long _globalTick;
	private readonly HashSet<(NucleusEntity ent, int slot)> _claimed = new();

	// Измерение UPS — сколько раз в секунду реально отработал SimTick(),
	// скользящим окном примерно в 1 секунду реального времени (а не кадров).
	private double _upsWindowTimer;
	private int _upsWindowTicks;

	// Пересчитываются раз в кадр в _Process из текущего зума камеры —
	// независимо друг от друга, у дырок и частиц разные пороги отключения.
	private bool _holesVisible = true;
	private bool _particlesVisible = true;
	// H — ручной тумблер поверх автоматического скрытия по зуму: итоговая
	// видимость дырок — это (зум не ниже HoleHideZoom) И (не скрыто вручную).
	private bool _holesManuallyHidden;

	// Выбор ядра на панели спавна (см. NucleusSpawnPanel.SelectSpawnPreset) —
	// null, пока ничего не выбрано, тогда ЛКМ по полю ничего не делает.
	private int? _selectedSpawnTier;
	private int _selectedSpawnHoleCount;
	// Удержание ЛКМ — чтобы не приходилось кликать по каждой клетке отдельно
	// (см. _UnhandledInput/TryPlaceAtMouseIfSelected): пока кнопка зажата,
	// каждый кадр в _Process пробуем поставить ядро под курсором, но не
	// повторяем попытку для той же самой клетки, пока курсор из неё не ушёл
	// (иначе — спам одинаковых попыток на неподвижной мыши).
	private bool _leftMouseHeld;
	private (int row, int col)? _lastPlacedCell;
	// ПКМ — удаление ядра под курсором (см. TryRemoveAtMouse), тем же
	// принципом удержания, что и установка: можно провести мышью с зажатой
	// ПКМ, чтобы стереть сразу несколько ядер, не кликая по каждому отдельно.
	private bool _rightMouseHeld;
	private (int row, int col)? _lastRemovedCell;
	// Полупрозрачный превью выбранного ядра под курсором — см.
	// UpdatePlacementPreview. ZIndex выше нуля, чтобы быть поверх чанков
	// (MultiMeshInstance2D чанков добавляются позже как дочерние узлы этого же
	// Node2D, и без ZIndex превью, созданный в _Ready, рисовался бы под ними).
	private Sprite2D _placementPreview;
	private Color[] _tierPreviewColors;
	// Слой энергии — полностью независимая структура данных (см.
	// EnergyLayer), но выбор на панели спавна взаимоисключающий: если выбран
	// тип энергии, выбор ядра нужно сбросить, и наоборот (иначе ЛКМ было бы
	// не ясно, что именно ставить). Ссылка нужна только для этого сброса.
	private EnergyLayer _energyLayer;
	// Второй, экспериментальный способ отображения энергии (см.
	// EnergyClusterLayer) — та же причина для ссылки, что и у _energyLayer:
	// взаимоисключающий сброс выбора. В сцене на каждый тир частиц — свой
	// отдельный узел с этим скриптом (см. шапку EnergyClusterLayer.cs), поэтому
	// список, а не одна ссылка; находится автоматически по типу скрипта среди
	// узлов того же родителя, а не по жёстко зашитым именам.
	private List<EnergyClusterLayer> _energyClusterLayers = new();
	// Чёрная дыра (см. BlackHoleLayer) — терраин-объект, не ядро: уничтожает
	// частицу в кольцевом слоте ядра, ориентированном на соседнюю клетку с
	// чёрной дырой (см. отдельный шаг поглощения в SimTick). Ссылка нужна и
	// для этого шага, и для взаимоисключающего сброса выбора на панели спавна
	// (та же причина, что и у _energyLayer/_energyClusterLayers).
	private BlackHoleLayer _blackHoleLayer;
	// Порядок специально подобран так, чтобы N штук дырок ложились симметрично:
	// первые 2 (0,4) — противоположная пара (N/S, "напротив друг друга" для С2);
	// первые 4 (0,4,2,6) — все 4 стороны света под 90° (С4); все 8 — просто всё
	// кольцо (С8). Если брать по порядку {0,2,4,6,...}, для 2 штук получались бы
	// соседние 90°-слоты, а не противоположные — было неверно, отсюда и правка.
	private static readonly int[] HolePriority = { 0, 4, 2, 6, 1, 5, 3, 7 };

	public override void _Ready()
	{
		var gridDraw = GetNode<GridDraw>("../GridLayer");
		CellSize = gridDraw.CellSize;
		_chunkWorldSize = ChunkSize * CellSize;
		_orbitRadius = CellSize * 0.5f * OrbitDiameterCoef;
		_energyCaptureTicks = (TierTicks.Length > 0 ? TierTicks[0] : 16) * 8;

		SetProcessInput(true);
		SetProcessUnhandledInput(true);

		_coreTexture = GD.Load<Texture2D>(CoreSpritePath);
		_holeTexture = GD.Load<Texture2D>(HoleSpritePath);
		_particleTexture = GD.Load<Texture2D>(ParticleSpritePath);
		var shader = GD.Load<Shader>(ShaderPath);

		if (_coreTexture == null || shader == null || _holeTexture == null || _particleTexture == null)
		{
			GD.PrintErr("NucleusLayer: не загрузился один из спрайтов или шейдер — проверьте пути res://.");
			return;
		}

		var paletteAtlas = BuildPaletteAtlas(PalettePaths);
		if (paletteAtlas == null) return;

		_tierCount = PalettePaths.Length;
		_material = new ShaderMaterial { Shader = shader };
		_material.SetShaderParameter("palette_tex", paletteAtlas);

		_coreQuad = new QuadMesh { Size = new Vector2(SpriteSize, SpriteSize) };
		_holeQuad = new QuadMesh { Size = new Vector2(HoleSpriteSize, HoleSpriteSize) };
		_particleQuad = new QuadMesh { Size = new Vector2(ParticleSpriteSize, ParticleSpriteSize) };

		_rng = new RandomNumberGenerator();
		_rng.Randomize();

		_tierPreviewColors = SampleTierColors(PalettePaths);
		_placementPreview = new Sprite2D
		{
			Texture = _coreTexture,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Visible = false,
			ZIndex = 100 // поверх чанков, см. комментарий у поля
		};
		AddChild(_placementPreview);

		_energyLayer = GetNodeOrNull<EnergyLayer>("../TileMapLayer");
		_blackHoleLayer = GetNodeOrNull<BlackHoleLayer>("../BlackHoleLayer");
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer clusterLayer)
				_energyClusterLayers.Add(clusterLayer);

		_ready = true;
		GD.Print($"[NucleusLayer] инициализирован. FillDensity={FillDensity}, ParticleFillChance={ParticleFillChance}.");
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.H)
			{
				_holesManuallyHidden = !_holesManuallyHidden;
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.T)
			{
				RandomFillEnabled = !RandomFillEnabled;
				GD.Print($"[NucleusLayer] случайное заполнение новых чанков: {(RandomFillEnabled ? "включено" : "выключено")}.");
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.Q)
			{
				PickNucleusUnderMouse();
				GetViewport().SetInputAsHandled();
			}
		}
	}

	// Пипетка (Q) — берёт тир и количество гнёзд ядра под курсором и сразу
	// выбирает их текущим пресетом для установки, как будто нажали
	// соответствующую кнопку на панели спавна (см. SelectSpawnPreset) —
	// удобно быстро "скопировать" уже стоящее на поле ядро (в т.ч. серое,
	// см. GrayCoreTier — пипетка тут ничем не отличается от обычного тира),
	// не подбирая его вручную на панели. Если под курсором пусто — просто
	// сообщение в лог, выбор (если был) не трогаем.
	private void PickNucleusUnderMouse()
	{
		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);

		if (!_entAt.TryGetValue((row, col), out var nucleus))
		{
			GD.Print($"[NucleusLayer] пипетка: в клетке ({row},{col}) нет ядра.");
			return;
		}

		int holeCount = 0;
		foreach (var slot in nucleus.Ring)
			if (slot.Exists) holeCount++;

		SelectSpawnPreset(nucleus.CoreTier, holeCount);
	}

	// _UnhandledInput (а не _Input) — намеренно: клик по кнопке на панели
	// спавна уже "съедается" GUI-системой и НЕ доходит сюда, так что нажатие
	// самой кнопки не ставит ещё и ядро под курсором заодно. ЛКМ — установка
	// (не только по мгновенному клику, но и всё время, пока зажата, см.
	// _leftMouseHeld/TryPlaceAtMouseIfSelected). ПКМ — удаление ядра под
	// курсором, тем же принципом удержания (см. _rightMouseHeld/
	// TryRemoveAtMouse), и НЕ требует выбранного пресета на панели.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mb) return;

		if (mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				if (!_selectedSpawnTier.HasValue) return;
				_leftMouseHeld = true;
				_lastPlacedCell = null; // разрешаем установку в клетку под курсором сразу же
				TryPlaceAtMouseIfSelected();
				GetViewport().SetInputAsHandled();
			}
			else
			{
				_leftMouseHeld = false;
			}
		}
		else if (mb.ButtonIndex == MouseButton.Right)
		{
			if (mb.Pressed)
			{
				_rightMouseHeld = true;
				_lastRemovedCell = null; // разрешаем удаление клетки под курсором сразу же
				TryRemoveAtMouse();
				GetViewport().SetInputAsHandled();
			}
			else
			{
				_rightMouseHeld = false;
			}
		}
	}

	// Вызывается с панели спавна (NucleusSpawnPanel) при нажатии одной из
	// кнопок 3x3 (или кнопки серого ядра, которое теперь просто передаёт
	// tier=GrayCoreTier — см. NucleusSpawnPanel.OnGraySpawnPressed) —
	// запоминает, что ставить следующим ЛКМ (или удержанием ЛКМ) по полю.
	// Сбрасывает выбор энергии (см. EnergyLayer.SelectEnergyType) — выбор
	// ядра и энергии взаимоисключающий, иначе ЛКМ было бы не ясно, что именно
	// ставить.
	public void SelectSpawnPreset(int tier, int holeCount)
	{
		_selectedSpawnTier = tier;
		_selectedSpawnHoleCount = holeCount;
		_lastPlacedCell = null;
		_energyLayer?.ClearSelection();
		_blackHoleLayer?.ClearSelection();
		foreach (var clusterLayer in _energyClusterLayers) clusterLayer.ClearSelection();
		GD.Print($"[NucleusLayer] выбрано для установки: тир {tier}, дырок {holeCount}/8. Клик (или удержание ЛКМ) по полю — поставить.");
	}

	// Вызывается EnergyLayer при выборе типа энергии на панели — сбрасывает
	// выбор ядра (см. комментарий у SelectSpawnPreset).
	public void ClearSelection()
	{
		_selectedSpawnTier = null;
	}

	// Общая точка входа и для одиночного клика, и для каждого кадра при
	// удержании ЛКМ (см. _UnhandledInput/_Process) — не даёт повторно
	// пытаться поставить ядро в ту же самую клетку, пока курсор из неё не
	// ушёл (иначе при удержании на месте — спам одинаковых "уже занята").
	private void TryPlaceAtMouseIfSelected()
	{
		if (!_selectedSpawnTier.HasValue) return;

		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);
		if (_lastPlacedCell.HasValue && _lastPlacedCell.Value == (row, col)) return;

		_lastPlacedCell = (row, col);
		TryPlaceNucleus(worldPos, _selectedSpawnTier.Value, _selectedSpawnHoleCount);
	}

	// Общая точка входа и для одиночного ПКМ-клика, и для каждого кадра при
	// удержании ПКМ (см. _UnhandledInput/_Process) — тем же принципом, что и
	// установка: не повторяем попытку для той же самой клетки, пока курсор из
	// неё не ушёл.
	private void TryRemoveAtMouse()
	{
		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);
		if (_lastRemovedCell.HasValue && _lastRemovedCell.Value == (row, col)) return;

		_lastRemovedCell = (row, col);
		RemoveNucleusAt(row, col);
	}

	// Удаляет ядро (если оно там есть) из клетки (row, col): убирает из
	// _entAt, _activeSet, из списка владеющего чанка и полностью пересобирает
	// его MultiMesh'и (см. RebuildChunkMeshes — Godot сбрасывает ВСЕ
	// инстансы при смене InstanceCount, точечно удалить один инстанс нельзя).
	// ВАЖНО: после удаления LocalIndex у ВСЕХ оставшихся ядер этого чанка
	// нужно пересчитать по их новой позиции в списке — иначе он будет
	// указывать на индексы инстансов, которых после пересборки уже не будет
	// (или которые сместились), и рендер/симуляция начнут путать слоты.
	private void RemoveNucleusAt(int row, int col)
	{
		if (!_entAt.TryGetValue((row, col), out var nucleus)) return;

		int cx = Mathf.FloorToInt((float)col / ChunkSize);
		int cy = Mathf.FloorToInt((float)row / ChunkSize);

		_entAt.Remove((row, col));
		_activeSet.Remove(nucleus);

		if (!_chunks.TryGetValue((cx, cy), out var chunk))
		{
			GD.PrintErr($"[NucleusLayer] у ядра в клетке ({row},{col}) не найден владеющий чанк — удалено только из общих структур.");
			return;
		}

		chunk.Nuclei.Remove(nucleus);
		for (int i = 0; i < chunk.Nuclei.Count; i++) chunk.Nuclei[i].LocalIndex = i;
		RebuildChunkMeshes(chunk);

		GD.Print($"[NucleusLayer] удалено ядро из клетки ({row},{col}).");
	}

	public override void _Process(double delta)
	{
		if (!_ready) return;

		var cam = GetViewport().GetCamera2D();
		if (cam == null) return;

		var viewportSize = GetViewport().GetVisibleRect().Size;
		var visibleSize = viewportSize / cam.Zoom;
		var visiblePos = cam.GetScreenCenterPosition() - visibleSize / 2f;
		var visibleRect = new Rect2(visiblePos, visibleSize).Grow(CullMargin);

		int minCx = Mathf.FloorToInt(visibleRect.Position.X / _chunkWorldSize);
		int maxCx = Mathf.FloorToInt((visibleRect.Position.X + visibleRect.Size.X) / _chunkWorldSize);
		int minCy = Mathf.FloorToInt(visibleRect.Position.Y / _chunkWorldSize);
		int maxCy = Mathf.FloorToInt((visibleRect.Position.Y + visibleRect.Size.Y) / _chunkWorldSize);

		var newVisible = new HashSet<(int cx, int cy)>();
		for (int cy = minCy; cy <= maxCy; cy++)
			for (int cx = minCx; cx <= maxCx; cx++)
				newVisible.Add((cx, cy));

		// Ниже своего порога каждый слой — неразличимые точки, а видимых
		// чанков уже много, поэтому его рендер (и пересчёт каждый кадр) просто
		// выключается целиком, а не только прячется по чанку (см.
		// HoleHideZoom/ParticleHideZoom — сейчас оба 0.25, но пороги
		// независимые, так что могут снова разойтись). Тела ядер (chunk.Node)
		// это не касается — они остаются видимы всегда. H дополнительно
		// ручками гасит дырки поверх автоматики по зуму (см. _Input).
		//
		// ВАЖНО: этот блок и весь _visible ниже — ТОЛЬКО про рендер (что
		// показывать/пересчитывать в MultiMesh), симуляции он больше не
		// касается. Раньше сюда же было завязано наполнение _activeSet
		// (ядро добавлялось/убиралось из симуляции при входе/выходе чанка из
		// поля зрения камеры) — из-за этого ядра вне экрана полностью
		// переставали тикать (не вращались, не передавали частицы, не
		// захватывали энергию, не поглощались чёрной дырой), пока камера не
		// возвращала их чанк в кадр. Теперь _activeSet — это ВСЕ живые ядра
		// (наполняется/чистится в PlaceNucleusAt/RemoveNucleusAt/генерации
		// чанка, см. комментарий у поля), а видимость чанка решает только,
		// рисовать ли его — как и должно быть у чисто рендерной оптимизации.
		_holesVisible = !_holesManuallyHidden && cam.Zoom.X >= HoleHideZoom;
		_particlesVisible = cam.Zoom.X >= ParticleHideZoom;

		foreach (var coord in newVisible)
		{
			if (_visible.Contains(coord)) continue;
			var chunk = GetOrCreateChunk(coord.cx, coord.cy);
			chunk.Node.Visible = true;
			chunk.HoleNode.Visible = _holesVisible;
			chunk.ParticleNode.Visible = _particlesVisible;
		}

		foreach (var coord in _visible)
		{
			if (newVisible.Contains(coord)) continue;
			if (_chunks.TryGetValue(coord, out var chunk))
			{
				chunk.Node.Visible = false;
				chunk.HoleNode.Visible = false;
				chunk.ParticleNode.Visible = false;
			}
		}

		_visible.Clear();
		foreach (var c in newVisible) _visible.Add(c);

		// Глобальные часы симуляции — фиксированный шаг, не зависящий от FPS.
		// Ограничиваем число "догоняющих" тиков за кадр, чтобы просадка FPS
		// не превратилась в спираль смерти.
		_tickAccumulatorMs += delta * 1000.0;
		int guard = 0;
		while (_tickAccumulatorMs >= PrototypeTickMs && guard < 10)
		{
			_tickAccumulatorMs -= PrototypeTickMs;
			_globalTick++;
			SimTick();
			guard++;
			_upsWindowTicks++;
		}

		// Обновляем CurrentUPS раз в ~секунду реального времени — сырое
		// количество тиков за кадр слишком дёргано для HUD.
		_upsWindowTimer += delta;
		if (_upsWindowTimer >= 1.0)
		{
			CurrentUPS = (float)(_upsWindowTicks / _upsWindowTimer);
			_upsWindowTimer = 0.0;
			_upsWindowTicks = 0;
		}

		foreach (var coord in newVisible)
		{
			if (!_chunks.TryGetValue(coord, out var chunk)) continue;
			// Зум мог измениться и без смены набора видимых чанков — держим
			// Visible в актуальном состоянии для уже показанных чанков тоже.
			chunk.HoleNode.Visible = _holesVisible;
			chunk.ParticleNode.Visible = _particlesVisible;
			UpdateChunkVisuals(chunk);
		}

		if (_leftMouseHeld) TryPlaceAtMouseIfSelected();
		if (_rightMouseHeld) TryRemoveAtMouse();
		UpdatePlacementPreview();
	}

	// Полупрозрачная "призрачная" копия выбранного ядра в клетке под
	// курсором — только если туда реально можно поставить (клетка свободна).
	// Цвет берём из той же самой палитровой текстуры, что красит настоящие
	// ядра (см. SampleTierColors), чтобы превью совпадало с итоговым видом.
	private void UpdatePlacementPreview()
	{
		if (!_selectedSpawnTier.HasValue)
		{
			_placementPreview.Visible = false;
			return;
		}

		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);

		if (_entAt.ContainsKey((row, col)))
		{
			_placementPreview.Visible = false; // клетка занята — сюда всё равно нельзя
			return;
		}

		int tier = _selectedSpawnTier.Value;
		var baseColor = (tier >= 0 && tier < _tierPreviewColors.Length) ? _tierPreviewColors[tier] : Colors.White;
		_placementPreview.Position = new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f);
		_placementPreview.Modulate = new Color(baseColor.R, baseColor.G, baseColor.B, 0.5f);
		_placementPreview.Visible = true;
	}

	// Сэмплирует по одному представительному цвету из каждой палитровой
	// текстуры тира (узкие 1-строчные полоски-градиенты, см.
	// BuildPaletteAtlas) — берём пиксель из середины полоски. Используется
	// только для превью установки (см. UpdatePlacementPreview), не влияет на
	// реальную покраску ядер/частиц шейдером.
	private static Color[] SampleTierColors(string[] paths)
	{
		var colors = new Color[paths.Length];
		for (int i = 0; i < paths.Length; i++)
		{
			var tex = GD.Load<Texture2D>(paths[i]);
			if (tex == null)
			{
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

	// Случайный тир для процедурной генерации (T/RandomFillEnabled), с
	// исключённым GrayCoreTier — серый тир не настоящий цвет, а специальное
	// игровое поведение "принимает любую частицу" (см. ColorAccepted), и
	// должен появляться на поле только по явной установке с панели спавна, а
	// не случайно наравне с обычными цветными тирами. Реализовано как
	// равномерный выбор из _tierCount-1 вариантов со сдвигом, а не через
	// retry-цикл ("кинуть кубик ещё раз, если выпал серый") — так после
	// исключения одного индекса распределение остаётся строго равномерным по
	// оставшимся тирам, без лишних итераций.
	private int RandomNonGrayTier()
	{
		if (_tierCount <= 1) return 0;
		int pick = _rng.RandiRange(0, _tierCount - 2);
		return pick >= GrayCoreTier ? pick + 1 : pick;
	}

	// --- симуляция ---

	private void SimTick()
	{
		if (_activeSet.Count == 0) return;

		// Шаг 1: поворот колец — только у ядер, чей тир как раз "щёлкает" на
		// этом глобальном тике. Сама ориентация теперь не хранится и не
		// сдвигается тут — она чистая функция (_globalTick, тир), см.
		// DiscreteRotationOffset. Этот шаг только снимает блокировку со всех
		// слотов ядра (антидребезг завершён к границе тика поворота).
		foreach (var n in _activeSet)
		{
			int ticks = n.CoreTier < TierTicks.Length ? TierTicks[n.CoreTier] : TierTicks[TierTicks.Length - 1];
			if (ticks <= 0 || _globalTick % ticks != 0) continue;
			OnRotationTick(n);
		}

		// Шаг 2: передача частиц, два прохода с "захватом" слотов, чтобы один
		// и тот же физический перенос не был учтён дважды (один раз со стороны
		// дырки, которая "тянет", и один раз со стороны частицы, которая
		// "толкает" в ту же дырку).
		_claimed.Clear();

		// Проход A ("pull"): дырка тянет частицу из соседа напротив. Внимание:
		// k здесь — сторона света (компас), а не индекс в Ring напрямую — у
		// каждого из двух ядер (n и neighbor) своя ориентация (зависит от его
		// CoreTier/Dir, см. DiscreteRotationOffset), поэтому физический слот,
		// отвечающий за компас-направление k, у них вычисляется независимо
		// через PhysicalSlotForCompass.
		foreach (var n in _activeSet)
		{
			for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
			{
				int k = OrthogonalSlots[idx];
				int p = PhysicalSlotForCompass(n, k);
				if (!n.Ring[p].Exists || !n.Ring[p].IsHole) continue;
				if (_claimed.Contains((n, p))) continue;

				// Раньше тут ещё проверялось _activeSet.Contains(neighbor) —
				// пока это множество означало "видимые ядра", сосед мог
				// физически существовать в _entAt, но не тикать (не быть
				// виден камере), и передавать частицу ему было бы нельзя.
				// Теперь _activeSet == "все живые ядра" ровно как и _entAt
				// (см. комментарий у поля), поэтому любой найденный тут
				// neighbor гарантированно активен — отдельная проверка стала
				// мёртвым кодом, убрана.
				var (dr, dc) = Adj8[k];
				if (!_entAt.TryGetValue((n.Row + dr, n.Col + dc), out var neighbor)) continue;

				int k2 = Opposite(k);
				int p2 = PhysicalSlotForCompass(neighbor, k2);
				ref var giverSlot = ref neighbor.Ring[p2];
				if (!giverSlot.Exists || giverSlot.IsHole || giverSlot.Locked) continue;
				if (_claimed.Contains((neighbor, p2))) continue;
				if (!TransferAllowed(receiver: n, giver: neighbor, giverColor: giverSlot.ColorTier)) continue;

				n.Ring[p] = new RingSlot { Exists = true, IsHole = false, ColorTier = giverSlot.ColorTier, Locked = true };
				neighbor.Ring[p2] = new RingSlot { Exists = true, IsHole = true };
				_claimed.Add((n, p));
				_claimed.Add((neighbor, p2));
			}
		}

		// Проход B ("push"): свободная (не заблокированная) частица толкается
		// в дырку соседа, если её ещё не разобрали в проходе A.
		foreach (var n in _activeSet)
		{
			for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
			{
				int k = OrthogonalSlots[idx];
				int p = PhysicalSlotForCompass(n, k);
				var giverSlot = n.Ring[p];
				if (!giverSlot.Exists || giverSlot.IsHole || giverSlot.Locked) continue;
				if (_claimed.Contains((n, p))) continue;

				// См. комментарий у прохода A выше — с _activeSet == "все живые
				// ядра" отдельная проверка активности соседа стала мёртвым
				// кодом, убрана.
				var (dr, dc) = Adj8[k];
				if (!_entAt.TryGetValue((n.Row + dr, n.Col + dc), out var neighbor)) continue;

				int k2 = Opposite(k);
				int p2 = PhysicalSlotForCompass(neighbor, k2);
				if (!neighbor.Ring[p2].Exists || !neighbor.Ring[p2].IsHole) continue;
				if (_claimed.Contains((neighbor, p2))) continue;
				if (!TransferAllowed(receiver: neighbor, giver: n, giverColor: giverSlot.ColorTier)) continue;

				neighbor.Ring[p2] = new RingSlot { Exists = true, IsHole = false, ColorTier = giverSlot.ColorTier, Locked = true };
				n.Ring[p] = new RingSlot { Exists = true, IsHole = true };
				_claimed.Add((n, p));
				_claimed.Add((neighbor, p2));
			}
		}

		// Шаг 3: захват энергии из источников частиц (EnergyClusterLayer).
		// ВАЖНО: раньше здесь была одна общая проверка на ВСЮ симуляцию сразу —
		// "_globalTick % _energyCaptureTicks == 0" — в надежде, что она будет
		// срабатывать раз в _energyCaptureTicks тиков для всех ядер разом. Но
		// _energyCaptureTicks (=TierTicks[0]*8=128) кратен полному периоду
		// вращения кольца ЛЮБОГО тира (8*16=128, 8*8=64, 8*4=32 — все делят
		// 128 нацело), поэтому единственная фаза поворота, которую видела эта
		// проверка, была одной и той же НАВСЕГДА — стробоскопический эффект:
		// захват мог сработать только для той одной дырки, что вечно стоит в
		// эту фазу лицом к источнику, а все остальные дырки, которым для этого
		// нужна другая фаза, не сэмплировались никогда. Отсюда и баг "захватило
		// один раз и больше никогда". Чиним индивидуальным кулдауном на КАЖДОЕ
		// ядро (NucleusEntity.NextCaptureTick) — проверяем и пытаемся захватить
		// каждый тик, но пропускаем ядро, если для него кулдаун ещё не истёк;
		// при успешном захвате взводим кулдаун на _energyCaptureTicks вперёд И
		// сразу прекращаем перебор направлений для этого ядра на этом тике —
		// один захват за одну попытку, как и раньше, но без привязки к фазе.
		if (_energyClusterLayers.Count > 0 && _energyCaptureTicks > 0)
		{
			foreach (var n in _activeSet)
			{
				if (_globalTick < n.NextCaptureTick) continue;

				for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
				{
					int k = OrthogonalSlots[idx];
					int p = PhysicalSlotForCompass(n, k);
					if (!n.Ring[p].Exists || !n.Ring[p].IsHole) continue;
					if (_claimed.Contains((n, p))) continue;

					// Мост между сетками: EnergyClusterLayer и NucleusLayer теперь
					// читают один и тот же GridDraw.CellSize, но на случай если он
					// когда-нибудь разъедется — переводим соседнюю клетку ядра в
					// мировые пиксели и уже из них пересчитываем row/col КАЖДОГО
					// слоя частиц по его собственному CellSize, а не складываем
					// индексы (dr,dc) напрямую с (Row,Col) разных сеток.
					var (dr, dc) = Adj8[k];
					Vector2 neighborWorld = n.Center + new Vector2(dc, dr) * CellSize;

					bool captured = false;
					foreach (var layer in _energyClusterLayers)
					{
						int srcCol = Mathf.FloorToInt(neighborWorld.X / layer.CellSize);
						int srcRow = Mathf.FloorToInt(neighborWorld.Y / layer.CellSize);
						if (!layer.HasClusterAt(srcRow, srcCol)) continue;

						// Та же проверка цвета, что и у TransferAllowed для передачи
						// между ядрами (RequireColorMatch/RequireOwnColorTier/серое
						// ядро — см. ColorAccepted) — источник тут не NucleusEntity, а
						// клетка поля, поэтому вызываем её вручную с layer.Tier.
						if (!ColorAccepted(n, layer.Tier)) continue;

						long got = layer.ConsumeAt(srcRow, srcCol, EnergyCaptureAmount);
						if (got <= 0) continue;

						n.Ring[p] = new RingSlot { Exists = true, IsHole = false, ColorTier = layer.Tier, Locked = true };
						_claimed.Add((n, p));
						n.NextCaptureTick = _globalTick + _energyCaptureTicks;
						captured = true;
						break; // клетка не может нести два тира сразу — как нашли, дальше не ищем
					}
					if (captured) break; // одна попытка захвата на ядро за тик — дальше направления не перебираем
				}
			}
		}

		// Шаг 4: поглощение частиц чёрной дырой (BlackHoleLayer) — зеркально
		// Шагу 3, но наоборот по направлению эффекта: там дырка ТЯНЕТ частицу
		// из источника, здесь заполненный слот, обращённый к чёрной дыре,
		// просто уничтожается (превращается в пустую дырку), без какого-либо
		// начисления. У чёрной дыры нет ни ёмкости, ни кулдауна — в отличие от
		// источников её нечем исчерпать, поэтому шаг выполняется КАЖДЫЙ тик,
		// без индивидуального NextCaptureTick на ядро (см. Шаг 3). Работает
		// как ещё один "giver" наравне с проходом B шага 2 — поэтому проверяет
		// _claimed, чтобы не забрать то, что этот же тик уже отдано соседнему
		// ядру шагом 2.
		if (_blackHoleLayer != null)
		{
			foreach (var n in _activeSet)
			{
				for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
				{
					int k = OrthogonalSlots[idx];
					int p = PhysicalSlotForCompass(n, k);
					var slot = n.Ring[p];
					if (!slot.Exists || slot.IsHole || slot.Locked) continue;
					if (_claimed.Contains((n, p))) continue;

					// Тот же мост через мировые пиксели, что и в шаге 3 —
					// на случай, если CellSize чёрной дыры когда-нибудь
					// разойдётся с CellSize ядра.
					var (dr, dc) = Adj8[k];
					Vector2 neighborWorld = n.Center + new Vector2(dc, dr) * CellSize;
					int bhCol = Mathf.FloorToInt(neighborWorld.X / _blackHoleLayer.CellSize);
					int bhRow = Mathf.FloorToInt(neighborWorld.Y / _blackHoleLayer.CellSize);
					if (!_blackHoleLayer.HasBlackHoleAt(bhRow, bhCol)) continue;

					n.Ring[p] = new RingSlot { Exists = true, IsHole = true };
					_claimed.Add((n, p));
				}
			}
		}
	}

	// ВАЖНО: содержимое Ring физически НЕ переставляется — меняется только то,
	// какая сторона света считается соответствующей какому физическому слоту
	// (см. DiscreteRotationOffset). Раньше здесь переставлялся сам массив, из-за
	// чего частица одновременно и плавно вращалась (по непрерывному углу
	// рендера), и мгновенно прыгала на +45° при каждом тике поворота (индекс
	// менялся) — это и была "телепортация в соседнюю дырку". Угол рендера слота
	// k (baseAngle + k*45°) и физический индекс k в Ring — одно и то же, никогда
	// не расходятся, поворот просто переопределяет, какой компас у какого k.
	// Этот метод вызывается на границе тика поворота этого тира — единственное,
	// что тут нужно физически сделать: снять блокировку "остывания" со всех
	// слотов (сама ориентация не хранится, см. комментарий у Ring выше).
	private static void OnRotationTick(NucleusEntity n)
	{
		for (int i = 0; i < 8; i++)
		{
			var slot = n.Ring[i];
			slot.Locked = false; // поворот завершил "остывание" — можно снова отдавать
			n.Ring[i] = slot;
		}
	}

	// Сколько шагов по 45° уже провернулось кольцо ЭТОГО тира к текущему
	// _globalTick, приведено по модулю 8 и с учётом направления вращения —
	// ЧИСТАЯ функция (_globalTick, CoreTier, Dir), без своего состояния на
	// ядро. Это принципиально: если бы вместо этого хранился накопительный
	// счётчик на каждом ядре (как было раньше), два ядра одного тира,
	// появившиеся на поле в разные моменты, синхронизировались бы только
	// случайно (если разница в тиках между их спавном кратна TierTicks[tier]),
	// а так — все ядра тира всегда в одной фазе, независимо от истории.
	private int DiscreteRotationOffset(NucleusEntity n)
	{
		int ticks = n.CoreTier < TierTicks.Length ? TierTicks[n.CoreTier] : TierTicks[TierTicks.Length - 1];
		if (ticks <= 0) return 0;
		long steps = (_globalTick / ticks) * n.Dir;
		return (int)(((steps % 8) + 8) % 8);
	}

	// Физический слот (индекс в Ring, он же индекс рендера), который у ЭТОГО
	// ядра прямо сейчас смотрит на сторону света compassIndex — учитывая, что
	// кольцо уже провернулось на DiscreteRotationOffset шагов по 45°.
	private int PhysicalSlotForCompass(NucleusEntity n, int compassIndex) =>
		((compassIndex - DiscreteRotationOffset(n)) % 8 + 8) % 8;

	// requireMatchingSpin=true — пока не блокирует ничего (все ядра крутятся в
	// одну сторону — SpinDirection), но уже готово к моменту, когда появится
	// разнонаправленное вращение.
	private bool TransferAllowed(NucleusEntity receiver, NucleusEntity giver, int giverColor)
	{
		if (receiver.Dir != giver.Dir) return false;
		return ColorAccepted(receiver, giverColor);
	}

	// Общая проверка "может ли receiver принять частицу цвета color" —
	// используется и при передаче между ядрами (TransferAllowed), и при
	// захвате из источника (SimTick, шаг 3), чтобы обе точки входа всегда
	// применяли одни и те же правила.
	//
	// Серое ядро (CoreTier == GrayCoreTier) — безусловное исключение из ВСЕХ
	// ограничений ниже: по заданию эксперимента оно принимает любой цвет
	// всегда, независимо от обоих флагов (RequireColorMatch/RequireOwnColorTier).
	//
	// RequireOwnColorTier (включено по вашему запросу, по умолчанию выключено
	// — это ЭКСПЕРИМЕНТ, см. [Export] выше): цвет должен совпадать с
	// собственным CoreTier ядра — то есть ядро тира "Ж" НИКОГДА не примет
	// К/С, даже если оно ещё совсем пустое. Это отдельная, более строгая
	// проверка, чем RequireColorMatch ниже.
	//
	// RequireColorMatch (включено по вашему более раннему запросу, в
	// прототипе выключено по умолчанию): ядро, уже держащее хоть один цвет в
	// кольце, отказывается принимать частицу другого цвета — пока полностью
	// не опустеет. Держит именно то, что уже физически лежит в кольце
	// (NucleusColorOrNull), а не CoreTier — до первой принятой частицы ядро с
	// этим флагом (но без RequireOwnColorTier) примет любой цвет.
	private bool ColorAccepted(NucleusEntity receiver, int color)
	{
		if (receiver.CoreTier == GrayCoreTier) return true;

		if (RequireOwnColorTier && receiver.CoreTier != color) return false;

		if (RequireColorMatch)
		{
			int? haveColor = NucleusColorOrNull(receiver);
			if (haveColor.HasValue && haveColor.Value != color) return false;
		}

		return true;
	}

	// Цвет, которым ядро уже "заняло" своё кольцо — цвет первой попавшейся
	// частицы (не дырки) в Ring, либо null, если в кольце частиц вообще нет
	// (ядро ещё ничем не "закоммитилось" и примет любой цвет первым).
	private static int? NucleusColorOrNull(NucleusEntity n)
	{
		foreach (var slot in n.Ring)
			if (slot.Exists && !slot.IsHole) return slot.ColorTier;
		return null;
	}

	// --- рендер ---

	private static readonly Transform2D HiddenTransform = new Transform2D(Vector2.Zero, Vector2.Zero, Vector2.Zero);

	// ВАЖНО: угол рендера теперь считается НАПРЯМУЮ из того же тикового
	// счётчика (_globalTick/_tickAccumulatorMs), что и логика передачи, а не
	// из отдельной случайной "фазы" ядра. Раньше рендер крутился независимо
	// от накопительного RotationOffset (случайный старт + свободный ход по
	// игровому времени), из-за чего экранное "дырка напротив частицы" не имело
	// вообще никакой связи с тем, что логика передачи считает выровненным по
	// стороне света — отсюда рассинхронизация ("совпали на экране — ничего не
	// произошло, а потом частица дёрнулась сама по себе"). Теперь физический
	// слот k рисуется строго под углом (k + DiscreteRotationOffset +
	// доля_до_следующего_тика*Dir)*45°-90° — то есть ровно та ориентация,
	// которую в этот момент использует PhysicalSlotForCompass для проверки
	// передачи (это ЧИСТАЯ функция времени, см. её комментарий — поэтому все
	// ядра одного тира всегда синхронны, когда бы они ни появились на поле),
	// плюс плавная интерполяция
	// между тиками поворота (никакого скачка — см. RotateRing). -90° — поправка,
	// чтобы компас-индекс 0 (N, см. Adj8) визуально указывал вверх/на соседа
	// сверху, а не в произвольную сторону.
	private void UpdateChunkVisuals(WorldChunk chunk)
	{
		// У дырок и частиц СВОИ независимые пороги отключения (HoleHideZoom /
		// ParticleHideZoom) — ниже порога слой не только прячется через
		// Visible, но и вообще не пересчитывается по инстансам (экономия CPU).
		// Если оба выключены разом, пропускаем чанк целиком.
		if (!_holesVisible && !_particlesVisible) return;

		var holeMM = _holesVisible ? chunk.HoleNode.Multimesh : null;
		var particleMM = _particlesVisible ? chunk.ParticleNode.Multimesh : null;
		float subTickFraction = (float)(_tickAccumulatorMs / PrototypeTickMs); // 0..1, доля пути до следующего глобального тика

		foreach (var n in chunk.Nuclei)
		{
			int ticks = n.CoreTier < TierTicks.Length ? TierTicks[n.CoreTier] : TierTicks[TierTicks.Length - 1];
			float continuousOffset = DiscreteRotationOffset(n);
			if (ticks > 0)
			{
				float ticksSinceRotation = _globalTick % ticks;
				float fraction = (ticksSinceRotation + subTickFraction) / ticks; // 0..1 до следующего шага поворота
				continuousOffset += fraction * n.Dir;
			}

			for (int k = 0; k < 8; k++)
			{
				int instanceIdx = n.LocalIndex * 8 + k;
				float angle = (k + continuousOffset) * (Mathf.Pi / 4f) - (Mathf.Pi / 2f);
				var pos = n.Center + _orbitRadius * AngleVec(angle);
				var slot = n.Ring[k];

				if (!slot.Exists)
				{
					// Слота тут физически нет (ядро со спавн-панели с < 8 гнёзд, см.
					// BuildFixedRing) — ни дырка, ни частица не рисуются вообще.
					holeMM?.SetInstanceTransform2D(instanceIdx, HiddenTransform);
					particleMM?.SetInstanceTransform2D(instanceIdx, HiddenTransform);
				}
				else if (slot.IsHole)
				{
					holeMM?.SetInstanceTransform2D(instanceIdx, new Transform2D(0f, pos));
					particleMM?.SetInstanceTransform2D(instanceIdx, HiddenTransform);
				}
				else
				{
					if (particleMM != null)
					{
						particleMM.SetInstanceTransform2D(instanceIdx, new Transform2D(0f, pos));
						float rowUv = (slot.ColorTier + 0.5f) / _tierCount;
						particleMM.SetInstanceCustomData(instanceIdx, new Color(rowUv, 0f, 0f, 0f));
					}
					holeMM?.SetInstanceTransform2D(instanceIdx, HiddenTransform);
				}
			}
		}
	}

	private WorldChunk GetOrCreateChunk(int cx, int cy)
	{
		var key = (cx, cy);
		if (_chunks.TryGetValue(key, out var existing)) return existing;

		var nuclei = new List<NucleusEntity>();

		int cellRowStart = cy * ChunkSize;
		int cellColStart = cx * ChunkSize;

		// RandomFillEnabled=false (умолчание) — чанк создаётся ПУСТЫМ, клетки
		// заполняются только вручную (см. TryPlaceNucleus). Переключается по T.
		if (RandomFillEnabled)
		{
			for (int r = 0; r < ChunkSize; r++)
			{
				for (int c = 0; c < ChunkSize; c++)
				{
					if (_rng.Randf() >= FillDensity) continue; // клетка остаётся пустой

					int worldRow = cellRowStart + r;
					int worldCol = cellColStart + c;
					var center = new Vector2(
						worldCol * CellSize + CellSize / 2f,
						worldRow * CellSize + CellSize / 2f);

					// CoreTier — сначала, отдельно от цвета частиц: при
					// RequireOwnColorTier цвет частиц ниже обязан совпасть именно
					// с ним (а не быть выбран независимо). RandomNonGrayTier —
					// серый (GrayCoreTier) исключён из случайного выбора: серый
					// тир — не настоящий цвет, а специальное поведение "принимает
					// всё" (см. ColorAccepted), ему не место среди случайно
					// выпадающих тиров при процедурной генерации.
					int coreTier = RandomNonGrayTier();

					// Один случайный цвет частиц на всё кольцо ПРИ ГЕНЕРАЦИИ — это
					// только стартовое состояние. После первой же передачи частицы
					// цвета в кольце одного ядра вполне могут стать разными — это
					// ожидаемое следствие настоящей передачи между ядрами.
					// При включённом RequireOwnColorTier ядро и так никогда не
					// примет чужой цвет через SimTick — но процедурная генерация
					// создаёт частицы напрямую, в обход ColorAccepted, поэтому без
					// этой проверки на поле тут же появлялись бы "нелегальные"
					// ядра с чужим цветом в кольце с самого спавна. Раз CoreTier
					// уже гарантированно не серый (см. выше), просто берём его же.
					int particleTier = RequireOwnColorTier ? coreTier : RandomNonGrayTier();
					var ring = new RingSlot[8];
					for (int k = 0; k < 8; k++)
					{
						bool isParticle = _rng.Randf() < ParticleFillChance;
						ring[k] = new RingSlot { Exists = true, IsHole = !isParticle, ColorTier = particleTier, Locked = false };
					}

					var nucleus = new NucleusEntity
					{
						Row = worldRow,
						Col = worldCol,
						Center = center,
						CoreTier = coreTier,
						Dir = SpinDirection,
						Ring = ring,
						LocalIndex = nuclei.Count
					};

					nuclei.Add(nucleus);
					_entAt[(worldRow, worldCol)] = nucleus;
					// Симуляция теперь не завязана на видимость чанка (см.
					// комментарий у _activeSet в шапке файла) — ядро начинает
					// тикать сразу же, а не только когда чанк попадёт в кадр.
					_activeSet.Add(nucleus);
				}
			}
		}

		var node = new MultiMeshInstance2D
		{
			Name = $"Chunk_{cx}_{cy}",
			Texture = _coreTexture,
			Material = _material,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Visible = false
		};
		AddChild(node);

		var holeNode = new MultiMeshInstance2D
		{
			Name = $"Holes_{cx}_{cy}",
			Texture = _holeTexture,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Modulate = new Color(1f, 1f, 1f, HoleOpacity),
			Visible = false
		};
		AddChild(holeNode);

		var particleNode = new MultiMeshInstance2D
		{
			Name = $"Particles_{cx}_{cy}",
			Texture = _particleTexture,
			Material = _material,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Visible = false
		};
		AddChild(particleNode);

		var worldRect = new Rect2(
			cellColStart * CellSize, cellRowStart * CellSize,
			ChunkSize * CellSize, ChunkSize * CellSize);

		var chunk = new WorldChunk
		{
			Node = node,
			HoleNode = holeNode,
			ParticleNode = particleNode,
			WorldRect = worldRect,
			Nuclei = nuclei
		};

		_chunks[key] = chunk;

		// Строит все 3 MultiMesh с нуля из chunk.Nuclei и сразу отрисовывает
		// (иначе до первого UpdateChunkVisuals слоты стояли бы в (0,0)).
		RebuildChunkMeshes(chunk);

		GD.Print($"[NucleusLayer] новый чанк ({cx},{cy}): ядер {nuclei.Count}, чанков всего: {_chunks.Count}");

		return chunk;
	}

	// Пересоздаёт все 3 MultiMesh чанка с нуля из ТЕКУЩЕГО chunk.Nuclei.
	// Нужно не только при первой генерации, но и при ручной установке ядра
	// (TryPlaceNucleus) — у Godot MultiMesh.InstanceCount при изменении
	// сбрасывает ВСЕ инстансы, поэтому просто "добавить один" нельзя, каждый
	// раз перестраиваем целиком (для размеров чанка это дёшево).
	private void RebuildChunkMeshes(WorldChunk chunk)
	{
		int nucleusCount = chunk.Nuclei.Count;
		int slotCount = nucleusCount * 8;

		// --- ядра: одна позиция на ядро, custom data = тир (строка в атласе палитр) ---
		var mm = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = _coreQuad,
			InstanceCount = nucleusCount
		};
		for (int i = 0; i < nucleusCount; i++)
		{
			mm.SetInstanceTransform2D(i, new Transform2D(0f, chunk.Nuclei[i].Center));
			float rowUv = (chunk.Nuclei[i].CoreTier + 0.5f) / _tierCount;
			mm.SetInstanceCustomData(i, new Color(rowUv, 0f, 0f, 0f));
		}
		chunk.Node.Multimesh = mm;

		// --- дырки и частицы: ФИКСИРОВАННЫЕ nucleusCount*8 инстансов у обоих
		// мешей (по одному представлению на каждый слот кольца каждого ядра).
		// В любой момент активен ровно один из двух представлений слота —
		// какой именно, решает live Ring[k].IsHole в UpdateChunkVisuals.
		chunk.HoleNode.Multimesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			Mesh = _holeQuad,
			InstanceCount = slotCount
		};
		chunk.ParticleNode.Multimesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = _particleQuad,
			InstanceCount = slotCount
		};

		UpdateChunkVisuals(chunk);
	}

	// Строит кольцо ровно с holeCount реальными гнёздами (из 8 возможных
	// физических позиций) — и ВСЕ они дырки, частиц нет вообще. Остальные
	// 8-holeCount позиций физически не существуют (Exists=false — не
	// рисуются и не участвуют в передаче, см. UpdateChunkVisuals/SimTick).
	// Позиции гнёзд берутся по HolePriority, чтобы получались симметричные
	// фигуры: 2 — строго друг напротив друга, 4 — крестом по всем сторонам
	// света, 8 — всё кольцо.
	private static RingSlot[] BuildFixedRing(int holeCount)
	{
		holeCount = Mathf.Clamp(holeCount, 0, 8);
		var exists = new bool[8];
		for (int i = 0; i < holeCount; i++) exists[HolePriority[i]] = true;

		var ring = new RingSlot[8];
		for (int k = 0; k < 8; k++)
			ring[k] = new RingSlot { Exists = exists[k], IsHole = true, ColorTier = 0, Locked = false };
		return ring;
	}

	// Ставит одно ядро в клетку под worldPos (см. _UnhandledInput/ЛКМ). Если
	// клетка уже занята — ничего не делает. Чанк-владелец создаётся лениво,
	// как обычно (пустым, если RandomFillEnabled выключен).
	private void TryPlaceNucleus(Vector2 worldPos, int tier, int holeCount)
	{
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);

		if (_entAt.ContainsKey((row, col)))
		{
			GD.Print($"[NucleusLayer] клетка ({row},{col}) уже занята — пропуск.");
			return;
		}

		int cx = Mathf.FloorToInt((float)col / ChunkSize);
		int cy = Mathf.FloorToInt((float)row / ChunkSize);
		var chunk = GetOrCreateChunk(cx, cy);

		var center = new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f);
		var nucleus = new NucleusEntity
		{
			Row = row,
			Col = col,
			Center = center,
			CoreTier = tier,
			Dir = SpinDirection,
			Ring = BuildFixedRing(holeCount),
			LocalIndex = chunk.Nuclei.Count
		};

		chunk.Nuclei.Add(nucleus);
		_entAt[(row, col)] = nucleus;
		RebuildChunkMeshes(chunk);

		// Симуляция не завязана на видимость чанка (см. комментарий у
		// _activeSet в шапке файла) — ядро включается в неё сразу при
		// установке, независимо от того, видим ли чанк прямо сейчас.
		_activeSet.Add(nucleus);

		int actualSlots = 0;
		int actualParticles = 0;
		foreach (var s in nucleus.Ring)
		{
			if (!s.Exists) continue;
			actualSlots++;
			if (!s.IsHole) actualParticles++;
		}
		GD.Print($"[NucleusLayer] установлено ядро тира {tier} в клетке ({row},{col}), запрошено гнёзд {holeCount}/8, реально гнёзд {actualSlots}/8 (частиц среди них: {actualParticles}).");
	}

	private static Vector2 AngleVec(float angle) => new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

	// Склеивает N узких (5x1) палитр в одну атлас-текстуру (N строк x 5
	// колонок) по сырым RGBA8-байтам.
	private Texture2D BuildPaletteAtlas(string[] paths)
	{
		if (paths == null || paths.Length == 0)
		{
			GD.PrintErr("NucleusLayer: PalettePaths пуст — задайте хотя бы одну палитру.");
			return null;
		}

		var rowImages = new List<Image>();
		int width = -1;

		foreach (var path in paths)
		{
			var tex = GD.Load<Texture2D>(path);
			if (tex == null)
			{
				GD.PrintErr($"NucleusLayer: не загрузилась палитра {path}");
				return null;
			}

			var img = tex.GetImage();
			img.Convert(Image.Format.Rgba8);

			if (img.GetHeight() != 1)
			{
				GD.PrintErr($"NucleusLayer: палитра {path} высотой {img.GetHeight()}px, ожидалась ровно 1 строка.");
				return null;
			}

			if (width == -1) width = img.GetWidth();
			else if (img.GetWidth() != width)
			{
				GD.PrintErr($"NucleusLayer: палитра {path} шириной {img.GetWidth()}px, ожидалось {width}px как у остальных.");
				return null;
			}

			rowImages.Add(img);
		}

		int height = rowImages.Count;
		var combined = new byte[width * height * 4];
		for (int row = 0; row < rowImages.Count; row++)
		{
			var rowBytes = rowImages[row].GetData();
			System.Array.Copy(rowBytes, 0, combined, row * width * 4, width * 4);
		}

		var atlasImage = Image.CreateFromData(width, height, false, Image.Format.Rgba8, combined);
		return ImageTexture.CreateFromImage(atlasImage);
	}
}
