using System.Collections.Generic;
using Godot;

// Порты чанков на слое 1 (T002, T004): ввод (переключение режима) и отрисовка.
// Данные и правила — PortSet (NucleusLayer.Ports), обмен частицами с ядрами —
// NucleusLayer.SimTick, шаг 2в. Этот узел ничего не симулирует.
//
// Жест: НАЖАТИЕ ЛКМ на клетку порта (только само нажатие, не удержание) —
// закрыт → выход → вход → закрыт. Ядро на клетку порта поставить нельзя, так
// что с установкой ядер жест не спорит; протяжка с зажатой ЛКМ порты не
// переключает. Узел стоит в сцене после NucleusLayer, поэтому получает
// _UnhandledInput раньше него и «съедает» клик по порту.
//
// Видимость (T004, GDD «Видимость портов»): порты видны только у блоков
// (NucleusLayer.IsBlock). У остальных чанков порты скрыты, но клетки
// зарезервированы: нажатие на них или попытка поставить туда ядро/источник —
// короткая красная подсветка клеток порта (FlashPort), режим не меняется.
//
// Вид: обычный — целая серая дырка того же спрайта, размера и прозрачности,
// что дырка молекулы (DrawPortHole), в ней атом, если есть. При включённой
// сетке (GridDraw.Shown) поверх — подробный вид (DrawPortDetail): половина
// дырки радиусом в клетку с точками частиц, обод по режиму и контур двух
// клеток порта. Те же функции рисуют порты блоков на слое 2 (MoleculeLayer) в
// тех же мировых координатах, поэтому при переключении зума картинка не меняется.
public partial class PortLayer : Node2D
{
	// Длительность подсветки «сюда нельзя» у клеток порта, секунд.
	[Export] public float FlashSeconds = 0.4f;

	public static readonly Color OutputColor = new Color(1f, 0.6f, 0.15f);
	public static readonly Color InputColor = new Color(0.25f, 0.85f, 1f);
	public static readonly Color ClosedColor = new Color(0.6f, 0.6f, 0.65f);
	private static readonly Color HoleColor = new Color(0.04f, 0.04f, 0.07f);
	private static readonly Color FlashColor = new Color(1f, 0.2f, 0.2f);

	private NucleusLayer _nucleusLayer;
	private MoleculeLayer _moleculeLayer;
	private int _cellSize;
	private int _chunkSize;
	private int _chunkWorldSize;
	private bool _ready;
	// Подсвеченные порты: оставшееся время подсветки, секунд.
	private readonly Dictionary<PortKey, float> _flash = new();
	private readonly List<PortKey> _flashKeys = new();

	public override void _Ready()
	{
		// Слой 2 выключен (ViewLayer.Layer2Enabled, T005) — портов нет.
		if (!ViewLayer.Layer2Enabled)
		{
			Visible = false;
			SetProcess(false);
			SetProcessUnhandledInput(false);
			return;
		}

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
		if (!_nucleusLayer.IsBlock(key.Cx, key.Cy))
		{
			FlashPort(key);
			GD.Print($"[PortLayer] чанк ({key.Cx},{key.Cy}) не блок — порт скрыт, клетки порта зарезервированы.");
			return;
		}

		int dropped = ports.Get(key).Atom.Count;
		var mode = ports.CycleMode(key);
		string drop = dropped > 0 ? $", сброшено частиц: {dropped}" : "";
		GD.Print($"[PortLayer] порт чанка ({key.Cx},{key.Cy}) сторона {(PortSide)key.Side}: {ModeName(mode)}{drop}.");
		QueueRedraw();
	}

	// Короткая подсветка клеток порта: сюда ничего не ставится.
	public void FlashPort(PortKey key)
	{
		if (_ready) _flash[key] = FlashSeconds;
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
		if (_flash.Count > 0)
		{
			_flashKeys.Clear();
			_flashKeys.AddRange(_flash.Keys);
			foreach (var key in _flashKeys)
			{
				float left = _flash[key] - (float)delta;
				if (left <= 0f) _flash.Remove(key);
				else _flash[key] = left;
			}
		}
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
		// Дырка порта выходит за чанк — запас в чанк вокруг кадра.
		var view = new Rect2(topLeft, size).Grow(_chunkWorldSize);

		var ports = _nucleusLayer.Ports;
		var colors = _nucleusLayer.TierPreviewColors;
		var holeTex = _nucleusLayer.HoleTexture;
		float holeSize = _nucleusLayer.HoleSpriteSize * _chunkSize;
		float holeAlpha = _nucleusLayer.HoleOpacity;
		bool detail = GridDraw.Shown;

		foreach (var (cx, cy) in _nucleusLayer.EnumerateBlocks())
		{
			if (!view.HasPoint(new Vector2((cx + 0.5f) * _chunkWorldSize, (cy + 0.5f) * _chunkWorldSize))) continue;
			for (int side = 0; side < PortSet.SideCount; side++)
			{
				var key = new PortKey(cx, cy, side);
				var state = ports.Get(key);
				DrawPortHole(this, PortCenter(key, _chunkWorldSize), holeTex, holeSize, holeAlpha, state.Atom, colors);
				if (detail) DrawPortDetail(this, ports, key, state, _cellSize, _chunkWorldSize, colors);
			}
		}

		foreach (var pair in _flash)
			DrawPortCells(this, ports, pair.Key, _cellSize, new Color(FlashColor, Mathf.Clamp(pair.Value / FlashSeconds, 0f, 1f)), filled: true);
	}

	// Обычный вид порта (T004): целая дырка молекулы — тот же спрайт, размер
	// (HoleSpriteSize × ChunkSize) и прозрачность, центр — середина стороны
	// чанка; атом в порту — значком, как в гнезде молекулы.
	public static void DrawPortHole(CanvasItem ci, Vector2 center, Texture2D holeTex, float holeSize, float alpha, Atom atom, Color[] tierColors)
	{
		if (holeTex != null)
		{
			var half = new Vector2(holeSize, holeSize) / 2f;
			ci.DrawTextureRect(holeTex, new Rect2(center - half, half * 2f), false, new Color(1f, 1f, 1f, alpha));
		}
		if (!atom.IsEmpty)
			MoleculeLayer.DrawAtom(ci, center, holeSize * 0.3f, atom, tierColors);
	}

	// Подробный вид порта (только при сетке): контур двух клеток порта и
	// половина дырки с точками частиц и ободом по режиму (DrawPortHalf).
	public static void DrawPortDetail(CanvasItem ci, PortSet ports, PortKey key, PortState state, float cellSize, float chunkWorldSize, Color[] tierColors)
	{
		var rim = state.Mode switch { PortMode.Output => OutputColor, PortMode.Input => InputColor, _ => ClosedColor };
		DrawPortCells(ci, ports, key, cellSize, new Color(rim, state.Mode == PortMode.Closed ? 0.45f : 0.9f), filled: false);
		DrawPortHalf(ci, PortCenter(key, chunkWorldSize), cellSize, key.Side, state, tierColors, 1f);
	}

	// Две клетки порта: контур или заливка с контуром (подсветка «нельзя»).
	public static void DrawPortCells(CanvasItem ci, PortSet ports, PortKey key, float cellSize, Color color, bool filled)
	{
		var (r0, c0) = ports.Cell(key, 0);
		var (r1, c1) = ports.Cell(key, 1);
		var rect = new Rect2(Mathf.Min(c0, c1) * cellSize, Mathf.Min(r0, r1) * cellSize,
			(Mathf.Abs(c1 - c0) + 1) * cellSize, (Mathf.Abs(r1 - r0) + 1) * cellSize);
		if (filled)
		{
			ci.DrawRect(rect, new Color(color, color.A * 0.35f));
			ci.DrawRect(rect, color, false, cellSize * 0.06f);
		}
		else ci.DrawRect(rect, color, false, cellSize * 0.04f);
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
