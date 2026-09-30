using Godot;

// Задание ЧД в HUD (T011): строка «Задание 1/4: Частицы Ж 40 / 64» и полоска
// прогресса под строкой инвентаря. Данные — NucleusLayer.Goals, счётчики —
// NucleusLayer.BlackHoles. Создаётся FpsLabel (в сцене узла нет).
// При смене этапа строка коротко вспыхивает (FlashSeconds).
public partial class GoalLabel : VBoxContainer
{
	[Export] public float FlashSeconds = 0.8f;
	private static readonly Color FlashColor = new Color(1f, 0.9f, 0.4f);

	private static readonly string[] ColorNames = { "Ж", "К", "С" };
	private static readonly string[] TierNames = { "Ж", "К", "С", "серые", "вращатели", "бросатели" };

	private NucleusLayer _nucleusLayer;
	private Label _label;
	private ProgressBar _bar;
	private int _seenVersion = -1;
	private float _flash;
	private readonly System.Text.StringBuilder _sb = new();

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		MouseFilter = MouseFilterEnum.Ignore;
		Alignment = AlignmentMode.Center;

		_label = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		_label.AddThemeColorOverride("font_color", Colors.White);
		_label.AddThemeColorOverride("font_outline_color", Colors.Black);
		_label.AddThemeConstantOverride("outline_size", 4);
		_label.AddThemeFontSizeOverride("font_size", 18);
		AddChild(_label);

		_bar = new ProgressBar
		{
			CustomMinimumSize = new Vector2(320f, 10f),
			ShowPercentage = false,
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
		};
		AddChild(_bar);

		SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop, LayoutPresetMode.KeepSize);
		GrowHorizontal = GrowDirection.Both;
		OffsetTop = 38f;
	}

	public override void _Process(double delta)
	{
		var goals = _nucleusLayer?.Goals;
		var holes = _nucleusLayer?.BlackHoles;
		if (goals == null || holes == null) { Visible = false; return; }
		Visible = true;

		if (goals.Version != _seenVersion)
		{
			if (_seenVersion >= 0) _flash = FlashSeconds;
			_seenVersion = goals.Version;
		}
		if (_flash > 0f) _flash = Mathf.Max(0f, _flash - (float)delta);
		Modulate = Colors.White.Lerp(FlashColor, FlashSeconds > 0f ? _flash / FlashSeconds : 0f);

		if (!_nucleusLayer.GoalsActive)
		{
			_label.Text = "Заданий нет: вся карта открыта (Новая игра — начать цепочку)";
			_bar.Visible = false;
			return;
		}
		if (goals.AllDone)
		{
			_label.Text = goals.StageCount > 0 ? "Все задания выполнены" : "Заданий нет";
			_bar.Visible = false;
			return;
		}

		var stage = goals.Current;
		_sb.Clear();
		_sb.Append("Задание ").Append(goals.Stage + 1).Append('/').Append(goals.StageCount).Append(':');
		long done = 0, need = 0;
		for (int i = 0; i < stage.Requirements.Count; i++)
		{
			var req = stage.Requirements[i];
			long p = goals.Progress(i, holes);
			done += p;
			need += System.Math.Max(0, req.Count);
			_sb.Append(i == 0 ? "  " : ",  ").Append(RequirementName(req)).Append(' ').Append(p).Append(" / ").Append(req.Count);
		}
		_label.Text = _sb.ToString();
		_bar.Visible = true;
		_bar.MaxValue = System.Math.Max(1, need);
		_bar.Value = done;
	}

	private static string RequirementName(GoalRequirement req)
	{
		if (req.Kind == GoalKind.Particle)
			return "частицы " + (req.Id >= 0 && req.Id < ColorNames.Length ? ColorNames[req.Id] : "#" + req.Id);
		return "атомы " + (req.Id >= 0 && req.Id < TierNames.Length ? TierNames[req.Id] : "#" + req.Id);
	}
}
