using Godot;

// Вид удаления ПКМ с удержанием (T026): полоска прогресса над следом цели, рамка цели,
// вспышка отказа. Только вид; логика — RemoveHold, ввод — NucleusLayer.
// Узел создаёт NucleusLayer из кода. ProcessMode.Always — вспышка и полоска не
// зависят от паузы меню.
public partial class RemoveHoldLayer : Node2D
{
	private static readonly Color BarBack = new Color("#0a0812");
	private static readonly Color Gray3 = new Color("#cfcde0");
	private static readonly Color DenyColor = new Color(1f, 0.2f, 0.2f);
	private const float FlashSeconds = 0.3f;
	private const float FrameWidthPx = 2f;
	private const float BarGapPx = 3f;

	public NucleusLayer Layer;
	// Толщина полоски в пикселях экрана.
	public float BarThicknessPx = 4f;

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
		bool changed = _target != target || !Mathf.IsEqualApprox(_progress, progress);
		_target = target;
		_progress = progress;
		if (changed) QueueRedraw();
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

			var fp = Footprint(t);
			float th = BarThicknessPx * px;
			var bar = new Rect2(fp.Position.X, fp.Position.Y - BarGapPx * px - th, fp.Size.X, th);
			DrawRect(bar.Grow(px), BarBack, true);
			DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * Mathf.Clamp(_progress, 0f, 1f), th)), Colors.White, true);
		}
	}
}
