// Решение «что будет, если инструментом-атомом кликнуть в эту клетку» (T030).
// Одно место истины для превью (красное/цветное/скрыто) и для клика/протяжки ЛКМ:
// NucleusLayer собирает описание клетки и вызывает Decide, условий не дублирует.
// Чистые данные и логика без Godot, только целые числа.
public enum PlaceOutcome
{
	Place,   // клетка пуста — поставить
	Replace, // в клетке другой атом — заменить
	Same,    // в клетке такой же атом — ничего (превью скрыто)
	Deny,    // нельзя (см. PlaceDenyReason), превью красное
	Toggle,  // DecideToggle: переключить режим перекрёсток ↔ обычный
}

public enum PlaceDenyReason
{
	None,
	Closed,    // закрытый чанк территории
	BlackHole, // клетка ЧД
	Star,      // клетка звезды
	Layer2,    // чанк занят молекулой слоя 2 (заморожено, рисует MoleculeLayer)
	Port,      // порт слоя 2
	Cargo,     // обломок (груз)
	Occupied,  // атом не из обычных переносчиков (заморожено) — заменять нельзя
	NoAtom,    // настоящий режим: нет атома нужного тира в инвентаре
	CrossroadAdjacent, // рядом (N/E/S/W) уже стоит перекрёсток (T040)
	CrossroadNotGray,  // перекрёсток бывает только у серого атома (T040)
}

// Инструмент в руке: тир, число дырок, спин (±1).
public readonly record struct PlaceTool(int Tier, int Holes, int Spin, bool IsCarrier, bool Crossroad = false);

// Атом в клетке. Exists = false — клетка без атома, остальные поля не читаются.
// IsCarrier — атом, который инструмент может заменить: Ж/К/С (серый — решение GDD 2026-10-01 — и
// вращатель/бросатель не заменяются).
public readonly record struct PlaceCellAtom(bool Exists, int Tier, int Holes, int Spin, bool IsCrossroad, bool IsCargo, bool IsCarrier);

// Свойства клетки, не связанные с атомом. CrossroadNeighbor — в одной из 4 соседних клеток (N/E/S/W)
// стоит перекрёсток; сама клетка не считается (T040).
public readonly record struct PlaceCellFlags(bool Open, bool BlackHole, bool Star, bool Layer2, bool Port, bool CanAfford, bool CrossroadNeighbor = false);

public readonly record struct PlaceResult(PlaceOutcome Outcome, PlaceDenyReason Reason)
{
	public bool Draws => Outcome != PlaceOutcome.Same;
	public bool Acts => Outcome == PlaceOutcome.Place || Outcome == PlaceOutcome.Replace;
}

public static class PlaceDecision
{
	// Серый тир (CoreTier 3, CLAUDE.md): только у него бывает режим перекрёстка (GDD «Перекрёсток», T040).
	public const int GrayTier = 3;

	public static PlaceResult Decide(in PlaceTool tool, in PlaceCellAtom atom, in PlaceCellFlags cell)
	{
		if (!cell.Open) return Deny(PlaceDenyReason.Closed);
		if (cell.BlackHole) return Deny(PlaceDenyReason.BlackHole);
		if (cell.Star) return Deny(PlaceDenyReason.Star);
		if (cell.Layer2) return Deny(PlaceDenyReason.Layer2);
		if (cell.Port) return Deny(PlaceDenyReason.Port);

		if (atom.Exists)
		{
			if (atom.IsCargo) return Deny(PlaceDenyReason.Cargo);
			// «Такой же» — тир, дырки и режим (T039). Спин сравниваем, только если оба не перекрёстки:
			// у перекрёстка его нет (SpinOf = NoSpin).
			bool same = atom.Tier == tool.Tier && atom.Holes == tool.Holes
				&& atom.IsCrossroad == tool.Crossroad
				&& (atom.IsCrossroad || atom.Spin == tool.Spin);
			if (same) return new PlaceResult(PlaceOutcome.Same, PlaceDenyReason.None);
			// IsCarrier атома = можно заменить инструментом (серый и замороженные — нет).
			if (!tool.IsCarrier || !atom.IsCarrier) return Deny(PlaceDenyReason.Occupied);
		}

		// Перекрёсток — только серый и не вплотную к другому (T040). После Same: такой же перекрёсток,
		// уже стоящий рядом с другим (старые сохранения), — Same, а не отказ.
		if (tool.Crossroad)
		{
			if (tool.Tier != GrayTier) return Deny(PlaceDenyReason.CrossroadNotGray);
			if (cell.CrossroadNeighbor) return Deny(PlaceDenyReason.CrossroadAdjacent);
		}

		if (!cell.CanAfford) return Deny(PlaceDenyReason.NoAtom);
		return new PlaceResult(atom.Exists ? PlaceOutcome.Replace : PlaceOutcome.Place, PlaceDenyReason.None);
	}

	// Клик по атому без инструмента (или по такому же атому с инструментом, T039): переключить режим
	// перекрёсток ↔ обычный (T040). Перекрёсток → обычный — всегда; обычный → перекрёсток — только серый
	// и без соседа-перекрёстка по 4 сторонам.
	public static PlaceResult DecideToggle(in PlaceCellAtom atom, bool crossroadNeighbor)
	{
		if (!atom.Exists) return Deny(PlaceDenyReason.None);
		if (atom.IsCargo) return Deny(PlaceDenyReason.Cargo);
		if (atom.IsCrossroad) return new PlaceResult(PlaceOutcome.Toggle, PlaceDenyReason.None);
		if (atom.Tier != GrayTier)
			return Deny(atom.IsCarrier ? PlaceDenyReason.CrossroadNotGray : PlaceDenyReason.Occupied);
		if (crossroadNeighbor) return Deny(PlaceDenyReason.CrossroadAdjacent);
		return new PlaceResult(PlaceOutcome.Toggle, PlaceDenyReason.None);
	}

	private static PlaceResult Deny(PlaceDenyReason reason) => new(PlaceOutcome.Deny, reason);
}
