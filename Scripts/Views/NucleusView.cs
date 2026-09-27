using Godot;
using Singulario.Core;

namespace Singulario.Views;

// One instance per Nucleus. Draws a tier-colored core, a small direction
// dot, and the 8 ring slots (holes as a faint outline, real energy as a
// filled dot, crafted nucleus-items as a small "atom" glyph -- a simplified
// flat-shape stand-in for the HTML prototype's animated atom icon; the
// glide/counter-rotation animation itself is deferred, see the port spec's
// "lower priority" section).
public partial class NucleusView : Node2D
{
	public Nucleus Model;

	const float CoreRadius = GridView.CellSize * 0.32f;
	const float RingRadius = GridView.CellSize * 0.42f;
	const float SlotRadius = GridView.CellSize * 0.09f;
	// Slightly bigger than a plain energy dot, matching the prototype's
	// "atom icon is a bit bigger than a regular particle" choice.
	const float ItemSlotRadius = GridView.CellSize * 0.13f;
	const float DirDotRadius = GridView.CellSize * 0.06f;

	static readonly Color HoleColor = new Color(1f, 1f, 1f, 0.5f);
	static readonly Color YellowColor = new Color(0.95f, 0.85f, 0.2f);
	static readonly Color BlueColor = new Color(0.25f, 0.55f, 0.95f);
	static readonly Color DirColor = new Color(1f, 1f, 1f, 0.9f);

	public void Refresh() => QueueRedraw();

	public override void _Draw()
	{
		if (Model == null) return;

		DrawCircle(Vector2.Zero, CoreRadius, TierColor(Model.Tier));

		// Direction indicator: offset toward N when Dir >= 0, toward S when
		// Dir < 0 -- cheap way to show spin direction without an arrow sprite.
		Vector2 dirOffset = Model.Dir >= 0 ? new Vector2(0, -1) : new Vector2(0, 1);
		DrawCircle(dirOffset * (CoreRadius * 0.6f), DirDotRadius, DirColor);

		for (int k = 0; k < 8; k++)
		{
			var slot = Model.Ring[k];
			if (slot == null) continue;

			// index 0 = N = straight up, then 45-degree steps clockwise.
			float angle = Mathf.DegToRad(k * 45f - 90f);
			Vector2 pos = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * RingRadius;

			if (slot.IsHole)
			{
				DrawArc(pos, SlotRadius, 0f, Mathf.Tau, 16, HoleColor, 1.6f);
			}
			else if (IsItem(slot.Color))
			{
				DrawAtomIcon(pos, ItemSlotRadius, TierColorFromItemKey(slot.Color));
			}
			else
			{
				DrawCircle(pos, SlotRadius, EnergyColor(slot.Color));
			}
		}
	}

	static bool IsItem(string color) => color != null && color.StartsWith("nucleus:");

	static Color EnergyColor(string color) => color switch
	{
		"yellow" => YellowColor,
		"blue" => BlueColor,
		_ => new Color(0.8f, 0.8f, 0.8f),
	};

	static Color TierColor(NucleusTier tier) => tier switch
	{
		NucleusTier.Red => new Color(0.85f, 0.25f, 0.25f),
		NucleusTier.Green => new Color(0.25f, 0.8f, 0.35f),
		_ => new Color(0.25f, 0.55f, 0.95f),
	};

	static Color TierColorFromItemKey(string itemColor)
	{
		string key = itemColor.Substring("nucleus:".Length);
		return TierColor(NucleusTierExtensions.ParseTier(key));
	}

	// Flat-shape stand-in for the atom-symbol icon: two crossing rings plus
	// a small core dot, all in the tier color the item represents.
	void DrawAtomIcon(Vector2 center, float radius, Color color)
	{
		DrawArc(center, radius, 0f, Mathf.Tau, 20, color, 1.4f);
		DrawArc(center, radius * 0.55f, 0f, Mathf.Tau, 16, color, 1.2f);
		DrawCircle(center, radius * 0.32f, color);
	}
}
