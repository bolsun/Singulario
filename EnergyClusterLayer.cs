using Godot;
using System.Collections.Generic;

// Экспериментальный, второй способ отображения источников энергии —
// параллельно основному EnergyLayer (Terrain-автотайлинг), НЕ заменяет его.
// Оба слоя существуют в сцене одновременно, но должны быть взаимоисключающе
// активны (см. ClearSelection/SelectClusterVariant) — иначе непонятно, что
// именно ставить по ЛКМ, и клик мог бы попасть в оба слоя сразу.
//
// По тирам этот способ раскладки устроен ИНАЧЕ, чем в EnergyLayer: там один
// узел хранит все тиры разом (тир — это просто число в словаре, форма кусков
// одинаковая). Здесь же на каждый тир — свой ОТДЕЛЬНЫЙ узел TileMapLayer с
// этим же скриптом (сейчас в сцене их три: жёлтый/красный/синий), уже заранее
// раскрашенный своей текстурой в редакторе — сам скрипт цвет не трогает и о
// нём не знает, только хранит Tier для идентификации (панель спавна/логи).
// Поэтому клетка не может физически принадлежать двум тирам одновременно —
// при установке на этом слое клетка стирается на всех братских тир-слоях
// (см. _siblingLayers/PlaceClusterAt), иначе они бы просто наложились друг на
// друга и часть тайлов оказалась бы визуально перекрыта.
//
// Принцип принципиально другой: тайлы тут НЕ зависят от соседей вообще —
// это просто набор из VariantCount независимых картинок "скопление частиц",
// и при установке в клетку выбирается СЛУЧАЙНЫЙ вариант из набора. Никакого
// Terrain/SetCellsTerrainConnect не нужно — обычный SetCell с случайными
// atlas-координатами.
//
// CellSize — не свой экспорт, единственный источник истины — GridDraw (тот
// же принцип, что и у NucleusLayer/EnergyLayer). Раньше здесь был независимый
// [Export] CellSize = 96, сознательно НЕ читавшийся из GridDraw — на момент,
// когда это писалось, у GridDraw было 128 (под ядра и основной слой энергии),
// а тайл частиц — 96px, и раскладка требовала точного совпадения тайла с
// клеткой, чтобы не было смещения/джиттера. Теперь GridDraw.CellSize тоже 96
// (см. GridDraw.cs), так что независимая копия стала лишней сущностью, которая
// может незаметно разъехаться с общей сеткой, если та снова поменяется —
// именно так уже ловили баг с "захват работает только по одной оси" (ядро
// и слой частиц физически совпадали числом 96, но обращения к соседней
// клетке шли из системы координат ядра — см. NucleusLayer.SimTick, шаг 3,
// это исправлено переводом через мировые пиксели, но раз уж значения теперь
// одинаковые и по смыслу должны совпадать всегда — берём их из одного места).
// ВАЖНО на будущее: если GridDraw.CellSize снова станет отличаться от 96px
// (нативного размера тайла частиц) — здесь снова появится визуальное
// смещение/растяжение тайла, и понадобится либо перерисовать тайл под новый
// размер, либо вернуть отдельную под-сетку (как SubTilesPerCell у EnergyLayer).
public partial class EnergyClusterLayer : TileMapLayer
{
	public int CellSize { get; private set; }

	// Сколько независимых вариантов тайла "скопление частиц" лежит в атласе.
	// Раскладка вариантов внутри атласа — прямоугольная сетка AtlasColumns
	// колонок; вариант N лежит в atlas-координатах (N % AtlasColumns,
	// N / AtlasColumns). Сейчас картинка particle_source_yellow_shade_2x2_96px.png
	// — сетка 2x2 (variant 0..3 -> (0,0),(1,0),(0,1),(1,1)), отсюда
	// VariantCount=4, AtlasColumns=2. Никакой Terrain-разметки не нужно.
	[Export] public int VariantCount = 4;

	// Сколько вариантов в атласе укладывается по горизонтали, прежде чем
	// раскладка переходит на следующую строку вниз (см. комментарий у
	// VariantCount). ВАЖНО: должно совпадать с тем, как реально раскроен
	// атлас в TileSet этого узла в редакторе — если добавите новые варианты
	// вправо/вниз, поправьте оба числа (VariantCount и AtlasColumns) вместе,
	// иначе часть вариантов будет указывать на несуществующие atlas-клетки
	// (клетка на поле останется пустой — см. диагностику в PlaceClusterAt).
	[Export] public int AtlasColumns = 2;

	// Индекс TileSetAtlasSource внутри TileSet этого узла (см. "sources/N" в
	// Main.tscn) — обычной нумерации источников, НЕ имеет отношения к
	// Terrain (этот слой Terrain вообще не использует).
	[Export] public int AtlasSourceId = 0;

	// Какой тир частиц представляет именно этот узел/экземпляр скрипта —
	// значение выставляется в инспекторе на каждом из узлов-тиров в сцене
	// (сейчас 0 = жёлтый, 1 = красный, 2 = синий — своя, отдельная нумерация,
	// НЕ совпадающая с тирами ядер/EnergyLayer, см. NucleusSpawnPanel). Сам
	// скрипт цвет по этому числу не выбирает — раскраска уже сделана на
	// уровне текстуры/узла в редакторе, Tier нужен только для UI и логов.
	[Export] public int Tier = 0;

	// Сколько частиц добавляет ОДНА поставленная клетка — общая ёмкость
	// кластера (см. ParticleCluster ниже) равна сумме этого числа по всем его
	// клеткам, расходуется тоже из общей суммы, а не с конкретной клетки.
	[Export] public long InitialAmount = 100;

	// Соседство, по которому клетки одного тира объединяются в один кластер
	// при установке (см. RegisterNewCell) — 8 направлений (с диагоналями, как
	// у старого Terrain-слоя энергии) или только 4 ортогонали. Чем шире
	// соседство, тем охотнее соседние мазки сливаются в одно пятно.
	[Export] public bool DiagonalClustering = true;

	// Что стоит в каждой клетке — сам номер варианта, чтобы при повторном
	// обращении к клетке (например, для сохранения/загрузки в будущем) не
	// нужно было отдельно спрашивать TileMapLayer, что там нарисовано.
	private readonly Dictionary<(int row, int col), int> _variantAt = new();

	// Кластер объединяет несколько соседних клеток ОДНОГО тира в общий пул
	// частиц (см. обсуждение в чате) — общая ёмкость равна сумме вкладов всех
	// клеток и расходуется тоже из общей суммы, а не с конкретной клетки.
	// Cells хранится явным множеством (не только "какая клетка на какую
	// ссылается"), потому что при обнулении пула нужно разом стереть тайлы
	// ВСЕХ клеток кластера — без явного списка членов пришлось бы сканировать
	// весь мир в поисках "чьих же они".
	private class ParticleCluster
	{
		public long Amount;
		public readonly HashSet<(int row, int col)> Cells = new();
	}

	// Кластер, которому сейчас принадлежит клетка — прямая ссылка на объект,
	// а не индекс/цепочка родителей, как в учебном Union-Find: это даёт Find
	// за O(1) без "подъёма по дереву" (не нужен даже path compression). Слияние
	// двух кластеров при этом делается по принципу "small-to-large" (см.
	// RegisterNewCell): меньший кластер целиком переносится в больший — тогда
	// суммарная стоимость ВСЕХ слияний за всю игру ограничена O(N log N), а не
	// O(N^2), потому что отдельная клетка может быть переброшена в другой
	// кластер не больше O(log N) раз (каждый раз — в кластер как минимум
	// вдвое больше прежнего).
	private readonly Dictionary<(int row, int col), ParticleCluster> _clusterOf = new();

	private static readonly (int dr, int dc)[] Neighbors8 =
	{
		(-1, -1), (-1, 0), (-1, 1),
		( 0, -1),          ( 0, 1),
		( 1, -1), ( 1, 0), ( 1, 1),
	};
	private static readonly (int dr, int dc)[] Neighbors4 =
	{
		(-1, 0), (0, -1), (0, 1), (1, 0),
	};

	private bool _selected;
	private NucleusLayer _nucleusLayer;
	private EnergyLayer _energyLayer;
	// Остальные узлы-тиры этого же семейства (см. комментарий у Tier в шапке
	// файла) — находятся автоматически среди узлов того же родителя по типу
	// скрипта, а не по жёстко зашитым именам (устойчиво к переименованию/
	// добавлению новых тиров в сцене). Нужны для двух вещей: взаимный сброс
	// выбора на панели спавна и защита клетки от двух тиров одновременно.
	private List<EnergyClusterLayer> _siblingLayers = new();
	// Чёрная дыра (см. BlackHoleLayer) — клетка не может одновременно быть и
	// источником, и поглотителем, поэтому установка кластера должна стирать
	// чёрную дыру в той же клетке (симметрично тому, как BlackHoleLayer сама
	// стирает источники при своей установке — см. BlackHoleLayer.PlaceBlackHoleAt),
	// а выбор режима кластера — сбрасывать выбор режима чёрной дыры.
	private BlackHoleLayer _blackHoleLayer;

	private bool _leftMouseHeld;
	private (int row, int col)? _lastPlacedCell;

	private readonly RandomNumberGenerator _rng = new();

	public override void _Ready()
	{
		var gridDraw = GetNode<GridDraw>("../GridLayer");
		CellSize = gridDraw.CellSize;

		_rng.Randomize();

		// Тот же порядок отрисовки, что и у основного слоя энергии — ниже
		// ядер, независимо от порядка узлов в дереве сцены.
		ZIndex = -1;

		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		_energyLayer = GetNodeOrNull<EnergyLayer>("../TileMapLayer");
		_blackHoleLayer = GetNodeOrNull<BlackHoleLayer>("../BlackHoleLayer");

		var parent = GetParent();
		if (parent != null)
		{
			foreach (var child in parent.GetChildren())
				if (child is EnergyClusterLayer layer && layer != this)
					_siblingLayers.Add(layer);
		}

		SetProcessUnhandledInput(true);
	}

	// Вызывается с панели спавна — включает режим установки "скопления
	// частиц" и сбрасывает выбор у ядер И у основного (Terrain) слоя энергии,
	// раз активен может быть только один способ расстановки одновременно.
	public void SelectClusterMode()
	{
		_selected = true;
		_lastPlacedCell = null;
		_nucleusLayer?.ClearSelection();
		_energyLayer?.ClearSelection();
		_blackHoleLayer?.ClearSelection();
		foreach (var sibling in _siblingLayers) sibling.ClearSelection();
		GD.Print($"[EnergyClusterLayer] выбран тир {Tier} (скопление частиц). Клик (или удержание ЛКМ) по полю — поставить.");
	}

	// Вызывается NucleusLayer/EnergyLayer при выборе своего пресета —
	// сбрасывает выбор здесь (см. комментарий у SelectClusterMode).
	public void ClearSelection()
	{
		_selected = false;
	}

	public bool HasClusterAt(int row, int col) => _clusterOf.ContainsKey((row, col));
	public int? VariantAt(int row, int col) => _variantAt.TryGetValue((row, col), out var v) ? v : null;
	// Сколько частиц осталось в пуле кластера, которому принадлежит клетка —
	// 0, если клетки/кластера тут нет. Только для чтения (HUD/отладка/захват
	// ядрами, см. ConsumeAt) — саму трату всегда делает ConsumeAt.
	public long AmountAt(int row, int col) => _clusterOf.TryGetValue((row, col), out var c) ? c.Amount : 0;
	// Сколько клеток сейчас в кластере, которому принадлежит клетка — 0, если
	// клетки/кластера тут нет. Нужно только для отображения (см. наведение
	// мышью — ClusterHoverProbe), саму кластеризацию не затрагивает.
	public int CellCountAt(int row, int col) => _clusterOf.TryGetValue((row, col), out var c) ? c.Cells.Count : 0;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;

		if (mb.Pressed)
		{
			if (!_selected) return;
			_leftMouseHeld = true;
			_lastPlacedCell = null;
			TryPlaceAtMouseIfSelected();
			GetViewport().SetInputAsHandled();
		}
		else
		{
			_leftMouseHeld = false;
		}
	}

	public override void _Process(double delta)
	{
		if (_leftMouseHeld) TryPlaceAtMouseIfSelected();
	}

	private void TryPlaceAtMouseIfSelected()
	{
		if (!_selected) return;

		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);
		if (_lastPlacedCell.HasValue && _lastPlacedCell.Value == (row, col)) return;

		_lastPlacedCell = (row, col);
		PlaceClusterAt(row, col);
	}

	// Ставит в клетку (row, col) случайный из VariantCount независимых
	// вариантов и добавляет её в кластеризацию (см. RegisterNewCell) — кроме
	// случая повторной установки в клетку, УЖЕ занятую этим же тиром: тогда
	// только перекатывается картинка (как и раньше), но частицы повторно НЕ
	// начисляются и кластер не трогается — иначе повторный клик по той же
	// клетке был бы бесконечным источником энергии.
	public void PlaceClusterAt(int row, int col)
	{
		var key = (row, col);

		// Клетка может нести частицы только одного тира одновременно (см.
		// комментарий у _siblingLayers/в шапке файла) — стираем эту же клетку
		// у братских тир-слоёв, иначе там осталась бы энергия другого тира,
		// просто перекрытая нашим тайлом сверху (и вернувшаяся бы обратно на
		// вид, если позже стереть текущий тир).
		foreach (var sibling in _siblingLayers) sibling.EraseClusterAt(row, col);
		// Клетка не может одновременно быть источником и чёрной дырой (см.
		// комментарий у _blackHoleLayer/BlackHoleLayer.PlaceBlackHoleAt).
		_blackHoleLayer?.EraseBlackHoleAt(row, col);

		bool alreadyOurs = _clusterOf.ContainsKey(key);

		int variant = _rng.RandiRange(0, VariantCount - 1);
		_variantAt[key] = variant;

		var atlasCoords = new Vector2I(variant % AtlasColumns, variant / AtlasColumns);
		var cell = new Vector2I(col, row);
		SetCell(cell, AtlasSourceId, atlasCoords);

		// Диагностика на случай будущего рассинхрона VariantCount/AtlasColumns
		// с реальной раскладкой атласа (см. комментарий у AtlasColumns) — если
		// для выбранных atlas-координат в источнике нет куска, GetCellSourceId
		// вернёт -1 и клетка на поле останется пустой без этого сообщения.
		if (GetCellSourceId(cell) == -1)
		{
			GD.Print($"[EnergyClusterLayer] тир {Tier}, клетка ({row},{col}): вариант {variant} -> atlas ({atlasCoords.X},{atlasCoords.Y}) " +
				"НЕ ПОСТАВЛЕН (в атласе нет такого куска — проверьте VariantCount/AtlasColumns и разметку в TileSet).");
		}

		if (!alreadyOurs) RegisterNewCell(key);
	}

	// Заводит новую клетку в кластеризацию: ищет среди соседей (см.
	// NeighborOffsets/DiagonalClustering) уже существующие кластеры этого же
	// тира, и либо создаёт свой новый (соседей нет), либо присоединяется к
	// единственному найденному, либо — если новая клетка оказалась мостиком
	// между несколькими РАЗНЫМИ кластерами — сливает их все в один
	// (small-to-large, см. комментарий у _clusterOf).
	private void RegisterNewCell((int row, int col) key)
	{
		HashSet<ParticleCluster> neighborClusters = null;
		foreach (var (dr, dc) in NeighborOffsets)
		{
			var neighborKey = (key.row + dr, key.col + dc);
			if (_clusterOf.TryGetValue(neighborKey, out var neighborCluster))
			{
				neighborClusters ??= new HashSet<ParticleCluster>();
				neighborClusters.Add(neighborCluster);
			}
		}

		ParticleCluster survivor;
		if (neighborClusters == null || neighborClusters.Count == 0)
		{
			survivor = new ParticleCluster();
		}
		else
		{
			// Выживает крупнейший по числу клеток — так каждая отдельная
			// клетка за всю игру переезжает в другой кластер не больше
			// O(log N) раз (см. комментарий у _clusterOf).
			survivor = null;
			foreach (var c in neighborClusters)
				if (survivor == null || c.Cells.Count > survivor.Cells.Count)
					survivor = c;

			foreach (var c in neighborClusters)
			{
				if (c == survivor) continue;
				foreach (var cellKey in c.Cells)
					_clusterOf[cellKey] = survivor;
				survivor.Cells.UnionWith(c.Cells);
				survivor.Amount += c.Amount;
				c.Cells.Clear();
			}
		}

		survivor.Cells.Add(key);
		survivor.Amount += InitialAmount;
		_clusterOf[key] = survivor;
	}

	private IEnumerable<(int dr, int dc)> NeighborOffsets => DiagonalClustering ? Neighbors8 : Neighbors4;

	// Тратит до amount частиц из кластера, которому принадлежит клетка
	// (row, col) — расход всегда идёт из ОБЩЕГО пула кластера, а не с этой
	// конкретной клетки (см. обсуждение кластеризации в чате). Возвращает,
	// сколько частиц реально удалось потратить: 0, если клетки/кластера тут
	// нет, иначе не больше amount и не больше того, что в пуле оставалось.
	// Когда пул опустошается до нуля — весь кластер исчезает целиком: тайлы
	// ВСЕХ его клеток стираются разом, не только клетки (row, col).
	public long ConsumeAt(int row, int col, long amount)
	{
		if (amount <= 0) return 0;
		if (!_clusterOf.TryGetValue((row, col), out var cluster)) return 0;

		long consumed = System.Math.Min(amount, cluster.Amount);
		cluster.Amount -= consumed;

		if (cluster.Amount <= 0) RemoveCluster(cluster);

		return consumed;
	}

	// Стирает тайлы и все данные ВСЕХ клеток исчерпанного кластера разом (см.
	// ConsumeAt) — единственное место, где стоимость реально пропорциональна
	// размеру кластера, но эта работа неизбежна: тайлы физически нужно убрать
	// с поля.
	private void RemoveCluster(ParticleCluster cluster)
	{
		foreach (var cellKey in cluster.Cells)
		{
			EraseCell(new Vector2I(cellKey.col, cellKey.row));
			_variantAt.Remove(cellKey);
			_clusterOf.Remove(cellKey);
		}
		cluster.Cells.Clear();
	}

	// Стирает клетку (row, col) на ЭТОМ тир-слое, если там что-то стоит —
	// вызывается братскими тир-слоями при установке своего тира в ту же
	// клетку (см. PlaceClusterAt), чтобы клетка не несла два тира сразу.
	// Намеренно НЕ пересчитывает/не разделяет оставшийся кластер, даже если
	// стёртая клетка была "мостиком" и остаток физически распался на два
	// визуально несвязанных пятна (см. обсуждение в чате про точечное
	// удаление) — сейчас этот путь используется только при перехвате клетки
	// другим тиром, что не разбивает соседство ВНУТРИ одного тира.
	public void EraseClusterAt(int row, int col)
	{
		var key = (row, col);
		if (!_variantAt.Remove(key))
		{
			_clusterOf.Remove(key); // на всякий случай, если данные вдруг разъехались
			return;
		}
		EraseCell(new Vector2I(col, row));

		if (_clusterOf.TryGetValue(key, out var cluster))
		{
			cluster.Cells.Remove(key);
			_clusterOf.Remove(key);
		}
	}
}
