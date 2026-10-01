// Подсказка управления (T012) — чистые данные без Godot: какие части
// подсказки игрок уже выполнил. Экземпляр — NucleusLayer.Hints, вид — HintLabel.
// При запуске и в старых сохранениях подсказка пройдена; «Новая игра» её сбрасывает.
public sealed class ControlHints
{
	public bool CameraDone { get; private set; } = true; // W, A, S, D — перемещение камеры
	public bool GridDone { get; private set; } = true;   // G — сетка
	public bool ClearDone { get; private set; } = true;  // C — очистка (T035)

	public bool AllDone => CameraDone && GridDone && ClearDone;

	public void Reset()
	{
		CameraDone = false;
		GridDone = false;
		ClearDone = false;
	}

	public void Set(bool cameraDone, bool gridDone, bool clearDone)
	{
		CameraDone = cameraDone;
		GridDone = gridDone;
		ClearDone = clearDone;
	}

	public void MarkCamera() => CameraDone = true;
	public void MarkGrid() => GridDone = true;
	public void MarkClear() => ClearDone = true;
}
