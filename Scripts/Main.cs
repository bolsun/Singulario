using Godot;

// Мир теперь бесконечный (см. NucleusLayer) — "подгонять камеру под размер
// поля" больше не имеет смысла, поля целиком просто не существует. Вместо
// этого — разумная стартовая точка обзора: центр мира (0,0), зум чуть
// отдалённый от максимума, чтобы сразу увидеть заметный кусок космоса, а не
// одну клетку. Дальше игрок сам крутит колесо/WASD (см. CameraController).
public partial class Main : Node2D
{
	[Export] public float InitialZoom = 0.15f;

	public override void _Ready()
	{
		ViewLayer.Reset();
		// Настройки (T013): язык и полный экран — до первого кадра.
		Settings.Load();
		Settings.Apply();
		var camera = GetNode<CameraController>("Camera2D");
		camera.Position = Vector2.Zero;
		camera.Zoom = new Vector2(InitialZoom, InitialZoom);
		// Главное меню (T013) — поверх всего, при запуске ставит игру на паузу.
		AddChild(new GameMenu { Name = "GameMenu" });
	}
}
