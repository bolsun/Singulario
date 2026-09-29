// Инвентарь игрока (T008, GDD «Производство») — чистые данные без Godot:
// счётчики атомов-переносчиков по тиру (Ж / К / С; позже — другие предметы)
// и режим игры. Экземпляр — NucleusLayer.Inventory.
//
// Песочница (по умолчанию, отладка): установка атомов бесплатна. Настоящий
// режим: установка атома Ж/К/С тратит атом его тира, ПКМ возвращает.
public sealed class Inventory
{
	// Тиры атомов-переносчиков: 0 Ж, 1 К, 2 С (CoreTier).
	public const int TierCount = 3;

	private readonly long[] _atoms = new long[TierCount];

	public bool Sandbox = true;

	// Меняется при каждом пополнении — для отклика в HUD.
	public int Version { get; private set; }
	// Тир последнего пополнения (для цвета отклика), -1 — не было.
	public int LastAddedTier { get; private set; } = -1;

	public static bool IsAtomTier(int tier) => tier >= 0 && tier < TierCount;

	public long Count(int tier) => IsAtomTier(tier) ? _atoms[tier] : 0;

	public void Add(int tier, long count = 1)
	{
		if (!IsAtomTier(tier) || count <= 0) return;
		_atoms[tier] += count;
		LastAddedTier = tier;
		Version++;
	}

	// Хватает ли на установку атома тира tier (в песочнице и для не-Ж/К/С — всегда).
	public bool CanAfford(int tier) => Sandbox || !IsAtomTier(tier) || _atoms[tier] > 0;

	// Списать атом за установку. false — не хватает (ничего не списано).
	public bool TrySpend(int tier)
	{
		if (Sandbox || !IsAtomTier(tier)) return true;
		if (_atoms[tier] <= 0) return false;
		_atoms[tier]--;
		return true;
	}

	// Для загрузки: счётчик как есть (отрицательное — в 0).
	public void Set(int tier, long count)
	{
		if (IsAtomTier(tier)) _atoms[tier] = count < 0 ? 0 : count;
	}

	// Только счётчики; режим задаёт владелец (настройка или сохранение).
	public void Clear()
	{
		System.Array.Clear(_atoms);
		LastAddedTier = -1;
	}
}
