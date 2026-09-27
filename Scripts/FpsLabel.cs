using Godot;

// Счётчик FPS + UPS (реальная частота SimTick) + сколько чанков и ядер
// реально рисуется/симулируется прямо сейчас — чтобы наглядно видеть, что
// culling по чанкам работает (visible должно быть заметно меньше total при
// близком зуме), и что тики симуляции не начинают "тормозить" отдельно от FPS.
public partial class FpsLabel : Label
{
	private NucleusLayer _nucleusLayer;

	public override void _Ready()
	{
		AddThemeColorOverride("font_color", Colors.White);
		AddThemeColorOverride("font_outline_color", Colors.Black);
		AddThemeConstantOverride("outline_size", 4);

		// Абсолютный путь от корня сцены — не зависит от того, где именно
		// в дереве лежит сам HUD/Label.
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
	}

	public override void _Process(double delta)
	{
		string extraInfo = _nucleusLayer != null
			? $"\nUPS: {_nucleusLayer.CurrentUPS:0}"
			+ $"\nChunks: {_nucleusLayer.VisibleChunkCount}/{_nucleusLayer.TotalChunkCount}"
			+ $"\nЯдра: {_nucleusLayer.ActiveNucleusCount}"
			: "";
		Text = $"FPS: {Engine.GetFramesPerSecond()}{extraInfo}";
	}
}
