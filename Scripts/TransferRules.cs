// Общие правила передачи содержимого гнезда между двумя объектами с кольцом —
// одни и те же для ядер слоя 1 (NucleusLayer: частицы), молекул слоя 2
// (MoleculeLayer: атомы) и неподвижных серых объектов (порт на слое 1, блок на
// слое 2). Чистый код без Godot и без состояния, рядом с RingMath.
//
// Здесь только законы тира и спина. Правила цвета частицы (RequireColorMatch/
// RequireOwnColorTier) и запрет для поворачивателя/бросателя — свойства
// содержимого и конкретного слоя, они остаются в NucleusLayer.ColorAccepted.
public static class TransferRules
{
	// Dir неподвижного объекта (порт, блок): спина нет, поэтому с ним
	// совместим любой спин — в обе стороны передачи. У ядер и молекул Dir
	// всегда +1/-1.
	public const int NoSpin = 0;

	// Можно ли передать от giver к receiver по тиру и спину.
	// grayAcceptsAnySpin — серый ПОЛУЧАТЕЛЬ не проверяет спин (см.
	// NucleusLayer.GrayAcceptsAnySpin). requireSameTier — разные цветные тиры
	// не взаимодействуют, серое с любой стороны — исключение (см.
	// NucleusLayer.RequireSameCoreTier).
	public static bool TierSpinAllowed(
		int receiverTier, int receiverDir,
		int giverTier, int giverDir,
		int grayTier, bool grayAcceptsAnySpin, bool requireSameTier)
	{
		bool skipSpinCheck = (grayAcceptsAnySpin && receiverTier == grayTier)
			|| receiverDir == NoSpin || giverDir == NoSpin;
		if (!skipSpinCheck && receiverDir != giverDir) return false;

		if (requireSameTier
			&& giverTier != grayTier
			&& receiverTier != grayTier
			&& giverTier != receiverTier)
			return false;

		return true;
	}
}
