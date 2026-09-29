using Godot;

// Звезда-сборщик (T006) — объект слоя 1, 3×3 клетки. Данные — StarSet
// (NucleusLayer.Stars), приём ингредиентов, производство и выход — NucleusLayer.
// Этот узел только:
//   - инструмент установки (кнопки ★Ж/★К/★С на панели слоя 1, SelectTool):
//     ЛКМ — поставить (клетка под курсором — центр), превью спрайта или
//     красный квадрат, если нельзя; удаление — ПКМ через NucleusLayer
//     (RemoveAllAtMouse → RemoveAt);
//   - отрисовка: спрайт star_gray на 3×3 клетки, цвет тира — шейдер палитр
//     атомов (общий материал NucleusLayer, строка палитры в INSTANCE_CUSTOM.x);
//     во время производства звезда пульсирует ярче (INSTANCE_CUSTOM.y), в
//     простое — тусклее (INSTANCE_CUSTOM.z). Все звёзды — один MultiMesh.
public partial class StarLayer : Node2D
{
	public const string TexturePath = "res://Resources/Textures/star_gray_96px.png";

	// Свечение во время производства: база и размах пульсации (0..1), частота (Гц).
	[Export] public float ProducingGlow = 0.3f;
	[Export] public float ProducingPulse = 0.2f;
	[Export] public float PulseHz = 1.2f;
	// Приглушение в простое (0..1).
	[Export] public float IdleDim = 0.45f;

	private static readonly Color BlockedColor = new Color(1f, 0.2f, 0.2f, 0.35f);

	private NucleusLayer _nucleusLayer;
	private EnergyLayer _energyLayer;
	private readonly System.Collections.Generic.List<EnergyClusterLayer> _clusterLayers = new();
	private StarSet _stars;
	private Texture2D _texture;
	private float _cellSize;
	private bool _ready;

	private MultiMesh _mesh;
	private MultiMeshInstance2D _meshNode;
	private int _meshVersion = -1;
	private float _time;

	private int? _toolTier;
	private bool _hadPreview;

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		if (_nucleusLayer == null || !_nucleusLayer.IsReady || _nucleusLayer.Stars == null)
		{
			GD.PrintErr("[StarLayer] NucleusLayer не найден или не инициализирован — звёзды не работают.");
			return;
		}
		_energyLayer = GetNodeOrNull<EnergyLayer>("../TileMapLayer");
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer layer)
				_clusterLayers.Add(layer);

		_texture = GD.Load<Texture2D>(TexturePath);
		if (_texture == null) GD.PrintErr($"[StarLayer] не загрузился спрайт {TexturePath}.");

		_stars = _nucleusLayer.Stars;
		_cellSize = _nucleusLayer.CellSize;
		TextureFilter = TextureFilterEnum.Nearest;

		float side = Star.Size * _cellSize;
		_mesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = new QuadMesh { Size = new Vector2(side, side) },
			// Как у эффекта ЧД: границы на весь мир, иначе canvas item отсекает
			// MultiMesh по прямоугольнику, закэшированному, пока он был пуст.
			CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1f), new Vector3(2e7f, 2e7f, 2f)),
		};
		AddChild(_meshNode = new MultiMeshInstance2D
		{
			Name = "Stars",
			Multimesh = _mesh,
			Texture = _texture,
			Material = _nucleusLayer.PaletteMaterial,
			TextureFilter = TextureFilterEnum.Nearest,
		});
		SetProcessUnhandledInput(true);
		_ready = true;
	}

	// --- инструмент (панель слоя 1) ---

	public void SelectTool(int tier)
	{
		if (!_ready) return;
		_nucleusLayer.ClearSelection(); // сбрасывает и этот инструмент
		_energyLayer?.ClearSelection();
		foreach (var layer in _clusterLayers) layer.ClearSelection();
		_toolTier = tier;
		GD.Print($"[StarLayer] выбрана звезда тира {tier}: ЛКМ — поставить (центр под курсором), ПКМ — удалить.");
	}

	public void ClearTool() => _toolTier = null;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_ready || ViewLayer.IsLayer2) return;
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;
		var (row, col) = CellUnderMouse();
		if (_toolTier.HasValue)
		{
			TryPlace(row - Star.Size / 2, col - Star.Size / 2, _toolTier.Value, log: true);
			GetViewport().SetInputAsHandled();
		}
	}

	private (int row, int col) CellUnderMouse()
	{
		var p = GetGlobalMousePosition();
		return (Mathf.FloorToInt(p.Y / _cellSize), Mathf.FloorToInt(p.X / _cellSize));
	}

	// Причина, по которой звезду нельзя поставить (верхняя левая клетка), или null.
	private string PlaceBlockReason(int row, int col)
	{
		if (_stars.Overlaps(row, col, Star.Size)) return "пересекается с другой звездой";
		for (int r = row; r < row + Star.Size; r++)
			for (int c = col; c < col + Star.Size; c++)
				if (!_nucleusLayer.CanPlaceStarCell(r, c)) return $"клетка ({r},{c}) занята";
		return null;
	}

	// Установка звезды (инструмент и загрузка сохранения). Рецепт по умолчанию —
	// атом тира звезды (если такой рецепт есть).
	public Star TryPlace(int row, int col, int tier, bool log, int? recipe = null)
	{
		if (!_ready) return null;
		string reason = PlaceBlockReason(row, col);
		if (reason != null)
		{
			if (log) GD.Print($"[StarLayer] звезда в клетку ({row},{col}): {reason} — пропуск.");
			return null;
		}
		var star = new Star(row, col, tier, recipe ?? DefaultRecipe(tier));
		_stars.Add(star);
		if (log) GD.Print($"[StarLayer] установлена звезда тира {tier} в клетке ({row},{col}), рецепт «{star.RecipeData.Name}».");
		return star;
	}

	private static int DefaultRecipe(int tier)
	{
		for (int i = 0; i < StarRecipes.Count; i++)
			if (StarRecipes.All[i].ResultTier == tier) return i;
		return 0;
	}

	// ПКМ по любой клетке звезды (вызывает NucleusLayer.RemoveAllAtMouse).
	// Недособранные ингредиенты сгорают.
	public void RemoveAt(int row, int col)
	{
		if (!_ready || !_stars.TryGetAt(row, col, out var star)) return;
		_stars.Remove(star);
		string burned = star.Producing || star.HasBuffered ? " Ингредиенты в работе и в буфере сгорели." : "";
		GD.Print($"[StarLayer] удалена звезда из клетки ({star.Row},{star.Col}).{burned}");
	}

	// --- отрисовка ---

	public override void _Process(double delta)
	{
		if (!_ready) return;
		_time += (float)delta;
		UpdateMesh();
		bool preview = _toolTier.HasValue && !ViewLayer.IsLayer2;
		if (_stars.Count > 0 || preview || _hadPreview) QueueRedraw();
		_hadPreview = preview;
	}

	// Звёзд мало — буфер пишется целиком каждый кадр (пульсация).
	private void UpdateMesh()
	{
		var stars = _stars.All;
		if (_meshVersion != _stars.Version)
		{
			_meshVersion = _stars.Version;
			_mesh.InstanceCount = stars.Count;
		}
		_meshNode.Visible = !ViewLayer.IsLayer2;
		float pulse = 0.5f + 0.5f * Mathf.Sin(_time * Mathf.Tau * PulseHz);
		for (int i = 0; i < stars.Count; i++)
		{
			var s = stars[i];
			var center = new Vector2((s.Col + Star.Size / 2f) * _cellSize, (s.Row + Star.Size / 2f) * _cellSize);
			_mesh.SetInstanceTransform2D(i, new Transform2D(0f, center));
			bool working = s.Producing && s.Elapsed < _nucleusLayer.StarDuration(s);
			float rowUv = (s.Tier + 0.5f) / _nucleusLayer.TierCount;
			float glow = working ? ProducingGlow + ProducingPulse * pulse : 0f;
			float dim = working ? 0f : IdleDim;
			_mesh.SetInstanceCustomData(i, new Color(rowUv, glow, dim, 0f));
		}
	}

	public override void _Draw()
	{
		if (!_ready || ViewLayer.IsLayer2) return;
		if (_toolTier.HasValue) DrawPreview();
	}

	private void DrawPreview()
	{
		var (row, col) = CellUnderMouse();
		row -= Star.Size / 2;
		col -= Star.Size / 2;
		var rect = new Rect2(col * _cellSize, row * _cellSize, Star.Size * _cellSize, Star.Size * _cellSize);
		if (PlaceBlockReason(row, col) != null) DrawRect(rect, BlockedColor);
		else if (_texture != null)
		{
			var colors = _nucleusLayer.TierPreviewColors;
			var tint = colors != null && _toolTier.Value < colors.Length ? colors[_toolTier.Value] : Colors.White;
			DrawTextureRect(_texture, rect, false, new Color(tint, 0.6f));
		}
	}
}
