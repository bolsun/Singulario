using Godot;

// Порты чанков на слое 1 (T002): ввод (переключение режима) и отрисовка.
// Данные и правила — PortSet (NucleusLayer.Ports), обмен частицами с ядрами —
// NucleusLayer.SimTick, шаг 2в. Этот узел ничего не симулирует.
//
// Жест: НАЖАТИЕ ЛКМ на клетку порта (только само нажатие, не удержание) —
// закрыт → выход → вход → закрыт. Ядро на клетку порта поставить нельзя, так
// что с установкой ядер жест не спорит; протяжка с зажатой ЛКМ порты не
// переключает. Узел стоит в сцене после NucleusLayer, поэтому получает
// _UnhandledInput раньше него и «съедает» клик по порту.
//
// Вид: половина дырки порта — полукруг радиусом в одну клетку с центром на
// середине стороны чанка (порт 2×2 = две половины двух соседних чанков), по
// полукругу 8 точек — набранные частицы своего цвета; полный атом — ещё и
// белое кольцо внутри. Обод — режим: закрыт (приглушённо) / выход / вход.
// Та же функция (DrawPortHalf) рисует дырки блока на слое 2 в тех же мировых
// координатах, поэтому при переключении зума картинка не меняется.
public partial class PortLayer : Node2D
{
	// Сколько чанков в кадре ещё рисуем с закрытыми портами (дальше — только открытые).
	[Export] public int MaxChunksForClosedPorts = 400;

	public static readonly Color OutputColor = new Color(1f, 0.6f, 0.15f);
	public static readonly Color InputColor = new Color(0.25f, 0.85f, 1f);
	public static readonly Color ClosedColor = new Color(0.6f, 0.6f, 0.65f);
	private static readonly Color HoleColor = new Color(0.04f, 0.04f, 0.07f);

	private NucleusLayer _nucleusLayer;
	private MoleculeLayer _moleculeLayer;
	private int _cellSize;
	private int _chunkSize;
	private int _chunkWorldSize;
	private bool _ready;

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		_moleculeLayer = GetNodeOrNull<MoleculeLayer>("../MoleculeLayer");
		if (_nucleusLayer == null || !_nucleusLayer.IsReady || _nucleusLayer.Ports == null)
		{
			GD.PrintErr("[PortLayer] NucleusLayer не найден или не инициализирован — порты не рисуются.");
			return;
		}
		_cellSize = _nucleusLayer.CellSize;
		_chunkSize = _nucleusLayer.ChunkSize;
		_chunkWorldSize = _cellSize * _chunkSize;
		SetProcessUnhandledInput(true);
		_ready = true;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_ready || ViewLayer.IsLayer2) return;
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;

		var p = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(p.X / _cellSize);
		int row = Mathf.FloorToInt(p.Y / _cellSize);
		var ports = _nucleusLayer.Ports;
		if (!ports.TryGetPortAtCell(row, col, out var key)) return;

		GetViewport().SetInputAsHandled();
		if (_moleculeLayer != null && _moleculeLayer.IsChunkTakenByLayer2(key.Cx, key.Cy))
		{
			GD.Print($"[PortLayer] чанк ({key.Cx},{key.Cy}) занят объектом слоя 2 (молекула или ЧД) — порт не открыть.");
			return;
		}

		int dropped = ports.Get(key).Atom.Count;
		var mode = ports.CycleMode(key);
		string drop = dropped > 0 ? $", сброшено частиц: {dropped}" : "";
		GD.Print($"[PortLayer] порт чанка ({key.Cx},{key.Cy}) сторона {(PortSide)key.Side}: {ModeName(mode)}{drop}.");
		QueueRedraw();
	}

	public static string ModeName(PortMode mode) => mode switch
	{
		PortMode.Output => "выход",
		PortMode.Input => "вход",
		_ => "закрыт",
	};

	public override void _Process(double delta)
	{
		if (!_ready) return;
		Visible = !ViewLayer.IsLayer2;
		if (Visible) QueueRedraw();
	}

	public override void _Draw()
	{
		if (!_ready || ViewLayer.IsLayer2) return;
		var cam = GetViewport().GetCamera2D();
		if (cam == null) return;

		var size = GetViewportRect().Size / cam.Zoom;
		var topLeft = cam.GetScreenCenterPosition() - size / 2f;
		int minCx = Mathf.FloorToInt(topLeft.X / _chunkWorldSize);
		int maxCx = Mathf.FloorToInt((topLeft.X + size.X) / _chunkWorldSize);
		int minCy = Mathf.FloorToInt(topLeft.Y / _chunkWorldSize);
		int maxCy = Mathf.FloorToInt((topLeft.Y + size.Y) / _chunkWorldSize);

		var ports = _nucleusLayer.Ports;
		var holes = _nucleusLayer.BlackHoles;
		var colors = _nucleusLayer.TierPreviewColors;
		long chunkCount = (long)(maxCx - minCx + 1) * (maxCy - minCy + 1);

		if (chunkCount <= MaxChunksForClosedPorts)
		{
			for (int cy = minCy; cy <= maxCy; cy++)
				for (int cx = minCx; cx <= maxCx; cx++)
				{
					if (holes.Contains(cx, cy)) continue; // дырки ЧД рисует BlackHoleLayer
					for (int side = 0; side < PortSet.SideCount; side++)
					{
						var key = new PortKey(cx, cy, side);
						DrawPortHalf(this, PortCenter(key, _chunkWorldSize), _cellSize, side, ports.Get(key), colors, 1f);
					}
				}
		}
		else
		{
			foreach (var pair in ports.Enumerate())
				DrawPortHalf(this, PortCenter(pair.Key, _chunkWorldSize), _cellSize, pair.Key.Side, pair.Value, colors, 1f);
		}
	}

	// Середина стороны чанка — центр дырки порта 2×2 (в мировых координатах).
	public static Vector2 PortCenter(PortKey key, float chunkWorldSize)
	{
		float x0 = key.Cx * chunkWorldSize, y0 = key.Cy * chunkWorldSize, h = chunkWorldSize / 2f;
		return (PortSide)key.Side switch
		{
			PortSide.N => new Vector2(x0 + h, y0),
			PortSide.S => new Vector2(x0 + h, y0 + chunkWorldSize),
			PortSide.W => new Vector2(x0, y0 + h),
			_ => new Vector2(x0 + chunkWorldSize, y0 + h),
		};
	}

	// Угол направления «внутрь чанка» от центра порта (ось Y вниз).
	private static float InwardAngle(int side) => (PortSide)side switch
	{
		PortSide.N => Mathf.Pi / 2f,
		PortSide.E => Mathf.Pi,
		PortSide.S => -Mathf.Pi / 2f,
		_ => 0f,
	};

	// Половина дырки порта / дырка блока: полукруг внутрь чанка, обод по режиму,
	// 8 точек частиц по полукругу, полный атом — белое кольцо. alpha — общая
	// прозрачность (закрытые стороны дополнительно приглушены). showSlots =
	// false — только дырка и обод (дырки ЧД: буфера нет, точки не нужны).
	public static void DrawPortHalf(CanvasItem ci, Vector2 center, float radius, int side, PortState state, Color[] tierColors, float alpha, bool showSlots = true)
	{
		const int ArcSegments = 16;
		float a0 = InwardAngle(side) - Mathf.Pi / 2f;
		bool closed = state.Mode == PortMode.Closed;
		float k = closed ? 0.45f : 1f;

		var poly = new Vector2[ArcSegments + 2];
		poly[0] = center;
		for (int i = 0; i <= ArcSegments; i++)
		{
			float a = a0 + Mathf.Pi * i / ArcSegments;
			poly[i + 1] = center + radius * new Vector2(Mathf.Cos(a), Mathf.Sin(a));
		}
		ci.DrawColoredPolygon(poly, new Color(HoleColor, 0.85f * alpha * k));

		var rim = state.Mode switch { PortMode.Output => OutputColor, PortMode.Input => InputColor, _ => ClosedColor };
		ci.DrawArc(center, radius, a0, a0 + Mathf.Pi, ArcSegments, new Color(rim, alpha * k), radius * 0.12f);

		if (closed || !showSlots) return;

		float dotR = radius * 0.1f;
		for (int i = 0; i < Atom.Size; i++)
		{
			float a = a0 + Mathf.Pi * (i + 0.5f) / Atom.Size;
			var pos = center + radius * 0.7f * new Vector2(Mathf.Cos(a), Mathf.Sin(a));
			if (i < state.Atom.Count)
			{
				int color = state.Atom.ColorAt(i);
				var c = (tierColors != null && color >= 0 && color < tierColors.Length) ? tierColors[color] : Colors.White;
				ci.DrawCircle(pos, dotR, new Color(c, alpha));
			}
			else
			{
				ci.DrawArc(pos, dotR, 0f, Mathf.Tau, 8, new Color(1f, 1f, 1f, 0.35f * alpha), dotR * 0.35f);
			}
		}

		if (state.Atom.IsFull)
			ci.DrawArc(center, radius * 0.38f, a0, a0 + Mathf.Pi, ArcSegments, new Color(1f, 1f, 1f, 0.9f * alpha), radius * 0.08f);
	}
}
