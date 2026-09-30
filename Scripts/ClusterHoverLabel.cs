using Godot;

// Обычный текстовый Label в левом нижнем углу экрана — сама подстановка
// текста (что именно писать при наведении на клетку со скоплением частиц)
// делается снаружи, в ClusterHoverProbe._Process (см. комментарий там),
// этот скрипт только задаёт позицию/стиль и по умолчанию прячет ярлык,
// пока курсор не наведён ни на одну клетку с кластером.
public partial class ClusterHoverLabel : Label
{
	public override void _Ready()
	{
		AddThemeColorOverride("font_color", Colors.White);
		AddThemeColorOverride("font_outline_color", Colors.Black);
		AddThemeConstantOverride("outline_size", 4);
		AddThemeFontSizeOverride("font_size", 20);

		// BottomLeft — угловой (точечный) анкор, Offset* отсчитываются от этой
		// точки, а не от левого верхнего угла экрана (тот же приём, что и у
		// NucleusSpawnPanel для TopRight).
		SetAnchorsPreset(LayoutPreset.BottomLeft);
		const int margin = 16;
		const int width = 480;
		const int height = 32;
		OffsetLeft = margin;
		OffsetBottom = -margin;
		OffsetTop = -margin - height;
		OffsetRight = margin + width;

		// Текст собирает ClusterHoverProbe через Tr (T013) — автоперевод не нужен.
		AutoTranslateMode = AutoTranslateModeEnum.Disabled;
		Text = "";
		Visible = false;
	}
}
