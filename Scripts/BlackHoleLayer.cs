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
//
// Эффект падения (GDD «Падение в ЧД») — только визуал: принятый атом летит по
// спирали в центр, уменьшаясь; у центра ускоряется, тускнеет, краснеет и
// вытягивается вдоль пути; попадание подсвечивает диск. Идёт по времени
// кадра, в симуляцию и сохранение не попадает. Производительность: узлов на
// атом нет — у каждой ЧД заранее выделенный массив из MaxFallingPerHole
// структур, всё рисуется одним _Draw этого узла. Сверх лимита атом
// засчитывается без анимации. ЧД вне экрана анимаций не заводит, не
// обновляет и не рисует (её текущие анимации сбрасываются).
public partial class BlackHoleLayer : Node2D
{
	public const string TexturePath = "res://Resources/Textures/black_hole_96px.png";

	[Export] public int MaxFallingPerHole = 32;
	[Export] public float FallSeconds = 1.6f;
	// Сколько оборотов делает атом по пути от края до центра.
	[Export] public float FallTurns = 1.25f;
	[Export] public float FlashDecayPerSecond = 2.5f;

	private static readonly Color HotColor = new Color(1f, 0.25f, 0.15f);
	private static readonly Color FlashColor = new Color(0.85f, 0.6f, 1f);

	private struct FallFx
	{
		public Vector2 Start; // откуда пришёл атом, относительно центра ЧД
		public float Age;     // секунд с начала падения
		public Atom Atom;
	}

	private sealed class HoleFx
	{
		public FallFx[] Items;
		public int Count;
		public float Flash; // 1 — только что попал атом, затухает до 0
	}

	// Только для ЧД, которые принимали атомы на экране; чистится при удалении ЧД.
	private readonly Dictionary<ChunkKey, HoleFx> _fx = new();
	private readonly List<ChunkKey> _fxToRemove = new();
	private int _fxHolesVersion = -1;
	private float _atomRadius;

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
		// Тот же размер, что у атома в гнезде молекулы (MoleculeLayer.DrawAtoms).
		_atomRadius = _nucleusLayer.HoleSpriteSize * _nucleusLayer.ChunkSize * 0.3f;
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
		if (!_ready || MaxFallingPerHole <= 0) return;
		if (!_viewRect.Intersects(HoleRect(cx, cy))) return;

		var key = new ChunkKey(cx, cy);
		if (!_fx.TryGetValue(key, out var fx))
		{
			fx = new HoleFx { Items = new FallFx[MaxFallingPerHole] };
			_fx[key] = fx;
		}
		if (fx.Count >= fx.Items.Length) return; // сверх лимита — без анимации
		fx.Items[fx.Count++] = new FallFx { Start = from - HoleCenter(cx, cy), Atom = atom };
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
		UpdateEffects((float)delta);
		if (_holes.Count > 0) QueueRedraw();
	}

	private void UpdateEffects(float dt)
	{
		if (_fxHolesVersion != _holes.Version)
		{
			_fxHolesVersion = _holes.Version;
			_fxToRemove.Clear();
			foreach (var key in _fx.Keys)
				if (!_holes.Contains(key.Cx, key.Cy)) _fxToRemove.Add(key);
			foreach (var key in _fxToRemove) _fx.Remove(key);
		}

		foreach (var pair in _fx)
		{
			var fx = pair.Value;
			if (fx.Count == 0 && fx.Flash <= 0f) continue;
			if (!_viewRect.Intersects(HoleRect(pair.Key.Cx, pair.Key.Cy)))
			{
				fx.Count = 0;
				fx.Flash = 0f;
				continue;
			}
			fx.Flash = Mathf.Max(0f, fx.Flash - dt * FlashDecayPerSecond);
			for (int i = 0; i < fx.Count;)
			{
				fx.Items[i].Age += dt;
				if (fx.Items[i].Age < FallSeconds) { i++; continue; }
				fx.Items[i] = fx.Items[--fx.Count]; // порядок отрисовки не важен
				fx.Flash = 1f;
			}
		}
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
			if (_fx.TryGetValue(hole, out var fx)) DrawEffects(HoleCenter(hole.Cx, hole.Cy), fx, colors);
		}
	}

	private void DrawEffects(Vector2 center, HoleFx fx, Color[] colors)
	{
		if (fx.Flash > 0f)
		{
			float rim = _chunkWorldSize * 0.47f;
			DrawArc(center, rim, 0f, Mathf.Tau, 48, new Color(FlashColor, 0.8f * fx.Flash), _cellSize * 0.6f);
			DrawCircle(center, _chunkWorldSize * 0.08f * (1f + fx.Flash), new Color(HotColor, 0.35f * fx.Flash));
		}

		float spin = FallTurns * Mathf.Tau;
		for (int i = 0; i < fx.Count; i++)
		{
			ref var f = ref fx.Items[i];
			float t = Mathf.Clamp(f.Age / FallSeconds, 0f, 1f);
			float e = t * t; // ускорение к центру
			float r0 = f.Start.Length();
			float a = f.Start.Angle() + spin * e;
			var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
			float r = r0 * (1f - e);
			var pos = center + r * dir;

			// Направление движения: радиальная скорость -r0·2t, угловая spin·2t.
			var vel = dir * (-r0) + new Vector2(-dir.Y, dir.X) * (r * spin);
			float heading = vel.LengthSquared() > 0f ? vel.Angle() : a;

			float stretch = 1f + 0.8f * e;
			DrawSetTransform(pos, heading, new Vector2(stretch, 1f / Mathf.Sqrt(stretch)));
			DrawFallingAtom(_atomRadius * Mathf.Lerp(1f, 0.12f, e), f.Atom, colors, e, 1f - 0.7f * e);
		}
		if (fx.Count > 0) DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
	}

	// Как MoleculeLayer.DrawAtom, но с покраснением (heat 0..1) и прозрачностью.
	private void DrawFallingAtom(float radius, Atom atom, Color[] tierColors, float heat, float alpha)
	{
		DrawCircle(Vector2.Zero, radius, new Color(0.08f, 0.08f, 0.1f, 0.9f * alpha));
		DrawArc(Vector2.Zero, radius, 0f, Mathf.Tau, 16, new Color(Colors.White.Lerp(HotColor, heat), alpha), radius * 0.12f);
		for (int i = 0; i < atom.Count; i++)
		{
			float a = i * Mathf.Tau / Atom.Size - Mathf.Pi / 2f;
			int color = atom.ColorAt(i);
			var c = (tierColors != null && color >= 0 && color < tierColors.Length) ? tierColors[color] : Colors.White;
			DrawCircle(radius * 0.6f * new Vector2(Mathf.Cos(a), Mathf.Sin(a)), radius * 0.2f, new Color(c.Lerp(HotColor, heat), alpha));
		}
	}
}
