using System;

// Текущий слой игры (1 — инженерия внутри чанка, 2 — логистика, клетка =
// чанк) — единственное место истины для всех узлов. Переключается только по
// зуму камеры (см. CameraController._Process → UpdateFromZoom): слой 2, когда
// чанк на экране не больше одной клетки слоя 1 при зуме 1, то есть
// Zoom <= 1/ChunkSize. Гистерезис (ExitFactor) — чтобы на самой границе слой
// не мигал туда-обратно при каждом шаге колеса.
//
// Узлы, подписавшиеся на Changed, обязаны отписаться в _ExitTree — событие
// статическое и переживает перезагрузку сцены.
public static class ViewLayer
{
	public const float ExitFactor = 1.05f;

	public static int Current { get; private set; } = 1;
	public static bool IsLayer2 => Current == 2;

	public static event Action<int> Changed;

	public static void UpdateFromZoom(float zoom, int chunkSize)
	{
		if (chunkSize <= 0) return;
		float enter = 1f / chunkSize;
		float exit = enter * ExitFactor;

		int next = Current;
		if (Current == 1 && zoom <= enter) next = 2;
		else if (Current == 2 && zoom >= exit) next = 1;

		if (next == Current) return;
		Current = next;
		Changed?.Invoke(next);
	}

	// Для старта сцены — сбросить состояние, оставшееся от предыдущего запуска
	// сцены в том же процессе (статика не выгружается вместе со сценой).
	public static void Reset()
	{
		Current = 1;
	}
}
