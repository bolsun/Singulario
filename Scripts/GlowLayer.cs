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
	[Export] public float AtomStrength = 0.3f;
	[Export] public float GrayStrength = 0.18f;
	// Звёзды: радиус (клеток) и сила по состояниям (T017); в работе сила «дышит»
	// синхронно с короной звезды: × (1 + StarBreath · sin(2π · фаза · StarBreathCycles)).
	[Export] public float StarRadiusCells = 2.5f;
	[Export] public float StarStrength = 0.45f;
	[Export] public float StarBlockedStrength = 0.35f;
	[Export] public float StarIdleStrength = 0.2f;
	[Export] public float StarBreath = 0.15f;
	[Export] public int StarBreathCycles = 2;
	// Яркость буфера (сумма каналов) не больше этого — плотная застройка не выгорает.
	[Export] public float GlowClamp = 1f;
	// ЧД: радиус (клеток) и сила; считается в шейдере фона.
	[Export] public float BlackHoleRadiusCells = 3f;
	[Export] public float BlackHoleStrength = 0.2f;
	// Сингулярность — свет ЧД на отдалении: ниже SingularityZoom переход (лог-шкала)
	// до SingularityZoom / SingularityRatio; радиус — не меньше SingularityRadiusCells
	// клеток и SingularityMinScreenPx пикселей экрана.
	[Export] public float SingularityZoom = 0.05f;
	[Export] public float SingularityRatio = 3f;
	[Export] public float SingularityRadiusCells = 12f;
	[Export] public float SingularityMinScreenPx = 48f;
	[Export] public float SingularityStrength = 1.3f;
	// Рампы Singulario 32, тоны 0..2 (0 — темнее): Ж, К, С, серое по 3 подряд.
	[Export] public Color[] TierTones =
	{
		new("5e2a1e"), new("a8501c"), new("e8911f"),
		new("4a1030"), new("8c1c3a"), new("d23a4a"),
		new("1a1f5c"), new("26479e"), new("3a7fe0"),
		new("6e6a88"), new("9d9ab3"), new("cfcde0"),
	};
	[Export] public Color[] BlackHoleTones = { new("372d4d"), new("8a2d9e"), new("ff7ae0") };
	[Export] public Color[] SingularityTones = { new("8a2d9e"), new("ff7ae0"), new("ffffff") };

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
	private DepositLayer _deposit;

	// Облака месторождений (T022): один MultiMesh пятен на видимый чанк месторождений.
	private sealed class DepositGlow
	{
		public MultiMeshInstance2D Node;
		public int Version = -1;
	}

	private readonly Dictionary<(int cx, int cy), DepositGlow> _depositChunks = new();
	private readonly List<(int cx, int cy)> _depositRemove = new();

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
		UpdateDeposits();
		UpdateStars();
	}

	// Параметры финального прохода — в материал фона (uniform'ы nebula_background).
	public void ApplyToMaterial(ShaderMaterial material, float zoom)
	{
		material.SetShaderParameter("glow_on", Enabled && _layer != null && _layer.CellSize > 0);
		if (!Enabled || _layer == null || _layer.CellSize <= 0) return;
		material.SetShaderParameter("glow_tex", Texture);
		material.SetShaderParameter("glow_origin", (Vector2)Origin);
		material.SetShaderParameter("glow_size", (Vector2)Size);
		material.SetShaderParameter("glow_clamp", Mathf.Max(0f, GlowClamp));
		material.SetShaderParameter("tier_tones", TierTones);
		material.SetShaderParameter("bh_tones", BlackHoleTones);
		material.SetShaderParameter("sing_tones", SingularityTones);

		float cell = _layer.CellSize;
		float s = SingularityT(zoom);
		float bhRadius = Mathf.Max(0.01f, BlackHoleRadiusCells) * cell;
		float singRadius = Mathf.Max(SingularityRadiusCells * cell, SingularityMinScreenPx / Mathf.Max(zoom, 1e-6f));
		material.SetShaderParameter("bh_glow_radius", Mathf.Lerp(bhRadius, singRadius, s));
		material.SetShaderParameter("bh_glow_strength", Mathf.Lerp(BlackHoleStrength, SingularityStrength, s));
		material.SetShaderParameter("sing_t", s);
	}

	// 0 при zoom ≥ SingularityZoom, 1 при zoom ≤ SingularityZoom / SingularityRatio.
	private float SingularityT(float zoom)
	{
		if (SingularityRatio <= 1f) return zoom < SingularityZoom ? 1f : 0f;
		float t = Mathf.Log(SingularityZoom / zoom) / Mathf.Log(SingularityRatio);
		return Mathf.SmoothStep(0f, 1f, Mathf.Clamp(t, 0f, 1f));
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

	// Облака месторождений: пятно на клетку источника цвета тира, радиус и сила — по стадии
	// запаса (раскладка — DepositLayout). Гаснут тем же fade, что и ореолы атомов (материал
	// общий, fade выставляет UpdateAtoms); на слое 2 и в старом виде (F7) не рисуются.
	private void UpdateDeposits()
	{
		_deposit ??= GetNodeOrNull<DepositLayer>("/root/Main/DepositLayer");
		if (_deposit == null) return;
		_deposit.Refresh();
		var visible = _deposit.VisibleChunks;
		float cell = _layer.CellSize;
		foreach (var key in visible)
		{
			if (!_deposit.TryGetSpots(key, out int version, out var spots)) continue;
			if (!_depositChunks.TryGetValue(key, out var glow))
			{
				glow = new DepositGlow { Node = new MultiMeshInstance2D { Material = _atomMaterial } };
				_tierRoot.AddChild(glow.Node);
				_depositChunks[key] = glow;
			}
			if (glow.Version == version) continue;
			glow.Version = version;
			float chunkWorld = _layer.ChunkSize * cell;
			var rect = new Rect2(key.cx * chunkWorld, key.cy * chunkWorld, chunkWorld, chunkWorld).Grow(2f * cell);
			var mm = NewMultiMesh(spots.Count, new Aabb(new Vector3(rect.Position.X, rect.Position.Y, -1f), new Vector3(rect.Size.X, rect.Size.Y, 2f)));
			for (int i = 0; i < spots.Count; i++)
			{
				var (tier, s) = (spots[i].Tier, spots[i].Spot);
				float side = 2f * s.Radius * cell;
				mm.SetInstanceTransform2D(i, new Transform2D(new Vector2(side, 0f), new Vector2(0f, side), new Vector2(s.X * cell, s.Y * cell)));
				mm.SetInstanceCustomData(i, TierWeights(tier, s.Strength));
			}
			glow.Node.Multimesh = mm;
		}
		// Ушедшие из кадра, исчезнувшие и при выключенном виде — удалить.
		_depositRemove.Clear();
		foreach (var key in _depositChunks.Keys)
			if (!_deposit.IsChunkVisible(key)) _depositRemove.Add(key);
		foreach (var key in _depositRemove)
		{
			_depositChunks[key].Node.QueueFree();
			_depositChunks.Remove(key);
		}
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

	// Звёзды — каждый кадр (их немного): сила по состоянию, в работе «дышит»
	// с фазой короны (StarLayer.LoopTicks, тик симуляции; на паузе стоит).
	private void UpdateStars()
	{
		var stars = _layer.Stars?.All;
		int count = stars?.Count ?? 0;
		var mm = _stars.Multimesh;
		if (mm == null || mm.InstanceCount != count)
		{
			mm = NewMultiMesh(count, new Aabb(new Vector3(-1e9f, -1e9f, -1f), new Vector3(2e9f, 2e9f, 2f)));
			_stars.Multimesh = mm;
		}
		if (count == 0) return;

		int loop = Mathf.Max(256, _starLayer?.LoopTicks ?? 512);
		float phase = ((float)(_layer.GlobalTick % loop) + _layer.SubTickFraction) / loop;
		float breath = 1f + StarBreath * Mathf.Sin(Mathf.Tau * phase * StarBreathCycles);
		float cell = _layer.CellSize;
		float side = 2f * StarRadiusCells * cell;
		for (int i = 0; i < count; i++)
		{
			var s = stars[i];
			var state = _starLayer?.StateOf(s) ?? StarLayer.StarState.Working;
			float strength = state switch
			{
				StarLayer.StarState.Working => StarStrength * breath,
				StarLayer.StarState.Blocked => StarBlockedStrength,
				_ => StarIdleStrength,
			};
			var center = new Vector2((s.Col + Star.Size / 2f) * cell, (s.Row + Star.Size / 2f) * cell);
			mm.SetInstanceTransform2D(i, new Transform2D(new Vector2(side, 0f), new Vector2(0f, side), center));
			mm.SetInstanceCustomData(i, TierWeights(s.Tier, strength));
		}
	}
}
