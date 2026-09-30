// Подсказка управления (T012) — чистые данные без Godot: какие части
// подсказки игрок уже выполнил. Экземпляр — NucleusLayer.Hints, вид — HintLabel.
// При запуске и в старых сохранениях подсказка пройдена; «Новая игра» её сбрасывает.
public sealed class ControlHints
{
	public bool CameraDone { get; private set; } = true; // W, A, S, D — перемещение камеры
	public bool GridDone { get; private set; } = true;   // G — сетка

	public bool AllDone => CameraDone && GridDone;

	public void Reset()
	{
		CameraDone = false;
		GridDone = false;
	}

	public void Set(bool cameraDone, bool gridDone)
	{
		CameraDone = cameraDone;
		GridDone = gridDone;
	}

	public void MarkCamera() => CameraDone = true;
	public void MarkGrid() => GridDone = true;
}
