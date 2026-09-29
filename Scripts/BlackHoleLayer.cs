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
// вытягивается вдоль пути; попадание подсвечивает диск (если ShowHitFlash). Идёт по времени
// кадра, в симуляцию и сохранение не попадает. Производительность: узлов на
// атом нет — у каждой ЧД заранее выделенный массив из MaxFallingPerHole
// структур; все летящие атомы всех ЧД — один MultiMesh (один draw call),
// буфер пишется целиком одним вызовом за кадр. Атом — кружки одной мягкой
// текстуры: обод, тёмное тело, 8 точек состава; мельче FallLodPixels на
// экране — один кружок среднего цвета. Сверх лимита атом засчитывается без
// анимации. ЧД вне экрана анимаций не заводит, не обновляет и не рисует
// (её текущие анимации сбрасываются).
public partial class BlackHoleLayer : Node2D
{
	public const string TexturePath = "res://Resources/Textures/black_hole_96px.png";

	[Export] public int MaxFallingPerHole = 32;
	[Export] public float FallSeconds = 1.6f;
	// Сколько оборотов делает атом по пути от края до центра.
	[Export] public float FallTurns = 1.25f;
	// Вспышка диска при попадании атома (кольцо и красный круг); выключена по умолчанию.
	[Export] public bool ShowHitFlash = false;
	[Export] public float FlashDecayPerSecond = 2.5f;
	// Радиус атома на экране (px), ниже которого атом рисуется одним кружком.
	[Export] public float FallLodPixels = 5f;

	private static readonly Color HotColor = new Color(1f, 0.25f, 0.15f);
	private static readonly Color FlashColor = new Color(0.85f, 0.6f, 1f);
	private static readonly Color AtomBodyColor = new Color(0.08f, 0.08f, 0.1f, 0.9f);

	// MultiMesh 2D с цветом: 8 float трансформа (2 строки по 4) + 4 float цвета.
	private const int Stride = 12;
	private const int InstancesPerAtom = 2 + Atom.Size;

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

	private MultiMesh _fxMesh;
	private float[] _fxBuffer = System.Array.Empty<float>();
	private int _fxCapacity;
	private bool _hadHoles;

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

		_fxMesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseColors = true,
			Mesh = new QuadMesh { Size = Vector2.One },
			// Canvas item кэширует прямоугольник отсечения при первом _draw
			// (MultiMesh ещё пуст) и не пересчитывает его при MultimeshSetBuffer —
			// без явных границ эффект отсекался целиком при сильном приближении.
			// Границы на весь мир; по экрану фильтрует FillEffectMesh.
			CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1f), new Vector3(2e7f, 2e7f, 2f)),
		};
		AddChild(new MultiMeshInstance2D
		{
			Name = "FallingAtoms",
			Multimesh = _fxMesh,
			Texture = BuildDiscTexture(64),
			TextureFilter = TextureFilterEnum.Linear,
		});
		_ready = true;
	}

	// Белый круг с мягким краем — общая текстура всех кружков эффекта.
	private static Texture2D BuildDiscTexture(int size)
	{
		var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		float c = size / 2f;
		for (int y = 0; y < size; y++)
			for (int x = 0; x < size; x++)
			{
				float d = new Vector2(x + 0.5f - c, y + 0.5f - c).Length();
				img.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp(c - d, 0f, 1.5f) / 1.5f));
			}
		return ImageTexture.CreateFromImage(img);
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
		FillEffectMesh();
		// Последнюю удалённую ЧД тоже нужно стереть — отсюда _hadHoles.
		if (_holes.Count > 0 || _hadHoles) QueueRedraw();
		_hadHoles = _holes.Count > 0;
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
			if (ShowHitFlash) fx.Flash = Mathf.Max(0f, fx.Flash - dt * FlashDecayPerSecond);
			else fx.Flash = 0f;
			for (int i = 0; i < fx.Count;)
			{
				fx.Items[i].Age += dt;
				if (fx.Items[i].Age < FallSeconds) { i++; continue; }
				fx.Items[i] = fx.Items[--fx.Count]; // порядок отрисовки не важен
				if (ShowHitFlash) fx.Flash = 1f;
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
				PortLayer.DrawPortHalf(this, PortLayer.PortCenter(key, _chunkWorldSize), _cellSize, side, inputPort, colors, 1f, showSlots: false);
				// Подробный вид порта (T004) — только при сетке: контур клеток порта.
				if (GridDraw.Shown) PortLayer.DrawPortCells(this, _ports, key, _cellSize, new Color(PortLayer.InputColor, 0.9f), filled: false);
			}
			if (ShowHitFlash && _fx.TryGetValue(hole, out var fx) && fx.Flash > 0f) DrawFlash(HoleCenter(hole.Cx, hole.Cy), fx.Flash);
		}
	}

	// Вспышка диска при попадании атома.
	private void DrawFlash(Vector2 center, float flash)
	{
		DrawArc(center, _chunkWorldSize * 0.47f, 0f, Mathf.Tau, 48, new Color(FlashColor, 0.8f * flash), _cellSize * 0.6f);
		DrawCircle(center, _chunkWorldSize * 0.08f * (1f + flash), new Color(HotColor, 0.35f * flash));
	}

	// Все летящие атомы видимых ЧД → буфер MultiMesh (порядок экземпляров =
	// порядок отрисовки: обод, тело, точки — поверх).
	private void FillEffectMesh()
	{
		int atoms = 0;
		foreach (var fx in _fx.Values) atoms += fx.Count;
		int needed = atoms * InstancesPerAtom;
		if (needed > _fxCapacity)
		{
			_fxCapacity = Mathf.Max(needed, _fxCapacity * 2);
			_fxBuffer = new float[_fxCapacity * Stride];
			_fxMesh.InstanceCount = _fxCapacity; // растёт только до пика, не каждый кадр
		}
		if (atoms == 0)
		{
			_fxMesh.VisibleInstanceCount = 0;
			return;
		}

		var colors = _nucleusLayer.TierPreviewColors;
		var cam = GetViewport().GetCamera2D();
		float zoom = cam != null ? cam.Zoom.X : 1f;
		float spin = FallTurns * Mathf.Tau;
		int n = 0;
		foreach (var pair in _fx)
		{
			var fx = pair.Value;
			if (fx.Count == 0) continue;
			var center = HoleCenter(pair.Key.Cx, pair.Key.Cy);
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
				var ax = vel.LengthSquared() > 0f ? vel.Normalized() : dir;
				var ay = new Vector2(-ax.Y, ax.X);
				float stretch = 1f + 0.8f * e;
				float sx = stretch, sy = 1f / Mathf.Sqrt(stretch);

				float radius = _atomRadius * Mathf.Lerp(1f, 0.12f, e);
				float alpha = 1f - 0.7f * e;

				if (radius * zoom < FallLodPixels)
				{
					Put(ref n, pos, ax, ay, 2f * radius * sx, 2f * radius * sy, new Color(MeanColor(f.Atom, colors).Lerp(HotColor, e), alpha));
					continue;
				}

				Put(ref n, pos, ax, ay, 2f * radius * sx, 2f * radius * sy, new Color(Colors.White.Lerp(HotColor, e), alpha));
				Put(ref n, pos, ax, ay, 1.76f * radius * sx, 1.76f * radius * sy, new Color(AtomBodyColor, AtomBodyColor.A * alpha));
				for (int k = 0; k < f.Atom.Count; k++)
				{
					float da = k * Mathf.Tau / Atom.Size - Mathf.Pi / 2f;
					var local = radius * 0.6f * new Vector2(Mathf.Cos(da), Mathf.Sin(da));
					var p = pos + ax * (local.X * sx) + ay * (local.Y * sy);
					var c = TierColor(f.Atom.ColorAt(k), colors);
					Put(ref n, p, ax, ay, 0.4f * radius * sx, 0.4f * radius * sy, new Color(c.Lerp(HotColor, e), alpha));
				}
			}
		}

		RenderingServer.MultimeshSetBuffer(_fxMesh.GetRid(), _fxBuffer);
		_fxMesh.VisibleInstanceCount = n;
	}

	// Один кружок: центр pos, оси ax/ay (единичные), размеры w × h, цвет c.
	private void Put(ref int n, Vector2 pos, Vector2 ax, Vector2 ay, float w, float h, Color c)
	{
		int o = n * Stride;
		var b = _fxBuffer;
		b[o] = ax.X * w; b[o + 1] = ay.X * h; b[o + 2] = 0f; b[o + 3] = pos.X;
		b[o + 4] = ax.Y * w; b[o + 5] = ay.Y * h; b[o + 6] = 0f; b[o + 7] = pos.Y;
		b[o + 8] = c.R; b[o + 9] = c.G; b[o + 10] = c.B; b[o + 11] = c.A;
		n++;
	}

	private static Color TierColor(int color, Color[] tierColors) =>
		(tierColors != null && color >= 0 && color < tierColors.Length) ? tierColors[color] : Colors.White;

	private static Color MeanColor(Atom atom, Color[] tierColors)
	{
		if (atom.Count == 0) return Colors.White;
		float r = 0, g = 0, b = 0;
		for (int i = 0; i < atom.Count; i++)
		{
			var c = TierColor(atom.ColorAt(i), tierColors);
			r += c.R; g += c.G; b += c.B;
		}
		return new Color(r / atom.Count, g / atom.Count, b / atom.Count);
	}
}
