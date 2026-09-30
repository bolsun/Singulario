using Godot;

// Подсказка управления внизу по центру (T012): «W, A, S, D — перемещение
// камеры» и «G — сетка». Часть исчезает, когда игрок её выполнил: подвинул
// камеру WASD (CameraController.PannedByPlayer) или переключил сетку
// (GridDraw.Shown). Состояние — NucleusLayer.Hints (сохраняется с игрой).
// Строки — через Tr(): ключ — сама русская строка, перевод добавит T013.
// Создаётся FpsLabel (в сцене узла нет).
public partial class HintLabel : VBoxContainer
{
	private NucleusLayer _nucleusLayer;
	private CameraController _camera;
	private Label _cameraLine;
	private Label _gridLine;
	private bool _gridShown;

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		_camera = GetNodeOrNull<CameraController>("/root/Main/Camera2D");
		_gridShown = GridDraw.Shown;
		MouseFilter = MouseFilterEnum.Ignore;
		Alignment = AlignmentMode.End;

		_cameraLine = MakeLine(Tr("W, A, S, D — перемещение камеры"));
		_gridLine = MakeLine(Tr("G — сетка"));

		SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom, LayoutPresetMode.KeepSize);
		GrowHorizontal = GrowDirection.Both;
		GrowVertical = GrowDirection.Begin;
		OffsetBottom = -24f;
	}

	private Label MakeLine(string text)
	{
		var label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		label.AddThemeColorOverride("font_color", Colors.White);
		label.AddThemeColorOverride("font_outline_color", Colors.Black);
		label.AddThemeConstantOverride("outline_size", 4);
		label.AddThemeFontSizeOverride("font_size", 18);
		AddChild(label);
		return label;
	}

	public override void _Process(double delta)
	{
		var hints = _nucleusLayer?.Hints;
		if (hints == null) { Visible = false; return; }

		// Действия считаются всегда: подсказка, сброшенная «Новой игрой»,
		// не должна засчитываться движением камеры до неё.
		bool panned = _camera != null && _camera.PannedByPlayer;
		if (_camera != null) _camera.PannedByPlayer = false;
		bool gridToggled = GridDraw.Shown != _gridShown;
		_gridShown = GridDraw.Shown;

		if (hints.AllDone) { Visible = false; return; }
		if (panned) hints.MarkCamera();
		if (gridToggled) hints.MarkGrid();

		_cameraLine.Visible = !hints.CameraDone;
		_gridLine.Visible = !hints.GridDone;
		Visible = !hints.AllDone;
	}
}
