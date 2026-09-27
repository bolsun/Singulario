using Godot;
using System.Collections.Generic;

// Наведение мышью на клетку со скоплением частиц (EnergyClusterLayer) —
// показывает внизу слева (см. ClusterHoverLabel), сколько частиц осталось в
// ОБЩЕМ пуле кластера, которому принадлежит клетка под курсором, и сколько
// клеток сейчас в этом кластере.
//
// Вычисление мировой позиции курсора нарочно вынесено в отдельный узел
// Node2D (этот скрипт), а не сделано прямо в самом Label: Label лежит внутри
// CanvasLayer HUD, а Control.GetGlobalMousePosition() там не то же самое,
// что у Node2D-версии — CanvasLayer по умолчанию не учитывает трансформацию
// Camera2D (зум/панорамирование), поэтому курсор в координатах Control не
// совпадёт с нужной клеткой поля при отличном от 1:1 зуме/сдвиге камеры.
// Node2D.GetGlobalMousePosition() (тот же приём, которым уже пользуется
// EnergyClusterLayer при установке по клику) даёт корректную мировую
// позицию с учётом активной камеры.
public partial class ClusterHoverProbe : Node2D
{
	private Label _label;
	private int _cellSize;
	private readonly List<EnergyClusterLayer> _layers = new();

	public override void _Ready()
	{
		var gridDraw = GetNode<GridDraw>("../GridLayer");
		_cellSize = gridDraw.CellSize;

		_label = GetNodeOrNull<Label>("/root/Main/HUD/ClusterHoverLabel");

		var main = GetNodeOrNull<Node>("/root/Main");
		if (main != null)
		{
			foreach (var child in main.GetChildren())
				if (child is EnergyClusterLayer layer)
					_layers.Add(layer);
		}

		if (_label == null)
			GD.PrintErr("[ClusterHoverProbe] не найден ClusterHoverLabel — подсказка при наведении не будет показываться.");

		SetProcess(_label != null && _layers.Count > 0);
	}

	public override void _Process(double delta)
	{
		if (_label == null) return;

		// На слое 2 источники слоя 1 скрыты — подсказку по ним не показываем.
		if (ViewLayer.IsLayer2)
		{
			_label.Visible = false;
			return;
		}

		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / _cellSize);
		int row = Mathf.FloorToInt(worldPos.Y / _cellSize);

		foreach (var layer in _layers)
		{
			if (!layer.HasClusterAt(row, col)) continue;

			_label.Text = $"Кластер ({TierLabel(layer.Tier)}): частиц {layer.AmountAt(row, col)}, клеток {layer.CellCountAt(row, col)}";
			_label.Visible = true;
			return;
		}

		_label.Visible = false;
	}

	// Та же однобуквенная маркировка тира, что и на кнопках спавн-панели (см.
	// NucleusSpawnPanel.ClusterTierLabel) — держим независимую копию, чтобы
	// не тянуть сюда зависимость от Control-панели ради одной функции.
	private static string TierLabel(int tier) => tier switch
	{
		0 => "Ж",
		1 => "К",
		2 => "С",
		_ => tier.ToString(),
	};
}
