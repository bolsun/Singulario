using Godot;

// Вид удаления ПКМ с удержанием (T026): круг прогресса у курсора, рамка цели,
// вспышка отказа. Только вид; логика — RemoveHold, ввод — NucleusLayer.
// Узел создаёт NucleusLayer из кода. ProcessMode.Always — вспышка и круг не
// зависят от паузы меню.
public partial class RemoveHoldLayer : Node2D
{
	private static readonly Color RingBack = new Color("#120e1d");
	private static readonly Color Gray3 = new Color("#cfcde0");
	private static readonly Color DenyColor = new Color(1f, 0.2f, 0.2f);
	private const float FlashSeconds = 0.3f;
	private const float RingOffsetPx = 14f;
	private const float BackWidthPx = 5f;
	private const float ArcWidthPx = 3f;
	private const float FrameWidthPx = 2f;
	private const int Segments = 48;

	public NucleusLayer Layer;
	// Диаметр круга в пикселях экрана.
	public float DiameterPx = 22f;

	private RemoveTarget? _target;
	private float _progress;
	private RemoveTarget _flashTarget;
	private float _flash;

	public override void _Ready()
	{
		ZIndex = 60;
		ProcessMode = ProcessModeEnum.Always;
	}

	// target == null — удержания нет, ничего не рисуется.
	public void SetHold(RemoveTarget? target, float progress)
	{
		// Круг идёт за курсором — пока удержание идёт, перерисовка каждый кадр.
		bool changed = _target != target || !Mathf.IsEqualApprox(_progress, progress);
		_target = target;
		_progress = progress;
		if (changed || target != null) QueueRedraw();
	}

	public void FlashDenied(int row, int col, int side)
	{
		_flashTarget = new RemoveTarget(0, row, col, side);
		_flash = FlashSeconds;
		QueueRedraw();
	}

	public override void _Process(double delta)
	{
		if (_flash <= 0f) return;
		_flash -= (float)delta;
		QueueRedraw();
	}

	private Rect2 Footprint(RemoveTarget t)
	{
		float cs = Layer.CellSize;
		return new Rect2(t.Col * cs, t.Row * cs, t.Side * cs, t.Side * cs);
	}

	public override void _Draw()
	{
		var cam = GetViewport().GetCamera2D();
		if (cam == null || Layer == null) return;
		float px = 1f / cam.Zoom.X; // один пиксель экрана в мировых единицах

		if (_flash > 0f)
		{
			float k = Mathf.Clamp(_flash / FlashSeconds, 0f, 1f);
			var r = Footprint(_flashTarget);
			DrawRect(r, new Color(DenyColor, 0.2f * k), true);
			DrawRect(r, new Color(DenyColor, 0.8f * k), false, FrameWidthPx * px);
		}

		if (_target is RemoveTarget t && _progress > 0f)
		{
			DrawRect(Footprint(t), new Color(Gray3, 0.6f), false, FrameWidthPx * px);

			var center = GetGlobalMousePosition() + new Vector2(RingOffsetPx, RingOffsetPx) * px;
			float radius = (DiameterPx * 0.5f - BackWidthPx * 0.5f) * px;
			DrawArc(center, radius, 0f, Mathf.Tau, Segments, RingBack, BackWidthPx * px, false);
			float start = -Mathf.Pi / 2f;
			DrawArc(center, radius, start, start + Mathf.Tau * Mathf.Clamp(_progress, 0f, 1f),
				Segments, Gray3, ArcWidthPx * px, false);
		}
	}
}
