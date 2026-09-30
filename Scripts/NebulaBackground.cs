using Godot;
using System.Collections.Generic;

// Фон-туманность (T016) — позади всего поля. Отдельный CanvasLayer (Layer −100)
// с одним полноэкранным ColorRect и шейдером nebula_background.gdshader; мир
// берётся из камеры (центр, зум), свет — из NucleusLayer.BlackHoles (только
// чтение). Узел создаёт Main в _Ready.
//
// Туманность видна только в свете ЧД: L = 1/(1+(d/R)²), сумма по MaxLights
// ближайшим к центру экрана ЧД, ограничена 1. Ниже FadeZoom гаснет к ровному
// первому цвету (к FadeZoom / FadeRatio — полностью, в лог-шкале).
//
// F4 — отладочный переключатель вариантов текстуры по кругу (не сохраняется):
// Облака → Дымка → Мрамор → Пыль → Выкл. При переключении на LabelSeconds
// показывается название варианта.
public partial class NebulaBackground : CanvasLayer
{
	private const string ShaderPath = "res://Resources/Shaders/nebula_background.gdshader";
	private const string TextureDir = "res://Resources/Textures/Nebula/";
	private const int ShaderMaxLights = 8;
	private const float LabelSeconds = 1.5f;
	private const float LabelFadeSeconds = 0.4f;

	// Варианты для F4: ключ перевода и файл; null — фон выключен.
	private static readonly (string name, string file)[] Variants =
	{
		("Облака", "nebula_clouds_512.png"),
		("Дымка", "nebula_filaments_512.png"),
		("Мрамор", "nebula_flow_512.png"),
		("Пыль", "nebula_dust_512.png"),
		("Выкл", null),
	};

	// Мировых единиц на тексель текстуры плотности (512 текселей × 15 ≈ 5 чанков).
	[Export] public float TexelWorldSize = 15f;
	// Доля движения фона от движения камеры.
	[Export] public float Parallax = 0.4f;
	// Радиус света ЧД R, в чанках (на расстоянии R свет = 0,5).
	[Export] public float LightRadius = 2.5f;
	[Export] public float Gain = 1.3f;
	// Клетка дизеринга — пикселей экрана, одинакова при любом зуме.
	[Export] public float DitherPixel = 3f;
	// Ниже этого зума фон гаснет; полностью погашен при FadeZoom / FadeRatio.
	[Export] public float FadeZoom = 0.1f;
	[Export] public float FadeRatio = 2f;
	// Сколько ЧД светят (ближайшие к центру экрана), не больше 8.
	[Export] public int MaxLights = 8;
	// Ступени палитры Singulario 32: пусто → самая светлая.
	[Export] public Color Color0 = new("#1b1629");
	[Export] public Color Color1 = new("#272038");
	[Export] public Color Color2 = new("#372d4d");
	[Export] public Color Color3 = new("#4d4268");

	private NucleusLayer _layer;
	private ColorRect _rect;
	private ShaderMaterial _material;
	private Texture2D _texture;
	private int _variant;
	private Label _label;
	private float _labelLeft;
	private readonly List<(float dist2, Vector3 light)> _candidates = new();
	private readonly Vector3[] _lights = new Vector3[ShaderMaxLights];

	public override void _Ready()
	{
		Layer = -100;
		ProcessMode = ProcessModeEnum.Always;
		_layer = GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");

		_material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
		_rect = new ColorRect
		{
			Name = "NebulaRect",
			Material = _material,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(_rect);

		// Надпись варианта — свой слой поверх поля и HUD, под меню (CanvasLayer 100).
		var labelLayer = new CanvasLayer { Name = "NebulaLabelLayer", Layer = 90 };
		AddChild(labelLayer);
		_label = new Label
		{
			Name = "NebulaVariantLabel",
			AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_label.AddThemeColorOverride("font_color", Colors.White);
		_label.AddThemeColorOverride("font_outline_color", Colors.Black);
		_label.AddThemeConstantOverride("outline_size", 4);
		_label.AddThemeFontSizeOverride("font_size", 22);
		_label.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
		_label.GrowHorizontal = Control.GrowDirection.Both;
		_label.OffsetTop = 64;
		labelLayer.AddChild(_label);

		ApplyVariant();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo || key.Keycode != Key.F4) return;
		_variant = (_variant + 1) % Variants.Length;
		ApplyVariant();
		_label.Text = string.Format(Tr("Фон: {0}"), Tr(Variants[_variant].name));
		_label.Visible = true;
		_labelLeft = LabelSeconds;
		GetViewport().SetInputAsHandled();
	}

	private void ApplyVariant()
	{
		string file = Variants[_variant].file;
		_rect.Visible = file != null;
		if (file == null) return;
		_texture = GD.Load<Texture2D>(TextureDir + file);
		_material.SetShaderParameter("density", _texture);
	}

	public override void _Process(double delta)
	{
		if (_labelLeft > 0f)
		{
			_labelLeft = Mathf.Max(0f, _labelLeft - (float)delta);
			_label.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_labelLeft / LabelFadeSeconds, 0f, 1f));
			_label.Visible = _labelLeft > 0f;
		}
		if (!_rect.Visible) return;
		var camera = GetViewport().GetCamera2D();
		if (camera == null) return;

		Vector2 screen = GetViewport().GetVisibleRect().Size;
		Vector2 camPos = camera.GetScreenCenterPosition();
		float zoom = camera.Zoom.X;

		_material.SetShaderParameter("screen_size", screen);
		_material.SetShaderParameter("cam_pos", camPos);
		_material.SetShaderParameter("zoom", zoom);
		_material.SetShaderParameter("parallax", Parallax);
		_material.SetShaderParameter("texel_world_size", TexelWorldSize);
		_material.SetShaderParameter("texture_size", (float)(_texture?.GetWidth() ?? 512));
		_material.SetShaderParameter("dither_pixel", Mathf.Max(1f, DitherPixel));
		_material.SetShaderParameter("gain", Gain);
		_material.SetShaderParameter("fade", Fade(zoom));
		_material.SetShaderParameter("color0", Color0);
		_material.SetShaderParameter("color1", Color1);
		_material.SetShaderParameter("color2", Color2);
		_material.SetShaderParameter("color3", Color3);
		UpdateLights(camPos);
	}

	// 1 при zoom ≥ FadeZoom, 0 при zoom ≤ FadeZoom / FadeRatio, между — по логарифму.
	private float Fade(float zoom)
	{
		if (FadeRatio <= 1f) return zoom >= FadeZoom ? 1f : 0f;
		float t = Mathf.Log(zoom / FadeZoom) / Mathf.Log(FadeRatio) + 1f;
		return Mathf.SmoothStep(0f, 1f, Mathf.Clamp(t, 0f, 1f));
	}

	// Свет — MaxLights ближайших к центру экрана ЧД, позиции относительно камеры.
	private void UpdateLights(Vector2 camPos)
	{
		int count = 0;
		if (_layer?.BlackHoles != null && _layer.CellSize > 0)
		{
			float cell = _layer.CellSize;
			float radius = Mathf.Max(0.01f, LightRadius) * _layer.ChunkSize * cell;
			_candidates.Clear();
			foreach (var hole in _layer.BlackHoles.Enumerate())
			{
				var center = new Vector2((hole.Col + hole.Size * 0.5f) * cell, (hole.Row + hole.Size * 0.5f) * cell);
				Vector2 rel = center - camPos;
				_candidates.Add((rel.LengthSquared(), new Vector3(rel.X, rel.Y, radius)));
			}
			// Порядок при равных расстояниях не важен: это только отрисовка.
			_candidates.Sort((a, b) => a.dist2.CompareTo(b.dist2));
			count = Mathf.Min(_candidates.Count, Mathf.Clamp(MaxLights, 0, ShaderMaxLights));
			for (int i = 0; i < count; i++) _lights[i] = _candidates[i].light;
		}
		for (int i = count; i < ShaderMaxLights; i++) _lights[i] = Vector3.Zero;
		_material.SetShaderParameter("light_count", count);
		_material.SetShaderParameter("lights", _lights);
	}
}
