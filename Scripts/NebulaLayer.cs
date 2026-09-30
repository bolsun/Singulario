using System.Collections.Generic;
using Godot;

// Туманности дальнего зума (T015, GDD «Визуал → Ступени детализации по зуму»):
// каждый непустой видимый чанк — мягкое радиальное пятно цвета преобладающего
// тира, чуть больше чанка, яркость — по весу чанка (ChunkComposition). Все пятна —
// один MultiMesh, смешивание аддитивное: соседние пятна сливаются, тиры
// смешиваются цветом. Только отрисовка; данные — NucleusLayer.Composition.
// Узел — дочерний NucleusLayer (создаётся в его _Ready): под звёздами и ЧД,
// на слое 2 скрыт вместе с ним.
public partial class NebulaLayer : Node2D
{
	private const int Stride = 12; // Transform2D (8) + цвет (4)
	private const int TextureSize = 64;

	private MultiMeshInstance2D _node;
	private MultiMesh _mm;
	private float[] _buf = new float[0];
	private int _capacity;

	public override void _Ready()
	{
		_mm = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseColors = true,
			Mesh = new QuadMesh { Size = new Vector2(TextureSize, TextureSize) },
			// Пятна пишутся только для видимых чанков — отсечение не нужно.
			CustomAabb = new Aabb(new Vector3(-1e9f, -1e9f, -1f), new Vector3(2e9f, 2e9f, 2f)),
		};
		_node = new MultiMeshInstance2D
		{
			Name = "Nebulae",
			Multimesh = _mm,
			Texture = BuildSoftTexture(),
			TextureFilter = CanvasItem.TextureFilterEnum.Linear,
			Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
		};
		AddChild(_node);
		Visible = false;
	}

	// fade 0..1 — общая проявленность (переход по зуму); 0 — узел скрыт.
	public void Refresh(IEnumerable<(int cx, int cy)> visible, ChunkComposition comp, Color[] tierColors,
		float chunkWorldSize, float scale, float maxAlpha, float saturationAtoms, float fade)
	{
		Visible = fade > 0f && _node != null;
		if (!Visible) return;

		float s = chunkWorldSize * scale / TextureSize;
		float half = chunkWorldSize / 2f;
		float sat100 = Mathf.Max(saturationAtoms, 0.01f) * 100f;
		int count = 0;
		foreach (var (cx, cy) in visible)
		{
			int tier = comp.Dominant(cx, cy);
			if (tier < 0) continue;
			if (count >= _capacity) Grow();
			float a = fade * maxAlpha * Mathf.Sqrt(Mathf.Min(1f, comp.Weight100(cx, cy) / sat100));
			var c = tierColors != null && tier < tierColors.Length ? tierColors[tier] : Colors.White;
			int o = count * Stride;
			_buf[o] = s; _buf[o + 1] = 0f; _buf[o + 2] = 0f; _buf[o + 3] = cx * chunkWorldSize + half;
			_buf[o + 4] = 0f; _buf[o + 5] = s; _buf[o + 6] = 0f; _buf[o + 7] = cy * chunkWorldSize + half;
			_buf[o + 8] = c.R; _buf[o + 9] = c.G; _buf[o + 10] = c.B; _buf[o + 11] = a;
			count++;
		}
		// Хвост буфера — нулевые инстансы (VisibleInstanceCount ограничивает отрисовку).
		if (_capacity > 0) RenderingServer.MultimeshSetBuffer(_mm.GetRid(), _buf);
		_mm.VisibleInstanceCount = count;
	}

	// InstanceCount сбрасывает буфер — растим ёмкость удвоением, редко.
	private void Grow()
	{
		_capacity = System.Math.Max(64, _capacity * 2);
		var buf = new float[_capacity * Stride];
		System.Array.Copy(_buf, buf, _buf.Length);
		_buf = buf;
		_mm.InstanceCount = _capacity;
	}

	// Белый круг с мягким краем: альфа плавно спадает от центра к краю.
	private static Texture2D BuildSoftTexture()
	{
		var img = Image.CreateEmpty(TextureSize, TextureSize, false, Image.Format.Rgba8);
		float c = (TextureSize - 1) / 2f, radius = TextureSize / 2f;
		for (int y = 0; y < TextureSize; y++)
			for (int x = 0; x < TextureSize; x++)
			{
				float d = Mathf.Min(1f, new Vector2(x - c, y - c).Length() / radius);
				float t = 1f - d;
				img.SetPixel(x, y, new Color(1f, 1f, 1f, t * t * (3f - 2f * t)));
			}
		return ImageTexture.CreateFromImage(img);
	}
}
