using System;
using System.Collections.Generic;
using Godot;
using Singulario.Core;

namespace Singulario.Views;

// One instance per Star. Draws the star's body (scaled to its Diameter)
// plus a "ready items" orbit: a Fisher-Yates-shuffled set of fixed points
// around the star, re-rolled whenever ReadyCount changes -- mirroring the
// HTML prototype's random repositioning rather than a continuous rotation.
public partial class StarView : Node2D
{
	public Star Model;

	static readonly Random _rng = new Random();

	Vector2[] _readyItemOffsets;
	int _lastReadyCount = -1;

	public void Refresh()
	{
		if (Model != null && Model.ReadyCount != _lastReadyCount)
		{
			RebuildOrbit();
			_lastReadyCount = Model.ReadyCount;
		}
		QueueRedraw();
	}

	void RebuildOrbit()
	{
		int phaseCount = Math.Max(8, 8 * Model.Diameter);
		var indices = new List<int>(phaseCount);
		for (int i = 0; i < phaseCount; i++) indices.Add(i);
		for (int i = indices.Count - 1; i > 0; i--)
		{
			int j = _rng.Next(i + 1);
			(indices[i], indices[j]) = (indices[j], indices[i]);
		}

		int count = Math.Min(Model.ReadyCount, phaseCount);
		_readyItemOffsets = new Vector2[count];
		float orbitRadius = GridView.CellSize * (0.55f * Model.Diameter + 0.35f);
		for (int i = 0; i < count; i++)
		{
			float angle = indices[i] * (Mathf.Tau / phaseCount);
			_readyItemOffsets[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * orbitRadius;
		}
	}

	public override void _Draw()
	{
		if (Model == null) return;

		float bodyRadius = GridView.CellSize * 0.42f * Model.Diameter;
		Color color = StarColor(Model.Color);
		DrawCircle(Vector2.Zero, bodyRadius, color);
		DrawArc(Vector2.Zero, bodyRadius, 0f, Mathf.Tau, 24, color.Lightened(0.3f), 2f);

		if (_readyItemOffsets != null)
		{
			foreach (var offset in _readyItemOffsets)
				DrawCircle(offset, GridView.CellSize * 0.12f, color.Lightened(0.2f));
		}
	}

	static Color StarColor(string color) => color switch
	{
		"blue" => new Color(0.25f, 0.55f, 0.95f),
		_ => new Color(0.8f, 0.8f, 0.3f),
	};
}
