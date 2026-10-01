using Godot;
using System.Collections.Generic;

// Чёрная дыра (ЧД, T005) — объект слоя 1: квадрат BlackHoleSize×BlackHoleSize
// клеток. Данные и геометрия горизонта — BlackHoleSet (NucleusLayer.BlackHoles),
// захват атомов и частиц на горизонте — NucleusLayer (FinishArrivedMoves,
// SimTick). Этот узел только:
//   - инструмент установки (кнопка «ЧД» на панели слоя 1, SelectTool): ЛКМ —
//     поставить (верхняя левая клетка под курсором), превью спрайта или
//     красный квадрат, если нельзя; удаление — ПКМ через NucleusLayer
//     (RemoveAllAtMouse → RemoveAt);
//   - отрисовка: спрайт на Size×Size клеток, без дырок портов;
//   - эффект падения.
//
// Эффект падения (GDD «Падение в ЧД») — только визуал: захваченный атом или
// высосанная частица летит по спирали в центр, уменьшаясь; у центра
// ускоряется, тускнеет, краснеет и вытягивается вдоль пути; попадание
// подсвечивает диск (если ShowHitFlash). Идёт по времени кадра, в симуляцию и
// сохранение не попадает. Производительность: узлов на атом/частицу нет — у
// каждой ЧД заранее выделенный массив из MaxFallingPerHole структур (атомы и
// частицы в одном лимите); всё летящее всех ЧД — один MultiMesh (один draw
// call), буфер пишется целиком одним вызовом за кадр. Атом — кружки одной
// мягкой текстуры: обод цвета тира, тёмное тело, точки частиц; мельче
// FallLodPixels на экране — один кружок. Частица — одна точка своего цвета.
// Сверх лимита засчитывается без анимации. ЧД вне экрана анимаций не
// заводит, не обновляет и не рисует (её текущие анимации сбрасываются).
public partial class BlackHoleLayer : Node2D
{
	public const string TexturePath = "res://Resources/Textures/black_hole_96px.png";

	// Сторона новой ЧД в клетках слоя 1 (T006d: 4×4).
	// У поставленной ЧД размер хранится в ней самой (и в сохранении).
	[Export] public int BlackHoleSize = 4;

	[Export] public int MaxFallingPerHole = 32;
	[Export] public float FallSeconds = 1.6f;
	// Сколько оборотов делает атом по пути от края до центра.
	[Export] public float FallTurns = 1.25f;
	// Вспышка диска при попадании (кольцо и красный круг); выключена по умолчанию.
	[Export] public bool ShowHitFlash = false;
	[Export] public float FlashDecayPerSecond = 2.5f;
	// Радиус атома на экране (px), ниже которого атом рисуется одним кружком.
	[Export] public float FallLodPixels = 5f;

	private static readonly Color HotColor = new Color(1f, 0.25f, 0.15f);
	private static readonly Color FlashColor = new Color(0.85f, 0.6f, 1f);
	private static readonly Color AtomBodyColor = new Color(0.08f, 0.08f, 0.1f, 0.9f);
	private static readonly Color BlockedColor = new Color(1f, 0.2f, 0.2f, 0.35f);

	// MultiMesh 2D с цветом: 8 float трансформа (2 строки по 4) + 4 float цвета.
	private const int Stride = 12;

	private struct FallFx
	{
		public Vector2 Start; // откуда пришёл, относительно центра ЧД
		public float Age;     // секунд с начала падения
		public int Tier;      // тир атома; -1 — одиночная частица
		public int Color;     // цвет частицы или тир предмета (только для Tier == -1)
		public bool Item;     // атом-предмет из дырки (T007): шар размером с частицу
		public Atom Particles; // частицы в дырках атома (цвета)

		public readonly int Instances => Tier < 0 ? (Item ? 2 : 1) : 2 + Particles.Count;
	}

	private sealed class HoleFx
	{
		public FallFx[] Items;
		public int Count;
		public float Flash; // 1 — только что попал атом, затухает до 0
	}

	// Только для ЧД, которые принимали атомы на экране; чистится при удалении ЧД.
	private readonly Dictionary<BlackHole, HoleFx> _fx = new();
	private readonly List<BlackHole> _fxToRemove = new();
	private int _fxHolesVersion = -1;
	private float _atomRadius;
	private float _particleRadius;

	private MultiMesh _fxMesh;
	private float[] _fxBuffer = System.Array.Empty<float>();
	private int _fxCapacity;
	private bool _hadHoles;

	private NucleusLayer _nucleusLayer;
	private EnergyLayer _energyLayer;
	private readonly List<EnergyClusterLayer> _clusterLayers = new();
	private BlackHoleSet _holes;
	private Texture2D _texture;
	private float _cellSize;
	private bool _ready;

	private bool _toolSelected;
	private bool _hadPreview;
	private Rect2 _viewRect;

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		if (_nucleusLayer == null || !_nucleusLayer.IsReady || _nucleusLayer.BlackHoles == null)
		{
			GD.PrintErr("[BlackHoleLayer] NucleusLayer не найден или не инициализирован — ЧД не работают.");
			return;
		}
		_energyLayer = GetNodeOrNull<EnergyLayer>("../TileMapLayer");
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer layer)
				_clusterLayers.Add(layer);

		_texture = GD.Load<Texture2D>(TexturePath);
		if (_texture == null) GD.PrintErr($"[BlackHoleLayer] не загрузился спрайт {TexturePath}.");

		_holes = _nucleusLayer.BlackHoles;
		_cellSize = _nucleusLayer.CellSize;
		_atomRadius = _cellSize * 0.4f;
		_particleRadius = _cellSize * 0.1f;
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
		SetProcessUnhandledInput(true);
		_ready = true;
	}

	// Белый круг с мягким краем — общая текстура всех кружков эффекта
	// (и эффекта поглощения звездой, StarLayer).
	internal static Texture2D BuildDiscTexture(int size)
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

	// --- инструмент (панель слоя 1) ---

	// Взаимоисключающий с остальными инструментами: сначала сбрасываем их
	// (NucleusLayer.ClearSelection заодно сбрасывает и этот), потом включаем свой.
	public void SelectTool()
	{
		if (!_ready) return;
		_nucleusLayer.ClearSelection();
		_energyLayer?.ClearSelection();
		foreach (var layer in _clusterLayers) layer.ClearSelection();
		_toolSelected = true;
		GD.Print($"[BlackHoleLayer] выбрана чёрная дыра {BlackHoleSize}×{BlackHoleSize}: ЛКМ — поставить (верхняя левая клетка), ПКМ — удалить.");
	}

	public void ClearTool() => _toolSelected = false;
	public bool HasTool => _toolSelected;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_ready || !_toolSelected || ViewLayer.IsLayer2) return;
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;
		var (row, col) = CellUnderMouse();
		TryPlace(row, col, BlackHoleSize, log: true);
		GetViewport().SetInputAsHandled();
	}

	private (int row, int col) CellUnderMouse()
	{
		var p = GetGlobalMousePosition();
		return (Mathf.FloorToInt(p.Y / _cellSize), Mathf.FloorToInt(p.X / _cellSize));
	}

	// Причина, по которой ЧД нельзя поставить, или null.
	private string PlaceBlockReason(int row, int col, int size)
	{
		if (size <= 0) return $"неверный размер {size}";
		if (_holes.Overlaps(row, col, size)) return "пересекается с другой ЧД";
		for (int r = row; r < row + size; r++)
			for (int c = col; c < col + size; c++)
				if (!_nucleusLayer.CanPlaceBlackHoleCell(r, c)) return $"клетка ({r},{c}) занята";
		return null;
	}

	// Установка ЧД (инструмент и загрузка сохранения).
	public bool TryPlace(int row, int col, int size, bool log)
	{
		if (!_ready) return false;
		string reason = PlaceBlockReason(row, col, size);
		if (reason != null)
		{
			if (log) GD.Print($"[BlackHoleLayer] ЧД в клетку ({row},{col}): {reason} — пропуск.");
			return false;
		}
		_holes.Add(row, col, size);
		if (log) GD.Print($"[BlackHoleLayer] установлена чёрная дыра {size}×{size} в клетке ({row},{col}).");
		return true;
	}

	// ПКМ по любой клетке ЧД (вызывает NucleusLayer.RemoveAllAtMouse).
	public void RemoveAt(int row, int col)
	{
		if (!_ready || !_holes.TryGetAt(row, col, out var hole)) return;
		_holes.Remove(hole);
		GD.Print($"[BlackHoleLayer] удалена чёрная дыра из клетки ({hole.Row},{hole.Col}).");
	}

	// --- эффект падения (только визуал, на симуляцию не влияет) ---

	// Атом захвачен целиком: from — мировая точка, откуда он упал; particles —
	// цвета частиц, что были в его дырках (не больше Atom.Size).
	public void OnAtomCaptured(BlackHole hole, Vector2 from, int tier, Atom particles) =>
		AddFx(hole, new FallFx { Start = from - HoleCenter(hole), Tier = System.Math.Max(0, tier), Particles = particles });

	// Частица высосана из атома на горизонте.
	public void OnParticleAbsorbed(BlackHole hole, Vector2 from, int color) =>
		AddFx(hole, new FallFx { Start = from - HoleCenter(hole), Tier = -1, Color = color });

	// Атом-предмет тира tier высосан из дырки атома на горизонте (T007).
	public void OnItemAbsorbed(BlackHole hole, Vector2 from, int tier) =>
		AddFx(hole, new FallFx { Start = from - HoleCenter(hole), Tier = -1, Color = tier, Item = true });

	private void AddFx(BlackHole hole, FallFx item)
	{
		if (!_ready || MaxFallingPerHole <= 0) return;
		if (!_viewRect.Intersects(HoleRect(hole))) return;

		if (!_fx.TryGetValue(hole, out var fx))
		{
			fx = new HoleFx { Items = new FallFx[MaxFallingPerHole] };
			_fx[hole] = fx;
		}
		if (fx.Count >= fx.Items.Length) return; // сверх лимита — без анимации
		fx.Items[fx.Count++] = item;
	}

	private Vector2 HoleCenter(BlackHole h) =>
		new Vector2((h.Col + h.Size / 2f) * _cellSize, (h.Row + h.Size / 2f) * _cellSize);

	private Rect2 HoleRect(BlackHole h) =>
		new Rect2(h.Col * _cellSize, h.Row * _cellSize, h.Size * _cellSize, h.Size * _cellSize);

	private void UpdateViewRect()
	{
		var cam = GetViewport().GetCamera2D();
		if (cam == null) { _viewRect = new Rect2(); return; }
		var size = GetViewportRect().Size / cam.Zoom;
		_viewRect = new Rect2(cam.GetScreenCenterPosition() - size / 2f, size);
	}

	// Вспышка всех ЧД при выполнении задания (T011): кольцо расходится от диска
	// и тает. Только визуал, по времени кадра.
	[Export] public float RewardFlashSeconds = 1.2f;
	private float _rewardFlash;

	public void FlashAll() => _rewardFlash = RewardFlashSeconds;

	public override void _Process(double delta)
	{
		if (!_ready) return;
		if (_rewardFlash > 0f) _rewardFlash = Mathf.Max(0f, _rewardFlash - (float)delta);
		UpdateViewRect();
		UpdateEffects((float)delta);
		FillEffectMesh();
		// Последнюю удалённую ЧД и погасшее превью тоже нужно стереть.
		bool preview = _toolSelected && !ViewLayer.IsLayer2;
		if (_holes.Count > 0 || _hadHoles || preview || _hadPreview || _rewardFlash > 0f) QueueRedraw();
		_hadHoles = _holes.Count > 0;
		_hadPreview = preview;
	}

	private void UpdateEffects(float dt)
	{
		if (_fxHolesVersion != _holes.Version)
		{
			_fxHolesVersion = _holes.Version;
			_fxToRemove.Clear();
			var alive = new HashSet<BlackHole>(_holes.Enumerate());
			foreach (var key in _fx.Keys)
				if (!alive.Contains(key)) _fxToRemove.Add(key);
			foreach (var key in _fxToRemove) _fx.Remove(key);
		}

		foreach (var pair in _fx)
		{
			var fx = pair.Value;
			if (fx.Count == 0 && fx.Flash <= 0f) continue;
			if (!_viewRect.Intersects(HoleRect(pair.Key)))
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
		if (!_ready) return;
		foreach (var hole in _holes.Enumerate())
		{
			var rect = HoleRect(hole);
			if (!_viewRect.Intersects(rect)) continue;
			if (_texture != null) DrawTextureRect(_texture, rect, false);
			if (ShowHitFlash && _fx.TryGetValue(hole, out var fx) && fx.Flash > 0f) DrawFlash(hole, fx.Flash);
			if (_rewardFlash > 0f) DrawRewardFlash(hole);
		}
		if (_toolSelected && !ViewLayer.IsLayer2) DrawPreview();
	}

	// Превью под курсором: полупрозрачный спрайт или красный квадрат, если нельзя.
	private void DrawPreview()
	{
		var (row, col) = CellUnderMouse();
		int size = BlackHoleSize;
		var rect = new Rect2(col * _cellSize, row * _cellSize, size * _cellSize, size * _cellSize);
		if (PlaceBlockReason(row, col, size) != null) DrawRect(rect, BlockedColor);
		else if (_texture != null) DrawTextureRect(_texture, rect, false, new Color(1f, 1f, 1f, 0.5f));
	}

	// Вспышка диска при попадании.
	private void DrawFlash(BlackHole hole, float flash)
	{
		float side = hole.Size * _cellSize;
		var center = HoleCenter(hole);
		DrawArc(center, side * 0.47f, 0f, Mathf.Tau, 48, new Color(FlashColor, 0.8f * flash), _cellSize * 0.1f);
		DrawCircle(center, side * 0.08f * (1f + flash), new Color(HotColor, 0.35f * flash));
	}

	private void DrawRewardFlash(BlackHole hole)
	{
		float t = 1f - _rewardFlash / Mathf.Max(0.01f, RewardFlashSeconds); // 0 → 1
		float side = hole.Size * _cellSize;
		var center = HoleCenter(hole);
		float alpha = 1f - t;
		DrawCircle(center, side * 0.5f, new Color(FlashColor, 0.35f * alpha * alpha));
		DrawArc(center, side * (0.5f + 1.5f * t), 0f, Mathf.Tau, 64, new Color(FlashColor, 0.9f * alpha), _cellSize * 0.15f);
	}

	// Всё летящее видимых ЧД → буфер MultiMesh (порядок экземпляров =
	// порядок отрисовки: обод, тело, точки — поверх).
	private void FillEffectMesh()
	{
		int needed = 0;
		foreach (var fx in _fx.Values)
			for (int i = 0; i < fx.Count; i++) needed += fx.Items[i].Instances;
		if (needed > _fxCapacity)
		{
			_fxCapacity = Mathf.Max(needed, _fxCapacity * 2);
			_fxBuffer = new float[_fxCapacity * Stride];
			_fxMesh.InstanceCount = _fxCapacity; // растёт только до пика, не каждый кадр
		}
		if (needed == 0)
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
			var center = HoleCenter(pair.Key);
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
				float shrink = Mathf.Lerp(1f, 0.12f, e);
				float alpha = 1f - 0.7f * e;

				if (f.Tier < 0)
				{
					float pr = _particleRadius * shrink;
					Put(ref n, pos, ax, ay, 2f * pr * sx, 2f * pr * sy, new Color(TierColor(f.Color, colors).Lerp(HotColor, e), alpha));
					continue;
				}

				float radius = _atomRadius * shrink;
				var rim = TierColor(f.Tier, colors);
				if (radius * zoom < FallLodPixels)
				{
					// Буфер рассчитан на полный атом, видимых экземпляров — ровно n.
					Put(ref n, pos, ax, ay, 2f * radius * sx, 2f * radius * sy, new Color(rim.Lerp(HotColor, e), alpha));
					continue;
				}

				Put(ref n, pos, ax, ay, 2f * radius * sx, 2f * radius * sy, new Color(rim.Lerp(HotColor, e), alpha));
				Put(ref n, pos, ax, ay, 1.76f * radius * sx, 1.76f * radius * sy, new Color(AtomBodyColor, AtomBodyColor.A * alpha));
				for (int k = 0; k < f.Particles.Count; k++)
				{
					float da = k * Mathf.Tau / Atom.Size - Mathf.Pi / 2f;
					var local = radius * 0.6f * new Vector2(Mathf.Cos(da), Mathf.Sin(da));
					var p = pos + ax * (local.X * sx) + ay * (local.Y * sy);
					var c = TierColor(f.Particles.ColorAt(k), colors);
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
}
