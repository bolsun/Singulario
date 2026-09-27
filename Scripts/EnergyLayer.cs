using Godot;
using System.Collections.Generic;

// Слой энергии — полностью независимая от ядер (NucleusLayer) структура
// данных: клетка поля может нести энергию совершенно независимо от того,
// стоит там ядро или нет (специально НЕ смешиваем с _entAt/NucleusEntity —
// см. обсуждение: так в дальнейшем разрешить или запретить ставить ядро
// поверх энергии — это одна локальная проверка внутри NucleusLayer, а не
// переделка структур).
//
// Рендер — через встроенный в этот же узел (TileMapLayer) TileSet Terrain:
// скрипт только решает, В КАКИХ клетках есть энергия и какого она тира,
// а форму конкретных кусков каждый раз подбирает сам Godot по соседям через
// SetCellsTerrainConnect — никакой ручной таблицы "маска соседей -> спрайт"
// в коде нет.
//
// ВАЖНО: этот TileMapLayer визуально МЕЛЬЧЕ игровой клетки (см.
// SubTilesPerCell) — чтобы автотайлинг собирал органический край из мелких
// (32px) кусков без размытия от апскейла, а не растягивал один крупный
// (96px) кусок на всю клетку. Игровая логика (что где стоит) по-прежнему
// оперирует обычными клетками поля (row,col, шаг CellSize) — пересчёт в
// саб-тайлы происходит только на этапе рендера, см. SubCellsOf.
//
// TerrainSetIndex/TerrainIndex — индексы Terrain Set/Terrain, настроенные
// в TileSet-ресурсе этого узла в редакторе (сейчас там один Terrain Set с
// одним Terrain — режим фактически не важен для этого кода, лишь бы
// terrain_set/terrain у каждого куска были проставлены). Стандартные 0/0
// подходят, пока Terrain Set/Terrain в редакторе ровно один.
//
// Пока существует только один (жёлтый) тайлсет — все три тира энергии
// временно рисуются им же (единственная реально готовая текстура). Тир
// каждой клетки при этом хранится честно в _energyAt, так что когда
// появятся свои тайлсеты или палитровая перекраска под С и К, переделывать
// эту часть не понадобится — только подставить нужный визуал.
public partial class EnergyLayer : TileMapLayer
{
	[Export] public int TerrainSetIndex = 0;
	[Export] public int TerrainIndex = 0;

	// Сколько саб-тайлов по стороне складывается в одну игровую клетку
	// (2 -> клетка 128px собирается из блока 2x2 кусков земли по 64px, как
	// договорились). TileSet этого узла должен иметь tile_size, РОВНО равный
	// CellSize / SubTilesPerCell (сейчас 128/2=64) — см. обсуждение и
	// комментарий в шапке файла. Если это число не делится нацело — саб-сетка
	// разъедется с игровой сеткой и энергия будет ставиться со смещением,
	// накапливающимся от клетки к клетке (см. проверку в _Ready).
	[Export] public int SubTilesPerCell = 2;

	// Источник истины: что физически есть в каждой клетке. Ключ — те же
	// мировые (row,col), что и у _entAt в NucleusLayer — та же система
	// координат, тот же CellSize, никакого пересчёта между слоями не нужно.
	private readonly Dictionary<(int row, int col), int> _energyAt = new();

	public int CellSize { get; private set; }
	public int SubCellSize { get; private set; }

	// Выбор типа энергии на панели спавна (см. SelectEnergyType) — null,
	// пока ничего не выбрано, тогда ЛКМ по полю ничего не делает. Ссылка на
	// NucleusLayer нужна только для взаимного сброса выбора (см. там же).
	private int? _selectedEnergyTier;
	private NucleusLayer _nucleusLayer;
	// Второй, экспериментальный способ отображения энергии (см.
	// EnergyClusterLayer) — та же причина для ссылки, что и у _nucleusLayer:
	// взаимоисключающий сброс выбора. В сцене на каждый тир частиц — свой
	// отдельный узел с этим скриптом, поэтому список, а не одна ссылка;
	// находится автоматически по типу скрипта среди узлов того же родителя.
	private List<EnergyClusterLayer> _energyClusterLayers = new();

	// Удержание ЛКМ — тем же принципом, что и у установки ядер: пока кнопка
	// зажата, каждый кадр пробуем поставить энергию под курсором, но не
	// повторяем попытку для той же самой клетки, пока курсор из неё не ушёл.
	private bool _leftMouseHeld;
	private (int row, int col)? _lastPlacedCell;

	// Все 8 соседей (не только 4 ортогональных) — Terrain здесь настроен в
	// режиме Match Corners and Sides, то есть форма куска зависит и от
	// диагоналей тоже. На практике выяснилось, что SetCellsTerrainConnect
	// пересчитывает форму ТОЛЬКО для тех клеток, которые ему передали явно —
	// уже стоящие соседние тайлы он не трогает, даже если из-за нового
	// соседа их форма должна была измениться (старый тайл "дотягивался"
	// только при повторном клике по нему же). Поэтому при каждой установке
	// в список на пересчёт добавляются ещё и все соседи, где уже есть
	// энергия — тогда Godot пересчитывает форму всех их разом вместе с новой.
	private static readonly (int dRow, int dCol)[] NeighborOffsets =
	{
		(-1, -1), (-1, 0), (-1, 1),
		( 0, -1),          ( 0, 1),
		( 1, -1), ( 1, 0), ( 1, 1),
	};

	public override void _Ready()
	{
		var gridDraw = GetNode<GridDraw>("../GridLayer");
		CellSize = gridDraw.CellSize;
		SubCellSize = CellSize / SubTilesPerCell;

		// Целочисленное деление выше молча округлит, если CellSize не делится
		// на SubTilesPerCell нацело — и тогда саб-сетка постепенно разъедется
		// с игровой (именно так когда-то и случилось смещение при переходе на
		// клетку 128 с забытым старым SubTilesPerCell=3). Проверяем явно,
		// чтобы в следующий раз это была ошибка в Output, а не загадка на поле.
		if (CellSize % SubTilesPerCell != 0)
		{
			GD.PrintErr($"[EnergyLayer] CellSize={CellSize} не делится нацело на SubTilesPerCell={SubTilesPerCell} " +
				$"(SubCellSize округлится до {SubCellSize}) — саб-тайлы энергии будут смещаться от игровой сетки. " +
				"Проверьте tile_size у TileSet этого узла и значение SubTilesPerCell.");
		}

		// Этой проверки раньше не было, и именно поэтому смещение при
		// CellSize 128 -> 96 осталось незамеченным: 96 % 2 == 0, так что
		// проверка выше молчала, хотя РЕАЛЬНЫЙ tile_size в TileSet этого узла
		// (64px, задан в Main.tscn) с SubTilesPerCell=2 покрывает не 96, а
		// 128px — расчётный SubCellSize и фактический размер тайла разошлись.
		// Сверяем с настоящим TileSet.TileSize (то, что Godot реально рисует),
		// а не только с арифметикой CellSize/SubTilesPerCell.
		var actualTileSize = TileSet?.TileSize ?? Vector2I.Zero;
		if (actualTileSize.X != SubCellSize || actualTileSize.Y != SubCellSize)
		{
			GD.PrintErr($"[EnergyLayer] TileSet.TileSize этого узла = {actualTileSize}, а по CellSize={CellSize}/" +
				$"SubTilesPerCell={SubTilesPerCell} ожидался {SubCellSize}x{SubCellSize} — саб-тайлы земли/энергии " +
				"будут смещены или растянуты относительно игровой сетки. Либо перерисуйте tile_size под " +
				$"{SubCellSize}px, либо подберите SubTilesPerCell = CellSize / {actualTileSize.X} (если делится нацело).");
		}

		// Энергия должна лежать визуально НИЖЕ ядер независимо от порядка
		// узлов в дереве сцены (сейчас TileMapLayer — последний ребёнок
		// Main, то есть рисовался бы поверх NucleusLayer без этого).
		ZIndex = -1;

		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer clusterLayer)
				_energyClusterLayers.Add(clusterLayer);

		SetProcessUnhandledInput(true);
	}

	// Вызывается с панели спавна при нажатии кнопки типа энергии —
	// запоминает, что ставить следующим ЛКМ (или удержанием ЛКМ) по полю, и
	// сбрасывает выбор ядра (взаимоисключающий выбор — см. комментарий у
	// NucleusLayer.SelectSpawnPreset).
	public void SelectEnergyType(int tier)
	{
		_selectedEnergyTier = tier;
		_lastPlacedCell = null;
		_nucleusLayer?.ClearSelection();
		foreach (var clusterLayer in _energyClusterLayers) clusterLayer.ClearSelection();
		GD.Print($"[EnergyLayer] выбран тип энергии {tier}. Клик (или удержание ЛКМ) по полю — поставить.");
	}

	// Вызывается NucleusLayer при выборе пресета ядра — сбрасывает выбор
	// энергии (см. комментарий у SelectEnergyType).
	public void ClearSelection()
	{
		_selectedEnergyTier = null;
	}

	public bool HasEnergyAt(int row, int col) => _energyAt.ContainsKey((row, col));
	public int? EnergyTierAt(int row, int col) => _energyAt.TryGetValue((row, col), out var tier) ? tier : null;

	// _UnhandledInput (а не _Input) — по той же причине, что и в
	// NucleusLayer: клик по кнопке на панели уже "съедается" GUI-системой и
	// не должен ещё и ставить энергию под курсором заодно.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;
		// На слое 2 клики обрабатывает только слой молекул (см. ViewLayer).
		if (ViewLayer.IsLayer2) return;

		if (mb.Pressed)
		{
			if (!_selectedEnergyTier.HasValue) return;
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

	public override void _Process(double delta)
	{
		// На слое 2 слой 1 не рисуется и не ставится (см. ViewLayer).
		Visible = !ViewLayer.IsLayer2;
		if (ViewLayer.IsLayer2) { _leftMouseHeld = false; return; }
		if (_leftMouseHeld) TryPlaceAtMouseIfSelected();
	}

	private void TryPlaceAtMouseIfSelected()
	{
		if (!_selectedEnergyTier.HasValue) return;

		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);
		if (_lastPlacedCell.HasValue && _lastPlacedCell.Value == (row, col)) return;

		_lastPlacedCell = (row, col);
		PlaceEnergyAt(row, col, _selectedEnergyTier.Value);
	}

	// Ставит энергию тира tier в клетку (row, col). Если там уже есть
	// энергия — тихо перезаписывает тир (форма не меняется, раз сам факт
	// наличия энергии в соседних клетках не изменился).
	public void PlaceEnergyAt(int row, int col, int tier)
	{
		_energyAt[(row, col)] = tier;

		// Логические клетки, чей визуал нужно пересчитать: сама + все уже
		// существующие соседи (см. комментарий у NeighborOffsets) — иначе на
		// стыке блоков форма обновилась бы только у новой клетки, а старая
		// соседняя осталась бы прежней (см. предыдущую правку).
		var logicalCells = new List<(int row, int col)> { (row, col) };
		foreach (var (dRow, dCol) in NeighborOffsets)
		{
			var neighbor = (row + dRow, col + dCol);
			if (_energyAt.ContainsKey(neighbor)) logicalCells.Add(neighbor);
		}

		// Каждая логическая клетка на самом узле — блок SubTilesPerCell x
		// SubTilesPerCell саб-тайлов; собираем их все в один список для
		// одного вызова SetCellsTerrainConnect (Godot сам согласует форму
		// кусков и внутри блока, и на стыке с соседними блоками).
		var cells = new Godot.Collections.Array<Vector2I>();
		foreach (var (lRow, lCol) in logicalCells)
			foreach (var sub in SubCellsOf(lRow, lCol))
				cells.Add(sub);

		SetCellsTerrainConnect(cells, TerrainSetIndex, TerrainIndex);

		// Диагностика — только по блоку только что поставленной клетки, чтобы
		// не заваливать Output десятками строк на каждый клик (блоки соседей
		// пересчитываются штатно и обычно уже были видны раньше). sourceId
		// == -1 значит для этой комбинации соседей в атласе нет подходящего
		// куска (самый частый случай — не хватает куска "нет соседей" для
		// саб-тайла на самом краю блока, у которого сосед — пустая клетка).
		int missing = 0;
		foreach (var sub in SubCellsOf(row, col))
			if (GetCellSourceId(sub) == -1) missing++;

		if (missing > 0)
		{
			GD.Print($"[EnergyLayer] клетка ({row},{col}): {missing}/{SubTilesPerCell * SubTilesPerCell} саб-тайлов НЕ ПОСТАВЛЕНО (в атласе нет куска под нужную комбинацию соседей)");
		}
	}

	// Саб-тайловые координаты (в системе этого TileMapLayer, где tile_size =
	// SubCellSize), которыми физически покрывается логическая клетка
	// (row, col) игрового поля.
	private IEnumerable<Vector2I> SubCellsOf(int row, int col)
	{
		int baseSubRow = row * SubTilesPerCell;
		int baseSubCol = col * SubTilesPerCell;
		for (int dr = 0; dr < SubTilesPerCell; dr++)
			for (int dc = 0; dc < SubTilesPerCell; dc++)
				yield return new Vector2I(baseSubCol + dc, baseSubRow + dr);
	}
}
