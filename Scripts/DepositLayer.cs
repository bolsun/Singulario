using Godot;
using System.Collections.Generic;

// Вид месторождений (T022): частицы у ядер и на мостиках + общий для GlowLayer индекс
// «чанк → клетки источников трёх тиров» и раскладка пятен облака (DepositLayout).
// Спецификация — Docs/Art/deposit-visual-spec.md. Узел создаёт Main.
//
// Видимость чанков месторождений считается здесь по прямоугольнику камеры и своему
// индексу: NucleusLayer.VisibleChunks содержит только чанки с атомами, а месторождение
// может стоять в чанке без атомов. Индекс перестраивается по VisualVersion трёх узлов
// EnergyClusterLayer (и по смене настроек), раскладка чанка — только если изменилось его
// содержимое (подпись клеток, стадий и связей) и только пока чанк в кадре.
// Частицы — один MultiMesh на видимый чанк с постоянными данными экземпляра; движение
// считает вершинный шейдер от uniform тика (CPU каждый кадр — только тик и видимость).
// F7 — отладка: новый вид (облако + частицы, старые тайлы скрыты) ↔ старые тайлы.
public partial class DepositLayer : Node2D
{
	private const string ShaderPath = "res://Resources/Shaders/deposit_particle.gdshader";
	// Полосы вариантов (T024): кадры n×n слева направо; размеры 16 / 8 / 4 / 2 px = SizeClass 0..3.
	private static readonly string[] Strips =
	{
		"res://Resources/Textures/particle_variants_gray_16px.png",
		"res://Resources/Textures/particle_variants_gray_8px.png",
		"res://Resources/Textures/particle_variants_gray_4px.png",
		"res://Resources/Textures/particle_variants_gray_2px.png",
	};
	private const float LabelSeconds = 1.5f;
	private const float LabelFadeSeconds = 0.4f;
	// Общий период всех периодов частиц (256 · k, k = 2..8): тик берётся по модулю — без потери точности.
	private const long TickWrap = 256L * 840L;

	public struct TierSpot
	{
		public int Tier;
		public DepositLayout.Spot Spot;
	}

	// Облако: по стадии запаса 0..3 (>75 / 50–75 / 25–50 / <25%).
	[Export] public float[] StageRadius = { 1.0f, 0.85f, 0.7f, 0.5f };
	[Export] public float[] StageStrength = { 0.30f, 0.25f, 0.18f, 0.10f };
	// Частица видна, если её приоритет меньше порога стадии.
	[Export] public float[] StageThreshold = { 1.01f, 0.72f, 0.46f, 0.24f };
	[Export] public float NeighborPull = 0.1f;
	[Export] public float NeighborPullMax = 0.2f;
	[Export] public float CoreJitter = 0.06f;
	[Export] public int CoreCount = 20;
	[Export] public int BridgeCount = 5;
	[Export] public float BridgeBend = 0.12f;
	// Движение (шейдер).
	[Export] public float SwayAngle = 0.3f;
	[Export] public float SwayDist = 0.04f;
	[Export] public float Squash = 0.85f;

	// Новый вид включён (F7). Старт — новый.
	public bool NewLook { get; private set; } = true;
	// Видимые в кадре чанки месторождений (обновляет Refresh); пусто при слое 2, выключенном виде и зуме ниже NebulaZoom.
	public IReadOnlyList<(int cx, int cy)> VisibleChunks => _visible;
	public bool ParticlesOn { get; private set; }
	public bool IsChunkVisible((int cx, int cy) key) => _visibleSet.Contains(key);

	private sealed class Chunk
	{
		public readonly List<(int row, int col)>[] Cells = { new(), new(), new() };
		public ulong Sig;
		public int Version;            // растёт при смене содержимого
		public int LayoutVersion = -1;
		public int MeshVersion = -1;
		public readonly List<TierSpot> Spots = new();
		public readonly List<(int tier, DepositLayout.Particle p)> Particles = new();
		public MultiMeshInstance2D Node;
	}

	private readonly Dictionary<(int cx, int cy), Chunk> _chunks = new();
	private readonly List<(int cx, int cy)> _visible = new();
	private readonly List<(int cx, int cy)> _prevVisible = new();
	private readonly HashSet<(int cx, int cy)> _visibleSet = new();
	private readonly List<(int cx, int cy)> _scratch = new();
	private readonly List<DepositLayout.Spot> _spotScratch = new();
	private readonly List<DepositLayout.Particle> _particleScratch = new();
	private readonly DepositLayout _layout = new();
	private readonly EnergyClusterLayer[] _layers = new EnergyClusterLayer[3];
	private NucleusLayer _layer;
	private ShaderMaterial _material;
	private QuadMesh _quad;
	private long _indexSum = -1;
	private int _versionCounter;
	private long _frame = -1;
	private Label _label;
	private float _labelLeft;

	public override void _Ready()
	{
		ZIndex = -1; // над тайлами месторождений (−1, раньше в дереве), под атомами
		_layer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer c && c.Tier >= 0 && c.Tier < _layers.Length)
				_layers[c.Tier] = c;

		var shader = GD.Load<Shader>(ShaderPath);
		_material = new ShaderMaterial { Shader = shader };
		BuildAtlas();
		_quad = new QuadMesh { Size = Vector2.One };

		var labelLayer = new CanvasLayer { Name = "DepositLabelLayer", Layer = 90 };
		AddChild(labelLayer);
		_label = new Label
		{
			Name = "DepositModeLabel",
			AutoTranslateMode = AutoTranslateModeEnum.Disabled,
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_label.AddThemeColorOverride("font_color", Colors.White);
		_label.AddThemeColorOverride("font_outline_color", Colors.Black);
		_label.AddThemeConstantOverride("outline_size", 4);
		_label.AddThemeFontSizeOverride("font_size", 22);
		_label.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
		_label.GrowHorizontal = Control.GrowDirection.Both;
		_label.OffsetTop = 96;
		labelLayer.AddChild(_label);
		ApplyLook();
	}

	// Атлас из четырёх полос в один ряд (T024: 48+24+12+2 = 86×16). Смещения полос (strip_x0),
	// размер атласа и число кадров каждого размера (ширина / высота) — из самих картинок:
	// шейдеру и раскладке (_layout.Frames).
	private void BuildAtlas()
	{
		var imgs = new Image[Strips.Length];
		int width = 0, height = 1;
		for (int i = 0; i < Strips.Length; i++)
		{
			var tex = GD.Load<Texture2D>(Strips[i]);
			if (tex == null) { GD.PrintErr($"DepositLayer: не загрузился {Strips[i]}"); continue; }
			imgs[i] = tex.GetImage();
			imgs[i].Convert(Image.Format.Rgba8);
			width += imgs[i].GetWidth();
			height = Mathf.Max(height, imgs[i].GetHeight());
		}
		var atlas = Image.CreateEmpty(Mathf.Max(1, width), height, false, Image.Format.Rgba8);
		var x0 = new float[4];
		int x = 0;
		for (int i = 0; i < Strips.Length; i++)
		{
			x0[i] = x;
			if (imgs[i] == null) { _layout.Frames[i] = 1; continue; }
			int side = imgs[i].GetHeight();
			_layout.Frames[i] = Mathf.Max(1, imgs[i].GetWidth() / side);
			atlas.BlitRect(imgs[i], new Rect2I(Vector2I.Zero, imgs[i].GetSize()), new Vector2I(x, 0));
			x += imgs[i].GetWidth();
		}
		_material.SetShaderParameter("atlas_tex", ImageTexture.CreateFromImage(atlas));
		_material.SetShaderParameter("atlas_size", new Vector2(atlas.GetWidth(), atlas.GetHeight()));
		_material.SetShaderParameter("strip_x0", new Vector4(x0[0], x0[1], x0[2], x0[3]));
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo || key.Keycode != Key.F7) return;
		NewLook = !NewLook;
		ApplyLook();
		_label.Text = string.Format(Tr("Месторождения: {0}"), Tr(NewLook ? "Облако" : "Тайлы"));
		_label.Visible = true;
		_labelLeft = LabelSeconds;
		GetViewport().SetInputAsHandled();
	}

	private void ApplyLook()
	{
		foreach (var l in _layers) l?.SetTilesVisible(!NewLook);
		if (!NewLook) ReleaseAll();
	}

	public override void _Process(double delta)
	{
		if (_labelLeft > 0f)
		{
			_labelLeft = Mathf.Max(0f, _labelLeft - (float)delta);
			_label.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_labelLeft / LabelFadeSeconds, 0f, 1f));
			_label.Visible = _labelLeft > 0f;
		}
		Refresh();
	}

	// Раз в кадр (вызывают и сам слой, и GlowLayer): индекс, видимые чанки, частицы.
	public void Refresh()
	{
		long frame = (long)Engine.GetProcessFrames();
		if (frame == _frame) return;
		_frame = frame;

		var cam = GetViewport().GetCamera2D();
		bool active = NewLook && _layer != null && _layer.IsReady && cam != null && !ViewLayer.IsLayer2
			&& cam.Zoom.X >= _layer.NebulaZoom;
		Visible = active;
		_prevVisible.Clear();
		_prevVisible.AddRange(_visible);
		_visible.Clear();
		_visibleSet.Clear();
		ParticlesOn = false;
		if (!active) { ReleaseChunks(); return; }

		EnsureIndex();
		float zoom = cam.Zoom.X;
		float cell = _layer.CellSize;
		float chunkWorld = _layer.ChunkSize * cell;
		var size = GetViewport().GetVisibleRect().Size / cam.Zoom;
		var rect = new Rect2(cam.GetScreenCenterPosition() - size / 2f, size).Grow(cell * 2f);
		int minCx = Mathf.FloorToInt(rect.Position.X / chunkWorld), maxCx = Mathf.FloorToInt(rect.End.X / chunkWorld);
		int minCy = Mathf.FloorToInt(rect.Position.Y / chunkWorld), maxCy = Mathf.FloorToInt(rect.End.Y / chunkWorld);
		long area = (long)(maxCx - minCx + 1) * (maxCy - minCy + 1);
		if (area <= _chunks.Count)
		{
			for (int cy = minCy; cy <= maxCy; cy++)
				for (int cx = minCx; cx <= maxCx; cx++)
					if (_chunks.ContainsKey((cx, cy))) _visible.Add((cx, cy));
		}
		else
		{
			foreach (var key in _chunks.Keys)
				if (key.cx >= minCx && key.cx <= maxCx && key.cy >= minCy && key.cy <= maxCy) _visible.Add(key);
		}
		foreach (var key in _visible) _visibleSet.Add(key);

		ParticlesOn = zoom >= _layer.AtomDotZoom;
		_material.SetShaderParameter("tick", (float)(_layer.GlobalTick % TickWrap) + _layer.SubTickFraction);
		_material.SetShaderParameter("cell", cell);
		_material.SetShaderParameter("sway_angle", SwayAngle);
		_material.SetShaderParameter("sway_dist", SwayDist);
		_material.SetShaderParameter("squash", Squash);
		if (_layer.PaletteAtlas != null) _material.SetShaderParameter("palette_tex", _layer.PaletteAtlas);

		// Ушедшие из кадра — освободить (раскладка и меш строятся заново при возвращении).
		foreach (var key in _prevVisible)
			if (!_visibleSet.Contains(key) && _chunks.TryGetValue(key, out var gone)) Release(gone);

		foreach (var key in _visible)
		{
			var chunk = _chunks[key];
			EnsureLayout(chunk);
			if (ParticlesOn) EnsureMesh(chunk);
			if (chunk.Node != null) chunk.Node.Visible = ParticlesOn;
		}
	}

	// Пятна облака чанка для GlowLayer: версия растёт при смене содержимого чанка.
	public bool TryGetSpots((int cx, int cy) key, out int version, out List<TierSpot> spots)
	{
		if (_chunks.TryGetValue(key, out var chunk) && chunk.LayoutVersion == chunk.Version)
		{
			version = chunk.Version;
			spots = chunk.Spots;
			return true;
		}
		version = -1;
		spots = null;
		return false;
	}

	private ulong SettingsSig()
	{
		ulong h = 1469598103934665603UL;
		void Add(float v) { h = (h ^ (ulong)System.BitConverter.SingleToInt32Bits(v)) * 1099511628211UL; }
		foreach (var a in new[] { StageRadius, StageStrength, StageThreshold })
			if (a != null) foreach (var v in a) Add(v);
		Add(NeighborPull); Add(NeighborPullMax); Add(CoreJitter); Add(CoreCount); Add(BridgeCount); Add(BridgeBend);
		return h;
	}

	private void ApplySettings()
	{
		if (StageRadius?.Length >= 4) _layout.StageRadius = StageRadius;
		if (StageStrength?.Length >= 4) _layout.StageStrength = StageStrength;
		if (StageThreshold?.Length >= 4) _layout.StageThreshold = StageThreshold;
		_layout.NeighborPull = NeighborPull;
		_layout.NeighborPullMax = NeighborPullMax;
		_layout.CoreJitter = CoreJitter;
		_layout.CoreCount = Mathf.Max(0, CoreCount);
		_layout.BridgeCount = Mathf.Max(0, BridgeCount);
		_layout.BridgeBend = BridgeBend;
	}

	// Индекс «чанк → клетки по тирам» — по версии вида трёх узлов; подпись чанка не зависит от
	// порядка обхода словаря (сумма хешей клеток), версия чанка растёт только при её смене.
	private void EnsureIndex()
	{
		ulong settings = SettingsSig();
		long sum = (long)(settings & 0x7FFFFFFF);
		foreach (var l in _layers) if (l != null) sum = sum * 31 + l.VisualVersion;
		if (sum == _indexSum) return;
		_indexSum = sum;
		ApplySettings();

		foreach (var chunk in _chunks.Values)
			foreach (var list in chunk.Cells) list.Clear();
		int cs = _layer.ChunkSize;
		for (int tier = 0; tier < _layers.Length; tier++)
		{
			if (_layers[tier] == null) continue;
			foreach (var (row, col) in _layers[tier].EnumerateCells())
			{
				var key = (FloorDiv(col, cs), FloorDiv(row, cs));
				if (!_chunks.TryGetValue(key, out var chunk)) _chunks[key] = chunk = new Chunk();
				chunk.Cells[tier].Add((row, col));
			}
		}

		_scratch.Clear();
		foreach (var (key, chunk) in _chunks)
		{
			if (chunk.Cells[0].Count + chunk.Cells[1].Count + chunk.Cells[2].Count == 0) { _scratch.Add(key); continue; }
			ulong sig = settings;
			for (int tier = 0; tier < _layers.Length; tier++)
			{
				var layer = _layers[tier];
				foreach (var (row, col) in chunk.Cells[tier])
				{
					ulong c = ((ulong)(uint)row << 32 | (uint)col) * 0x9E3779B97F4A7C15UL + (ulong)tier;
					c ^= (ulong)(layer.StageAt(row, col) + 1) * 0xBF58476D1CE4E5B9UL;
					if (layer.SameClusterAt(row, col, row, col + 1)) c ^= 0x1111111111111111UL;
					if (layer.SameClusterAt(row, col, row, col - 1)) c ^= 0x2222222222222222UL;
					if (layer.SameClusterAt(row, col, row + 1, col)) c ^= 0x4444444444444444UL;
					if (layer.SameClusterAt(row, col, row - 1, col)) c ^= 0x8888888888888888UL;
					c = (c ^ (c >> 29)) * 0x94D049BB133111EBUL;
					sig += c ^ (c >> 32);
				}
			}
			if (chunk.Version == 0 || sig != chunk.Sig)
			{
				chunk.Sig = sig;
				chunk.Version = ++_versionCounter;
			}
		}
		foreach (var key in _scratch)
		{
			Release(_chunks[key]);
			_chunks.Remove(key);
		}
	}

	private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

	private void EnsureLayout(Chunk chunk)
	{
		if (chunk.LayoutVersion == chunk.Version) return;
		chunk.LayoutVersion = chunk.Version;
		chunk.MeshVersion = -1;
		chunk.Spots.Clear();
		chunk.Particles.Clear();
		for (int tier = 0; tier < _layers.Length; tier++)
		{
			var layer = _layers[tier];
			if (layer == null || chunk.Cells[tier].Count == 0) continue;
			_layout.Build(chunk.Cells[tier], layer.StageAt, layer.SameClusterAt, _spotScratch, _particleScratch);
			foreach (var s in _spotScratch) chunk.Spots.Add(new TierSpot { Tier = tier, Spot = s });
			foreach (var p in _particleScratch) chunk.Particles.Add((tier, p));
		}
	}

	private void EnsureMesh(Chunk chunk)
	{
		if (chunk.MeshVersion == chunk.Version) return;
		chunk.MeshVersion = chunk.Version;
		if (chunk.Particles.Count == 0)
		{
			if (chunk.Node != null) chunk.Node.Multimesh = null;
			return;
		}
		if (chunk.Node == null)
		{
			chunk.Node = new MultiMeshInstance2D { Material = _material };
			AddChild(chunk.Node);
		}
		float cell = _layer.CellSize;
		var mm = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseColors = true,
			UseCustomData = true,
			Mesh = _quad,
			InstanceCount = chunk.Particles.Count,
		};
		// Границы — все клетки чанка (частицы и мостики уходят не дальше ~2 клеток от клетки).
		float chunkWorld = _layer.ChunkSize * cell;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		for (int i = 0; i < chunk.Particles.Count; i++)
		{
			var (tier, p) = chunk.Particles[i];
			float ax = p.X * cell, ay = p.Y * cell;
			minX = Mathf.Min(minX, ax); maxX = Mathf.Max(maxX, ax);
			minY = Mathf.Min(minY, ay); maxY = Mathf.Max(maxY, ay);
			mm.SetInstanceTransform2D(i, new Transform2D(Vector2.Right, Vector2.Down, new Vector2(ax, ay)));
			// y — упаковка size + 4·variant + 16·dark (T024, разбор — в шейдере).
			float packed = p.SizeClass + 4 * p.Variant + (p.Dark ? 16 : 0);
			mm.SetInstanceCustomData(i, new Color((tier + 0.5f) / _layer.TierCount, packed, p.Phase, p.PeriodK));
			mm.SetInstanceColor(i, p.Kind == 0
				? new Color(p.Angle, p.Dist, 0f, 0f)
				: new Color(p.Dx, p.Dy, p.Bend, 1f));
		}
		float m = cell * 2f + 16f;
		mm.CustomAabb = new Aabb(new Vector3(minX - m, minY - m, -1f), new Vector3(maxX - minX + 2f * m, maxY - minY + 2f * m, 2f));
		chunk.Node.Multimesh = mm;
	}

	private void Release(Chunk chunk)
	{
		chunk.LayoutVersion = -1;
		chunk.MeshVersion = -1;
		chunk.Spots.Clear();
		chunk.Particles.Clear();
		if (chunk.Node != null)
		{
			chunk.Node.QueueFree();
			chunk.Node = null;
		}
	}

	private void ReleaseChunks()
	{
		foreach (var chunk in _chunks.Values)
			if (chunk.LayoutVersion >= 0 || chunk.Node != null) Release(chunk);
	}

	private void ReleaseAll()
	{
		ReleaseChunks();
		_visible.Clear();
		_visibleSet.Clear();
		_indexSum = -1;
		_frame = -1;
	}
}
