using Godot;

// Панель "Новая игра" (T009, NucleusLayer.NewGame — с подтверждением) и
// "Экспорт"/"Импорт" всего поля (ядра, источники, порты, ЧД, молекулы — см.
// NucleusLayer.ExportFieldJson/ImportFieldJson) в виде JSON-строки. Построена
// программно в _Ready, тем же приёмом, что и NucleusSpawnPanel (см. её шапку)
// — никаких дочерних узлов в самой сцене не нужно, кроме этого Control.
//
// Обе кнопки открывают один и тот же диалог с многострочным текстовым полем:
//   Экспорт — считает JSON прямо сейчас, кладёт его в поле (для копирования
//             пользователем) и запоминает как "последний экспортированный" —
//             и в памяти, и на диске (user://, см. LastExportPath), чтобы
//             Импорт мог сразу предложить его же, даже в следующем запуске
//             игры (по заданию клиента: "запоминай последнее сохранённое и
//             сразу вставляй его в поле импорта").
//   Импорт  — открывает тот же диалог, СРАЗУ подставив в поле последнюю
//             запомненную строку (см. выше), а по подтверждению (OK) разбирает
//             ТЕКУЩЕЕ содержимое поля (можно вставить свою строку вместо
//             предложенной) и полностью пересобирает поле из него.
public partial class SaveLoadPanel : Control
{
	private const string LastExportPath = "user://last_field_export.json";

	private NucleusLayer _nucleusLayer;
	private AcceptDialog _dialog;
	private TextEdit _textEdit;
	private Label _statusLabel;
	private ConfirmationDialog _newGameDialog;

	// true — диалог сейчас открыт в режиме "Импорт" (OK должен разобрать
	// содержимое поля и пересобрать поле), false — в режиме "Экспорт" (OK
	// просто закрывает диалог, само сохранение уже сделано при открытии).
	private bool _importMode;

	// Последняя посчитанная строка экспорта — источник правды для поля Импорта
	// при его открытии. Загружается с диска в _Ready (см. LoadLastExportedFromDisk),
	// чтобы Импорт предлагал что-то осмысленное, даже если Экспорт в этом
	// запуске игры ещё ни разу не нажимали.
	private string _lastExportedJson = "";

	public override void _Ready()
	{
		// Абсолютный путь от корня сцены — тот же приём, что и у FpsLabel/
		// NucleusSpawnPanel, не зависит от места этого узла в дереве.
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		_lastExportedJson = LoadLastExportedFromDisk();

		// BottomRight — угловой (точечный) анкор, тем же приёмом, что и у
		// NucleusSpawnPanel (TopRight)/ClusterHoverLabel (BottomLeft) — Offset*
		// отсчитываются от этой точки, а не от левого верхнего угла экрана.
		// Раньше панель стояла в TopLeft сразу под FpsLabel (offset_top=72), но
		// FpsLabel — обычный Label БЕЗ обрезки текста по границам своего Rect
		// (см. FpsLabel.cs: реально рисует 4 строки — FPS/UPS/Chunks/Ядра,
		// заметно выше объявленных в сцене offset_top..offset_bottom=8..64) —
		// поэтому кнопки перекрывали настоящий, а не заявленный, размер этой
		// панели. Свободный от всех остальных HUD-элементов угол — правый
		// нижний, поэтому переехали туда целиком, а не стали подбирать точный
		// отступ под текущую высоту FpsLabel (она может снова вырасти, если
		// добавят ещё строку статистики).
		SetAnchorsPreset(LayoutPreset.BottomRight);
		const int margin = 16;
		const int width = 354;
		const int height = 40;
		OffsetRight = -margin;
		OffsetBottom = -margin;
		OffsetLeft = -margin - width;
		OffsetTop = -margin - height;
		// Тот же защитный приём, что и у NucleusSpawnPanel (см. её комментарий
		// у MouseFilter в _Ready) — этот Control сам по себе не должен ловить
		// клики по игровому полю там, где нет кнопки.
		MouseFilter = MouseFilterEnum.Ignore;

		var box = new HBoxContainer();
		AddChild(box);

		var newGameButton = new Button { Text = "Новая игра", CustomMinimumSize = new Vector2(110, 32) };
		newGameButton.Pressed += OnNewGamePressed;
		box.AddChild(newGameButton);

		var exportButton = new Button { Text = "Экспорт", CustomMinimumSize = new Vector2(110, 32) };
		exportButton.Pressed += OnExportPressed;
		box.AddChild(exportButton);

		var importButton = new Button { Text = "Импорт", CustomMinimumSize = new Vector2(110, 32) };
		importButton.Pressed += OnImportPressed;
		box.AddChild(importButton);

		BuildDialog();

		_newGameDialog = new ConfirmationDialog
		{
			Title = "Новая игра",
			DialogText = "Начать новую игру? Текущее поле и инвентарь будут очищены.",
		};
		_newGameDialog.Confirmed += OnNewGameConfirmed;
		AddChild(_newGameDialog);
	}

	private void OnNewGamePressed()
	{
		if (_nucleusLayer == null)
		{
			GD.PrintErr("[SaveLoadPanel] не найден NucleusLayer — новая игра недоступна.");
			return;
		}
		_newGameDialog.PopupCentered();
	}

	// Ошибка (нет шаблона стартовой зоны) — в лог и в диалог; поле не тронуто.
	private void OnNewGameConfirmed()
	{
		if (_nucleusLayer.NewGame(out string error)) return;
		_importMode = false;
		_dialog.Title = "Новая игра не начата";
		_statusLabel.Text = $"Ошибка: {error}";
		_textEdit.Text = "";
		_dialog.PopupCentered();
	}

	// Один общий диалог на обе кнопки (см. шапку файла) — разница между
	// режимами живёт только в _importMode и в том, что именно делает OnDialogConfirmed.
	private void BuildDialog()
	{
		_dialog = new AcceptDialog
		{
			Title = "Поле — JSON",
			Size = new Vector2I(560, 440),
		};
		AddChild(_dialog);

		var vbox = new VBoxContainer();
		vbox.SetAnchorsPreset(LayoutPreset.FullRect);
		vbox.OffsetLeft = 8;
		vbox.OffsetTop = 8;
		vbox.OffsetRight = -8;
		vbox.OffsetBottom = -8;
		_dialog.AddChild(vbox);

		_statusLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		vbox.AddChild(_statusLabel);

		_textEdit = new TextEdit
		{
			CustomMinimumSize = new Vector2(540, 360),
			WrapMode = TextEdit.LineWrappingMode.Boundary,
			SizeFlagsVertical = SizeFlags.Fill | SizeFlags.Expand,
		};
		vbox.AddChild(_textEdit);

		_dialog.Confirmed += OnDialogConfirmed;
	}

	private void OnExportPressed()
	{
		if (_nucleusLayer == null)
		{
			GD.PrintErr("[SaveLoadPanel] не найден NucleusLayer — экспорт недоступен.");
			return;
		}

		_importMode = false;
		string json = _nucleusLayer.ExportFieldJson();
		_lastExportedJson = json;
		SaveLastExportedToDisk(json);

		_dialog.Title = "Экспорт поля — скопируйте строку ниже";
		_statusLabel.Text = "Поле сохранено в JSON. Выделите текст (Ctrl+A) и скопируйте (Ctrl+C).";
		_textEdit.Text = json;
		_dialog.PopupCentered();
	}

	private void OnImportPressed()
	{
		if (_nucleusLayer == null)
		{
			GD.PrintErr("[SaveLoadPanel] не найден NucleusLayer — импорт недоступен.");
			return;
		}

		_importMode = true;
		_dialog.Title = "Импорт поля — вставьте JSON и нажмите OK";
		_statusLabel.Text = "Подставлена последняя сохранённая строка — при необходимости замените своей, затем нажмите OK.";
		_textEdit.Text = _lastExportedJson;
		_dialog.PopupCentered();
	}

	private void OnDialogConfirmed()
	{
		// Экспорт — OK просто закрывает диалог, само сохранение уже сделано в
		// OnExportPressed до открытия диалога, разбирать поле тут не нужно.
		if (!_importMode) return;

		string json = _textEdit.Text;
		bool ok = _nucleusLayer.ImportFieldJson(json, out string error);
		if (!ok)
		{
			GD.PrintErr($"[SaveLoadPanel] импорт не удался: {error}");
			// Открываем диалог заново с сообщением об ошибке, не теряя введённый
			// пользователем текст — проще поправить и повторить.
			_statusLabel.Text = $"Ошибка импорта: {error}";
			_dialog.PopupCentered();
			return;
		}

		GD.Print("[SaveLoadPanel] поле успешно загружено из JSON.");
	}

	private static void SaveLastExportedToDisk(string json)
	{
		using var file = FileAccess.Open(LastExportPath, FileAccess.ModeFlags.Write);
		if (file == null)
		{
			GD.PrintErr($"[SaveLoadPanel] не удалось сохранить {LastExportPath}: {FileAccess.GetOpenError()}");
			return;
		}
		file.StoreString(json);
	}

	private static string LoadLastExportedFromDisk()
	{
		if (!FileAccess.FileExists(LastExportPath)) return "";
		using var file = FileAccess.Open(LastExportPath, FileAccess.ModeFlags.Read);
		if (file == null) return "";
		return file.GetAsText();
	}
}
