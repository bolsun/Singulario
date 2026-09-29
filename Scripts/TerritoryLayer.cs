using Godot;
using System.Collections.Generic;

// Затемнение закрытых чанков (T009) — «туман» поверх всех объектов слоя 1,
// под превью установки (ZIndex 100). Данные — NucleusLayer.Territory, правило
// открытости — NucleusLayer.IsChunkOpen. Узел создаёт NucleusLayer в _Ready.
//
// Рисуется только видимая область: закрытые чанки одной строки склеиваются в
// один прямоугольник, поэтому на дальнем зуме — десятки прямоугольников, а не
// тысячи. Красная вспышка чанка — отказ действия в закрытом чанке (FlashChunk).
public partial class TerritoryLayer : Node2D
{
	private static readonly Color FogColor = new Color(0.02f, 0.02f, 0.05f, 0.92f);
	private static readonly Color FlashColor = new Color(1f, 0.2f, 0.2f, 0.45f);
	private const float FlashSeconds = 0.4f;
	// Больше чанков в видимой области не перебираем (очень дальний зум).
	private const int MaxChunksPerAxis = 512;

	public NucleusLayer Layer;

	private readonly Dictionary<(int cx, int cy), float> _flashes = new();
	private readonly List<(int cx, int cy)> _flashKeys = new();

	public override void _Ready()
	{
		ZIndex = 50;
		Layer ??= GetNodeOrNull<NucleusLayer>("/root/Main/NucleusLayer");
	}

	public void FlashChunk(int cx, int cy) => _flashes[(cx, cy)] = FlashSeconds;

	public override void _Process(double delta)
	{
		Visible = !ViewLayer.IsLayer2;
		if (_flashes.Count > 0)
		{
			_flashKeys.Clear();
			_flashKeys.AddRange(_flashes.Keys);
			foreach (var key in _flashKeys)
			{
				float left = _flashes[key] - (float)delta;
				if (left <= 0f) _flashes.Remove(key);
				else _flashes[key] = left;
			}
		}
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (Layer == null || !Layer.IsReady || Layer.Territory == null) return;
		float chunkWorld = Layer.ChunkSize * Layer.CellSize;

		if (!Layer.Inventory.Sandbox && !Layer.Territory.AllOpen)
		{
			var inv = GetViewport().GetCanvasTransform().AffineInverse();
			var size = GetViewport().GetVisibleRect().Size;
			Vector2 a = inv * Vector2.Zero, b = inv * size;
			int cx0 = Mathf.FloorToInt(Mathf.Min(a.X, b.X) / chunkWorld);
			int cx1 = Mathf.FloorToInt(Mathf.Max(a.X, b.X) / chunkWorld);
			int cy0 = Mathf.FloorToInt(Mathf.Min(a.Y, b.Y) / chunkWorld);
			int cy1 = Mathf.FloorToInt(Mathf.Max(a.Y, b.Y) / chunkWorld);
			if (cx1 - cx0 < MaxChunksPerAxis && cy1 - cy0 < MaxChunksPerAxis)
			{
				for (int cy = cy0; cy <= cy1; cy++)
				{
					int runStart = int.MinValue;
					for (int cx = cx0; cx <= cx1 + 1; cx++)
					{
						bool closed = cx <= cx1 && !Layer.Territory.IsOpen(cx, cy);
						if (closed && runStart == int.MinValue) runStart = cx;
						else if (!closed && runStart != int.MinValue)
						{
							DrawRect(new Rect2(runStart * chunkWorld, cy * chunkWorld, (cx - runStart) * chunkWorld, chunkWorld), FogColor);
							runStart = int.MinValue;
						}
					}
				}
			}
		}

		foreach (var pair in _flashes)
		{
			var c = FlashColor;
			c.A *= pair.Value / FlashSeconds;
			DrawRect(new Rect2(pair.Key.cx * chunkWorld, pair.Key.cy * chunkWorld, chunkWorld, chunkWorld), c);
		}
	}
}
