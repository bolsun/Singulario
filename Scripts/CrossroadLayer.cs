using Godot;

// Вид перекрёстка (T010): два вертикальных кольца, как гироскоп — ось E–W и ось
// N–S, цвет тира. Высота над полем проецируется на экран сдвигом HeightDir,
// поэтому кольца видны эллипсами: верхняя (ближняя к камере) половина ярче,
// нижняя — тоньше и тусклее. Частицы в пути рисует NucleusLayer
// (UpdateCrossroadVisuals) по той же проекции (RingPoint). Только отрисовка.
// Узел создаёт NucleusLayer в _Ready.
public partial class CrossroadLayer : Node2D
{
	// Экранный сдвиг на единицу высоты: вверх и немного вправо, чтобы кольцо
	// N–S не схлопывалось в линию.
	public static readonly Vector2 HeightDir = new Vector2(0.3f, -0.5f);
	private const int Segments = 32;

	public NucleusLayer Layer;

	private readonly Vector2[] _near = new Vector2[Segments / 2 + 1];
	private readonly Vector2[] _far = new Vector2[Segments / 2 + 1];

	public override void _Ready()
	{
		ZIndex = 5; // поверх тел атомов и частиц, под туманом территории
		Layer ??= GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
	}

	// Точка кольца оси axis (0 — E–W, 1 — N–S) радиуса r относительно центра
	// атома: phi = 0 — сторона W (N), phi = π — сторона E (S), phi = π/2 —
	// вершина над центром.
	public static Vector2 RingPoint(int axis, float phi, float r)
	{
		float along = -Mathf.Cos(phi) * r;
		float height = Mathf.Sin(phi) * r;
		var alongVec = axis == 0 ? new Vector2(along, 0f) : new Vector2(0f, along);
		return alongVec + HeightDir * height;
	}

	public override void _Process(double delta)
	{
		Visible = !ViewLayer.IsLayer2;
		if (Visible) QueueRedraw();
	}

	public override void _Draw()
	{
		if (Layer == null || !Layer.IsReady || Layer.CrossroadCount == 0) return;
		var cam = GetViewport().GetCamera2D();
		if (cam != null && cam.Zoom.X < Layer.ParticleHideZoom) return;

		var inv = GetViewport().GetCanvasTransform().AffineInverse();
		var size = GetViewport().GetVisibleRect().Size;
		var view = new Rect2(inv * Vector2.Zero, Vector2.Zero).Expand(inv * size).Grow(Layer.CellSize);

		float r = Layer.OrbitRadius;
		float width = Mathf.Max(1f, Layer.CellSize * 0.05f);
		var colors = Layer.TierPreviewColors;
		foreach (var (center, tier) in Layer.Crossroads())
		{
			if (!view.HasPoint(center)) continue;
			var color = colors != null && tier < colors.Length ? colors[tier] : Colors.White;
			for (int axis = 0; axis < 2; axis++)
			{
				for (int i = 0; i <= Segments / 2; i++)
				{
					float phi = Mathf.Pi * i / (Segments / 2);
					_near[i] = center + RingPoint(axis, phi, r);
					_far[i] = center + RingPoint(axis, phi + Mathf.Pi, r);
				}
				DrawPolyline(_far, new Color(color, 0.35f), width * 0.6f, true);
				DrawPolyline(_near, new Color(color, 0.9f), width, true);
			}
		}
	}
}
