using Godot;
using System.Collections.Generic;

// Чёрная дыра (ЧД, T003) — встроенный объект слоя 2 на всю клетку (= чанк
// слоя 1). Данные и правила — BlackHoleSet (NucleusLayer.BlackHoles), этот
// узел только:
//   - на тех же часах после слоя 2 забирает готовые атомы из выходных портов
//     соседних чанков, стоящих против сторон ЧД (SimTick);
//   - рисует ЧД на обоих слоях в мировых координатах (одна камера): спрайт на
//     весь чанк без вращения и 4 неподвижные дырки-входа (PortLayer.DrawPortHalf).
// Установка/удаление — инструмент слоя 2 в MoleculeLayer (там же правило
// занятости клетки). С ядрами слоя 1 ЧД напрямую не взаимодействует.
public partial class BlackHoleLayer : Node2D
{
	public const string TexturePath = "res://Resources/Textures/black_hole_96px.png";

	private NucleusLayer _nucleusLayer;
	private BlackHoleSet _holes;
	private PortSet _ports;
	private Texture2D _texture;
	private float _cellSize;
	private float _chunkWorldSize;
	private bool _ready;

	private readonly List<PortAbsorb> _portEvents = new();
	private Rect2 _viewRect;

	public Texture2D Texture => _texture;

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		if (_nucleusLayer == null || !_nucleusLayer.IsReady || _nucleusLayer.BlackHoles == null)
		{
			GD.PrintErr("[BlackHoleLayer] NucleusLayer не найден или не инициализирован — ЧД не работают.");
			return;
		}
		_texture = GD.Load<Texture2D>(TexturePath);
		if (_texture == null) GD.PrintErr($"[BlackHoleLayer] не загрузился спрайт {TexturePath}.");

		_holes = _nucleusLayer.BlackHoles;
		_ports = _nucleusLayer.Ports;
		_cellSize = _nucleusLayer.CellSize;
		_chunkWorldSize = _cellSize * _nucleusLayer.ChunkSize;
		TextureFilter = TextureFilterEnum.Nearest;
		_ready = true;
	}

	// Вызывается NucleusLayer после тика слоя 1 и слоя 2 (общие часы и пауза).
	public void SimTick(long tick)
	{
		if (!_ready || _holes.Count == 0) return;
		_portEvents.Clear();
		_holes.AbsorbFromPorts(_ports, _portEvents);
		foreach (var e in _portEvents)
			OnAtomAbsorbed(e.Hole.Cx, e.Hole.Cy, e.Atom, PortLayer.PortCenter(e.From, _chunkWorldSize));
	}

	// Атом засчитан ЧД (cx, cy); from — мировая точка, откуда он пришёл
	// (центр порта или гнездо молекулы). Только визуал, на симуляцию не влияет.
	public void OnAtomAbsorbed(int cx, int cy, Atom atom, Vector2 from)
	{
	}

	public Vector2 HoleCenter(int cx, int cy) =>
		new Vector2((cx + 0.5f) * _chunkWorldSize, (cy + 0.5f) * _chunkWorldSize);

	private Rect2 HoleRect(int cx, int cy) =>
		new Rect2(cx * _chunkWorldSize, cy * _chunkWorldSize, _chunkWorldSize, _chunkWorldSize);

	private void UpdateViewRect()
	{
		var cam = GetViewport().GetCamera2D();
		if (cam == null) { _viewRect = new Rect2(); return; }
		var size = GetViewportRect().Size / cam.Zoom;
		_viewRect = new Rect2(cam.GetScreenCenterPosition() - size / 2f, size);
	}

	public override void _Process(double delta)
	{
		if (!_ready) return;
		UpdateViewRect();
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (!_ready || _holes.Count == 0) return;
		var colors = _nucleusLayer.TierPreviewColors;
		var inputPort = new PortState { Mode = PortMode.Input };
		foreach (var hole in _holes.Enumerate())
		{
			var rect = HoleRect(hole.Cx, hole.Cy);
			if (!_viewRect.Intersects(rect)) continue;
			if (_texture != null) DrawTextureRect(_texture, rect, false);
			for (int side = 0; side < PortSet.SideCount; side++)
			{
				var key = new PortKey(hole.Cx, hole.Cy, side);
				PortLayer.DrawPortHalf(this, PortLayer.PortCenter(key, _chunkWorldSize), _cellSize, side, inputPort, colors, 1f);
			}
		}
	}
}
