using System;

// Удаление ПКМ с удержанием (T026) — чистая логика без Godot. Время удержания =
// секундНаКлетку × сторона следа объекта; сторона 1 — удаляется сразу. Таймер —
// реальное время ввода (секунды кадра), в симуляцию и StateHash не входит.
public enum RemoveHoldResult { None, Progress, Remove }

// Ключ цели: вид объекта и верхняя левая клетка следа, сторона следа в клетках.
public readonly record struct RemoveTarget(int Kind, int Row, int Col, int Side);

public sealed class RemoveHold
{
	private RemoveTarget? _target;
	private float _elapsed;

	public RemoveTarget? Target => _target;
	// 0..1; 0 — удержания нет.
	public float Progress { get; private set; }

	public void Reset()
	{
		_target = null;
		_elapsed = 0f;
		Progress = 0f;
	}

	// target == null — под курсором нечего удалять (сброс). Смена цели — с нуля.
	// После Remove состояние сбрасывается.
	public RemoveHoldResult Update(RemoveTarget? target, float dt, float secondsPerCell)
	{
		if (target == null) { Reset(); return RemoveHoldResult.None; }
		var t = target.Value;
		if (t.Side <= 1) { Reset(); return RemoveHoldResult.Remove; }
		if (_target != t) { _target = t; _elapsed = 0f; }
		_elapsed += Math.Max(0f, dt);
		float need = secondsPerCell * t.Side;
		if (_elapsed >= need) { Reset(); return RemoveHoldResult.Remove; }
		Progress = need > 0f ? _elapsed / need : 1f;
		return RemoveHoldResult.Progress;
	}
}
