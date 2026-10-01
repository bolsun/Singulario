using Godot;

// Вид перекрёстка (T010, T029): атом «орбитами ребром». Здесь — дорожка «+» (от края тела
// до края дырки, поверх тела не идёт) и шевроны на месте скрытых дырок выхода занятых осей.
// Дырки и частицы в пути пишет NucleusLayer (UpdateCrossroadVisuals). Только отрисовка.
// Узел создаёт NucleusLayer в _Ready.
public partial class CrossroadLayer : Node2D
{
	[Export] public Color TrackColor = new Color("372d4d");
	[Export] public Color ChevronColor = new Color("4d4268");
	[Export] public float TrackWidth = 2f;
	[Export] public float ChevronHeight = 10f;
	[Export] public float ChevronHalfWidth = 10f;

	public NucleusLayer Layer;

	public override void _Ready()
	{
		ZIndex = 5; // поверх тел атомов и частиц, под туманом территории
		Layer ??= GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
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
		if (cam != null && (cam.Zoom.X < Layer.HoleHideZoom || cam.Zoom.X < Layer.AtomDotZoom)) return;

		var inv = GetViewport().GetCanvasTransform().AffineInverse();
		var size = GetViewport().GetVisibleRect().Size;
		var view = new Rect2(inv * Vector2.Zero, Vector2.Zero).Expand(inv * size).Grow(Layer.CellSize);

		float r = Layer.OrbitRadius;
		float from = Layer.BodyRadius;
		float to = r - Layer.HoleRadius;
		foreach (var (center, exitMask) in Layer.Crossroads())
		{
			if (!view.HasPoint(center)) continue;
			for (int side = 0; side < 4; side++)
			{
				var dir = NucleusLayer.SideDir(side);
				bool exit = (exitMask & (1 << side)) != 0;
				// У шеврона дорожка идёт до его центра, у дырки — до её края.
				float end = exit ? r : to;
				if (end > from)
					DrawLine(center + dir * from, center + dir * end, TrackColor, TrackWidth, false);
				if (exit) DrawChevron(center + dir * r, dir);
			}
		}
	}

	// Ломаная из 3 точек, остриём наружу (как StarLayer.DrawOutputChevron).
	private void DrawChevron(Vector2 at, Vector2 dir)
	{
		var perp = new Vector2(-dir.Y, dir.X);
		var tip = at + dir * (ChevronHeight * 0.5f);
		var back = at - dir * (ChevronHeight * 0.5f);
		DrawPolyline(new[] { back + perp * ChevronHalfWidth, tip, back - perp * ChevronHalfWidth }, ChevronColor, TrackWidth, false);
	}
}
