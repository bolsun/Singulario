using Godot;
using System.Collections.Generic;

// Слой 2: молекулы — ядра слоя 2. Клетка слоя 2 = один чанк слоя 1, её
// координаты (cx, cy) — те же, что NucleusLayer считает через ChunkSize.
// Оба слоя живут в одних мировых координатах с одной камерой: молекула
// заполняет свою L2-клетку, то есть в мире она в ChunkSize раз больше ядра
// (1536 вместо 96 единиц), а на экране в момент переключения слоя (зум
// 1/ChunkSize) выглядит ровно как ядро слоя 1 при зуме 1.
//
// Вращение — те же законы, что у ядер, но в тиках слоя 2: 1 тик слоя 2 =
// L2TickRatio (k) тиков слоя 1, поэтому период шага молекулы = TierTicks[tier] *
// L2TickRatio. k — настройка прототипа (T001b: сравниваем k = 1 и k = 8, GDD
// пока фиксирует k = 8), в сохранение не пишется; K переключает её на лету. Фаза не хранится — чистая функция глобального тика
// NucleusLayer (единые часы, пауза общая), см. RingMath. Своих тиков у слоя
// нет: переноса атомов пока нет (T002), поэтому и "сна" до фазы 0, как у
// ядер слоя 1, у молекулы нет — она сразу крутится в общей фазе своего тира.
//
// Правило занятости: L2-клетка — либо чанк с содержимым слоя 1 (ядра,
// источники, чёрные дыры), либо молекула, но не то и другое сразу.
//
// Ещё этот узел рисует на слое 2 упрощённый вид содержимого слоя 1 —
// заливку L2-клеток (см. _Draw), пока детальная отрисовка слоя 1 скрыта.
public partial class MoleculeLayer : Node2D
{
	// Соотношение времени слоёв k: 1 тик слоя 2 = k тиков слоя 1 — одно место
	// истины. Допустимо 1/2/4/8 (степени двойки, чтобы периоды молекул делили
	// периоды колец); иное значение при запуске приводится к ближайшему.
	[Export] public int L2TickRatio = 1;
	private static readonly int[] AllowedTickRatios = { 1, 2, 4, 8, 16 };

	[Export] public float FillAlpha = 0.35f;
	[Export] public float BlockedAlpha = 0.35f;
	// Затухание гнёзд молекул на слое 1: от перехода со слоя 2 до зума, на
	// котором появляются дырки ядер слоя 1 (NucleusLayer.HoleHideZoom).
	// Прогресс t считается по логарифму зума (колесо меняет зум умножением),
	// прозрачность = HoleOpacity * (1 - t)^HoleFadeExponent — при показателе
	// > 1 гнёзда быстро бледнеют сразу после перехода, затем хвост уходит в 0.
	[Export] public float HoleFadeExponent = 2f;

	private static readonly Color NucleiFillColor = new Color(0.8f, 0.8f, 0.85f);
	private static readonly Color BlackHoleFillColor = new Color(0.45f, 0.2f, 0.6f);
	private static readonly Color BlockFillColor = new Color(0.55f, 0.55f, 0.6f);
	[Export] public float BlockAlpha = 0.45f;
	private static readonly Color BlockedColor = new Color(1f, 0.15f, 0.15f);
	private static readonly Transform2D HiddenTransform = new Transform2D(Vector2.Zero, Vector2.Zero, Vector2.Zero);

	private class Molecule
	{
		public int Cx, Cy;
		public int Tier;
		public int Dir;
		public int HoleCount;
		public bool[] Slots; // RingMath.SlotMask(HoleCount)
		public MoleculeSlot[] Content = new MoleculeSlot[RingMath.Positions]; // содержимое гнёзд (T002)
	}

	// Содержимое гнезда молекулы: пусто / атом. Locked — только что принятый
	// атом, отдать дальше можно после следующего поворота этой молекулы (тот же
	// закон, что у частиц в гнёздах ядер слоя 1).
	private struct MoleculeSlot
	{
		public bool HasAtom;
		public Atom Atom;
		public bool Locked;
	}

	// Для сохранения (см. NucleusLayer.ExportFieldJson/ImportFieldJson).
	public class SavedMolecule
	{
		public int Cx { get; set; }
		public int Cy { get; set; }
		public int Tier { get; set; }
		public int Dir { get; set; }
		public int HoleCount { get; set; }
	}

	private NucleusLayer _nucleusLayer;
	private BlackHoleLayer _blackHoleLayer;
	private readonly List<EnergyClusterLayer> _clusterLayers = new();

	// Поиск по клетке + список в порядке установки (он же индекс инстанса в
	// MultiMesh и порядок экспорта — детерминирован, не зависит от словаря).
	private readonly Dictionary<(int cx, int cy), Molecule> _at = new();
	private readonly List<Molecule> _list = new();

	private MultiMeshInstance2D _bodyNode;
	private MultiMeshInstance2D _holeNode;
	private Node2D _atomNode;
	private QuadMesh _coreQuad;
	private QuadMesh _holeQuad;
	private Sprite2D _preview;
	private Polygon2D _blockedMarker;

	private int _cellSize;
	private int _chunkSize;
	private int _chunkWorldSize;
	private float _scale; // во сколько раз молекула больше ядра слоя 1 (= ChunkSize)

	private int? _selectedTier;
	private int _selectedHoleCount;
	private int _currentSpinDirection = 1;

	private bool _leftMouseHeld;
	private bool _rightMouseHeld;
	private (int cx, int cy)? _lastPlacedCell;
	private (int cx, int cy)? _lastRemovedCell;

	// Заливка L2-клеток на слое 2 (см. RebuildFills/_Draw).
	private readonly Dictionary<(int cx, int cy), Color> _fills = new();
	private readonly List<(int cx, int cy)> _blocks = new();
	private bool _ready;

	public int MoleculeCount => _list.Count;

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		_blackHoleLayer = GetNodeOrNull<BlackHoleLayer>("../BlackHoleLayer");
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer layer)
				_clusterLayers.Add(layer);

		if (_nucleusLayer == null || !_nucleusLayer.IsReady)
		{
			GD.PrintErr("[MoleculeLayer] NucleusLayer не найден или не инициализирован — слой молекул выключен.");
			return;
		}

		_cellSize = _nucleusLayer.CellSize;
		_chunkSize = _nucleusLayer.ChunkSize;
		_chunkWorldSize = _cellSize * _chunkSize;
		_scale = _chunkSize;
		_currentSpinDirection = _nucleusLayer.SpinDirection;

		int ratio = NearestAllowedTickRatio(L2TickRatio);
		if (ratio != L2TickRatio)
		{
			GD.PushWarning($"[MoleculeLayer] L2TickRatio = {L2TickRatio} недопустимо (1/2/4/8) — используется {ratio}.");
			L2TickRatio = ratio;
		}

		_coreQuad = new QuadMesh { Size = new Vector2(_nucleusLayer.SpriteSize, _nucleusLayer.SpriteSize) };
		_holeQuad = new QuadMesh { Size = new Vector2(_nucleusLayer.HoleSpriteSize, _nucleusLayer.HoleSpriteSize) };

		_bodyNode = new MultiMeshInstance2D
		{
			Name = "MoleculeBodies",
			Texture = _nucleusLayer.CoreTexture,
			Material = _nucleusLayer.PaletteMaterial,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
		};
		AddChild(_bodyNode);

		_holeNode = new MultiMeshInstance2D
		{
			Name = "MoleculeHoles",
			Texture = _nucleusLayer.HoleTexture,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Modulate = new Color(1f, 1f, 1f, _nucleusLayer.HoleOpacity),
		};
		AddChild(_holeNode);

		// Атомы в гнёздах — поверх полупрозрачных дырок (см. DrawAtoms).
		_atomNode = new Node2D { Name = "MoleculeAtoms" };
		_atomNode.Draw += DrawAtoms;
		AddChild(_atomNode);

		_preview = new Sprite2D
		{
			Texture = _nucleusLayer.CoreTexture,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Scale = new Vector2(_scale, _scale),
			Visible = false,
			ZIndex = 100,
		};
		AddChild(_preview);

		// Прямоугольник в единицах 0..1 — масштабируется под размер клетки
		// нужного слоя (клетка слоя 1 или L2-клетка), см. ShowBlocked.
		_blockedMarker = new Polygon2D
		{
			Polygon = new[] { Vector2.Zero, new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
			Color = new Color(BlockedColor.R, BlockedColor.G, BlockedColor.B, BlockedAlpha),
			Visible = false,
			ZIndex = 101,
		};
		AddChild(_blockedMarker);

		RebuildMeshes();
		ViewLayer.Changed += OnViewLayerChanged;
		_ready = true;
	}

	public override void _ExitTree()
	{
		ViewLayer.Changed -= OnViewLayerChanged;
	}

	// Ближайшее допустимое k; при равном расстоянии — меньшее.
	private static int NearestAllowedTickRatio(int value)
	{
		int best = AllowedTickRatios[0];
		foreach (int r in AllowedTickRatios)
			if (System.Math.Abs(r - value) < System.Math.Abs(best - value)) best = r;
		return best;
	}

	// K — следующее k по кругу 1 → 2 → 4 → 8 → 1 (сравнение темпа на лету;
	// скачок фазы молекул при переключении допустим).
	private void CycleTickRatio()
	{
		int i = System.Array.IndexOf(AllowedTickRatios, L2TickRatio);
		L2TickRatio = AllowedTickRatios[(i + 1) % AllowedTickRatios.Length];
		GD.Print($"[MoleculeLayer] соотношение времени слоёв k = {L2TickRatio}.");
	}

	private void OnViewLayerChanged(int layer)
	{
		_leftMouseHeld = false;
		_rightMouseHeld = false;
		QueueRedraw(); // убрать/показать заливку клеток
	}

	// --- выбор инструмента (панель спавна, см. NucleusSpawnPanel) ---

	public void SelectPreset(int tier, int holeCount)
	{
		_selectedTier = tier;
		_selectedHoleCount = holeCount;
		_lastPlacedCell = null;
		GD.Print($"[MoleculeLayer] выбрана молекула: тир {tier}, гнёзд {holeCount}/8, направление {(_currentSpinDirection > 0 ? "по часовой" : "против часовой")} (R — переключить).");
	}

	// --- ввод (только на слое 2) ---

	public override void _Input(InputEvent @event)
	{
		if (!_ready) return;
		if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

		// K работает на обоих слоях — это настройка прототипа, не действие с молекулой.
		if (key.Keycode == Key.K)
		{
			CycleTickRatio();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (!ViewLayer.IsLayer2) return;

		if (key.Keycode == Key.R)
		{
			ToggleSpinDirectionUnderMouse();
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode == Key.Q)
		{
			PickUnderMouse();
			GetViewport().SetInputAsHandled();
		}
	}

	// _UnhandledInput — клики по кнопкам панели сюда не доходят (как у NucleusLayer).
	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_ready || !ViewLayer.IsLayer2) return;
		if (@event is not InputEventMouseButton mb) return;

		if (mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				if (!_selectedTier.HasValue) return;
				_leftMouseHeld = true;
				_lastPlacedCell = null;
				TryPlaceAtMouse();
				GetViewport().SetInputAsHandled();
			}
			else _leftMouseHeld = false;
		}
		else if (mb.ButtonIndex == MouseButton.Right)
		{
			if (mb.Pressed)
			{
				_rightMouseHeld = true;
				_lastRemovedCell = null;
				TryRemoveAtMouse();
				GetViewport().SetInputAsHandled();
			}
			else _rightMouseHeld = false;
		}
	}

	private (int cx, int cy) ChunkUnderMouse()
	{
		var p = GetGlobalMousePosition();
		return (Mathf.FloorToInt(p.X / _chunkWorldSize), Mathf.FloorToInt(p.Y / _chunkWorldSize));
	}

	private void TryPlaceAtMouse()
	{
		if (!_selectedTier.HasValue) return;
		var cell = ChunkUnderMouse();
		if (_lastPlacedCell.HasValue && _lastPlacedCell.Value == cell) return;
		_lastPlacedCell = cell;
		TryPlace(cell.cx, cell.cy, _selectedTier.Value, _selectedHoleCount, _currentSpinDirection, log: true);
	}

	private void TryRemoveAtMouse()
	{
		var cell = ChunkUnderMouse();
		if (_lastRemovedCell.HasValue && _lastRemovedCell.Value == cell) return;
		_lastRemovedCell = cell;
		if (Remove(cell.cx, cell.cy))
			GD.Print($"[MoleculeLayer] удалена молекула из L2-клетки ({cell.cx},{cell.cy}).");
	}

	private void ToggleSpinDirectionUnderMouse()
	{
		var cell = ChunkUnderMouse();
		if (!_at.TryGetValue(cell, out var m))
		{
			GD.Print($"[MoleculeLayer] R: в L2-клетке ({cell.cx},{cell.cy}) нет молекулы.");
			return;
		}
		m.Dir = -m.Dir;
		_currentSpinDirection = m.Dir;
		GD.Print($"[MoleculeLayer] направление молекулы ({cell.cx},{cell.cy}) переключено на {(m.Dir > 0 ? "по часовой" : "против часовой")}.");
	}

	private void PickUnderMouse()
	{
		var cell = ChunkUnderMouse();
		if (!_at.TryGetValue(cell, out var m))
		{
			GD.Print($"[MoleculeLayer] пипетка: в L2-клетке ({cell.cx},{cell.cy}) нет молекулы.");
			return;
		}
		_currentSpinDirection = m.Dir;
		SelectPreset(m.Tier, m.HoleCount);
	}

	// --- правило занятости ---

	// Есть ли в чанке (cx, cy) что-то из слоя 1: ядра, источники, чёрные дыры,
	// открытый порт (такой чанк — блок слоя 2).
	public bool ChunkHasLayer1Content(int cx, int cy)
	{
		if (_nucleusLayer.ChunkHasNuclei(cx, cy)) return true;
		if (_nucleusLayer.Ports != null && _nucleusLayer.Ports.IsBlock(cx, cy)) return true;

		int row0 = cy * _chunkSize;
		int col0 = cx * _chunkSize;
		for (int r = row0; r < row0 + _chunkSize; r++)
		{
			for (int c = col0; c < col0 + _chunkSize; c++)
			{
				if (_blackHoleLayer != null && _blackHoleLayer.HasBlackHoleAt(r, c)) return true;
				foreach (var layer in _clusterLayers)
					if (layer.HasClusterAt(r, c)) return true;
			}
		}
		return false;
	}

	public bool HasMoleculeAtChunk(int cx, int cy) => _at.ContainsKey((cx, cy));

	// Для слоёв слоя 1: можно ли ставить объект в клетку (row, col) — нельзя,
	// если её чанк занят молекулой.
	public bool HasMoleculeAtCell(int row, int col)
	{
		if (_chunkSize <= 0) return false;
		return _at.ContainsKey((FloorDiv(col, _chunkSize), FloorDiv(row, _chunkSize)));
	}

	private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

	private bool IsNormalTier(int tier) =>
		tier != _nucleusLayer.GrayCoreTier && tier != _nucleusLayer.RotatorCoreTier && tier != _nucleusLayer.ThrowerCoreTier;

	// Причина, по которой молекулу нельзя поставить в клетку, или null.
	private string PlaceBlockReason(int cx, int cy, int tier)
	{
		if (ChunkHasLayer1Content(cx, cy)) return "в чанке есть объекты слоя 1";
		if (_at.TryGetValue((cx, cy), out var existing) && (!IsNormalTier(tier) || !IsNormalTier(existing.Tier)))
			return "клетка уже занята";
		return null;
	}

	private bool TryPlace(int cx, int cy, int tier, int holeCount, int dir, bool log)
	{
		string reason = PlaceBlockReason(cx, cy, tier);
		if (reason != null)
		{
			if (log) GD.Print($"[MoleculeLayer] L2-клетка ({cx},{cy}): {reason} — пропуск.");
			return false;
		}

		// Цветная на цветную — замена (то же правило, что у ядер слоя 1).
		if (_at.ContainsKey((cx, cy))) RemoveWithoutRebuild(cx, cy);

		var m = new Molecule
		{
			Cx = cx,
			Cy = cy,
			Tier = tier,
			Dir = dir,
			HoleCount = holeCount,
			Slots = RingMath.SlotMask(holeCount),
		};
		_at[(cx, cy)] = m;
		_list.Add(m);
		RebuildMeshes();

		if (log) GD.Print($"[MoleculeLayer] установлена молекула тира {tier}, гнёзд {holeCount}/8 в L2-клетке ({cx},{cy}).");
		return true;
	}

	private bool Remove(int cx, int cy)
	{
		if (!RemoveWithoutRebuild(cx, cy)) return false;
		RebuildMeshes();
		return true;
	}

	private bool RemoveWithoutRebuild(int cx, int cy)
	{
		if (!_at.Remove((cx, cy), out var m)) return false;
		_list.Remove(m);
		return true;
	}

	// --- сохранение/загрузка ---

	public List<SavedMolecule> ExportMolecules()
	{
		var result = new List<SavedMolecule>(_list.Count);
		foreach (var m in _list)
			result.Add(new SavedMolecule { Cx = m.Cx, Cy = m.Cy, Tier = m.Tier, Dir = m.Dir, HoleCount = m.HoleCount });
		return result;
	}

	public void ClearAll()
	{
		_at.Clear();
		_list.Clear();
		if (_ready) RebuildMeshes();
	}

	// Вызывать после того, как слой 1 уже загружен: молекула в чанк с
	// содержимым слоя 1 не ставится (как и при ручной установке).
	public int ImportMolecules(List<SavedMolecule> molecules)
	{
		if (!_ready || molecules == null) return 0;
		int placed = 0;
		foreach (var sm in molecules)
		{
			int dir = sm.Dir >= 0 ? 1 : -1;
			if (_at.ContainsKey((sm.Cx, sm.Cy)) || !TryPlace(sm.Cx, sm.Cy, sm.Tier, sm.HoleCount, dir, log: false))
				GD.PrintErr($"[MoleculeLayer] импорт: L2-клетка ({sm.Cx},{sm.Cy}) занята — молекула пропущена.");
			else placed++;
		}
		return placed;
	}

	// --- отрисовка ---

	private Vector2 CenterOf(Molecule m) =>
		new Vector2(m.Cx * _chunkWorldSize + _chunkWorldSize / 2f, m.Cy * _chunkWorldSize + _chunkWorldSize / 2f);

	private void RebuildMeshes()
	{
		int count = _list.Count;
		var bodyMM = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = _coreQuad,
			InstanceCount = count,
		};
		var scale = new Vector2(_scale, _scale);
		for (int i = 0; i < count; i++)
		{
			var m = _list[i];
			bodyMM.SetInstanceTransform2D(i, new Transform2D(0f, scale, 0f, CenterOf(m)));
			float rowUv = (m.Tier + 0.5f) / _nucleusLayer.TierCount;
			bodyMM.SetInstanceCustomData(i, new Color(rowUv, 0f, 0f, 0f));
		}
		_bodyNode.Multimesh = bodyMM;

		_holeNode.Multimesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			Mesh = _holeQuad,
			InstanceCount = count * RingMath.Positions,
		};
		UpdateHoles();
	}

	private int PeriodTicks(int tier)
	{
		var tierTicks = _nucleusLayer.TierTicks;
		int ticks = tier < tierTicks.Length ? tierTicks[tier] : tierTicks[tierTicks.Length - 1];
		return ticks * L2TickRatio;
	}

	// Тот же расчёт угла, что у ядер слоя 1 (NucleusLayer.UpdateChunkVisuals),
	// только период шага — в тиках слоя 2, а орбита и спрайт — в _scale раз больше.
	private void UpdateHoles()
	{
		var holeMM = _holeNode.Multimesh;
		if (holeMM == null) return;

		var scale = new Vector2(_scale, _scale);

		for (int i = 0; i < _list.Count; i++)
		{
			var m = _list[i];
			float offset = RenderOffset(m);
			for (int k = 0; k < RingMath.Positions; k++)
			{
				int idx = i * RingMath.Positions + k;
				if (!m.Slots[k])
				{
					holeMM.SetInstanceTransform2D(idx, HiddenTransform);
					continue;
				}
				holeMM.SetInstanceTransform2D(idx, new Transform2D(0f, scale, 0f, SlotPosition(m, k, offset)));
			}
		}
	}

	// Плавный угол кольца (в шагах по 45°) для рендера: дискретная фаза плюс
	// доля до следующего шага.
	private float RenderOffset(Molecule m)
	{
		long tick = _nucleusLayer.GlobalTick;
		int period = PeriodTicks(m.Tier);
		float offset = RingMath.RotationStep(tick, period, m.Dir);
		if (period > 0)
			offset += (RingMath.TicksIntoStep(tick, period) + _nucleusLayer.SubTickFraction) / period * m.Dir;
		return offset;
	}

	private Vector2 SlotPosition(Molecule m, int k, float offset)
	{
		float angle = (k + offset) * (Mathf.Pi / 4f) - (Mathf.Pi / 2f);
		return CenterOf(m) + _nucleusLayer.OrbitRadius * _scale * new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
	}

	// Атом в гнезде: кружок и 8 точек по составу.
	private void DrawAtoms()
	{
		if (!ViewLayer.IsLayer2) return;
		var colors = _nucleusLayer.TierPreviewColors;
		float radius = _nucleusLayer.HoleSpriteSize * _scale * 0.3f;
		foreach (var m in _list)
		{
			float offset = RenderOffset(m);
			for (int k = 0; k < RingMath.Positions; k++)
			{
				if (!m.Slots[k] || !m.Content[k].HasAtom) continue;
				DrawAtom(_atomNode, SlotPosition(m, k, offset), radius, m.Content[k].Atom, colors);
			}
		}
	}

	public static void DrawAtom(CanvasItem ci, Vector2 center, float radius, Atom atom, Color[] tierColors)
	{
		ci.DrawCircle(center, radius, new Color(0.08f, 0.08f, 0.1f, 0.9f));
		ci.DrawArc(center, radius, 0f, Mathf.Tau, 24, Colors.White, radius * 0.12f);
		for (int i = 0; i < atom.Count; i++)
		{
			float a = i * Mathf.Tau / Atom.Size - Mathf.Pi / 2f;
			int color = atom.ColorAt(i);
			var c = (tierColors != null && color >= 0 && color < tierColors.Length) ? tierColors[color] : Colors.White;
			ci.DrawCircle(center + radius * 0.6f * new Vector2(Mathf.Cos(a), Mathf.Sin(a)), radius * 0.2f, c);
		}
	}

	// --- перенос атомов (T002) ---
	// Законы ядер слоя 1 (NucleusLayer.SimTick, шаг 2), только в тиках слоя 2:
	// шаг выполняется на тиках слоя 1, кратных k. Передача — только по 4
	// сторонам, из гнезда в гнездо, смотрящие друг на друга; тир и спин — общие
	// правила TransferRules с флагами слоя 1; принятый атом блокируется до
	// следующего поворота принявшей молекулы. Блок — неподвижный серый объект
	// без спина: молекула забирает готовый атом с выходной стороны и отдаёт атом
	// во входную. Порядок обхода — _list (порядок установки), детерминирован.

	private static readonly (int dx, int dy)[] Compass4 = { (0, -1), (1, 0), (0, 1), (-1, 0) }; // N, E, S, W
	private readonly HashSet<(Molecule m, int slot)> _claimed = new();

	private int PhysicalSlot(Molecule m, int compass, long tick)
	{
		int step = RingMath.RotationStep(tick, PeriodTicks(m.Tier), m.Dir);
		return ((compass - step) % RingMath.Positions + RingMath.Positions) % RingMath.Positions;
	}

	// Поворачиватель и бросатель на слое 2 в T002 не участвуют — атомов не держат.
	private bool CanHoldAtoms(Molecule m) =>
		m.Tier != _nucleusLayer.RotatorCoreTier && m.Tier != _nucleusLayer.ThrowerCoreTier;

	// Вызывается NucleusLayer после каждого тика слоя 1 (общие часы и пауза).
	public void SimTick(long tick)
	{
		if (!_ready || _list.Count == 0 || L2TickRatio <= 0 || tick % L2TickRatio != 0) return;

		// Поворот: снять блокировку у молекул, чей шаг приходится на этот тик.
		foreach (var m in _list)
		{
			int period = PeriodTicks(m.Tier);
			if (period <= 0 || tick % period != 0) continue;
			for (int i = 0; i < RingMath.Positions; i++) m.Content[i].Locked = false;
		}

		_claimed.Clear();

		// Проход A ("pull"): пустое гнездо забирает атом у соседа напротив.
		foreach (var m in _list)
		{
			if (!CanHoldAtoms(m)) continue;
			for (int side = 0; side < 4; side++)
			{
				int k = side * 2;
				int p = PhysicalSlot(m, k, tick);
				if (!m.Slots[p] || m.Content[p].HasAtom || _claimed.Contains((m, p))) continue;
				var (dx, dy) = Compass4[side];
				if (!_at.TryGetValue((m.Cx + dx, m.Cy + dy), out var nb) || !CanHoldAtoms(nb)) continue;
				int p2 = PhysicalSlot(nb, (k + 4) % 8, tick);
				var giver = nb.Content[p2];
				if (!nb.Slots[p2] || !giver.HasAtom || giver.Locked || _claimed.Contains((nb, p2))) continue;
				if (!_nucleusLayer.TierSpinAllowed(m.Tier, m.Dir, nb.Tier, nb.Dir)) continue;

				m.Content[p] = new MoleculeSlot { HasAtom = true, Atom = giver.Atom, Locked = true };
				nb.Content[p2] = default;
				_claimed.Add((m, p));
				_claimed.Add((nb, p2));
			}
		}

		// Проход B ("push"): свободный атом толкается в пустое гнездо соседа.
		foreach (var m in _list)
		{
			if (!CanHoldAtoms(m)) continue;
			for (int side = 0; side < 4; side++)
			{
				int k = side * 2;
				int p = PhysicalSlot(m, k, tick);
				var giver = m.Content[p];
				if (!m.Slots[p] || !giver.HasAtom || giver.Locked || _claimed.Contains((m, p))) continue;
				var (dx, dy) = Compass4[side];
				if (!_at.TryGetValue((m.Cx + dx, m.Cy + dy), out var nb) || !CanHoldAtoms(nb)) continue;
				int p2 = PhysicalSlot(nb, (k + 4) % 8, tick);
				if (!nb.Slots[p2] || nb.Content[p2].HasAtom || _claimed.Contains((nb, p2))) continue;
				if (!_nucleusLayer.TierSpinAllowed(nb.Tier, nb.Dir, m.Tier, m.Dir)) continue;

				nb.Content[p2] = new MoleculeSlot { HasAtom = true, Atom = giver.Atom, Locked = true };
				m.Content[p] = default;
				_claimed.Add((m, p));
				_claimed.Add((nb, p2));
			}
		}

		// Молекула ↔ блок. На одну сторону блока смотрит ровно одна L2-клетка,
		// поэтому спора за порт между молекулами нет.
		var ports = _nucleusLayer.Ports;
		if (ports == null) return;
		int gray = _nucleusLayer.GrayCoreTier;
		foreach (var m in _list)
		{
			if (!CanHoldAtoms(m)) continue;
			for (int side = 0; side < 4; side++)
			{
				int k = side * 2;
				int p = PhysicalSlot(m, k, tick);
				if (!m.Slots[p] || _claimed.Contains((m, p))) continue;
				var (dx, dy) = Compass4[side];
				var key = new PortKey(m.Cx + dx, m.Cy + dy, PortSet.OppositeSide(side));
				var mode = ports.ModeOf(key);
				if (mode == PortMode.Closed) continue;

				var slot = m.Content[p];
				if (mode == PortMode.Output && !slot.HasAtom && ports.HasReadyAtom(key)
					&& _nucleusLayer.TierSpinAllowed(m.Tier, m.Dir, gray, TransferRules.NoSpin))
				{
					m.Content[p] = new MoleculeSlot { HasAtom = true, Atom = ports.TakeAtom(key), Locked = true };
					_claimed.Add((m, p));
				}
				else if (mode == PortMode.Input && slot.HasAtom && !slot.Locked && ports.CanAcceptAtom(key)
					&& _nucleusLayer.TierSpinAllowed(gray, TransferRules.NoSpin, m.Tier, m.Dir))
				{
					ports.AcceptAtom(key, slot.Atom);
					m.Content[p] = default;
					_claimed.Add((m, p));
				}
			}
		}
	}

	public override void _Process(double delta)
	{
		if (!_ready) return;

		float holeAlpha = HoleAlpha();
		_holeNode.Visible = holeAlpha > 0f;
		_holeNode.Modulate = new Color(1f, 1f, 1f, holeAlpha);
		if (_holeNode.Visible) UpdateHoles();

		if (ViewLayer.IsLayer2)
		{
			if (_leftMouseHeld) TryPlaceAtMouse();
			if (_rightMouseHeld) TryRemoveAtMouse();
			RebuildFills();
			QueueRedraw();
			_atomNode.QueueRedraw();
			UpdateLayer2Hover();
		}
		else
		{
			_preview.Visible = false;
			UpdateLayer1Hover();
		}
	}

	private float HoleAlpha()
	{
		float full = _nucleusLayer.HoleOpacity;
		if (ViewLayer.IsLayer2) return full;

		var cam = GetViewport().GetCamera2D();
		if (cam == null) return full;

		float start = ViewLayer.ExitFactor / _chunkSize; // зум возврата на слой 1
		float end = _nucleusLayer.HoleHideZoom;
		if (end <= start) return 0f;

		float t = Mathf.Log(cam.Zoom.X / start) / Mathf.Log(end / start);
		t = Mathf.Clamp(t, 0f, 1f);
		return full * Mathf.Pow(1f - t, HoleFadeExponent);
	}

	// Слой 2: превью молекулы в свободной клетке или красная клетка, если нельзя.
	private void UpdateLayer2Hover()
	{
		_blockedMarker.Visible = false;
		_preview.Visible = false;
		if (!_selectedTier.HasValue) return;

		var (cx, cy) = ChunkUnderMouse();
		int tier = _selectedTier.Value;
		var origin = new Vector2(cx * _chunkWorldSize, cy * _chunkWorldSize);

		if (PlaceBlockReason(cx, cy, tier) != null)
		{
			ShowBlocked(origin, _chunkWorldSize);
			return;
		}

		var colors = _nucleusLayer.TierPreviewColors;
		var baseColor = (colors != null && tier >= 0 && tier < colors.Length) ? colors[tier] : Colors.White;
		_preview.Position = origin + new Vector2(_chunkWorldSize / 2f, _chunkWorldSize / 2f);
		_preview.Modulate = new Color(baseColor.R, baseColor.G, baseColor.B, 0.5f);
		_preview.Visible = true;
	}

	// Слой 1: если выбран инструмент установки, а клетка под курсором в чанке
	// с молекулой — красная клетка (ставить туда нельзя).
	private void UpdateLayer1Hover()
	{
		_blockedMarker.Visible = false;
		bool placing = _nucleusLayer.HasSpawnSelection || (_blackHoleLayer?.IsPlacing ?? false);
		foreach (var layer in _clusterLayers) placing |= layer.IsPlacing;
		if (!placing) return;

		var p = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(p.X / _cellSize);
		int row = Mathf.FloorToInt(p.Y / _cellSize);
		if (!HasMoleculeAtCell(row, col)) return;

		ShowBlocked(new Vector2(col * _cellSize, row * _cellSize), _cellSize);
	}

	private void ShowBlocked(Vector2 origin, float size)
	{
		_blockedMarker.Position = origin;
		_blockedMarker.Scale = new Vector2(size, size);
		_blockedMarker.Visible = true;
	}

	// Упрощённый вид содержимого слоя 1 на слое 2: одна заливка на L2-клетку.
	// Приоритет тона: источник (цвет его тира) > чёрная дыра > ядра. Блоки
	// (чанки с открытым портом) не заливаются — их рисует DrawBlocks.
	private void RebuildFills()
	{
		_fills.Clear();
		_blocks.Clear();
		if (_nucleusLayer.Ports != null)
			foreach (var chunk in _nucleusLayer.Ports.EnumerateBlocks()) _blocks.Add(chunk);

		var nucleiColor = new Color(NucleiFillColor.R, NucleiFillColor.G, NucleiFillColor.B, FillAlpha);
		foreach (var chunk in _nucleusLayer.EnumerateOccupiedChunks())
			_fills[chunk] = nucleiColor;

		if (_blackHoleLayer != null)
		{
			var holeColor = new Color(BlackHoleFillColor.R, BlackHoleFillColor.G, BlackHoleFillColor.B, FillAlpha);
			foreach (var (row, col) in _blackHoleLayer.EnumerateCells())
				_fills[(FloorDiv(col, _chunkSize), FloorDiv(row, _chunkSize))] = holeColor;
		}

		var colors = _nucleusLayer.TierPreviewColors;
		foreach (var layer in _clusterLayers)
		{
			var c = (colors != null && layer.Tier >= 0 && layer.Tier < colors.Length) ? colors[layer.Tier] : Colors.White;
			var tierColor = new Color(c.R, c.G, c.B, FillAlpha);
			foreach (var (row, col) in layer.EnumerateCells())
				_fills[(FloorDiv(col, _chunkSize), FloorDiv(row, _chunkSize))] = tierColor;
		}

		foreach (var chunk in _blocks) _fills.Remove(chunk);
	}

	// Блок слоя 2 — неподвижный серый объект на всю L2-клетку с 4 дырками у
	// краёв сторон: те же полудырки и в тех же мировых координатах, что порты
	// слоя 1 (PortLayer.DrawPortHalf), закрытые стороны приглушены, в дырке
	// открытого порта виден атом (точки по составу).
	private void DrawBlocks()
	{
		var ports = _nucleusLayer.Ports;
		var colors = _nucleusLayer.TierPreviewColors;
		float inset = _cellSize * 0.5f;
		foreach (var (cx, cy) in _blocks)
		{
			var rect = new Rect2(cx * _chunkWorldSize + inset, cy * _chunkWorldSize + inset,
				_chunkWorldSize - 2 * inset, _chunkWorldSize - 2 * inset);
			DrawRect(rect, new Color(BlockFillColor, BlockAlpha));
			DrawRect(rect, new Color(BlockFillColor, 0.9f), false, _cellSize * 0.25f);
			for (int side = 0; side < PortSet.SideCount; side++)
			{
				var key = new PortKey(cx, cy, side);
				PortLayer.DrawPortHalf(this, PortLayer.PortCenter(key, _chunkWorldSize), _cellSize, side, ports.Get(key), colors, 1f);
			}
		}
	}

	public override void _Draw()
	{
		if (!_ready || !ViewLayer.IsLayer2) return;
		foreach (var pair in _fills)
		{
			var (cx, cy) = pair.Key;
			DrawRect(new Rect2(cx * _chunkWorldSize, cy * _chunkWorldSize, _chunkWorldSize, _chunkWorldSize), pair.Value);
		}
		DrawBlocks();
	}
}
