using Godot;

// Зум колесом мыши + панорамирование WASD. Стартовые Position/Zoom
// выставляет Main.cs (мир бесконечный — "подгонять под поле" больше не
// применимо). Zoom.X больше -> камера ближе (мир виднее меньше) — это
// подтверждённая семантика Camera2D.Zoom в Godot 4.
public partial class CameraController : Camera2D
{
	[Export] public float ZoomStep = 1.1f;
	[Export] public float MinZoom = 0.02f;
	[Export] public float MaxZoom = 1f;
	[Export] public float PanSpeed = 800f;

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
	}

	// _Input вызывается ДО _UnhandledInput и раньше любых GUI-нод —
	// надёжнее для глобального ввода вроде колеса мыши в минимальной сцене.
	public override void _Input(InputEvent @event)
	{
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
		}
	}

	private void ApplyZoom(float factor)
	{
		float z = Mathf.Clamp(Zoom.X * factor, MinZoom, MaxZoom);
		Zoom = new Vector2(z, z);
		GD.Print("[Camera] zoom after=", z);
	}
}
