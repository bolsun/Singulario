using Godot;

// Главное меню и меню паузы (T013). Создаётся Main (в сцене узла нет), рисуется
// поверх всего и работает при паузе дерева (ProcessMode.Always).
//
// Режимы:
//   Main  — при запуске: «Продолжить» (если есть автосохранение — загружает его),
//           «Новая игра» (при существующем сохранении — с подтверждением);
//           Esc ничего не делает.
//   Pause — Esc в игре: «Продолжить» (или Esc) закрывает меню, «Новая игра» —
//           с подтверждением.
// Пока меню открыто, дерево на паузе (GetTree().Paused): симуляция, камера и ввод
// поля стоят. Пауза по пробелу (NucleusLayer) — отдельная, меню её не трогает:
// игра, стоявшая на паузе до меню, после закрытия остаётся на паузе.
//
// Автосохранение (AutosavePath, тот же JSON, что у экспорта): при открытии меню
// паузы, при выходе (кнопка и закрытие окна) и раз в Settings.AutosaveMinutes
// минут игры. Пишется только после того, как в этом запуске началась игра
// («Новая игра», «Продолжить» или импорт — MarkGameActive), чтобы стартовое
// поле не затёрло сохранение.
//
// Тексты — ключи перевода (русская строка): кнопки, подписи и диалоги Godot
// переводит сам и сразу перепереводит при смене языка.
public partial class GameMenu : CanvasLayer
{
	public const string AutosavePath = "user://autosave.json";
	private const string AutosaveTempPath = "user://autosave.tmp";

	// Подсказка управления в меню — ключи перевода, по строке на Label.
	private static readonly string[] ControlLines =
	{
		"W, A, S, D — перемещение камеры",
		"Колесо мыши — масштаб",
		"G — сетка",
		"ЛКМ — поставить или заменить, ПКМ — убрать",
		"Q — взять атом как образец; Q по пустому — снять инструмент",
		"R — направление вращения атома, выход звезды",
		"ЛКМ по атому без инструмента — перекрёсток",
		"Ctrl / Shift + ЛКМ по звезде — забрать в инвентарь",
		"H — скрыть дырки",
		"M — песочница / настоящий режим",
		"Пробел — пауза, Esc — меню",
	};

	private enum Mode { Closed, Main, Pause }

	private NucleusLayer _nucleusLayer;
	private Mode _mode = Mode.Closed;
	private bool _gameActive;
	private double _autosaveTimer;

	private Control _root;
	private Button _continueButton;
	private Button _newGameButton;
	private ConfirmationDialog _newGameDialog;
	private ConfirmationDialog _quitDialog;
	private AcceptDialog _errorDialog;

	public override void _Ready()
	{
		Layer = 100;
		ProcessMode = ProcessModeEnum.Always;
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		Build();
		Open(Mode.Main);
	}

	// Игра началась не из меню (импорт на панели) — с этого момента автосохранение пишется.
	public void MarkGameActive()
	{
		_gameActive = true;
		_autosaveTimer = 0;
	}

	private void Build()
	{
		// Затемнение поля; заодно ловит мышь, чтобы клики не уходили в HUD.
		_root = new ColorRect { Color = new Color(0f, 0f, 0f, 0.65f), MouseFilter = Control.MouseFilterEnum.Stop };
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(_root);

		var center = new CenterContainer();
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_root.AddChild(center);

		var panel = new PanelContainer();
		center.AddChild(panel);
		var margin = new MarginContainer();
		foreach (var side in new[] { "left", "right", "top", "bottom" })
			margin.AddThemeConstantOverride("margin_" + side, 24);
		panel.AddChild(margin);
		var box = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
		box.AddThemeConstantOverride("separation", 10);
		margin.AddChild(box);

		var title = new Label
		{
			Text = "Singulario",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
		};
		title.AddThemeFontSizeOverride("font_size", 32);
		box.AddChild(title);

		_continueButton = AddButton(box, "Продолжить", OnContinuePressed);
		_newGameButton = AddButton(box, "Новая игра", OnNewGamePressed);
		box.AddChild(new HSeparator());

		var language = new OptionButton { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
		foreach (var name in Settings.LanguageNames) language.AddItem(name);
		language.Selected = System.Array.IndexOf(Settings.LanguageCodes, Settings.Language);
		language.ItemSelected += OnLanguageSelected;
		AddRow(box, "Язык", language);

		var fullscreen = new CheckBox { Text = "Полный экран", ButtonPressed = Settings.Fullscreen };
		fullscreen.Toggled += OnFullscreenToggled;
		box.AddChild(fullscreen);

		AddRow(box, "Громкость звука", MakeVolumeSlider(Settings.SoundVolume, v => Settings.SoundVolume = v));
		AddRow(box, "Громкость музыки", MakeVolumeSlider(Settings.MusicVolume, v => Settings.MusicVolume = v));

		var autosave = new OptionButton { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
		foreach (int m in Settings.AutosaveMinuteOptions) autosave.AddItem(m.ToString());
		autosave.Selected = System.Array.IndexOf(Settings.AutosaveMinuteOptions, Settings.AutosaveMinutes);
		autosave.ItemSelected += OnAutosaveSelected;
		AddRow(box, "Автосохранение, мин", autosave);

		box.AddChild(new HSeparator());
		AddButton(box, "Выйти из игры", OnQuitPressed);
		box.AddChild(new HSeparator());

		foreach (var line in ControlLines)
		{
			var hint = new Label { Text = line, HorizontalAlignment = HorizontalAlignment.Center };
			hint.AddThemeFontSizeOverride("font_size", 14);
			hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
			box.AddChild(hint);
		}

		_newGameDialog = MakeConfirm("Новая игра", "Начать новую игру? Текущая игра будет потеряна.", StartNewGame);
		_quitDialog = MakeConfirm("Выйти из игры", "Выйти из игры? Игра будет сохранена.", QuitGame);
		_errorDialog = new AcceptDialog();
		AddChild(_errorDialog);
	}

	private static Button AddButton(Container box, string text, System.Action onPressed)
	{
		var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 40) };
		button.Pressed += onPressed;
		box.AddChild(button);
		return button;
	}

	private static void AddRow(Container box, string label, Control control)
	{
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		control.CustomMinimumSize = new Vector2(180, 0);
		control.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
		row.AddChild(control);
		box.AddChild(row);
	}

	// Громкость — заглушка (T013): значение только сохраняется, звука пока нет.
	private static HSlider MakeVolumeSlider(double value, System.Action<double> store)
	{
		var slider = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, Value = value };
		slider.ValueChanged += v => { store(v); Settings.Save(); };
		return slider;
	}

	private ConfirmationDialog MakeConfirm(string title, string text, System.Action onConfirmed)
	{
		var dialog = new ConfirmationDialog { Title = title, DialogText = text, OkButtonText = "Да", CancelButtonText = "Отмена" };
		dialog.Confirmed += onConfirmed;
		AddChild(dialog);
		return dialog;
	}

	private void Open(Mode mode)
	{
		_mode = mode;
		// «Продолжить»: в главном меню — только при сохранённой игре; в паузе — всегда (закрыть меню).
		_continueButton.Visible = mode == Mode.Pause || HasAutosave();
		_root.Visible = true;
		GetTree().Paused = true;
		(_continueButton.Visible ? _continueButton : _newGameButton).GrabFocus();
	}

	private void Close()
	{
		_mode = Mode.Closed;
		_root.Visible = false;
		GetTree().Paused = false;
	}

	private static bool HasAutosave() => FileAccess.FileExists(AutosavePath);

	// Esc — через _UnhandledInput: открытый диалог (подтверждение, экспорт) сам
	// закрывается по Esc раньше и событие сюда не доходит.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo || key.Keycode != Key.Escape) return;
		if (_mode == Mode.Closed)
		{
			Autosave(); // выход в меню
			Open(Mode.Pause);
		}
		else if (_mode == Mode.Pause) Close();
		GetViewport().SetInputAsHandled();
	}

	public override void _Process(double delta)
	{
		if (_mode != Mode.Closed || !_gameActive) return;
		_autosaveTimer += delta;
		if (_autosaveTimer >= Settings.AutosaveMinutes * 60.0) Autosave();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest) Autosave();
	}

	private void OnContinuePressed()
	{
		if (_mode == Mode.Pause) { Close(); return; }
		if (_nucleusLayer == null) return;

		string json = ReadAutosave(out string error);
		if (json == null || !_nucleusLayer.ImportFieldJson(json, out error))
		{
			GD.PrintErr($"[GameMenu] автосохранение не загружено: {error}");
			ShowError("Сохранение не загружено", error);
			return;
		}
		GD.Print("[GameMenu] игра загружена из автосохранения.");
		_nucleusLayer.ShowTerritory();
		MarkGameActive();
		Close();
	}

	private void OnNewGamePressed()
	{
		if (_mode == Mode.Pause || HasAutosave()) _newGameDialog.PopupCentered();
		else StartNewGame();
	}

	private void StartNewGame()
	{
		if (_nucleusLayer == null) return;
		if (!_nucleusLayer.NewGame(out string error))
		{
			ShowError("Новая игра не начата", error);
			return;
		}
		MarkGameActive();
		Autosave(); // «Продолжить» доступна сразу, даже если игру закроют аварийно
		Close();
	}

	private void OnQuitPressed() => _quitDialog.PopupCentered();

	private void QuitGame()
	{
		Autosave();
		GetTree().Quit();
	}

	private void OnLanguageSelected(long index)
	{
		Settings.Language = Settings.LanguageCodes[index];
		Settings.ApplyLanguage();
		Settings.Save();
	}

	private void OnFullscreenToggled(bool on)
	{
		Settings.Fullscreen = on;
		Settings.ApplyFullscreen();
		Settings.Save();
	}

	private void OnAutosaveSelected(long index)
	{
		Settings.AutosaveMinutes = Settings.AutosaveMinuteOptions[index];
		_autosaveTimer = 0;
		Settings.Save();
	}

	private void ShowError(string title, string error)
	{
		_errorDialog.Title = title;
		_errorDialog.DialogText = Tr("Ошибка:") + " " + error;
		_errorDialog.PopupCentered();
	}

	// Сначала во временный файл, потом замена — сбой посреди записи не портит сохранение.
	private void Autosave()
	{
		_autosaveTimer = 0;
		if (!_gameActive || _nucleusLayer == null) return;
		string json = _nucleusLayer.ExportFieldJson();
		using (var file = FileAccess.Open(AutosaveTempPath, FileAccess.ModeFlags.Write))
		{
			if (file == null)
			{
				GD.PrintErr($"[GameMenu] автосохранение не записано: {FileAccess.GetOpenError()}");
				return;
			}
			file.StoreString(json);
		}
		string temp = ProjectSettings.GlobalizePath(AutosaveTempPath);
		string target = ProjectSettings.GlobalizePath(AutosavePath);
		var err = DirAccess.RenameAbsolute(temp, target);
		if (err != Error.Ok)
		{
			GD.PrintErr($"[GameMenu] автосохранение не записано: {err}");
			return;
		}
		GD.Print($"[GameMenu] автосохранение ({json.Length} байт).");
	}

	private static string ReadAutosave(out string error)
	{
		error = null;
		using var file = FileAccess.Open(AutosavePath, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			error = $"{AutosavePath}: {FileAccess.GetOpenError()}";
			return null;
		}
		return file.GetAsText();
	}
}
