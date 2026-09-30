using Godot;
using System.Collections.Generic;

// Буфер свечения (T020). SubViewport в разрешении клетки дизеринга фона:
// 1 пиксель буфера = DitherPixel пикселей экрана. Пиксели буфера совпадают с
// клетками дизеринга, сетка отсчитывается от начала мира — узор привязан к
// полю, как у фона (T016c). Преобразование мир → буфер: w · zoom / DitherPixel
// − Origin, где Origin — целый номер клетки левого верхнего пикселя буфера.
//
// Источники — мягкие пятна (glow_spot.gdshader), все MultiMesh: атомы — по
// одному узлу на видимый чанк (серые и цветные отдельно, перестройка только
// при смене версии чанка), звёзды — один общий. Каналы: R/G/B — Ж/К/С, A — серое.
// Буфер читает шейдер фона (NebulaBackground) — там же финальный проход, свет
// ЧД/Сингулярности и смешение с фоном. Узел создаёт NebulaBackground.
public partial class GlowLayer : Node
{
	private const string SpotShaderPath = "res://Resources/Shaders/glow_spot.gdshader";

	// Атомы: радиус пятна (клеток) и сила; серые — очень слабо.
	[Export] public float AtomRadiusCells = 0.7f;
	[Export] public float AtomStrength = 0.5f;
	[Export] public float GrayStrength = 0.25f;
	// Звёзды: радиус (клеток) и сила по состояниям (T017); в работе сила «дышит»
	// синхронно с короной звезды: × (1 + StarBreath · sin(2π · фаза · StarBreathCycles)).
	[Export] public float StarRadiusCells = 2.5f;
	[Export] public float StarStrength = 0.9f;
	[Export] public float StarBlockedStrength = 0.7f;
	[Export] public float StarIdleStrength = 0f;
	[Export] public float StarBreath = 0.15f;
	[Export] public int StarBreathCycles = 2;

	public bool Enabled = true;
	public Texture2D Texture => _viewport?.GetTexture();
	public Vector2I Origin { get; private set; }
	public Vector2I Size { get; private set; }

	private NucleusLayer _layer;
	private StarLayer _starLayer;
	private SubViewport _viewport;
	private Node2D _world, _grayRoot, _tierRoot;
	private ShaderMaterial _atomMaterial, _starMaterial;
	private QuadMesh _quad;
	private MultiMeshInstance2D _stars;

	private sealed class ChunkGlow
	{
		public MultiMeshInstance2D Gray, Tier;
		public int Version = -1;
	}

	private readonly Dictionary<(int cx, int cy), ChunkGlow> _chunks = new();
	private readonly HashSet<(int cx, int cy)> _shown = new();
	private readonly HashSet<(int cx, int cy)> _nowShown = new();
	private readonly List<(int cx, int cy)> _removeScratch = new();
	private readonly List<(Vector2 center, int tier)> _atomScratch = new();

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;
		_layer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		_starLayer = GetNodeOrNull<StarLayer>("/root/Main/StarLayer");

		_viewport = new SubViewport
		{
			Name = "GlowViewport",
			TransparentBg = true,
			UseHdr2D = true,
			Disable3D = true,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			Size = new Vector2I(4, 4),
		};
		AddChild(_viewport);
		_world = new Node2D { Name = "GlowWorld" };
		_viewport.AddChild(_world);
		// Серые — первыми (см. glow_spot.gdshader), цветные и звёзды — после.
		_grayRoot = new Node2D { Name = "Gray" };
		_tierRoot = new Node2D { Name = "Tier" };
		_world.AddChild(_grayRoot);
		_world.AddChild(_tierRoot);

		var shader = GD.Load<Shader>(SpotShaderPath);
		_atomMaterial = new ShaderMaterial { Shader = shader };
		_starMaterial = new ShaderMaterial { Shader = shader };
		_quad = new QuadMesh { Size = Vector2.One };

		_stars = new MultiMeshInstance2D { Name = "Stars", Material = _starMaterial };
		_tierRoot.AddChild(_stars);
	}

	// Вызывает NebulaBackground каждый кадр, до установки uniform'ов фона.
	public void UpdateView(Vector2 screen, Vector2 camPos, float zoom, float ditherPixel)
	{
		_viewport.RenderTargetUpdateMode = Enabled ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
		if (!Enabled || _layer == null || _layer.CellSize <= 0) return;

		// Экранная позиция начала мира и номер клетки левого верхнего пикселя буфера
		// (с запасом в клетку), размер буфера — экран в клетках + запас.
		Vector2 originW = screen * 0.5f - camPos * zoom;
		Origin = new Vector2I(Mathf.FloorToInt(-originW.X / ditherPixel) - 1, Mathf.FloorToInt(-originW.Y / ditherPixel) - 1);
		Size = new Vector2I(Mathf.CeilToInt(screen.X / ditherPixel) + 3, Mathf.CeilToInt(screen.Y / ditherPixel) + 3);
		if (_viewport.Size != Size) _viewport.Size = Size;
		float scale = zoom / ditherPixel;
		_world.Transform = new Transform2D(new Vector2(scale, 0f), new Vector2(0f, scale), -(Vector2)Origin);

		UpdateAtoms(zoom);
		UpdateStars();
	}

	// Ореолы атомов гаснут к NebulaZoom — та же полоса, что у туманностей чанков (T015).
	private float AtomFade(float zoom)
	{
		if (zoom < _layer.NebulaZoom) return 0f;
		if (_layer.NebulaFadeRatio <= 1f) return 1f;
		float t = Mathf.Log(zoom / _layer.NebulaZoom) / Mathf.Log(_layer.NebulaFadeRatio);
		return Mathf.Clamp(t, 0f, 1f);
	}

	private void UpdateAtoms(float zoom)
	{
		float fade = ViewLayer.IsLayer2 ? 0f : AtomFade(zoom);
		_atomMaterial.SetShaderParameter("fade", fade);
		_nowShown.Clear();
		if (fade > 0f)
		{
			foreach (var key in _layer.VisibleChunks)
			{
				if (!_layer.TryGetChunkGlowVersion(key.cx, key.cy, out int version)) continue;
				if (!_chunks.TryGetValue(key, out var glow))
				{
					glow = new ChunkGlow
					{
						Gray = new MultiMeshInstance2D { Material = _atomMaterial },
						Tier = new MultiMeshInstance2D { Material = _atomMaterial },
					};
					_grayRoot.AddChild(glow.Gray);
					_tierRoot.AddChild(glow.Tier);
					_chunks[key] = glow;
				}
				if (glow.Version != version) RebuildChunk(key, glow, version);
				glow.Gray.Visible = true;
				glow.Tier.Visible = true;
				_nowShown.Add(key);
			}
		}
		// Ушедшие из кадра — спрятать; исчезнувшие чанки (новая игра) — удалить.
		_removeScratch.Clear();
		foreach (var key in _shown)
		{
			if (_nowShown.Contains(key)) continue;
			var glow = _chunks[key];
			if (_layer.TryGetChunkGlowVersion(key.cx, key.cy, out _))
			{
				glow.Gray.Visible = false;
				glow.Tier.Visible = false;
			}
			else _removeScratch.Add(key);
		}
		foreach (var key in _removeScratch)
		{
			_chunks[key].Gray.QueueFree();
			_chunks[key].Tier.QueueFree();
			_chunks.Remove(key);
		}
		_shown.Clear();
		_shown.UnionWith(_nowShown);
	}

	private void RebuildChunk((int cx, int cy) key, ChunkGlow glow, int version)
	{
		glow.Version = version;
		_layer.CollectGlowAtoms(key.cx, key.cy, _atomScratch);
		int gray = 0;
		foreach (var (_, tier) in _atomScratch)
			if (tier == _layer.GrayCoreTier) gray++;

		float cell = _layer.CellSize;
		float side = 2f * AtomRadiusCells * cell;
		float chunkWorld = _layer.ChunkSize * cell;
		var rect = new Rect2(key.cx * chunkWorld, key.cy * chunkWorld, chunkWorld, chunkWorld).Grow(side);
		var aabb = new Aabb(new Vector3(rect.Position.X, rect.Position.Y, -1f), new Vector3(rect.Size.X, rect.Size.Y, 2f));
		var grayMesh = NewMultiMesh(gray, aabb);
		var tierMesh = NewMultiMesh(_atomScratch.Count - gray, aabb);
		int gi = 0, ti = 0;
		foreach (var (center, tier) in _atomScratch)
		{
			var xf = new Transform2D(new Vector2(side, 0f), new Vector2(0f, side), center);
			if (tier == _layer.GrayCoreTier)
			{
				grayMesh.SetInstanceTransform2D(gi, xf);
				grayMesh.SetInstanceCustomData(gi++, new Color(0f, 0f, 0f, GrayStrength));
			}
			else
			{
				tierMesh.SetInstanceTransform2D(ti, xf);
				tierMesh.SetInstanceCustomData(ti++, TierWeights(tier, AtomStrength));
			}
		}
		glow.Gray.Multimesh = grayMesh;
		glow.Tier.Multimesh = tierMesh;
	}

	private MultiMesh NewMultiMesh(int count, Aabb aabb) => new()
	{
		TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
		UseCustomData = true,
		Mesh = _quad,
		CustomAabb = aabb,
		InstanceCount = count,
	};

	// Сила в канал тира: Ж → R, К → G, С → B.
	private static Color TierWeights(int tier, float strength) => tier switch
	{
		0 => new Color(strength, 0f, 0f, 0f),
		1 => new Color(0f, strength, 0f, 0f),
		2 => new Color(0f, 0f, strength, 0f),
		_ => new Color(0f, 0f, 0f, 0f),
	};

	private void UpdateStars()
	{
	}
}
