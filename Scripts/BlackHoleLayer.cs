using Godot;
using System.Collections.Generic;

// Чёрная дыра — терраин-объект, как и источники энергии (EnergyClusterLayer),
// а НЕ ядро: своего чанкования/колец/MultiMesh у неё нет, она просто живёт в
// собственном разреженном множестве клеток на этом TileMapLayer (тот же
// принцип, что и у "источников" — сначала физическая клетка на карте, потом
// побочный эффект, который эта клетка производит на ядра рядом).
//
// Единственная функция чёрной дыры — уничтожать частицу, попавшую в
// кольцевой слот СОСЕДНЕГО ядра, если этот слот ориентирован на клетку с
// чёрной дырой (см. отдельный шаг поглощения в NucleusLayer.SimTick). Сама
// чёрная дыра ничего не хранит и не накапливает — ни ёмкости, ни
// кластеризации, ни вариантов тайла (спрайт один и тот же для любой клетки,
// см. black_hole_96px.png).
//
// CellSize — тот же принцип единственного источника истины через GridDraw,
// что и у NucleusLayer/EnergyLayer/EnergyClusterLayer.
public partial class BlackHoleLayer : TileMapLayer
{
	public int CellSize { get; private set; }

	// Индекс TileSetAtlasSource внутри TileSet этого узла (см. "sources/N" в
	// Main.tscn) — как и у EnergyClusterLayer.AtlasSourceId, Terrain тут не
	// используется, обычная нумерация источников атласа.
	[Export] public int AtlasSourceId = 0;

	// Клетки, где стоит чёрная дыра — просто множество, без доп. данных:
	// в отличие от EnergyClusterLayer тут нет ни количества частиц, ни
	// кластеризации соседних клеток — каждая клетка с чёрной дырой действует
	// независимо и вечно (поглощает, пока стоит на карте).
	private readonly HashSet<(int row, int col)> _cells = new();

	private bool _selected;
	private NucleusLayer _nucleusLayer;
	private EnergyLayer _energyLayer;
	// Тир-слои источников частиц — нужны для взаимного исключения: клетка не
	// может одновременно быть и источником, и поглотителем (см.
	// PlaceBlackHoleAt), а также для сброса их выбора на панели спавна при
	// выборе режима чёрной дыры (тот же принцип, что и у
	// EnergyClusterLayer._siblingLayers, только между разными типами слоёв).
	private readonly List<EnergyClusterLayer> _clusterLayers = new();

	private bool _leftMouseHeld;
	private (int row, int col)? _lastPlacedCell;

	public override void _Ready()
	{
		var gridDraw = GetNode<GridDraw>("../GridLayer");
		CellSize = gridDraw.CellSize;

		// Тот же порядок отрисовки, что и у остальных терраин-слоёв — ниже
		// ядер, независимо от порядка узлов в дереве сцены.
		ZIndex = -1;

		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		_energyLayer = GetNodeOrNull<EnergyLayer>("../TileMapLayer");

		var parent = GetParent();
		if (parent != null)
		{
			foreach (var child in parent.GetChildren())
				if (child is EnergyClusterLayer layer)
					_clusterLayers.Add(layer);
		}

		SetProcessUnhandledInput(true);
	}

	// Вызывается с панели спавна — включает режим установки чёрной дыры и
	// сбрасывает выбор у всех остальных способов расстановки (тот же принцип
	// взаимоисключающего выбора, что и у EnergyClusterLayer.SelectClusterMode/
	// NucleusLayer.SelectSpawnPreset) — иначе ЛКМ было бы не ясно, что именно
	// ставить.
	public void SelectBlackHoleMode()
	{
		_selected = true;
		_lastPlacedCell = null;
		_nucleusLayer?.ClearSelection();
		_energyLayer?.ClearSelection();
		foreach (var layer in _clusterLayers) layer.ClearSelection();
		GD.Print("[BlackHoleLayer] выбрана чёрная дыра. Клик (или удержание ЛКМ) по полю — поставить.");
	}

	// Вызывается NucleusLayer/EnergyLayer/EnergyClusterLayer при выборе своего
	// пресета — сбрасывает выбор здесь (см. комментарий у SelectBlackHoleMode).
	public void ClearSelection()
	{
		_selected = false;
	}

	public bool HasBlackHoleAt(int row, int col) => _cells.Contains((row, col));

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;
		// На слое 2 клики обрабатывает только слой молекул (см. ViewLayer).
		if (ViewLayer.IsLayer2) return;

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
		// На слое 2 слой 1 не рисуется и не ставится (см. ViewLayer).
		Visible = !ViewLayer.IsLayer2;
		if (ViewLayer.IsLayer2) { _leftMouseHeld = false; return; }
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
		PlaceBlackHoleAt(row, col);
	}

	// Ставит чёрную дыру в клетку (row, col) — один и тот же тайл всегда, без
	// вариантов (в отличие от EnergyClusterLayer.PlaceClusterAt). Стирает в
	// этой же клетке источник частиц на всех тир-слоях, если он там был —
	// клетка не может одновременно быть и источником, и поглотителем
	// (симметрично тому, как сами тир-слои стирают друг друга при взаимном
	// перехвате клетки — см. EnergyClusterLayer.PlaceClusterAt/_siblingLayers).
	// Повторная установка в ту же клетку безвредна (HashSet, SetCell идемпотентны).
	public void PlaceBlackHoleAt(int row, int col)
	{
		foreach (var layer in _clusterLayers) layer.EraseClusterAt(row, col);

		_cells.Add((row, col));
		SetCell(new Vector2I(col, row), AtlasSourceId, Vector2I.Zero);
	}

	// Убирает чёрную дыру из клетки — публичный симметричный метод на случай
	// будущего инструмента стирания (сейчас в UI кнопки на это нет, как и у
	// EnergyClusterLayer.EraseClusterAt для соответствующего случая).
	public void EraseBlackHoleAt(int row, int col)
	{
		if (!_cells.Remove((row, col))) return;
		EraseCell(new Vector2I(col, row));
	}
}
