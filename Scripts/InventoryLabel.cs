using Godot;

// Строка инвентаря в HUD (T008): атомы по тиру, звёзды (T011, если есть) и режим. Создаётся FpsLabel.
// При пополнении строка коротко вспыхивает цветом тира (FlashSeconds).
// Строка собирается каждый кадр из ключей перевода (Tr, T013), автоперевод Label выключен.
public partial class InventoryLabel : Label
{
	[Export] public float FlashSeconds = 0.35f;

	private static readonly string[] TierNames = { "Ж", "К", "С" };

	private NucleusLayer _nucleusLayer;
	private int _seenVersion;
	private float _flash;
	private Color _flashColor = Colors.White;
	private readonly System.Text.StringBuilder _sb = new();

	public override void _Ready()
	{
		_nucleusLayer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
		AddThemeColorOverride("font_color", Colors.White);
		AddThemeColorOverride("font_outline_color", Colors.Black);
		AddThemeConstantOverride("outline_size", 4);
		AddThemeFontSizeOverride("font_size", 20);
		HorizontalAlignment = HorizontalAlignment.Center;
		SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop, LayoutPresetMode.KeepSize);
		GrowHorizontal = GrowDirection.Both;
		OffsetTop = 8f;
		MouseFilter = MouseFilterEnum.Ignore;
		AutoTranslateMode = AutoTranslateModeEnum.Disabled;
	}

	public override void _Process(double delta)
	{
		var inv = _nucleusLayer?.Inventory;
		if (inv == null) return;

		if (inv.Version != _seenVersion)
		{
			_seenVersion = inv.Version;
			_flash = FlashSeconds;
			var colors = _nucleusLayer.TierPreviewColors;
			int t = inv.LastAddedTier;
			_flashColor = colors != null && t >= 0 && t < colors.Length ? colors[t] : Colors.White;
		}
		if (_flash > 0f) _flash = Mathf.Max(0f, _flash - (float)delta);
		Modulate = Colors.White.Lerp(_flashColor, FlashSeconds > 0f ? _flash / FlashSeconds : 0f);

		_sb.Clear();
		_sb.Append(Tr("Инвентарь:"));
		for (int t = 0; t < Inventory.TierCount; t++)
			_sb.Append("  ").Append(Tr(TierNames[t])).Append(' ').Append(inv.Count(t));
		for (int t = 0; t < Inventory.TierCount; t++)
			if (inv.StarCount(t) > 0) _sb.Append("  ★").Append(Tr(TierNames[t])).Append(' ').Append(inv.StarCount(t));
		_sb.Append("   ·   ").Append(Tr(inv.Sandbox ? "Песочница" : "Настоящий режим")).Append(" (M)");
		Text = _sb.ToString();
	}
}
