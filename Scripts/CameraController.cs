using Godot;
using System;

// Зум колесом мыши + панорамирование WASD. Стартовые Position/Zoom
// выставляет Main.cs (мир бесконечный — "подгонять под поле" больше не
// применимо). Zoom.X больше -> камера ближе (мир виднее меньше) — это
// подтверждённая семантика Camera2D.Zoom в Godot 4.
public partial class CameraController : Camera2D
{
	[Export] public float ZoomStep = 1.1f;
	[Export] public float MinZoom = 0.004f; // глубоко внутрь слоя 2 (порог слоя — 1/ChunkSize, см. ViewLayer)
	[Export] public float MaxZoom = 1f;
	[Export] public float PanSpeed = 800f;
	// Поле вокруг показываемой области (T012) — доля её размера с каждой стороны.
	[Export] public float ShowMargin = 0.08f;

	private int _chunkSize = 16;

	// Полёт камеры (T012, ShowRect): пока идёт — колесо и WASD игнорируются.
	private bool _flying;
	private float _flightTime, _flightSeconds;
	private Vector2 _fromPos, _toPos;
	private float _fromZoom, _toZoom;
	private Action _onArrived;
	public bool IsFlying => _flying;

	// Игрок сам подвинул камеру WASD (подсказка управления, T012). Сбрасывает
	// тот, кто читает.
	public bool PannedByPlayer { get; set; }

	public override void _Ready()
	{
		// Маркер сборки: если этой строки нет в Output при запуске — значит
		// Godot запустил СТАРУЮ сборку C#-скриптов (ошибка компиляции или
		// сборка не переподхватилась), и весь остальной код в этом файле
		// сейчас не выполняется вообще, вне зависимости от ввода.
		GD.Print("=== CameraController READY (build marker v2) ===");

		// Явно включаем обработку ввода — на случай, если авто-детект
		// переопределённых методов не сработал (например, после hot-reload
		// скрипта в редакторе). Без этого _Input/_UnhandledInput могли не
		// вызываться вовсе, что и объясняет "зум не работает".
		SetProcessInput(true);
		SetProcessUnhandledInput(true);

		var nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
		if (nucleusLayer != null) _chunkSize = nucleusLayer.ChunkSize;
	}

	// _Input вызывается ДО _UnhandledInput и раньше любых GUI-нод —
	// надёжнее для глобального ввода вроде колеса мыши в минимальной сцене.
	public override void _Input(InputEvent @event)
	{
		if (_flying)
		{
			if (@event is InputEventMouseButton wheel && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
				GetViewport().SetInputAsHandled();
			return;
		}
		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp)
			{
				GD.Print("[Camera] wheel up, zoom before=", Zoom.X);
				ApplyZoom(ZoomStep);
				GetViewport().SetInputAsHandled();
			}
			else if (mb.ButtonIndex == MouseButton.WheelDown)
			{
				GD.Print("[Camera] wheel down, zoom before=", Zoom.X);
				ApplyZoom(1f / ZoomStep);
				GetViewport().SetInputAsHandled();
			}
		}
	}

	public override void _Process(double delta)
	{
		// Слой пересчитывается каждый кадр от фактического Zoom — так учтено
		// любое его изменение (колесо, стартовый зум из Main.cs и т.п.).
		ViewLayer.UpdateFromZoom(Zoom.X, _chunkSize);

		if (_flying)
		{
			StepFlight((float)delta);
			return;
		}

		var dir = Vector2.Zero;
		if (Input.IsKeyPressed(Key.W)) dir.Y -= 1;
		if (Input.IsKeyPressed(Key.S)) dir.Y += 1;
		if (Input.IsKeyPressed(Key.A)) dir.X -= 1;
		if (Input.IsKeyPressed(Key.D)) dir.X += 1;
		if (dir != Vector2.Zero)
		{
			// Делим на Zoom.X, чтобы скорость панорамирования была одинаковой
			// в мировых единицах независимо от текущего уровня зума.
			Position += dir.Normalized() * PanSpeed * (float)delta / Zoom.X;
			PannedByPlayer = true;
		}
	}

	// Показать прямоугольник мира целиком (с полем ShowMargin) с учётом размера
	// окна. seconds <= 0 — сразу; иначе плавный полёт (smoothstep: позиция
	// линейно, зум — в логарифмической шкале), по прилёте — onArrived.
	// Новый показ поверх идущего полёта сначала завершает прежний (его onArrived
	// вызывается сразу).
	public void ShowRect(Rect2 world, float seconds = 0f, Action onArrived = null)
	{
		FinishFlight();
		var view = GetViewportRect().Size;
		float w = Mathf.Max(1f, world.Size.X * (1f + 2f * ShowMargin));
		float h = Mathf.Max(1f, world.Size.Y * (1f + 2f * ShowMargin));
		float z = Mathf.Clamp(Mathf.Min(view.X / w, view.Y / h), MinZoom, MaxZoom);
		var center = world.GetCenter();
		if (seconds <= 0f)
		{
			Position = center;
			Zoom = new Vector2(z, z);
			onArrived?.Invoke();
			return;
		}
		_flying = true;
		_flightTime = 0f;
		_flightSeconds = seconds;
		_fromPos = Position;
		_toPos = center;
		_fromZoom = Zoom.X;
		_toZoom = z;
		_onArrived = onArrived;
	}

	// Прервать полёт без onArrived (новая игра, загрузка) — камера остаётся где есть.
	public void CancelFlight()
	{
		_flying = false;
		_onArrived = null;
	}

	private void StepFlight(float delta)
	{
		_flightTime += delta;
		if (_flightTime >= _flightSeconds)
		{
			FinishFlight();
			return;
		}
		float s = Mathf.SmoothStep(0f, 1f, _flightTime / _flightSeconds);
		Position = _fromPos.Lerp(_toPos, s);
		float z = Mathf.Exp(Mathf.Lerp(Mathf.Log(_fromZoom), Mathf.Log(_toZoom), s));
		Zoom = new Vector2(z, z);
	}

	private void FinishFlight()
	{
		if (!_flying) return;
		_flying = false;
		Position = _toPos;
		Zoom = new Vector2(_toZoom, _toZoom);
		var done = _onArrived;
		_onArrived = null;
		done?.Invoke();
	}

	private void ApplyZoom(float factor)
	{
		float z = Mathf.Clamp(Zoom.X * factor, MinZoom, MaxZoom);
		Zoom = new Vector2(z, z);
		GD.Print("[Camera] zoom after=", z);
	}
}
