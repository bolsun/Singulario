using Godot;

// Счётчик FPS + UPS (реальная частота SimTick) + сколько чанков и ядер
// реально рисуется/симулируется прямо сейчас — чтобы наглядно видеть, что
// culling по чанкам работает (visible должно быть заметно меньше total при
// близком зуме), и что тики симуляции не начинают "тормозить" отдельно от FPS.
public partial class FpsLabel : Label
{
	private NucleusLayer _nucleusLayer;
	private MoleculeLayer _moleculeLayer;

	public override void _Ready()
	{
		AddThemeColorOverride("font_color", Colors.White);
		AddThemeColorOverride("font_outline_color", Colors.Black);
		AddThemeConstantOverride("outline_size", 4);

		// Абсолютный путь от корня сцены — не зависит от того, где именно
		// в дереве лежит сам HUD/Label.
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		_moleculeLayer = GetNodeOrNull<MoleculeLayer>("/root/Main/MoleculeLayer");
	}

	public override void _Process(double delta)
	{
		string extraInfo = _nucleusLayer != null
			? $"\nUPS: {_nucleusLayer.CurrentUPS:0}"
			+ $"\nChunks: {_nucleusLayer.VisibleChunkCount}/{_nucleusLayer.TotalChunkCount}"
			+ $"\nЯдра: {_nucleusLayer.ActiveNucleusCount}"
			+ PortCounters(_nucleusLayer.Ports)
			+ BlackHoleCounters(_nucleusLayer.BlackHoles)
			: "";
		string ratio = _moleculeLayer != null ? $"  k = {_moleculeLayer.L2TickRatio}" : "";
		Text = $"FPS: {Engine.GetFramesPerSecond()}  Слой {ViewLayer.Current}{ratio}{extraInfo}";
	}

	// Отладка портов (T002): упаковано частиц / атомов создано / атомов
	// распаковано / частиц выдано (+ сброшено при смене режима). На замкнутой
	// схеме: упаковано = 8 × создано, выдано ≤ 8 × распаковано.
	private static string PortCounters(PortSet ports)
	{
		if (ports == null) return "";
		string dropped = ports.ParticlesDiscarded > 0 ? $"  сброшено {ports.ParticlesDiscarded}" : "";
		return $"\nПорты: упак. {ports.ParticlesPacked}  атомов {ports.AtomsCreated}"
			+ $"  распак. {ports.AtomsUnpacked}  выдано {ports.ParticlesEmitted}{dropped}";
	}

	private static readonly string[] ColorLabels = { "Ж", "К", "С", "Сер" };
	private readonly System.Text.StringBuilder _sb = new();

	// Поглощено чёрными дырами (T003): атомов всего и частиц по цветам.
	private string BlackHoleCounters(BlackHoleSet holes)
	{
		if (holes == null || (holes.Count == 0 && holes.AtomsAbsorbed == 0)) return "";
		_sb.Clear();
		_sb.Append("\nЧД (").Append(holes.Count).Append("): атомов ").Append(holes.AtomsAbsorbed);
		for (int c = 0; c < BlackHoleSet.ColorCount; c++)
		{
			long n = holes.ParticlesAbsorbed[c];
			if (n == 0) continue;
			_sb.Append("  ").Append(c < ColorLabels.Length ? ColorLabels[c] : "#" + c).Append(' ').Append(n);
		}
		return _sb.ToString();
	}
}
