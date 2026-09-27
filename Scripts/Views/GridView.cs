using Godot;
using Singulario.Core;

namespace Singulario.Views;

// Draws the terrain layer: grid lines plus a per-cell tint for sources
// (mixed toward gray as they deplete), black holes, and the singularity.
// Stars and nuclei are drawn by their own view classes on top of this.
public partial class GridView : Node2D
{
	// Shared by every view class in this folder -- one place controls the
	// whole game's pixel scale.
	public const float CellSize = 32f;

	GameWorld _world;

	static readonly Color GridLineColor = new Color(1f, 1f, 1f, 0.08f);
	static readonly Color EmptyCellColor = new Color(0.12f, 0.12f, 0.14f);
	static readonly Color BlackholeColor = new Color(0.04f, 0.04f, 0.05f);
	static readonly Color SingularityColor = new Color(0.55f, 0.15f, 0.75f);
	static readonly Color SourceYellow = new Color(0.95f, 0.85f, 0.2f);
	static readonly Color SourceBlue = new Color(0.25f, 0.55f, 0.95f);

	public void SetWorld(GameWorld world)
	{
		_world = world;
		Refresh();
	}

	public void Refresh() => QueueRedraw();

	public override void _Draw()
	{
		if (_world == null) return;

		for (int r = 0; r < _world.Rows; r++)
		{
			for (int c = 0; c < _world.Cols; c++)
			{
				var cell = _world.Grid[r, c];
				var topLeft = new Vector2(c * CellSize, r * CellSize);
				Color fill = EmptyCellColor;

				if (cell.IsSingularity)
				{
					fill = SingularityColor;
				}
				else if (cell.IsBlackhole)
				{
					fill = BlackholeColor;
				}
				else if (cell.Source != null)
				{
					Color baseColor = SourceColor(cell.Source.Color);
					float frac = cell.Source.Total > 0f
						? Mathf.Clamp(cell.Source.Remaining / cell.Source.Total, 0f, 1f)
						: 0f;
					fill = EmptyCellColor.Lerp(baseColor, frac);
				}

				DrawRect(new Rect2(topLeft, new Vector2(CellSize, CellSize)), fill, true);
			}
		}

		float width = _world.Cols * CellSize;
		float height = _world.Rows * CellSize;
		for (int r = 0; r <= _world.Rows; r++)
			DrawLine(new Vector2(0, r * CellSize), new Vector2(width, r * CellSize), GridLineColor);
		for (int c = 0; c <= _world.Cols; c++)
			DrawLine(new Vector2(c * CellSize, 0), new Vector2(c * CellSize, height), GridLineColor);
	}

	static Color SourceColor(string color) => color switch
	{
		"yellow" => SourceYellow,
		"blue" => SourceBlue,
		_ => new Color(0.6f, 0.6f, 0.6f),
	};
}
