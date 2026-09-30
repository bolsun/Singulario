using Godot;

// Настройки игры (T013): язык, полный экран, громкости (заглушки — звука пока
// нет, значение только хранится), интервал автосохранения, прогиб сетки (T019, Shift+G). Файл — SettingsPath,
// отдельно от сохранения игры. Load + Apply — при запуске (Main), Save — при
// каждом изменении в меню (GameMenu).
public static class Settings
{
	public const string SettingsPath = "user://settings.cfg";
	private const string Section = "settings";

	// Языки меню: код локали и подпись (подписи не переводятся).
	public static readonly string[] LanguageCodes = { "ru", "en" };
	public static readonly string[] LanguageNames = { "Русский", "English" };
	public static readonly int[] AutosaveMinuteOptions = { 1, 2, 5, 10 };

	public static string Language = "ru";
	public static bool Fullscreen;
	public static double SoundVolume = 100;
	public static double MusicVolume = 100;
	public static int AutosaveMinutes = 5;
	public static bool GridWarp = true; // T019: прогиб сетки под ЧД и звёздами (Shift+G)

	public static void Load()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(SettingsPath) != Error.Ok) return; // нет файла — значения по умолчанию
		string lang = (string)cfg.GetValue(Section, "language", Language);
		if (System.Array.IndexOf(LanguageCodes, lang) >= 0) Language = lang;
		Fullscreen = (bool)cfg.GetValue(Section, "fullscreen", Fullscreen);
		SoundVolume = Mathf.Clamp((double)cfg.GetValue(Section, "sound_volume", SoundVolume), 0, 100);
		MusicVolume = Mathf.Clamp((double)cfg.GetValue(Section, "music_volume", MusicVolume), 0, 100);
		int minutes = (int)cfg.GetValue(Section, "autosave_minutes", AutosaveMinutes);
		if (System.Array.IndexOf(AutosaveMinuteOptions, minutes) >= 0) AutosaveMinutes = minutes;
		GridWarp = (bool)cfg.GetValue(Section, "grid_warp", GridWarp);
	}

	public static void Save()
	{
		var cfg = new ConfigFile();
		cfg.SetValue(Section, "language", Language);
		cfg.SetValue(Section, "fullscreen", Fullscreen);
		cfg.SetValue(Section, "sound_volume", SoundVolume);
		cfg.SetValue(Section, "music_volume", MusicVolume);
		cfg.SetValue(Section, "autosave_minutes", AutosaveMinutes);
		cfg.SetValue(Section, "grid_warp", GridWarp);
		var err = cfg.Save(SettingsPath);
		if (err != Error.Ok) GD.PrintErr($"[Settings] не удалось сохранить {SettingsPath}: {err}");
	}

	public static void Apply()
	{
		ApplyLanguage();
		ApplyFullscreen();
	}

	public static void ApplyLanguage() => TranslationServer.SetLocale(Language);

	// Окно трогаем, только если режим действительно меняется (не сбрасываем развёрнутое окно).
	public static void ApplyFullscreen()
	{
		var mode = DisplayServer.WindowGetMode();
		bool isFull = mode == DisplayServer.WindowMode.Fullscreen || mode == DisplayServer.WindowMode.ExclusiveFullscreen;
		if (Fullscreen && !isFull) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
		else if (!Fullscreen && isFull) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
	}
}
