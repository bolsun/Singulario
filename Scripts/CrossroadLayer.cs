using Godot;

// Вид перекрёстка (T010, T029, T032, T033, T037): атом «орбитами ребром». Здесь — шевроны на обоих
// выходах (всегда; выходы постоянные, маска Crossroad.OutputMask): между телом и дыркой, цвет — линия
// за выходом (T040: сосед Ж/К/С — тон 3 его тира, иначе NeutralChevronColor), при заторе (выход забит
// не меньше периода P) пульсируют красным, как у звезды.
// На двух входах — серые шевроны остриём внутрь (InputChevronColor, без тира и пульса).
// Дырки на всех 4 портах и частицы пишет NucleusLayer (UpdateCrossroadVisuals). Дорожки нет.
// Только отрисовка.
// Узел создаёт NucleusLayer в _Ready.
public partial class CrossroadLayer : Node2D
{
	// Цвет шеврона выхода по тиру соседа (Ж, К, С, серое): тон 3 рампы тира.
	[Export] public Color[] ChevronTierColors =
	{
		new Color("ffc93c"), new Color("ff6e5e"), new Color("69b8ff"), new Color("cfcde0"),
	};
	// Шеврон выхода, когда сосед не цветной (пусто, серый, звезда, ЧД, обломок, закрытая клетка): тон 3 серого.
	[Export] public Color NeutralChevronColor = new Color("cfcde0");
	// Цвет шеврона входа (остриём к телу): тон дырки; запасной, если не читается, — 4d4268.
	[Export] public Color InputChevronColor = new Color("372d4d");
	[Export] public float ChevronLineWidth = 2f;
	[Export] public float ChevronHeight = 8f;
	[Export] public float ChevronHalfWidth = 8f;

	public NucleusLayer Layer;
	private StarLayer _starLayer;

	public override void _Ready()
	{
		ZIndex = 5; // поверх тел атомов и частиц, под туманом территории
		Layer ??= GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		_starLayer = GetNodeOrNull<StarLayer>("../StarLayer");
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

		// Центр шеврона — посередине между краем тела и краем дырки выхода.
		float at = (Layer.BodyRadius + Layer.OrbitRadius - Layer.HoleRadius) * 0.5f;
		int step = System.Math.Max(1, _starLayer?.ChevronStepTicks ?? 24);
		var pulse = StarLayer.ChevronPulse[(int)(Layer.GlobalTick / step % StarLayer.ChevronPulse.Length)];
		foreach (var (center, exitMask, blockedMask, lineTiers) in Layer.Crossroads())
		{
			if (!view.HasPoint(center)) continue;
			for (int side = 0; side < 4; side++)
			{
				if ((exitMask & (1 << side)) == 0) continue;
				var dir = NucleusLayer.SideDir(side);
				var color = (blockedMask & (1 << side)) != 0 ? pulse : LineColor(NucleusLayer.LineTierOf(lineTiers, side));
				DrawChevron(center + dir * at, dir, color);
			}
			int inputMask = ~exitMask & 0xF;
			for (int side = 0; side < 4; side++)
			{
				if ((inputMask & (1 << side)) == 0) continue;
				var dir = NucleusLayer.SideDir(side);
				DrawChevron(center + dir * at, -dir, InputChevronColor);
			}
		}
	}

	private Color LineColor(int tier) =>
		tier == NucleusLayer.NeutralLineTier || tier < 0 || tier >= ChevronTierColors.Length
			? NeutralChevronColor
			: ChevronTierColors[tier];

	// Ломаная из 3 точек, остриём по dir (как StarLayer.DrawOutputChevron).
	private void DrawChevron(Vector2 at, Vector2 dir, Color color)
	{
		var perp = new Vector2(-dir.Y, dir.X);
		var tip = at + dir * (ChevronHeight * 0.5f);
		var back = at - dir * (ChevronHeight * 0.5f);
		DrawPolyline(new[] { back + perp * ChevronHalfWidth, tip, back - perp * ChevronHalfWidth }, color, ChevronLineWidth, false);
	}
}
