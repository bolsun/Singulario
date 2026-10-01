using Godot;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// Мир — не заранее заданное поле Rows x Cols, а разреженная сетка чанков:
// Dictionary<(cx,cy), WorldChunk>. Чанк создаётся ЛЕНИВО — в первый момент,
// когда его прямоугольник впервые пересёкся с видимой областью камеры (см.
// GetOrCreateChunk, вызывается только из _Process).
//
// На чанк — три MultiMeshInstance2D:
//   Node          — тела ядер (палитра через custom data, см. BuildPaletteAtlas)
//   HoleNode      — "дырки" кольца: обычная полупрозрачная текстура, без шейдера
//   ParticleNode  — "частицы" кольца: та же палитровая перекраска, что и ядро
//
// У каждого ядра — НАСТОЯЩЕЕ кольцо из 8 ФИЗИЧЕСКИХ слотов (Ring[8]) — индекс
// слота совпадает с индексом рендера (слот k всегда рисуется под углом
// baseAngle+k*45° и физически никогда не переставляется — только реальные
// передачи меняют содержимое конкретного физического слота). Каждый слот —
// либо дырка, либо частица определённого цвета (RingSlot).
//
// Поворот кольца (тики TierTicks[tier]) не переставляет Ring — вместо этого
// какой физический слот сейчас "смотрит" на какую сторону света вычисляется
// напрямую из (_globalTick, тир) в DiscreteRotationOffset, БЕЗ отдельного
// состояния на ядро (раньше был накопительный счётчик NucleusEntity.
// RotationOffset, стартующий с 0 в момент спавна — из-за этого ядра одного
// тира, появившиеся в разные моменты, расходились по фазе). Разделение угла
// рендера и физического индекса слота принципиально по другой причине: если
// бы поворот переставлял сам массив, слот одновременно получал бы и
// непрерывный сдвиг угла от рендера, и мгновенный скачок индекса — визуально
// это выглядело как частица, телепортирующаяся в соседнюю дырку (реальный
// баг, который тут был и почему сделано именно так).
//
// Раз в тик (PrototypeTickMs) вызывается SimTick(): сначала граница поворота
// (снятие блокировки "остывания" у ядер, чей тир как раз "щёлкает" —
// TierTicks[tier], см. OnRotationTick), потом передача частиц между соседними
// ядрами по умолчательным правилам прототипа:
// только по ортогоналям (без диагоналей, см. Adj8/OrthogonalSlots — это уже
// индексы сторон света, компас, а не физические слоты), получатель должен
// вращаться в ту же сторону, что и отдающий (requireMatchingSpin), только что
// полученная частица на тик "заблокирована" и разблокируется при следующем
// повороте этого же ядра — так же, как в JS-прототипе.
//
// Рендер слота — плавно вращающаяся точка на орбите, angle = baseAngle(t) +
// k*45°, где k — физический индекс в Ring; что показывать в каждом слоте
// (дырку или частицу нужного цвета) решается каждый кадр по live-данным Ring.
public partial class NucleusLayer : Node2D
{
	// CellSize — не свой экспорт, единственный источник истины — GridDraw.
	public int CellSize { get; private set; }

	[Export] public int SpriteSize = 32; // нативный размер спрайта ядра
	[Export] public int ChunkSize = 16; // сторона чанка в клетках
	[Export] public float CullMargin = 128f; // запас в пикселях вокруг видимой области камеры
	[Export] public float FillDensity = 0.5f; // доля клеток чанка, получающих ядро при генерации (тест)
	// T — переключает это в рантайме (см. _Input). По умолчанию выключено:
	// новые чанки генерируются ПУСТЫМИ, чтобы поле заполнялось только вручную
	// через панель спавна (см. TryPlaceNucleus) — раньше это было единственным
	// режимом и мешало тестировать конкретные расстановки.
	[Export] public bool RandomFillEnabled = false;

	[Export] public string CoreSpritePath = "res://Resources/Textures/nuclear_core_gray_32px.png";
	[Export] public string ShaderPath = "res://Resources/Shaders/nucleus_palette.gdshader";
	[Export] public string[] PalettePaths = new string[]
	{
		"res://Resources/palettes/singulario32/palette_yellow.png",
		"res://Resources/palettes/singulario32/palette_red.png",
		"res://Resources/palettes/singulario32/palette_blue.png",
		"res://Resources/palettes/singulario32/palette_gray.png",   // тир 3 — экспериментальное серое ядро, см. GrayCoreTier
		"res://Resources/Textures/palette_green.png",  // тир 4 — экспериментальный "поворачиватель", см. RotatorCoreTier
		"res://Resources/Textures/palette_violet.png", // тир 5 — экспериментальный "бросатель", см. ThrowerCoreTier
	};

	[Export] public string HoleSpritePath = "res://Resources/Textures/hole_ring_16px.png";
	[Export] public int HoleSpriteSize = 16;
	[Export] public float HoleOpacity = 1f;
	[Export] public float OrbitDiameterCoef = 1f; // диаметр орбиты кольца = CellSize * этот коэффициент
	// Направление вращения кольца НА МОМЕНТ ЗАПУСКА сцены — стартовое значение
	// для _currentSpinDirection (см. поле ниже), которое дальше можно
	// переключать в рантайме по R. Само по себе после _Ready больше нигде не
	// читается — вся установка ядер использует именно _currentSpinDirection.
	[Export] public int SpinDirection = 1;
	[Export] public int[] TierTicks = new int[] { 32, 16, 8, 4, 16, 16 }; // тики на шаг поворота (45°) по тирам — значения по вашему заданию (было 8,4,1 из дефолтного прототипа); 4-е значение — тир GrayCoreTier (та же скорость, что у тира 0); 5-е — тир RotatorCoreTier, СОБСТВЕННАЯ скорость вращения его кольца (период самого эффекта поворота соседей считается ОТДЕЛЬНО, от неё же — см. TriggerRotatorRotation/OwnRotationTicks); 6-е — тир ThrowerCoreTier, та же логика, что и у RotatorCoreTier (тоже толкает соседей, см. ThrowerCoreTier)
	// Экспериментальное серое ядро (см. обсуждение в чате) — не отдельный
	// ортогональный флаг на NucleusEntity, а просто ЕЩЁ ОДИН тир: 4-я
	// палитра (PalettePaths[3] = palette_gray.png, реально серая картинка) и
	// 4-е значение TierTicks (та же скорость, что у тира 0). Рендер тела и
	// частиц не знает о "серости" вообще — CoreTier=GrayCoreTier просто
	// выбирает нужную строку в атласе палитр, как и любой другой тир, без
	// какого-либо ветвления в шейдере или в C#. "Принимает любой цвет" —
	// единственное, что у него особенное, и это чисто игровое правило (см.
	// ColorAccepted), не имеющее отношения к рендеру.
	[Export] public int GrayCoreTier = 3;

	// Экспериментальный "поворачиватель" (клеточный автомат, см. обсуждение в
	// чате) — тоже просто ещё один тир (PalettePaths[4] = palette_green.png),
	// без своего рендера/ветвлений, как и GrayCoreTier. Особенное у него —
	// поведение в SimTick: каждый раз, когда наступает расчётный момент
	// следующего поворота (см. комментарий у TriggerRotatorRotation), он
	// проворачивает соседей вокруг себя по кругу (по Adj8, порядок которого
	// идёт по часовой при Dir=+1 и против часовой при Dir=-1, см. R/
	// _currentSpinDirection/TriggerRotatorRotation) — каждый сосед физически
	// переезжает в следующую по циклу клетку, а не просто переставляет
	// содержимое кольца, см. TriggerRotatorRotation/StartMove. TierTicks[RotatorCoreTier]
	// отвечает ТОЛЬКО за собственное вращение кольца/разблокировку слотов
	// этого ядра — как у любого другого тира, без исключений и без
	// удвоений — а момент срабатывания самого эффекта поворота соседей
	// считается от неё же, но ОТДЕЛЬНОЙ формулой (см. TriggerRotatorRotation),
	// а не хранится как ещё одна независимая настраиваемая величина.
	[Export] public int RotatorCoreTier = 4;

	// "Бросатель" (см. обсуждение в чате) — момент и направления
	// срабатывания считаются ТОЧНО как у поворачивателя (та же собственная
	// скорость вращения кольца, то же вычисление активных направлений и
	// периода — TriggerRotatorRotation/MaybeTriggerRotatorRotation
	// расширены проверкой CoreTier==ThrowerCoreTier наравне с
	// RotatorCoreTier, а не продублированы), а вот САМО действие — другое:
	// вместо того чтобы толкнуть соседа на новую фазу (как обычный
	// поворачиватель) и лишь потом отпустить в полёт, бросатель сразу же, с
	// текущего места соседа, срывает его в полёт по КАСАТЕЛЬНОЙ к
	// направлению собственного вращения (радиус от бросателя к ядру,
	// сдвинутый на 90°/2 позиции компаса в сторону Dir этого бросателя) — по
	// клетке за ThrowTicksPerCell тиков, пока не сработает одно из условий
	// окончания полёта — см. NucleusEntity.IsFlying/FlightDir/
	// FlightCellsRemaining/PendingFlightDir и EvaluateFlightStep.
	[Export] public int ThrowerCoreTier = 5;

	// Скорость полёта брошенного (см. ThrowerCoreTier) ядра — тиков на одну
	// клетку прямолинейного движения, В ТОМ ЧИСЛЕ на самый первый "прыжок":
	// сам бросок — это уже первый шаг полёта по касательной, а не отдельный
	// медленный толчок с собственной скоростью вращателя (см.
	// TriggerRotatorRotation) — единый, постоянный темп на весь полёт.
	[Export] public int ThrowTicksPerCell = 8;

	// Максимальная дистанция полёта в клетках (по заданию — 16), после
	// которой ядро исчезает само, если ни во что не врезалось раньше.
	[Export] public int ThrowMaxDistance = 16;

	// Пауза (в тиках) после того, как вращатель/бросатель довернул соседа на
	// очередной шаг, — прежде чем он снова станет кандидатом на следующий
	// толчок (см. MaybeTriggerRotatorRotation). По умолчанию 0 — по заданию
	// клиента вращение непрерывное, без задержки (см. комментарий там же).
	// Если поставить > 0, между двумя толчками появится чистое время
	// простоя сверх самого переезда (moveDuration) — ЭТУ величину, а не
	// длительность переезда, кольцо просто ждёт, никак не поворачиваясь.
	// Общая для всех тиров/holeCount (не привязана к конкретному вращателю),
	// как и TierTicks — если понадобится разная пауза у разных тиров,
	// придётся завести массив по образцу TierTicks.
	[Export] public int PauseTicksAfterStep = 0;

	[Export] public float PrototypeTickMs = 16f;
	// Потолок скорости порта чанка (T002): порт обменивается частицей с ядрами
	// только на тиках, кратных этому числу — не чаще одной частицы за столько
	// тиков. По GDD порт не быстрее серой линии (серое: 1 частица за 4 тика).
	// Фаза — чистая функция глобального тика, как у поворота колец.
	[Export] public int PortTicksPerParticle = 4;
	// Дырки менее информативны, чем частицы, и их визуально намного больше —
	// поэтому свой порог отключения: слой дырок гаснет раньше (при более
	// сильном отдалении камеры), чем слой частиц (см. ParticleHideZoom).
	[Export] public float HoleHideZoom = 0.25f;
	// Приглушение тела груза (T006) в шейдере палитр: 0 — как рабочий атом, 1 — максимум.
	[Export] public float CargoDim = 0.7f;

	// --- частицы ---
	// T024: полоса вариантов осколка (кадры n×n слева направо; число кадров = ширина / высота).
	[Export] public string ParticleSpritePath = "res://Resources/Textures/particle_variants_gray_16px.png";
	[Export] public int ParticleSpriteSize = 16;
	[Export] public float ParticleFillChance = 0.5f; // доля слотов кольца (из 8), становящихся частицей вместо дырки при генерации
	// При Zoom.X меньше этого значения слой частиц вообще не считается и не
	// рисуется (на таком отдалении это всё равно неразличимые точки, а
	// чанков видно много — экономим CPU/GPU). Было 0.1 (частицы гасли позже
	// дырок) — поднято до 0.25, вровень с HoleHideZoom: раз симуляция ядер
	// теперь не завязана на видимость камеры (см. _activeSet), при большом
	// количестве ядер именно рендер частиц/дырок остаётся единственным, что
	// ещё можно дёшево срезать по зуму, не трогая саму симуляцию.
	[Export] public float ParticleHideZoom = 0.25f;
	// T014, уровень детализации: ниже этого зума атом — точка цвета тира (без
	// частиц и дырок — их гасят HoleHideZoom/ParticleHideZoom).
	[Export] public float AtomDotZoom = 0.25f;
	// T014: случайное заполнение (T) создаёт новые чанки только при зуме не
	// ниже этого — на дальнем зуме в кадре тысячи и миллионы координат чанков.
	[Export] public float RandomFillMinZoom = 0.06f;
	// T015, туманности: ниже NebulaZoom атомы не рисуются, каждый непустой чанк —
	// мягкое пятно цвета преобладающего тира (NebulaLayer). Переход — полоса зумов
	// от NebulaZoom до NebulaZoom × NebulaFadeRatio (в лог-шкале): точки гаснут,
	// пятна проявляются. NebulaFadeRatio ≤ 1 — резкое переключение.
	[Export] public float NebulaZoom = 0.05f;
	[Export] public float NebulaFadeRatio = 1.6f;
	// Размер пятна в чанках (больше 1 — соседние пятна сливаются).
	[Export] public float NebulaScale = 2f;
	// Непрозрачность пятна при насыщении и вес чанка (в атомах), при котором оно наступает.
	[Export] public float NebulaMaxAlpha = 0.5f;
	[Export] public float NebulaSaturation = 64f;
	// Вес клетки источника в составе чанка относительно одного атома.
	[Export] public float NebulaSourceWeight = 1f;

	// --- симуляция передачи частиц (правила из JS-прототипа) ---
	// requireMatchingSpin=true, diagonalTransfer=false, requireLowerTotal=false,
	// requireLowerColorTotal=false — дефолтные значения CONFIG прототипа, жёстко
	// зашиты, других профилей пока нет. RequireColorMatch — единственное
	// исключение: в прототипе по умолчанию OFF, но по вашему запросу включено
	// здесь (см. [Export] ниже) — ядро, уже держащее один цвет, отказывается
	// принимать другой, пока не опустеет полностью.
	[Export] public bool RequireColorMatch = true;

	// ЭКСПЕРИМЕНТАЛЬНЫЙ флаг (одна настройка, один переключатель — по
	// заданию): когда включён, обычное (не серое) ядро принимает только
	// частицы СВОЕГО цвета — то есть цвет частицы должен совпадать с
	// CoreTier этого ядра (0=Ж/1=К/2=С, та же нумерация, что и у
	// RingSlot.ColorTier — см. NucleusSpawnPanel.Tiers/PalettePaths), а не с
	// тем, что ядро уже держит в кольце (это отдельная, уже существующая
	// проверка — RequireColorMatch, см. выше). Проверки независимы и
	// действуют одновременно: RequireColorMatch не даёт держать в кольце два
	// разных цвета сразу, RequireOwnColorTier — не даёт держать цвет,
	// отличный от собственного тира ядра. Серое ядро (CoreTier==GrayCoreTier)
	// — единственное исключение из ОБЕИХ проверок сразу, см. ColorAccepted.
	[Export] public bool RequireOwnColorTier = false;

	// По заданию клиента: ядра РАЗНОГО тира (цвета) вообще не взаимодействуют
	// друг с другом — не передают частицы между собой (см. Шаг 2 в SimTick,
	// TransferAllowed), КРОМЕ серого (GrayCoreTier), который остаётся
	// универсальным посредником с любой стороны обмена (как и раньше — см.
	// ColorAccepted). В отличие от RequireOwnColorTier/RequireColorMatch выше
	// (которые смотрят на ЦВЕТ уже лежащей в кольце частицы), этот флаг
	// сравнивает CoreTier ДАВАТЕЛЯ и ПОЛУЧАТЕЛЯ напрямую, друг с другом — то
	// есть жёлтое ядро не передаст и не примет частицу от красного/синего
	// ядра, даже если сама частица физически другого цвета (например, успела
	// попасть туда ещё до включения этого правила, при генерации мира, или
	// через серое ядро-посредник) — простая проверка "два тира разные —
	// значит не взаимодействуют", без зависимости от истории конкретной
	// частицы.
	[Export] public bool RequireSameCoreTier = false;

	// По заданию клиента: серое ядро (GrayCoreTier) ПРИНИМАЕТ частицу от
	// любого соседа независимо от направления вращения (Dir) — то есть для
	// него общая проверка requireMatchingSpin (receiver.Dir != giver.Dir,
	// см. TransferAllowed) не действует, когда серое выступает ПОЛУЧАТЕЛЕМ.
	// А вот когда серое само ОТДАЁТ частицу обычному ядру (то есть серое —
	// giver, а receiver — не серое), обычная проверка спина работает как и
	// для любой другой пары — здесь ничего менять не нужно, это уже
	// действует само собой (see TransferAllowed: сравнение идёт по
	// receiver.Dir, а receiver в этом случае не серый). Флаг влияет только
	// на "серое как получатель" половину правила.
	[Export] public bool GrayAcceptsAnySpin = true;

	// Месторождение отдаёт частицы только атому своего тира и серому (T009,
	// GDD «Законы → Правила передачи»): источник тира X — атому тира X или
	// серому. Только захват из источника (шаг 3 SimTick); перенос между
	// атомами не меняется. Оба режима.
	[Export] public bool DepositGivesOwnTierOnly = true;

	// --- захват энергии из источников частиц (EnergyClusterLayer) ---
	// Интервал захвата ОДИНАКОВ для любого тира ядра (в отличие от
	// TierTicks[CoreTier], который управляет только поворотом кольца этого
	// конкретного тира) — по заданию, TierTicks_уровня_0 * 8. Считается один
	// раз в _Ready из ТЕКУЩЕГО TierTicks[0] (после того, как экспортируемые
	// поля уже применены редактором), а не хранится как независимая
	// константа — так если TierTicks[0] поменяют, интервал захвата само
	// собой пересчитается при следующем запуске.
	private int _energyCaptureTicks;
	// Сколько частиц забирается из кластера-источника за одну успешную
	// попытку захвата (см. _energyCaptureTicks) — отдельный, самостоятельный
	// баланс-параметр, не завязанный на InitialAmount у EnergyClusterLayer.
	[Export] public int EnergyCaptureAmount = 1;

	// --- перемещение ядер по сетке (клеточный автомат, см. RotatorCoreTier/
	// ThrowerCoreTier) ---
	// Собственная скорость вращения кольца ЭТОГО тира (тики на одну фазу/45°)
	// — обычная скорость тира, БЕЗ каких-либо удвоений или иных поправок
	// (кольцо крутится как у любого другого тира). Используется как база для
	// длительности переезда соседа — по заданию, длительность движения = это
	// значение × число сдвигаемых фаз (1/2/4 в зависимости от holeCount
	// поворачивателя/бросателя, см. TriggerRotatorRotation) — чем больше
	// поворот, тем дольше едет, с одной и той же угловой скоростью, а также
	// как база для расчёта периода срабатывания самого эффекта (см.
	// MaybeTriggerRotatorRotation) — период НЕ хранится отдельным полем, а
	// пересчитывается на лету из этого значения и holeCount КОНКРЕТНОГО
	// ротатора/бросателя при каждой проверке в SimTick, чтобы не заводить
	// лишнюю синхронизируемую величину.
	//
	// Раньше это было закешированное в _Ready поле _rotatorOwnTicks, годное
	// только для одного конкретного тира (RotatorCoreTier). С появлением
	// ThrowerCoreTier (толкает соседей точно так же, см. его комментарий)
	// понадобилась та же величина для ДРУГОГО тира — превращено в обычную
	// функцию от CoreTier конкретного ядра, тем же приёмом, что уже
	// используется в SimTick/DiscreteRotationOffset для тиков поворота.
	private int OwnRotationTicks(int coreTier) =>
		coreTier < TierTicks.Length ? TierTicks[coreTier] : TierTicks[TierTicks.Length - 1];

	// По заданию клиента — вращатель и бросатель не должны взаимодействовать
	// друг с другом (в любом сочетании: вращатель-вращатель, вращатель-
	// бросатель, бросатель-бросатель) — единый предикат "это один из двух
	// специальных тиров", используемый везде, где ротатор/бросатель мог бы
	// физически подействовать на соседнее/пролетающее ядро ДРУГОГО такого же
	// тира (толчок/бросок в TriggerRotatorRotation, поимка в
	// TryCatchFlyingNeighbor) — вместо точечных проверок конкретной пары
	// тиров, чтобы не плодить бespoke-условия под каждую комбинацию.
	private bool IsSpinnerTier(int coreTier) => coreTier == RotatorCoreTier || coreTier == ThrowerCoreTier;

	// "Обычный" (цветной) тир — не серый и не спиннер (поворачиватель/
	// бросатель), т.е. Ж/К/С и любой другой цветной тир, который может
	// появиться позже (не завязано на конкретные числа 0/1/2 — так же, как
	// RandomNormalTier). Используется в TryPlaceNucleus для замены ядра под
	// курсором по ЛКМ поверх уже занятой клетки (см. её комментарий) — по
	// заданию клиента такая замена разрешена, только если ОБА ядра, и старое,
	// и новое, обычные: серое/поворачиватель/бросатель ни ставить таким
	// способом, ни затирать так нельзя — их продуманную настройку (кольцо,
	// сцепление с соседями) слишком легко потерять случайным кликом/протяжкой,
	// поэтому для них клетка по-прежнему просто "занята" и требует явного
	// ПКМ-удаления перед установкой.
	private bool IsNormalTier(int coreTier) => coreTier != GrayCoreTier && !IsSpinnerTier(coreTier);

	// Канонический (не завязанный на текущую фазу вращения кольца) набор
	// активных направлений поворачивателя/бросателя — см. подробное
	// объяснение у TriggerRotatorRotation. Используется и там, и в
	// TryCatchFlyingNeighbor — единая точка правды вместо двух copy-paste
	// циклов, которые раньше по отдельности заворачивали через
	// PhysicalSlotForCompass (живой, "уезжающий" разворот).
	private bool[] CanonicalActiveDirections(NucleusEntity rotator, out int activeCount)
	{
		var active = new bool[8];
		activeCount = 0;
		for (int k = 0; k < 8; k++)
		{
			active[k] = rotator.Ring[k].Exists;
			if (active[k]) activeCount++;
		}
		return active;
	}

	private static readonly (int dr, int dc)[] Adj8 =
	{
		(-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1)
	};
	private static readonly int[] OrthogonalSlots = { 0, 2, 4, 6 }; // N,E,S,W — диагонали (нечётные) выключены (diagonalTransfer=false)
	private static int Opposite(int k) => (k + 4) % 8;

	// Для HUD/отладки. TotalChunkCount — сколько чанков сгенерировано ЗА ВСЁ
	// ВРЕМЯ (растёт по мере исследования мира, не размер мира целиком).
	public int VisibleChunkCount => _visible.Count;
	public int TotalChunkCount => _chunks.Count;
	// Сколько ядер сейчас участвует в симуляции (SimTick бежит по _activeSet —
	// см. комментарий там же) — теперь это ВСЕ живые ядра, а не только
	// видимые, так что в норме совпадает с TotalChunkCount-суммой ядер по
	// всем чанкам. Название и HUD (см. FpsLabel) намеренно не переименовывал —
	// после разделения "симулируется"/"видимо" это то же самое число, что и
	// "живо", просто с исторически прежним именем.
	public int ActiveNucleusCount => _activeSet.Count;
	// UPS — сколько раз в секунду реально отработал SimTick() (не путать с
	// FPS кадров рендера): считается скользящим окном ~1 секунда, а не просто
	// 1000/PrototypeTickMs, чтобы было видно, если тики начинают "тормозить"
	// и досрочно упираются в guard в _Process (см. цикл там же).
	public float CurrentUPS { get; private set; }

	// Замер производительности (T014, HUD по F3): окно ~1 с реального времени,
	// в HUD — итог последнего закрытого окна.
	public sealed class PerfWindow
	{
		public double TickMsAvg, TickMsMax;
		public double TicksPerFrameAvg; public int TicksPerFrameMax;
		public double FrameMsAvg;
		public double RenderMsAvg, RenderMsMax;
	}
	public PerfWindow Perf { get; } = new();
	public int TotalAtomCount => _activeSet.Count + _sleepingSet.Count + _cargoSet.Count;
	public int VisibleAtomCount { get; private set; }
	private double _perfTickMsSum, _perfTickMsMax, _perfRenderMsSum, _perfRenderMsMax, _perfFrameMsSum;
	private int _perfTicks, _perfFrames, _perfTicksPerFrameMax;

	// --- для слоя 2 (MoleculeLayer): единые часы симуляции и общие ресурсы
	// отрисовки ядра, только чтение. Молекулы не тикают сами — их фаза
	// считается от этого же _globalTick (см. RingMath).
	public bool IsReady => _ready;
	public long GlobalTick => _globalTick;
	public float SubTickFraction => _subTickFraction;
	public ShaderMaterial PaletteMaterial => _material;
	public Texture2D CoreTexture => _coreTexture;
	public Texture2D HoleTexture => _holeTexture;
	public int TierCount => _tierCount;
	// Атлас палитр тиров (строка тира — (tier + 0.5) / TierCount); для слоёв вида (T022).
	public Texture2D PaletteAtlas { get; private set; }
	public Color[] TierPreviewColors => _tierPreviewColors;
	public float OrbitRadius => _orbitRadius;
	// Порты чанков (T002) — одно место истины для слоя 1 (обмен частицами в
	// SimTick, PortLayer) и слоя 2 (блоки и атомы, MoleculeLayer).
	public PortSet Ports { get; private set; }
	// Чёрные дыры (T005): объекты слоя 1 (Size×Size клеток) и счётчики
	// поглощённого. Захват атомов и частиц на горизонте — здесь (SimTick,
	// FinishArrivedMoves), установка и отрисовка — BlackHoleLayer.
	public BlackHoleSet BlackHoles { get; private set; }

	// Клетка закрыта для объектов слоя 1 (атомов, источников, влёта брошенного
	// атома): её чанк закрыт (T009, IsCellOpen), занят молекулой (только при
	// включённом слое 2) или в ней ЧД/звезда.
	public bool IsCellBlockedForLayer1(int row, int col) =>
		!IsCellOpen(row, col)
		|| (_moleculeLayer != null && _moleculeLayer.IsCellTakenByLayer2(row, col))
		|| BlackHoles.TryGetAt(row, col, out _)
		|| Stars.TryGetAt(row, col, out _);

	// Звёзды-сборщики (T006): данные — StarSet, приём ингредиентов, производство
	// и выход — здесь (SimTick, FinishArrivedMoves), установка и отрисовка — StarLayer.
	public StarSet Stars { get; private set; }
	// Инвентарь игрока и режим песочница/настоящий (T008).
	public Inventory Inventory { get; private set; }
	// Режим при запуске (и для сохранений без режима); M — переключить.
	// Песочница — установка бесплатна; настоящий — атом Ж/К/С тратится из
	// инвентаря, ПКМ возвращает его и атомы-предметы из дырок.
	[Export] public bool SandboxMode = true;

	// Открытая территория (T009): данные — Territory, правило — здесь.
	// Закрытый чанк: ставить, удалять, бросать и везти туда нельзя (через
	// IsCellBlockedForLayer1/CanPlaceBlackHoleCell), рисуется затемнённым
	// (TerritoryLayer). Песочница открывает всю карту.
	public Territory Territory { get; private set; }
	// Состав чанков для туманностей дальнего зума (T015): атомы и источники по тиру.
	public readonly ChunkComposition Composition = new();
	private readonly int[] _compScratch = new int[ChunkComposition.TierCount];
	private int _compSourceVersion = -1;
	private TerritoryLayer _territoryLayer;
	private CameraController _camera;
	// Подсказка управления (T012): данные — ControlHints, вид — HintLabel.
	public ControlHints Hints { get; } = new();

	// Показ расширения камерой (T012): первые ExpansionCameraShows расширений
	// камера летит в центр и отдаляется до всей открытой области, затем сходит
	// затемнение нового кольца, на середине схода — вспышка ЧД. Дальше —
	// расширение без движения камеры.
	[Export] public int ExpansionCameraShows = 4;
	[Export] public float ExpansionFlightSeconds = 1.2f;
	private CrossroadLayer _crossroadLayer;

	// Атомы в режиме перекрёстка (T010) — только для отрисовки колец
	// (CrossroadLayer), на симуляцию порядок набора не влияет.
	private readonly HashSet<NucleusEntity> _crossSet = new();
	public int CrossroadCount => _crossSet.Count;
	public IEnumerable<(Vector2 center, int exitMask)> Crossroads()
	{
		foreach (var n in _crossSet) yield return (EffectiveCenter(n), n.Cross.ExitMask);
	}

	// Радиус видимого тела атома в пикселях мира (спрайт ядра 1:1) — от него дорожка перекрёстка (T029).
	public float BodyRadius => SpriteSize * 0.5f;
	public float HoleRadius => HoleSpriteSize * 5f / 16f; // кольцо дырки d10 на холсте 16

	public int ChunkOf(int cell) => Mathf.FloorToInt((float)cell / ChunkSize);
	public bool IsChunkOpen(int cx, int cy) => Inventory.Sandbox || Territory.IsOpen(cx, cy);
	public bool IsCellOpen(int row, int col) => IsChunkOpen(ChunkOf(col), ChunkOf(row));

	// Открыть чанки (T010 — расширение по награде; сейчас — отладочная клавиша O).
	public int OpenChunks(IEnumerable<(int cx, int cy)> chunks)
	{
		int added = Territory.Open(chunks);
		if (added > 0) GD.Print($"[NucleusLayer] открыто чанков: {added} (всего открыто {Territory.OpenCount}).");
		return added;
	}

	// Задания ЧД (T011): цепочка этапов из GoalsPath (данные — GoalChain),
	// награды — здесь (CompleteGoalStage). Цепочка идёт только в игре с
	// ограниченной территорией (после «Новой игры»); при открытой всей карте
	// (запуск, старые сохранения) — стоит.
	[Export] public string GoalsPath = "res://Data/goals.json";
	public GoalChain Goals { get; private set; }

	// Типы звёзд и рецепты (T027): таблица из RecipesPath (данные — StarCatalog).
	// Ошибка при старте — встроенная таблица StarCatalog.Default; F9 — перечитать.
	[Export] public string RecipesPath = "res://Data/recipes.json";
	public StarCatalog Catalog { get; private set; } = StarCatalog.Default;
	private System.Func<StarRecipe, bool> _recipeAvailable;
	public System.Func<StarRecipe, bool> RecipeAvailable => _recipeAvailable ??= IsRecipeAvailable;

	// F9 (отладка, T027): перечитать RecipesPath без перезапуска. Ошибка — старая
	// таблица остаётся. Успех — у всех звёзд сгорают буфер ингредиентов и работа,
	// рецепт фабрики ищется по Id (пропал — по умолчанию), выходной буфер обрезается
	// до новой ёмкости; звезда, которая в новом размере не помещается (центр на
	// месте), удаляется (в настоящем режиме — в инвентарь с выходным буфером).
	// Детерминизм от этого действия не требуется.
	private void ReloadRecipes()
	{
		var catalog = ReadCatalog(out string error);
		if (catalog == null)
		{
			GD.PrintErr($"[NucleusLayer] F9 рецепты: {error} Остаются прежние.");
			_starLayer?.ShowMessage(Tr("Рецепты: ошибка в файле, см. лог"));
			return;
		}
		Catalog = catalog;
		var stars = new List<Star>(Stars.All);
		Stars.Clear();
		int removed = 0;
		foreach (var star in stars)
		{
			int centerRow = star.Row + star.Size / 2, centerCol = star.Col + star.Size / 2;
			int size = catalog.TypeOf(star.Type).Size;
			int row = centerRow - size / 2, col = centerCol - size / 2;
			string reason = _starLayer?.PlaceBlockReason(row, col, size);
			if (reason != null)
			{
				removed++;
				if (!Inventory.Sandbox)
				{
					while (star.Output.Count > 0) Inventory.AddItem(star.Output.Dequeue());
					Inventory.AddStar(star.Type);
				}
				GD.PushWarning($"[NucleusLayer] F9 рецепты: звезда ({star.Row},{star.Col}) в размере {size}×{size} не помещается ({reason}) — удалена{(Inventory.Sandbox ? "" : ", возвращена в инвентарь")}.");
				continue;
			}
			star.Rebind(catalog, row, col);
			if (star.Choice == StarChoice.Player && star.RecipeId == null)
				star.SetRecipe(catalog.DefaultRecipe(star.Type, RecipeAvailable));
			Stars.Add(star);
		}
		GD.Print($"[NucleusLayer] F9 рецепты: {catalog.Recipes.Count} из {RecipesPath}; у {stars.Count - removed} звёзд(ы) буфер и работа сгорели{(removed > 0 ? $", удалено {removed}" : "")}.");
		_starLayer?.ShowMessage(Tr("Рецепты перечитаны"));
	}

	// Каталог из файла; null — ошибка в error.

	private StarCatalog ReadCatalog(out string error)
	{
		if (!FileAccess.FileExists(RecipesPath)) { error = $"файл {RecipesPath} не найден."; return null; }
		using var file = FileAccess.Open(RecipesPath, FileAccess.ModeFlags.Read);
		if (file == null) { error = $"не удалось открыть {RecipesPath}: {FileAccess.GetOpenError()}."; return null; }
		return StarCatalog.Parse(file.GetAsText(), out error);
	}

	private StarCatalog LoadCatalog()
	{
		var catalog = ReadCatalog(out string error);
		if (catalog == null)
		{
			GD.PrintErr($"[NucleusLayer] рецепты: {error} Работает встроенная таблица.");
			return StarCatalog.Default;
		}
		GD.Print($"[NucleusLayer] рецепты: {catalog.Recipes.Count} из {RecipesPath}.");
		return catalog;
	}
	public bool GoalsActive => Goals != null && !Territory.AllOpen;

	// Цепочка из файла; ошибка — пустая цепочка (заданий нет) и сообщение.
	private GoalChain LoadGoals()
	{
		GoalChainData data = null;
		string error;
		if (!FileAccess.FileExists(GoalsPath)) error = $"файл {GoalsPath} не найден.";
		else
		{
			using var file = FileAccess.Open(GoalsPath, FileAccess.ModeFlags.Read);
			if (file == null) error = $"не удалось открыть {GoalsPath}: {FileAccess.GetOpenError()}.";
			else data = GoalChainData.Parse(file.GetAsText(), out error);
		}
		if (data == null)
		{
			GD.PrintErr($"[NucleusLayer] задания: {error} Заданий нет.");
			return new GoalChain(null);
		}
		foreach (var st in data.Stages)
			foreach (var id in st.Reward.Recipes)
				if (Catalog.IndexOf(id) < 0) GD.PushWarning($"[NucleusLayer] задания: этап «{st.Name}» открывает неизвестный рецепт «{id}».");
		GD.Print($"[NucleusLayer] задания: {data.Stages.Count} этап(ов) из {GoalsPath}, seed {data.Seed}.");
		return new GoalChain(data);
	}

	// Рецепт звезды доступен игроку: в песочнице — все, иначе — открытые заданиями.
	public bool IsRecipeAvailable(StarRecipe recipe) =>
		recipe != null && (Inventory.Sandbox || Goals.IsRecipeOpen(recipe.Id));

	// После каждого тика: этап выполнен — награда и следующий этап.
	private void CheckGoals()
	{
		if (GoalsActive && !Goals.AllDone && Goals.IsStageComplete(BlackHoles)) CompleteGoalStage();
	}

	// Награда текущего этапа (GDD «Задания ЧД и расширение»): открыть рецепты,
	// кольцо чанков, поставить шаблоны в свободные чанки нового кольца; затем
	// следующий этап (прогресс — с текущих счётчиков ЧД).
	private void CompleteGoalStage()
	{
		int stage = Goals.Stage;
		var st = Goals.Current;
		GD.Print($"[NucleusLayer] задание {stage + 1} «{st.Name}» выполнено.");
		var reward = st.Reward;
		foreach (var id in reward.Recipes)
		{
			var recipe = Catalog.Get(id);
			if (recipe == null) { GD.PushWarning($"[NucleusLayer] награда: неизвестный рецепт «{id}» — пропущен."); continue; }
			if (Goals.OpenRecipe(id)) GD.Print($"[NucleusLayer] открыт рецепт «{recipe.Name}».");
		}
		bool shown = false;
		if (reward.Ring)
		{
			var ring = Territory.NextRing();
			OpenChunks(ring);
			PlaceRewardTemplates(reward.Templates, ring, Goals.PlacementSeed(stage));
			shown = ShowExpansion(ring);
			if (!shown) _territoryLayer?.RevealChunks(ring);
		}
		else if (reward.Templates.Count > 0)
			GD.PushWarning($"[NucleusLayer] награда этапа {stage + 1}: шаблоны без кольца не ставятся.");
		Goals.BeginStage(stage + 1, BlackHoles);
		if (!shown) _blackHoleLayer?.FlashAll();
		GD.Print(Goals.AllDone ? "[NucleusLayer] все задания выполнены." : $"[NucleusLayer] задание {Goals.Stage + 1}: «{Goals.Current.Name}».");
	}

	// Показ расширения камерой (см. ExpansionCameraShows). Номер расширения —
	// из размера территории: 2×2 — старт, 4×4 — первое, 6×6 — второе…
	// Логика (чанки, шаблоны) уже применена; здесь — только вид.
	private bool ShowExpansion(List<(int cx, int cy)> ring)
	{
		if (_camera == null || _territoryLayer == null || ring.Count == 0) return false;
		if (!Territory.TryGetBounds(out int x0, out int y0, out int x1, out int y1)) return false;
		int expansion = (System.Math.Max(x1 - x0, y1 - y0) + 1 - 2) / 2;
		if (expansion < 1 || expansion > ExpansionCameraShows) return false;
		_territoryLayer.HoldChunks(ring);
		int session = _showSession;
		_camera.ShowRect(ChunkRectWorld(x0, y0, x1, y1), ExpansionFlightSeconds, () =>
		{
			if (session != _showSession) return;
			_territoryLayer.RevealChunks(ring);
			GetTree().CreateTimer(TerritoryLayer.RevealSeconds * 0.5f).Timeout += () =>
			{
				if (session == _showSession) _blackHoleLayer?.FlashAll();
			};
		});
		return true;
	}

	// Меняется при очистке поля — отложенные шаги показа прежнего поля не выполняются.
	private int _showSession;

	// Прямоугольник чанков [x0..x1]×[y0..y1] в мировых координатах.
	private Rect2 ChunkRectWorld(int x0, int y0, int x1, int y1)
	{
		float chunkWorld = ChunkSize * CellSize;
		return new Rect2(x0 * chunkWorld, y0 * chunkWorld, (x1 - x0 + 1) * chunkWorld, (y1 - y0 + 1) * chunkWorld);
	}

	// Шаблоны награды — в случайные места нового кольца (seed — явный): шаблон
	// целиком внутри кольца, не пересекается с другими шаблонами и с чанками,
	// где уже что-то есть. Места нет — сообщение, шаблон пропускается.
	private void PlaceRewardTemplates(List<string> paths, List<(int cx, int cy)> ring, ulong seed)
	{
		if (paths.Count == 0) return;
		var free = new HashSet<(int cx, int cy)>();
		foreach (var c in ring) if (!ChunkHasContent(c.cx, c.cy)) free.Add(c);
		var rng = new Rng(seed);
		var candidates = new List<(int cx, int cy)>();
		foreach (var path in paths)
		{
			var t = LoadTemplate(path, out string error);
			if (t == null) { GD.PrintErr($"[NucleusLayer] награда: {error}"); continue; }
			int w = System.Math.Max(1, t.WidthChunks), h = System.Math.Max(1, t.HeightChunks);
			candidates.Clear();
			// Кандидаты — в порядке кольца (cy, cx): выбор зависит только от seed.
			foreach (var (cx, cy) in ring)
			{
				bool fits = true;
				for (int dy = 0; dy < h && fits; dy++)
					for (int dx = 0; dx < w && fits; dx++)
						fits = free.Contains((cx + dx, cy + dy));
				if (fits) candidates.Add((cx, cy));
			}
			if (candidates.Count == 0)
			{
				GD.PushWarning($"[NucleusLayer] награда: для шаблона {path} ({w}×{h}) нет места в новом кольце — пропущен.");
				continue;
			}
			var at = candidates[rng.NextInt(candidates.Count)];
			for (int dy = 0; dy < h; dy++)
				for (int dx = 0; dx < w; dx++)
					free.Remove((at.cx + dx, at.cy + dy));
			PlaceTemplate(t, at.cx, at.cy, path);
		}
	}

	// В чанке есть атом, источник, ЧД или звезда.
	private bool ChunkHasContent(int cx, int cy)
	{
		if (_chunks.TryGetValue((cx, cy), out var chunk) && chunk.Nuclei.Count > 0) return true;
		int row0 = cy * ChunkSize, col0 = cx * ChunkSize;
		if (BlackHoles.Overlaps(row0, col0, ChunkSize) || Stars.Overlaps(row0, col0, ChunkSize)) return true;
		foreach (var layer in _energyClusterLayers)
			foreach (var (row, col) in layer.EnumerateCells())
				if (ChunkOf(row) == cy && ChunkOf(col) == cx) return true;
		return false;
	}

	// Клетка в закрытом чанке — красная вспышка чанка, true. Для отказов ввода.
	public bool DenyIfClosed(int row, int col)
	{
		if (IsCellOpen(row, col)) return false;
		_territoryLayer?.FlashChunk(ChunkOf(col), ChunkOf(row));
		return true;
	}
	private static readonly Color DeniedPreviewColor = new Color(1f, 0.2f, 0.2f, 0.5f);

	// Клетка свободна под звезду: как под ЧД, и не в ЧД. Пересечение звёзд — StarSet.
	public bool CanPlaceStarCell(int row, int col) =>
		CanPlaceBlackHoleCell(row, col) && !BlackHoles.TryGetAt(row, col, out _);

	// Можно ли накрыть клетку чёрной дырой: нет атома, источника, клетки порта
	// и молекулы в чанке (последние два — только при включённом слое 2).
	// Пересечение с другой ЧД проверяет BlackHoleSet.Add.
	public bool CanPlaceBlackHoleCell(int row, int col)
	{
		if (!IsCellOpen(row, col)) return false; // закрытый чанк (T009)
		if (_entAt.ContainsKey((row, col))) return false;
		if (Stars.TryGetAt(row, col, out _)) return false; // клетка звезды (T006)
		if (Ports != null && Ports.IsPortCell(row, col)) return false;
		if (_moleculeLayer != null && _moleculeLayer.IsCellTakenByLayer2(row, col)) return false;
		foreach (var layer in _energyClusterLayers)
			if (layer.HasClusterAt(row, col)) return false;
		return true;
	}
	// Выбран ли сейчас пресет ядра для установки — для подсветки запрета на
	// слое 1 (клетка в чанке с молекулой, см. MoleculeLayer).
	public bool HasSpawnSelection => _selectedSpawnTier.HasValue;

	// Есть ли в чанке хоть одно ядро слоя 1 — правило "клетка слоя 2 — либо
	// чанк, либо объект слоя 2" (см. MoleculeLayer).
	public bool ChunkHasNuclei(int cx, int cy) =>
		_chunks.TryGetValue((cx, cy), out var chunk) && chunk.Nuclei.Count > 0;

	// Свечение (T020) — только чтение для отрисовки: видимые чанки, версия
	// атомов чанка (растёт при каждой перестройке его MultiMesh) и рабочие
	// атомы Ж/К/С/серые (без обломков, вращателей, бросателей) — центр и тир.
	public IReadOnlyCollection<(int cx, int cy)> VisibleChunks => _visible;

	public bool TryGetChunkGlowVersion(int cx, int cy, out int version)
	{
		version = 0;
		if (!_chunks.TryGetValue((cx, cy), out var chunk)) return false;
		version = chunk.Version;
		return true;
	}

	public void CollectGlowAtoms(int cx, int cy, List<(Vector2 center, int tier)> into)
	{
		into.Clear();
		if (!_chunks.TryGetValue((cx, cy), out var chunk)) return;
		foreach (var n in chunk.Nuclei)
			if (!n.IsCargo && ChunkComposition.IsCountedTier(n.CoreTier)) into.Add((n.Center, n.CoreTier));
	}

	public IEnumerable<(int cx, int cy)> EnumerateOccupiedChunks()
	{
		foreach (var pair in _chunks)
			if (pair.Value.Nuclei.Count > 0) yield return pair.Key;
	}

	// Блок (T004, GDD «Порты и блоки») — чанк хотя бы с одним ядром игрока или
	// открытым портом. Источники (месторождения) блоком не делают. Признака
	// «дикое ядро» в коде нет (случайные ядра — только отладочный
	// RandomFillEnabled), поэтому все ядра считаются ядрами игрока.
	// Единственное место истины: видимость портов (PortLayer) и блоки слоя 2
	// (MoleculeLayer). На симуляцию не влияет.
	public bool IsBlock(int cx, int cy) =>
		ChunkHasNuclei(cx, cy) || (Ports != null && Ports.HasOpenPort(cx, cy));

	// Все блоки, каждый ровно один раз. Порядок — только для отрисовки.
	public IEnumerable<(int cx, int cy)> EnumerateBlocks()
	{
		foreach (var chunk in EnumerateOccupiedChunks()) yield return chunk;
		if (Ports == null) yield break;
		foreach (var chunk in Ports.EnumerateOpenPortChunks())
			if (!ChunkHasNuclei(chunk.cx, chunk.cy)) yield return chunk;
	}

	private struct RingSlot
	{
		public bool Exists;   // false — физического слота тут вообще нет (не рисуется ни дыркой,
		                      // ни частицей, не участвует в передаче). Используется ядрами со
		                      // спавн-панели с числом гнёзд меньше 8 (см. BuildFixedRing) — у
		                      // обычных (случайно сгенерированных) ядер всегда true на все 8.
		public bool IsHole;   // значим, только если Exists
		public int ColorTier; // значим, только если Exists и !IsHole; у предмета — тир атома (0 Ж, 1 К, 2 С)
		public bool Locked;   // только что принятая частица — нельзя отдать дальше до следующего поворота этого ядра
		// Атом-предмет (T007, GDD «Атом-предмет»): вместо частицы в дырке лежит
		// атом-переносчик тира ColorTier. Едет по линии по тем же правилам, что
		// частица (материал на перенос не влияет). Значим, только если !IsHole.
		public bool IsItem;
		// T024: только вид — форма осколка (сырой байт хеша; кадр = Variant % кадров атласа).
		// Задаётся при рождении частицы и копируется при передаче; на законы не влияет,
		// в StateHash не входит, не сохраняется.
		public byte Variant;
	}

	// T024: вариант частицы — детерминированный целочисленный хеш (SplitMix64), без Random.
	private static byte ParticleVariant(long a, long b, long c) =>
		(byte)(Rng.Mix(Rng.Mix(((ulong)(uint)a << 32) | (uint)b) ^ (ulong)c) >> 56);

	private class NucleusEntity
	{
		// Номер создания (T014) — последний ключ фиксированного порядка обхода
		// (CanonicalOrder): различает атом в пути (числится на старой клетке) и
		// атом, вставший в освободившуюся клетку.
		public long Id;
		public int Row, Col; // мировые координаты клетки — ключ в _entAt
		// Работа по событиям (T014): место в _byTier, отметки «изменился» и
		// «в очереди прохода A/B», флаг «в _activeSet», «рядом с источником».
		public int TierIndex = -1;
		public long OrderKey;
		public long ClaimEpoch; public int ClaimMask; // занятые на тике стороны (IsClaimed/Claim) // CanonicalOrder без Id: чанк и клетка в чанке (UpdateOrderKey)
		public long ChangedEpoch = -1, PassStampA = -1, PassStampB = -1;
		public bool IsActiveSim;
		public bool NearSource;
		public Vector2 Center;
		// CoreTier == GrayCoreTier — экспериментальное "серое" ядро (см.
		// [Export] GrayCoreTier в шапке файла): для рендера и скорости
		// вращения это просто ещё один обычный тир (своя строка в атласе
		// палитр — уже серая картинка, PalettePaths[GrayCoreTier], и своя
		// запись в TierTicks) — никакого отдельного флага/ветвления в рендере
		// не нужно. Единственное, что у него особенное, — игровое правило в
		// ColorAccepted: он принимает частицу любого цвета безусловно,
		// независимо от RequireColorMatch/RequireOwnColorTier.
		public int CoreTier;
		public int Dir; // направление вращения (для requireMatchingSpin); пока = SpinDirection у всех
		// Ring индексирован ФИЗИЧЕСКИМ слотом (k=0..7 — тот же индекс, что и в
		// рендере: физический слот k всегда рисуется под углом baseAngle+k*45°
		// и НИКОГДА не переставляется поворотом — иначе, помимо непрерывного
		// вращения угла, содержимое ещё и прыгало бы между индексами, что и
		// давало эффект "телепортации" частицы в соседнюю дырку). Ориентация
		// кольца (какой физический слот сейчас смотрит на какую сторону света)
		// НЕ хранится тут как отдельное состояние — раньше это было
		// накопительное поле RotationOffset, стартующее с 0 в момент спавна, и
		// из-за этого ядра одного тира, появившиеся на поле в разные моменты,
		// расходились по фазе (см. DiscreteRotationOffset). Теперь ориентация
		// — чистая функция (_globalTick, CoreTier), одинаковая для всех ядер
		// тира одновременно, независимо от истории конкретного ядра.
		public RingSlot[] Ring; // 8 физических слотов
		// Тик, начиная с которого ЭТОМУ ядру снова можно захватывать энергию из
		// источника (см. Шаг 3 в SimTick). Раньше вместо этого поля вся попытка
		// захвата была завёрнута в общее условие "_globalTick % _energyCaptureTicks
		// == 0" — но т.к. _energyCaptureTicks кратен полному периоду вращения
		// кольца ЛЮБОГО тира (128 = 8*16 = 8*8*2 = 8*4*4), проверка каждый раз
		// приходилась на ОДНУ И ТУ ЖЕ фазу поворота — ядро вечно "видело" только
		// одну и ту же дырку в нужном направлении и захват срабатывал максимум
		// один раз (стробоскопический эффект). Индивидуальный кулдаун на ядро
		// (а не глобальный тик-модуль на всех сразу) убирает эту привязку к фазе.
		public long NextCaptureTick;
		public int LocalIndex; // индекс в WorldChunk.Nuclei — для адресации инстансов MultiMesh (localIndex*8+slot)

		// --- перемещение по сетке (см. RotatorCoreTier/StartMove/FinishArrivedMoves) ---
		// Пока IsMoving — ядро физически едет из (MoveFromRow,MoveFromCol) в
		// (MoveToRow,MoveToCol) за MoveDurationTicks тиков начиная с
		// MoveStartTick; Row/Col/Center у него в этот момент ещё указывают на
		// СТАРУЮ клетку (обновляются только в момент прибытия, см.
		// FinishArrivedMoves) — и на время переезда ядро полностью изъято из
		// _entAt (см. StartMove), поэтому автоматически невидимо как сосед ни
		// для передачи частиц, ни для захвата, ни для порта, ни для ПКМ
		// удаления — что и требуется ("не взаимодействует ни с кем на время
		// переезда"), без отдельных проверок в каждом месте, которое ходит
		// через _entAt.
		//
		// MoveDurationTicks — это длительность ТОЛЬКО самого движения, без
		// какого-либо "ожидания" внутри (было иначе — см. историю в
		// StartMove/MaybeTriggerRotatorRotation: удвоение длительности здесь
		// держало ядро изъятым из _entAt ещё и на время ожидания, из-за чего
		// повёрнутые ядра почти не удалялись ПКМ). "Ждать в точке поворота
		// столько же, сколько занял сам поворот" теперь получается снаружи,
		// из периода срабатывания ротатора (см. MaybeTriggerRotatorRotation) —
		// ядро просто доезжает и сразу становится обычным, полностью
		// интерактивным ядром до следующего толчка.
		public bool IsMoving;
		public int MoveFromRow, MoveFromCol, MoveToRow, MoveToCol;
		public Vector2 MoveFromCenter, MoveToCenter;
		public long MoveStartTick;
		public int MoveDurationTicks;

		// Если MoveIsCircular — рендер интерполирует не по прямой между
		// MoveFromCenter/MoveToCenter, а по дуге окружности радиусом
		// MoveArcRadius с центром в MoveArcPivot, от MoveArcStartAngle на
		// MoveArcSweepAngle радиан (см. EffectiveCenter). Используется для
		// поворота на 2 или 4 фазы (90°/180°) вокруг поворачивателя — по
		// заданию, чтобы сосед не "срезал" по прямой мимо самого
		// поворачивателя, а визуально обходил его по кругу (см.
		// TriggerRotatorRotation). При повороте на 1 фазу (45°) остаётся
		// обычная прямая интерполяция — MoveIsCircular=false.
		public bool MoveIsCircular;
		public Vector2 MoveArcPivot;
		public float MoveArcRadius;
		public float MoveArcStartAngle;
		public float MoveArcSweepAngle;

		// --- полёт после толчка бросателем (см. ThrowerCoreTier/EvaluateFlightStep) ---
		// PendingFlightDir — выставляется TriggerRotatorRotation ДО StartMove,
		// когда толкающее ядро — бросатель: сам толчок едет как обычный
		// поворотный переезд (в т.ч. по дуге, если сдвиг больше 1 фазы), а
		// направление, в котором нужно полететь ДАЛЬШЕ после его завершения,
		// нужно сохранить где-то до момента прибытия — переезд не хранит
		// "зачем" он едет. Забирается и обнуляется в FinishArrivedMoves ровно
		// один раз, в момент прибытия.
		public int? PendingFlightDir;
		// IsFlying=true — ядро сейчас находится в режиме самостоятельного
		// прямолинейного полёта (после того как PendingFlightDir уже
		// подхвачен): едет по клетке за ThrowTicksPerCell тиков в направлении
		// FlightDir, пока не сработает одно из условий окончания полёта (см.
		// EvaluateFlightStep) — обычное ядро по соседству, исчерпанная
		// дистанция или занятая следующая клетка. Технически всё
		// то же самое перемещение (IsMoving/StartMove/EffectiveCenter), что и у
		// обычного толчка — IsFlying лишь помечает, что после прибытия в
		// FinishArrivedMoves нужно снова вызвать EvaluateFlightStep, а не
		// просто оставить ядро стоять.
		public bool IsFlying;
		public int FlightDir; // компас-индекс (см. Adj8), в каком направлении летит
		public int FlightCellsRemaining; // сколько клеток ещё МОЖНО пролететь, прежде чем исчезнуть само (см. ThrowMaxDistance)

		// --- "сон" свежепоставленного ядра (см. TryPlaceNucleus/
		// WakeSleepingNuclei) --- ядро, поставленное НЕ ровно в момент, когда
		// его собственная фаза поворота (DiscreteRotationOffset) равна 0, до
		// этого момента исключено из _activeSet (не крутится, не передаёт/не
		// принимает частицы, не толкает и не захватывает как ротатор/
		// бросатель) — а рендер кольца рисует его "замороженным" на фазе 0
		// (см. UpdateChunkVisuals), т.е. ровно так, как её расставил
		// BuildFixedRing. AsleepUntilTick — ближайший тик (может быть равен
		// текущему — тогда сна нет вовсе), на котором фаза этого КОНКРЕТНОГО
		// тира естественным образом становится 0 — просыпается без всякого
		// визуального скачка, т.к. живая формула в этот самый момент и так
		// даёт офсет 0.
		public long AsleepUntilTick;

		// --- мгновенная передача между двумя соседними вращателями/
		// бросателями (см. TryHandOffSharedOccupant) --- когда клетка,
		// занятая этим ядром, оказывается ОДНОВРЕМЕННО активным
		// направлением ДВУХ разных активных вращателей/бросателей сразу
		// (иначе они триггерятся синхронно на одни и те же глобальные тики,
		// см. MaybeTriggerRotatorRotation, и общий сосед доставался бы
		// всегда только ОДНОМУ из них — никогда второму), ядро немедленно, а
		// не по расписанию, продвигается на шаг вокруг кольца ДРУГОГО —
		// ровно как у передачи частиц (Шаг 2), только для целого ядра.
		// HandoffLastGiver — кто именно последним "отдал" это ядро таким
		// образом: следующая передача НЕ имеет права вернуть ядро обратно
		// ЕМУ ЖЕ (блокировка обратного захвата, по заданию клиента) — но не
		// блокирует передачу ДАЛЬШЕ, третьему вращателю в цепочке.
		public NucleusEntity HandoffLastGiver;

		// Груз (T006, GDD «Груз и установленный атом»): атом, выложенный
		// звездой. Занимает клетку (_entAt) — его носят вращатели и бросатели,
		// ЧД захватывает, — но не в _activeSet: не вращается, частиц не берёт и
		// не отдаёт (соседи его пропускают), не крутит и не бросает. Рисуется
		// приглушённым, кольцо стоит на фазе 0. ЛКМ — стать рабочим (ActivateCargo).
		public bool IsCargo;

		// Перекрёсток (T010): не null — атом в режиме «орбитали», частицы едут по
		// осям Cross, а кольцо Ring пустое (только число гнёзд — для ёмкости оси и
		// возврата в обычный режим). Переключение — ToggleCrossroad.
		public Crossroad Cross;
	}

	private class WorldChunk
	{
		public MultiMeshInstance2D Node;
		public MultiMeshInstance2D HoleNode;
		public MultiMeshInstance2D ParticleNode;
		public Rect2 WorldRect;
		public List<NucleusEntity> Nuclei;
		// Буферы MultiMesh тел, дырок и частиц (T014) — пишутся целиком, см. RebuildChunkMeshes.
		public float[] BodyBuf, HoleBuf, ParticleBuf;
		public bool Dots; // тела нарисованы точками (дальний зум, AtomDotZoom)
		public int Cx, Cy;
		public int Version; // +1 при каждой перестройке (свечение T020)
	}

	private readonly Dictionary<(int cx, int cy), WorldChunk> _chunks = new();
	private HashSet<(int cx, int cy)> _visible = new();

	// Настоящая модель поля: что физически существует в каждой клетке —
	// нужно для поиска соседей при передаче частиц (независимо от чанков).
	private readonly Dictionary<(int row, int col), NucleusEntity> _entAt = new();
	// ВСЕ живые ядра — участвуют в симуляции (SimTick: поворот, передача,
	// захват энергии, обмен с портами) независимо от того, виден ли
	// их чанк камере. Раньше это множество наполнялось/чистилось по
	// видимости чанка в _Process (ядра за кадром экрана были полностью
	// "заморожены") — по факту это был баг, а не намеренное поведение:
	// _activeSet отвечал одновременно и за "что рисовать", и за "что
	// тикать", хотя это два разных вопроса. Теперь наполняется/чистится
	// только в местах создания/удаления ядра (PlaceNucleusAt,
	// RemoveNucleusAt, генерация чанка) — видимость (_visible) осталась
	// чисто рендерным понятием и на это множество больше не влияет.
	//
	// T014: порядок обхода фиксирован (CanonicalOrder: чанк, клетка, номер
	// создания) — при споре за дырку побеждает тот, кто раньше в этом порядке,
	// одинаково от запуска к запуску. Ключ зависит от Row/Col — менять клетку
	// атома только через SetEntityCell.
	private readonly SortedSet<NucleusEntity> _activeSet;
	private long _nextEntityId;
	private readonly List<NucleusEntity> _spinnerScratch = new(); // вращатели и бросатели на этом тике (SimTick, шаг 1)

	public NucleusLayer()
	{
		_canonical = new CanonicalOrder();
		_activeSet = new SortedSet<NucleusEntity>(_canonical);
		_spinners = new SortedSet<NucleusEntity>(_canonical);
		_captureCandidates = new SortedSet<NucleusEntity>(_canonical);
		_passQueue = new PriorityQueue<NucleusEntity, NucleusEntity>(_canonical);
	}

	// Фиксированный порядок обхода атомов (T014): чанк (cy, cx), клетка (row, col), Id.
	private sealed class CanonicalOrder : IComparer<NucleusEntity>
	{
		public int Compare(NucleusEntity a, NucleusEntity b)
		{
			int c = a.OrderKey.CompareTo(b.OrderKey);
			return c != 0 ? c : a.Id.CompareTo(b.Id);
		}

		public static int FloorDiv(int v, int d) => v >= 0 ? v / d : (v - d + 1) / d;
	}

	// Ключ порядка (T014): (cy, cx, строка в чанке, столбец в чанке) в одном long —
	// сравнение дешевле, чем пересчёт чанка на каждом сравнении. Пересчитывается
	// при создании (RegisterEntity) и смене клетки (SetEntityCell).
	private void UpdateOrderKey(NucleusEntity n)
	{
		int cs = ChunkSize;
		int cy = CanonicalOrder.FloorDiv(n.Row, cs), cx = CanonicalOrder.FloorDiv(n.Col, cs);
		// Биты: cy 16 | cx 16 | строка в чанке 15 | столбец 16 — знаковый бит не задет.
		n.OrderKey = ((long)(cy + 0x8000) << 47) | ((long)(cx + 0x8000) << 31)
			| ((long)(n.Row - cy * cs) << 16) | (long)(n.Col - cx * cs);
	}

	// Порядок по номеру создания — для наборов, где клетка не ключ (в пути, спящие, груз).
	private sealed class IdOrder : IComparer<NucleusEntity>
	{
		public static readonly IdOrder Instance = new();
		public int Compare(NucleusEntity a, NucleusEntity b) => a.Id.CompareTo(b.Id);
	}

	// Смена клетки атома (T014): ключ порядка _activeSet зависит от Row/Col —
	// атом вынимается из упорядоченных наборов, меняет клетку и центр, возвращается.
	private void SetEntityCell(NucleusEntity n, int row, int col)
	{
		bool active = _activeSet.Remove(n);
		bool spinner = IsSpinnerTier(n.CoreTier) && _spinners.Remove(n);
		if (n.NearSource) _captureCandidates.Remove(n);
		n.NearSource = false;
		int oldCx = ChunkOf(n.Col), oldCy = ChunkOf(n.Row);
		n.Row = row;
		n.Col = col;
		UpdateOrderKey(n);
		n.Center = new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f);
		if (active) _activeSet.Add(n);

		// Атом числится в списке чанка своей клетки: при смене чанка — перенос
		// между списками и пересборка MultiMesh обоих (раньше это делало только
		// прибытие, а поимка летящего вращателем оставляла атом в старом списке).
		int newCx = ChunkOf(col), newCy = ChunkOf(row);
		if (oldCx != newCx || oldCy != newCy)
		{
			if (_chunks.TryGetValue((oldCx, oldCy), out var oldChunk) && oldChunk.Nuclei.Remove(n))
			{
				for (int i = 0; i < oldChunk.Nuclei.Count; i++) oldChunk.Nuclei[i].LocalIndex = i;
				RebuildChunkMeshes(oldChunk);
			}
			var newChunk = GetOrCreateChunk(newCx, newCy);
			n.LocalIndex = newChunk.Nuclei.Count;
			newChunk.Nuclei.Add(n);
			RebuildChunkMeshes(newChunk);
		}
		if (spinner) _spinners.Add(n);
		UpdateCaptureCandidate(n);
		Touch(n);
	}

	// --- работа по событиям (T014) ---
	// Результат бит в бит как у полного обхода в фиксированном порядке
	// (FullScanDebug = true — старый путь, для сверки: --sim-selfcheck).
	//
	// Передача (проходы A/B) между атомами n и m может впервые стать возможной,
	// только если n или m изменился: кольцо, поворот (ориентация и снятие
	// блокировки), клетка, режим. Всё это отмечает Touch. В проходы идут атомы,
	// изменившиеся с начала передачи на прошлом тике (_changedPrev), их соседи и
	// все атомы в пути (они видят соседей со старой клетки, а их — никто).
	// Обход — в том же порядке CanonicalOrder; атом, изменившийся во время
	// прохода, добавляет себя и соседей: дальше по порядку — в текущий проход,
	// всех — в проход B (как полный обход, который прошёл бы их позже).

	// Отладка: полный обход всех атомов вместо работы по событиям (Shift+F3).
	[Export] public bool FullScanDebug = false;

	private readonly CanonicalOrder _canonical;
	// Все атомы (рабочие, спящие, груз) по тиру — поворот на тике t трогает только
	// тиры с t % период == 0. Порядок внутри списка не важен (поворот атома
	// независим от других), важен только порядок проходов.
	private readonly List<List<NucleusEntity>> _byTier = new();
	// Вращатели и бросатели (рабочие и спящие) — свой список, фиксированный порядок.
	private readonly SortedSet<NucleusEntity> _spinners;
	// Атомы, у которых рядом месторождение (NearSource) — кандидаты захвата энергии.
	private readonly SortedSet<NucleusEntity> _captureCandidates;
	private int _clusterVersion = -1;

	// Атомы, вытесненные из _entAt откатом прибытия (см. FinishArrivedMoves):
	// как атомы в пути, видят соседей, а их — никто; в проходы идут всегда.
	private readonly SortedSet<NucleusEntity> _detachedSet = new(IdOrder.Instance);

	private List<NucleusEntity> _changed = new();
	private List<NucleusEntity> _changedPrev = new();
	private long _changeEpoch;

	private readonly PriorityQueue<NucleusEntity, NucleusEntity> _passQueue;
	private readonly List<NucleusEntity> _passBList = new();
	private readonly List<NucleusEntity> _passSorted = new(); // основа прохода, отсортирована; добавленные по ходу — в _passQueue
	private int _passPhase; // 0 — не в проходе, 1 — проход A, 2 — проход B
	private NucleusEntity _passCur;
	private long _passSerial, _serialA, _serialB;

	private List<NucleusEntity> TierList(int tier)
	{
		while (_byTier.Count <= tier) _byTier.Add(new List<NucleusEntity>());
		return _byTier[tier];
	}

	// Новый атом в поле (любой: рабочий, спящий, груз) — вызывается один раз при создании.
	private void RegisterEntity(NucleusEntity n)
	{
		UpdateOrderKey(n);
		var list = TierList(n.CoreTier);
		n.TierIndex = list.Count;
		list.Add(n);
		if (IsSpinnerTier(n.CoreTier)) _spinners.Add(n);
		UpdateCaptureCandidate(n);
		Touch(n);
	}

	private void UnregisterEntity(NucleusEntity n)
	{
		if (n.TierIndex >= 0)
		{
			var list = TierList(n.CoreTier);
			int last = list.Count - 1;
			var moved = list[last];
			list[n.TierIndex] = moved;
			moved.TierIndex = n.TierIndex;
			list.RemoveAt(last);
			n.TierIndex = -1;
		}
		if (IsSpinnerTier(n.CoreTier)) _spinners.Remove(n);
		if (n.NearSource) { _captureCandidates.Remove(n); n.NearSource = false; }
	}

	private void AddActive(NucleusEntity n)
	{
		_activeSet.Add(n);
		n.IsActiveSim = true;
		Touch(n);
	}

	private void RemoveActive(NucleusEntity n)
	{
		_activeSet.Remove(n);
		n.IsActiveSim = false;
	}

	// Атом изменился (кольцо, поворот, клетка, режим) — на следующем тике он и
	// его соседи идут в проходы передачи; во время прохода — ещё и в текущий.
	private void Touch(NucleusEntity n)
	{
		if (n.ChangedEpoch != _changeEpoch)
		{
			n.ChangedEpoch = _changeEpoch;
			_changed.Add(n);
		}
		if (_passPhase != 0) AddToPassWithNeighbors(n);
	}

	private void AddToPassWithNeighbors(NucleusEntity x)
	{
		AddToPass(x);
		for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
		{
			var (dr, dc) = Adj8[OrthogonalSlots[idx]];
			if (_entAt.TryGetValue((x.Row + dr, x.Col + dc), out var y)) AddToPass(y);
		}
	}

	private void AddToPass(NucleusEntity y)
	{
		bool after = _passCur == null || _canonical.Compare(y, _passCur) > 0;
		if (y.PassStampB != _serialB)
		{
			y.PassStampB = _serialB;
			_passBList.Add(y);
			if (_passPhase == 2 && after) _passQueue.Enqueue(y, y);
		}
		if (_passPhase == 1 && after && y.PassStampA != _serialA)
		{
			y.PassStampA = _serialA;
			if (_passCur == null) _passSorted.Add(y); // сбор основы прохода A
			else _passQueue.Enqueue(y, y);
		}
	}

	private static bool TakesPartInPasses(NucleusEntity n) => n.IsActiveSim && !(n.IsMoving && n.IsFlying);

	// Проходы A и B только по изменившимся атомам и их соседям (см. выше).
	private void RunEventPasses()
	{
		_serialA = ++_passSerial;
		_serialB = ++_passSerial;
		_passBList.Clear();
		_passSorted.Clear();
		_passQueue.Clear();
		_passCur = null;
		_passPhase = 1;
		foreach (var x in _changedPrev) AddToPassWithNeighbors(x);
		foreach (var m in _movingSet) AddToPass(m);
		foreach (var m in _detachedSet) AddToPass(m);
		_passSorted.Sort(_canonical);
		RunPass(pull: true);

		_passPhase = 2;
		_passCur = null;
		_passSorted.Clear();
		_passSorted.AddRange(_passBList);
		_passSorted.Sort(_canonical);
		RunPass(pull: false);
		_passPhase = 0;
		_passCur = null;
	}

	// Обход прохода в порядке CanonicalOrder: слияние отсортированной основы
	// (_passSorted) и атомов, добавленных по ходу (_passQueue, все — дальше текущего).
	private void RunPass(bool pull)
	{
		int i = 0;
		while (true)
		{
			NucleusEntity n;
			bool fromList = i < _passSorted.Count;
			if (_passQueue.TryPeek(out var head, out _))
				n = fromList && _canonical.Compare(_passSorted[i], head) < 0 ? _passSorted[i++] : _passQueue.Dequeue();
			else if (fromList)
				n = _passSorted[i++];
			else
				break;
			_passCur = n;
			if (!TakesPartInPasses(n)) continue;
			if (pull) PullPass(n); else PushPass(n);
		}
	}

	// Начало передачи на тике: изменения «с прошлой передачи» уходят в _changedPrev.
	private void BeginChangeEpoch()
	{
		(_changedPrev, _changed) = (_changed, _changedPrev);
		_changed.Clear();
		_changeEpoch++;
	}

	// Поворот тиров, у которых на этом тике шаг: снятие блокировки (и шаг
	// перекрёстка) у рабочих атомов, отметка всех (ориентация сменилась и у спящих).
	private void RotateDueTiers(bool unlock)
	{
		for (int tier = 0; tier < _byTier.Count; tier++)
		{
			int ticks = OwnRotationTicks(tier);
			if (ticks <= 0 || _globalTick % ticks != 0) continue;
			foreach (var n in _byTier[tier])
			{
				if (n.IsCargo) continue; // груз не вращается и не передаёт (T006)
				if (unlock && n.IsActiveSim && !(n.IsMoving && n.IsFlying)) OnRotationTick(n);
				Touch(n);
			}
		}
	}

	// Рядом ли месторождение — то же условие, что в TryCaptureEnergy (без кулдауна и гнёзд).
	private bool IsNearSource(NucleusEntity n)
	{
		for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
		{
			var (dr, dc) = Adj8[OrthogonalSlots[idx]];
			Vector2 neighborWorld = n.Center + new Vector2(dc, dr) * CellSize;
			foreach (var layer in _energyClusterLayers)
				if (layer.HasClusterAt(Mathf.FloorToInt(neighborWorld.Y / layer.CellSize), Mathf.FloorToInt(neighborWorld.X / layer.CellSize)))
					return true;
		}
		return false;
	}

	private void UpdateCaptureCandidate(NucleusEntity n)
	{
		bool near = IsNearSource(n);
		if (near == n.NearSource) return;
		n.NearSource = near;
		if (near) _captureCandidates.Add(n); else _captureCandidates.Remove(n);
	}

	// Месторождения поменялись (установка, удаление, загрузка) — пересчёт всех кандидатов.
	private void RefreshCaptureCandidatesIfNeeded()
	{
		int version = 0;
		foreach (var layer in _energyClusterLayers) version += layer.Version;
		if (version == _clusterVersion) return;
		_clusterVersion = version;
		foreach (var list in _byTier)
			foreach (var n in list)
				UpdateCaptureCandidate(n);
	}
	// Подмножество _activeSet, которое сейчас физически едет между клетками
	// (IsMoving=true) — отдельный набор, чтобы каждый тик проверять "кто уже
	// доехал" (FinishArrivedMoves) без обхода ВСЕХ активных ядер, а только
	// реально движущихся (их обычно на порядки меньше).
	private readonly SortedSet<NucleusEntity> _movingSet = new(IdOrder.Instance);
	// Свежепоставленные ядра, которые ещё "спят" (см. NucleusEntity.
	// AsleepUntilTick/TryPlaceNucleus) — НЕ входят в _activeSet (не тикают),
	// пока их собственная фаза поворота не станет 0 сама по себе.
	// Отдельный набор ровно по тому же принципу, что и _movingSet — раз в
	// тик проверяем только реально спящих, а не все живые ядра сразу (см.
	// WakeSleepingNuclei).
	private readonly SortedSet<NucleusEntity> _sleepingSet = new(IdOrder.Instance);
	// Груз (T006, см. NucleusEntity.IsCargo) — не тикает; набор нужен для
	// сохранения и очистки, как _sleepingSet.
	private readonly SortedSet<NucleusEntity> _cargoSet = new(IdOrder.Instance);

	private Texture2D _coreTexture;
	private Texture2D _dotTexture; // T014: атом-точка на дальнем зуме
	private bool _atomDots;
	// T015: атомы рисуются (зум не ниже NebulaZoom); точки — свой материал с
	// параметром fade для перехода к туманностям.
	private bool _atomsShown = true;
	private ShaderMaterial _dotMaterial;
	private float _dotFade = 1f;
	private NebulaLayer _nebulaLayer;
	private HashSet<(int cx, int cy)> _newVisible = new();
	private Texture2D _holeTexture;
	private Texture2D _particleTexture;
	private ShaderMaterial _particleMaterial;

	// T023/T024: атлас в ряд — кадры осколка (полоса ParticleSpritePath), последним — шар
	// атома-предмета (16×16 → 64×16). Нет шара или он другого размера — последний кадр — осколок 0.
	[Export] public string ItemSpritePath = "res://Resources/Textures/atom_item_gray_16px.png";
	private int _shardFrames = 1; // кадров осколка в атласе частиц (кадр предмета — _shardFrames)

	private Texture2D BuildParticleAtlas()
	{
		var strip = LoadImageOf(ParticleSpritePath);
		if (strip == null) return null;
		int side = strip.GetHeight();
		_shardFrames = Mathf.Max(1, strip.GetWidth() / side);
		var ball = LoadImageOf(ItemSpritePath);
		strip.Convert(Image.Format.Rgba8);
		if (ball == null || ball.GetSize() != new Vector2I(side, side))
		{
			GD.PrintErr("NucleusLayer: спрайт атома-предмета не загрузился — рисуется осколком.");
			ball = strip.GetRegion(new Rect2I(0, 0, side, side));
		}
		ball.Convert(Image.Format.Rgba8);
		var atlas = Image.CreateEmpty(side * (_shardFrames + 1), side, false, Image.Format.Rgba8);
		atlas.BlitRect(strip, new Rect2I(0, 0, side * _shardFrames, side), Vector2I.Zero);
		atlas.BlitRect(ball, new Rect2I(Vector2I.Zero, ball.GetSize()), new Vector2I(side * _shardFrames, 0));
		return ImageTexture.CreateFromImage(atlas);
	}

	// Кадр атласа частиц: предмет — шар (последний), частица — свой вариант осколка.
	private float ParticleFrame(bool isItem, byte variant) => isItem ? _shardFrames : variant % _shardFrames;

	// Картинка из импортированной текстуры; нет импорта (новый файл до открытия редактора) — прямо из PNG.
	private static Image LoadImageOf(string path)
	{
		var tex = GD.Load<Texture2D>(path);
		if (tex != null) return tex.GetImage();
		var img = new Image();
		return img.Load(ProjectSettings.GlobalizePath(path)) == Error.Ok ? img : null;
	}

	private ShaderMaterial _material; // общий и для ядра, и для частиц — один и тот же шейдер/атлас палитр
	private QuadMesh _coreQuad;
	private QuadMesh _holeQuad;
	private QuadMesh _particleQuad;
	private RandomNumberGenerator _rng;
	private float _orbitRadius;
	private int _tierCount;
	private float _chunkWorldSize;
	private bool _ready;

	// Глобальные часы симуляции — независимы от FPS, шаг ровно PrototypeTickMs.
	private double _tickAccumulatorMs;
	private long _globalTick;
	// «Занятые» на этом тике стороны атомов (было HashSet<(атом, слот)>, T014 —
	// отметка у самого атома): эпоха + маска бит, ключ — SideKey (0..7 слот, 8..11 сторона перекрёстка).
	private long _claimEpoch = 1;
	private bool IsClaimed(NucleusEntity n, int key) => n.ClaimEpoch == _claimEpoch && (n.ClaimMask & (1 << key)) != 0;
	private void Claim(NucleusEntity n, int key)
	{
		if (n.ClaimEpoch != _claimEpoch) { n.ClaimEpoch = _claimEpoch; n.ClaimMask = 0; }
		n.ClaimMask |= 1 << key;
	}

	// --- пауза по пробелу (см. _Input/_Process/TryPlaceAtMouseIfSelected) ---
	// _paused=true — SimTick/_globalTick полностью заморожены (см. цикл в
	// _Process), но видимость чанков и рендер продолжают работать как обычно
	// (камеру можно двигать, а ядра остаются на месте с точно выставленной
	// фазой поворота, а не "подрагивают" по _subTickFraction). Пробел, когда
	// уже нажат (_pausePending, ещё не пауза) — отменяет запрос; когда уже
	// _paused — снимает паузу немедленно (для возобновления фазовая привязка
	// не нужна, она нужна только для ВХОДА в паузу).
	//
	// По заданию, пауза не включается мгновенно по нажатию, а только когда
	// _globalTick дойдёт до тика, на котором ВСЕ ядра (любого тира) стоят
	// ровно в позиции 0 (а не где-то в процессе поворота на 45°) — см.
	// _pauseAlignTicks.
	private bool _paused;
	private bool _pausePending;

	// Период (в тиках), на котором фаза поворота кольца ОДНОВРЕМЕННО равна 0
	// у ядер ЛЮБОГО тира: у тира с TierTicks[tier]=T текущий физический слот
	// возвращается к исходному (DiscreteRotationOffset==0) каждые T*8 тиков
	// (полный оборот кольца на 8 шагов по 45°) — а поскольку тиры могут
	// вращаться с разной скоростью (TierTicks различны), общая точка, где
	// это верно СРАЗУ для всех тиров — НОК (наименьшее общее кратное) всех
	// T*8, что равно 8*НОК(всех T) (см. Lcm/Gcd). Считается один раз в
	// _Ready из ТЕКУЩЕГО TierTicks — если значения в инспекторе поменяют,
	// период пересчитается при следующем запуске сцены.
	private long _pauseAlignTicks;
	// Доля пути (0..1) до следующего глобального тика — считается один раз за
	// кадр в _Process и переиспользуется везде, где нужна плавная
	// интерполяция по времени (угол поворота кольца в UpdateChunkVisuals,
	// позиция едущего ядра в EffectiveCenter), вместо пересчёта одной и той
	// же формулы в каждом месте по отдельности.
	private float _subTickFraction;

	// Измерение UPS — сколько раз в секунду реально отработал SimTick(),
	// скользящим окном примерно в 1 секунду реального времени (а не кадров).
	private double _upsWindowTimer;
	private int _upsWindowTicks;

	// Пересчитываются раз в кадр в _Process из текущего зума камеры —
	// независимо друг от друга, у дырок и частиц разные пороги отключения.
	private bool _holesVisible = true;
	private bool _particlesVisible = true;
	// H — ручной тумблер поверх автоматического скрытия по зуму: итоговая
	// видимость дырок — это (зум не ниже HoleHideZoom) И (не скрыто вручную).
	private bool _holesManuallyHidden;

	// Выбор ядра на панели спавна (см. NucleusSpawnPanel.SelectSpawnPreset) —
	// null, пока ничего не выбрано, тогда ЛКМ по полю ничего не делает.
	private int? _selectedSpawnTier;
	private int _selectedSpawnHoleCount;
	// Направление вращения (NucleusEntity.Dir), с которым будет установлено
	// СЛЕДУЮЩЕЕ ядро (см. R в _Input) — отдельная от _selectedSpawnTier/
	// _selectedSpawnHoleCount ось выбора: тир/дырки и направление
	// переключаются независимо (тир — кликом по панели, направление — R), но
	// оба применяются к следующей установке разом (см. TryPlaceNucleus).
	// Стартует со SpinDirection (см. её комментарий), дальше живёт только
	// здесь. Пипетка (Q) тоже пишет сюда — см. PickNucleusUnderMouse.
	private int _currentSpinDirection;
	// Удержание ЛКМ — чтобы не приходилось кликать по каждой клетке отдельно
	// (см. _UnhandledInput/TryPlaceAtMouseIfSelected): пока кнопка зажата,
	// каждый кадр в _Process пробуем поставить ядро под курсором, но не
	// повторяем попытку для той же самой клетки, пока курсор из неё не ушёл
	// (иначе — спам одинаковых попыток на неподвижной мыши).
	private bool _leftMouseHeld;
	private (int row, int col)? _lastPlacedCell;
	// ПКМ — удаление ядра под курсором (см. TryRemoveAtMouse), тем же
	// принципом удержания, что и установка: можно провести мышью с зажатой
	// ПКМ, чтобы стереть сразу несколько ядер, не кликая по каждому отдельно.
	private bool _rightMouseHeld;
	private (int row, int col)? _lastRemovedCell;
	// Удаление с удержанием (T026): логика — RemoveHold, вид — RemoveHoldLayer.
	// Время — реальное (delta кадра), в симуляцию и StateHash не входит.
	[Export] public float RemoveHoldSecondsPerCell = 0.2f;
	// Толщина полоски прогресса в пикселях экрана.
	[Export] public float RemoveHoldBarPx = 4f;
	private readonly RemoveHold _removeHold = new();
	private RemoveHoldLayer _removeHoldLayer;
	// Отказ, уже показанный для объекта под курсором (вспышка раз на заход).
	private RemoveTarget? _lastDenied;
	// Полупрозрачный превью выбранного ядра под курсором — см.
	// UpdatePlacementPreview. ZIndex выше нуля, чтобы быть поверх чанков
	// (MultiMeshInstance2D чанков добавляются позже как дочерние узлы этого же
	// Node2D, и без ZIndex превью, созданный в _Ready, рисовался бы под ними).
	private Sprite2D _placementPreview;
	private Color[] _tierPreviewColors;
	// Слой энергии — полностью независимая структура данных (см.
	// EnergyLayer), но выбор на панели спавна взаимоисключающий: если выбран
	// тип энергии, выбор ядра нужно сбросить, и наоборот (иначе ЛКМ было бы
	// не ясно, что именно ставить). Ссылка нужна только для этого сброса.
	private EnergyLayer _energyLayer;
	// Второй, экспериментальный способ отображения энергии (см.
	// EnergyClusterLayer) — та же причина для ссылки, что и у _energyLayer:
	// взаимоисключающий сброс выбора. В сцене на каждый тир частиц — свой
	// отдельный узел с этим скриптом (см. шапку EnergyClusterLayer.cs), поэтому
	// список, а не одна ссылка; находится автоматически по типу скрипта среди
	// узлов того же родителя, а не по жёстко зашитым именам.
	private List<EnergyClusterLayer> _energyClusterLayers = new();
	// Чёрные дыры (T005) — объекты слоя 1; узел нужен для эффекта падения
	// (захват атома/частицы), инструмента установки и удаления ПКМ. Данные — BlackHoles.
	private BlackHoleLayer _blackHoleLayer;
	private StarLayer _starLayer; // звёзды (T006): инструмент, отрисовка, удаление ПКМ
	// Слой 2 — только для правила занятости: в чанк с молекулой объекты слоя 1 не ставятся.
	private MoleculeLayer _moleculeLayer;
	private PortLayer _portLayer; // подсветка клетки порта при попытке поставить ядро (T004)
	// Порядок гнёзд (симметричные раскладки 2/4/8) — см. RingMath.HolePriority.

	public override void _Ready()
	{
		var gridDraw = GetNode<GridDraw>("../GridLayer");
		CellSize = gridDraw.CellSize;
		_chunkWorldSize = ChunkSize * CellSize;
		_orbitRadius = CellSize * 0.5f * OrbitDiameterCoef;
		_energyCaptureTicks = (TierTicks.Length > 0 ? TierTicks[0] : 16) * 8;

		long lcmTicks = 1;
		foreach (var t in TierTicks) if (t > 0) lcmTicks = Lcm(lcmTicks, t);
		_pauseAlignTicks = lcmTicks * 8;

		SetProcessInput(true);
		SetProcessUnhandledInput(true);

		_coreTexture = GD.Load<Texture2D>(CoreSpritePath);
		_holeTexture = GD.Load<Texture2D>(HoleSpritePath);
		_particleTexture = BuildParticleAtlas();
		var shader = GD.Load<Shader>(ShaderPath);

		if (_coreTexture == null || shader == null || _holeTexture == null || _particleTexture == null)
		{
			GD.PrintErr("NucleusLayer: не загрузился один из спрайтов или шейдер — проверьте пути res://.");
			return;
		}

		var paletteAtlas = BuildPaletteAtlas(PalettePaths);
		if (paletteAtlas == null) return;

		_tierCount = PalettePaths.Length;
		PaletteAtlas = paletteAtlas;
		_material = new ShaderMaterial { Shader = shader };
		_material.SetShaderParameter("palette_tex", paletteAtlas);
		_dotMaterial = (ShaderMaterial)_material.Duplicate();
		// T023/T024: частицы чанка берут из атласа кадр осколка или шар предмета (INSTANCE_CUSTOM.w).
		_particleMaterial = (ShaderMaterial)_material.Duplicate();
		_particleMaterial.SetShaderParameter("atlas_frames", (float)(_shardFrames + 1));

		_coreQuad = new QuadMesh { Size = new Vector2(SpriteSize, SpriteSize) };
		_holeQuad = new QuadMesh { Size = new Vector2(HoleSpriteSize, HoleSpriteSize) };
		_particleQuad = new QuadMesh { Size = new Vector2(ParticleSpriteSize, ParticleSpriteSize) };
		_dotTexture = BuildDotTexture();

		_rng = new RandomNumberGenerator();
		_rng.Randomize();

		_tierPreviewColors = SampleTierColors(PalettePaths);
		_placementPreview = new Sprite2D
		{
			Texture = _coreTexture,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Visible = false,
			ZIndex = 100 // поверх чанков, см. комментарий у поля
		};
		AddChild(_placementPreview);
		// Туманности (T015) — первый дочерний узел: под чанками, звёздами и ЧД.
		_nebulaLayer = new NebulaLayer { Name = "NebulaLayer" };
		AddChild(_nebulaLayer);
		MoveChild(_nebulaLayer, 0);

		_energyLayer = GetNodeOrNull<EnergyLayer>("../TileMapLayer");
		_blackHoleLayer = GetNodeOrNull<BlackHoleLayer>("../BlackHoleLayer");
		// Слой 2 выключен (ViewLayer.Layer2Enabled, T005) — молекул и портов нет:
		// ссылки и PortSet не заводятся, клетки 7–8 свободны, обмена с портами нет.
		if (ViewLayer.Layer2Enabled)
		{
			_moleculeLayer = GetNodeOrNull<MoleculeLayer>("../MoleculeLayer");
			_portLayer = GetNodeOrNull<PortLayer>("../PortLayer");
		}
		foreach (var child in GetParent().GetChildren())
			if (child is EnergyClusterLayer clusterLayer)
				_energyClusterLayers.Add(clusterLayer);

		_currentSpinDirection = SpinDirection;
		Ports = ViewLayer.Layer2Enabled ? new PortSet(ChunkSize) : null;
		BlackHoles = new BlackHoleSet();
		Stars = new StarSet();
		Inventory = new Inventory { Sandbox = SandboxMode };
		Territory = new Territory();
		// Запуск — песочное поле (вся карта открыта): все рецепты открыты, цепочка стоит.
		Catalog = LoadCatalog();
		Goals = LoadGoals();
		Goals.OpenAllRecipes(Catalog);
		Goals.BeginStage(0, BlackHoles);
		// Затемнение закрытых чанков — отдельный узел поверх объектов слоя 1,
		// создаётся из кода (в сцене его нет).
		_territoryLayer = new TerritoryLayer { Name = "TerritoryLayer", Layer = this };
		_camera = GetNodeOrNull<CameraController>("../Camera2D");
		GetParent().CallDeferred(Node.MethodName.AddChild, _territoryLayer);
		// Кольца перекрёстков (T010) — тоже из кода, поверх атомов.
		_crossroadLayer = new CrossroadLayer { Name = "CrossroadLayer", Layer = this };
		GetParent().CallDeferred(Node.MethodName.AddChild, _crossroadLayer);
		// Круг и рамки удаления с удержанием (T026).
		_removeHoldLayer = new RemoveHoldLayer { Name = "RemoveHoldLayer", Layer = this, BarThicknessPx = RemoveHoldBarPx };
		GetParent().CallDeferred(Node.MethodName.AddChild, _removeHoldLayer);
		_starLayer = GetNodeOrNull<StarLayer>("../StarLayer");

		_ready = true;
		GD.Print($"[NucleusLayer] инициализирован. FillDensity={FillDensity}, ParticleFillChance={ParticleFillChance}.");
		// Сверка работы по событиям с полным обходом (T014) — только из командной строки.
		if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--sim-selfcheck") >= 0)
			Callable.From(RunSimSelfCheck).CallDeferred();
		foreach (var arg in OS.GetCmdlineUserArgs())
			if (arg.StartsWith("--render-bench="))
			{
				_benchZoom = float.Parse(arg.Substring(15), System.Globalization.CultureInfo.InvariantCulture);
				Callable.From(StartRenderBench).CallDeferred();
			}
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.H)
			{
				_holesManuallyHidden = !_holesManuallyHidden;
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.F9)
			{
				ReloadRecipes();
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.F3 && key.ShiftPressed)
			{
				FullScanDebug = !FullScanDebug;
				GD.Print($"[NucleusLayer] симуляция: {(FullScanDebug ? "полный обход (отладка)" : "по событиям")}.");
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.T)
			{
				RandomFillEnabled = !RandomFillEnabled;
				GD.Print($"[NucleusLayer] случайное заполнение новых чанков: {(RandomFillEnabled ? "включено" : "выключено")}.");
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.Q && !ViewLayer.IsLayer2)
			{
				PickNucleusUnderMouse();
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.M)
			{
				Inventory.Sandbox = !Inventory.Sandbox;
				GD.Print($"[NucleusLayer] режим: {(Inventory.Sandbox ? "песочница (установка бесплатна)" : "настоящий (установка тратит атомы из инвентаря)")}.");
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.O && !ViewLayer.IsLayer2)
			{
				// Отладка (T009): открыть чанк под курсором.
				var mouse = GetGlobalMousePosition();
				int cx = ChunkOf(Mathf.FloorToInt(mouse.X / CellSize)), cy = ChunkOf(Mathf.FloorToInt(mouse.Y / CellSize));
				if (OpenChunks(new[] { (cx, cy) }) == 0) GD.Print($"[NucleusLayer] чанк ({cx},{cy}) уже открыт.");
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.P)
			{
				// Отладка (T011): засчитать текущее задание.
				if (GoalsActive && !Goals.AllDone) CompleteGoalStage();
				else GD.Print("[NucleusLayer] засчитывать нечего: заданий нет или все выполнены.");
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.B && !ViewLayer.IsLayer2)
			{
				// Шаблон чанков (T009): два нажатия — два угла прямоугольника.
				TemplateCornerAtMouse();
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.Space)
			{
				TogglePause();
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.R && !ViewLayer.IsLayer2)
			{
				ToggleSpinDirectionUnderMouse();
				GetViewport().SetInputAsHandled();
			}
		}
	}

	// R — переключает направление вращения (NucleusEntity.Dir) ядра ПОД
	// КУРСОРОМ прямо сейчас (не пресет для будущей установки — это делает
	// пипетка, Q, см. PickNucleusUnderMouse) — тот же поиск, что и у
	// пипетки (см. FindNucleusUnderMouse), в т.ч. с допуском на едущие/
	// летящие ядра. Заодно синхронизирует _currentSpinDirection с новым
	// значением — чтобы следующая установка (ЛКМ) по умолчанию продолжила
	// тем же направлением, а не молча вернулась к старому.
	// Выход звезды — на следующую сторону по часовой (R, ЛКМ по печи, T027).
	// У звезды без рецептов (С) выхода нет — ничего.
	public void TurnStarOutput(Star star)
	{
		if (star.Choice == StarChoice.None) return;
		star.OutputSide = (star.OutputSide + 1) % Star.SideCount;
		var (orow, ocol) = star.OutputCell;
		GD.Print($"[NucleusLayer] выход звезды ({star.Row},{star.Col}) — клетка ({orow},{ocol}).");
	}

	private void ToggleSpinDirectionUnderMouse()
	{
		// R над звездой (T006) — выход на следующую сторону по часовой.
		var mouse = GetGlobalMousePosition();
		if (Stars.TryGetAt(Mathf.FloorToInt(mouse.Y / CellSize), Mathf.FloorToInt(mouse.X / CellSize), out var star))
		{
			TurnStarOutput(star);
			return;
		}

		var nucleus = FindNucleusUnderMouse(out int row, out int col);
		if (nucleus == null)
		{
			GD.Print($"[NucleusLayer] R: в клетке ({row},{col}) нет ядра.");
			return;
		}

		nucleus.Dir = -nucleus.Dir;
		Touch(nucleus);
		_currentSpinDirection = nucleus.Dir;
		GD.Print($"[NucleusLayer] направление вращения ядра в клетке ({nucleus.Row},{nucleus.Col}) переключено на {(nucleus.Dir > 0 ? "по часовой" : "против часовой")}.");
	}

	// Пробел — см. комментарий у полей _paused/_pausePending/_pauseAlignTicks.
	// Три состояния: идёт симуляция → запрос на паузу (сработает на ближайшей
	// фазе 0); запрос уже стоит → отменить запрос (снова просто "идёт");
	// уже на паузе → снять паузу немедленно (для выхода фазовая привязка не
	// нужна).
	private void TogglePause()
	{
		if (_paused)
		{
			_paused = false;
			GD.Print("[NucleusLayer] пауза снята.");
		}
		else if (_pausePending)
		{
			_pausePending = false;
			GD.Print("[NucleusLayer] запрос на паузу отменён.");
		}
		else
		{
			_pausePending = true;
			GD.Print("[NucleusLayer] пауза будет включена, как только фаза поворота у всех ядер дойдёт до 0.");
		}
	}

	// Пипетка (Q) — берёт тир и количество гнёзд ядра под курсором и сразу
	// выбирает их текущим пресетом для установки, как будто нажали
	// соответствующую кнопку на панели спавна (см. SelectSpawnPreset) —
	// удобно быстро "скопировать" уже стоящее на поле ядро (в т.ч. серое,
	// см. GrayCoreTier — пипетка тут ничем не отличается от обычного тира),
	// не подбирая его вручную на панели. Если под курсором пусто — просто
	// сообщение в лог, выбор (если был) не трогаем.
	// Общий поиск ядра под курсором мыши — используется и пипеткой (Q, см.
	// PickNucleusUnderMouse), и переключателем направления (R, см.
	// ToggleSpinDirectionUnderMouse). Сначала обычный поиск по клетке через
	// _entAt, а если там пусто — проверяем ещё и едущих/летящих (IsMoving,
	// см. StartMove): на время переезда ядро полностью изъято из _entAt,
	// поэтому обычный поиск по клетке его не находит, а визуально оно всё
	// ещё явно "где-то тут", просто между двумя клетками (см. историю бага
	// "пипетка периодически не выбирает" — было привязано именно к этому).
	// Плата за то, что ищем по клетке, а не по факту — если курсор оказался
	// точно на пустой клетке, а ближайшее едущее ядро всё же в пределах
	// половины клетки от него, оно всё равно будет найдено (см. bestDistSq)
	// — тот же допуск, что нужен, чтобы поймать ядро посреди дуги/прямой
	// переезда.
	private NucleusEntity FindNucleusUnderMouse(out int row, out int col)
	{
		var worldPos = GetGlobalMousePosition();
		col = Mathf.FloorToInt(worldPos.X / CellSize);
		row = Mathf.FloorToInt(worldPos.Y / CellSize);

		_entAt.TryGetValue((row, col), out var nucleus);

		if (nucleus == null && _movingSet.Count > 0)
		{
			float bestDistSq = (CellSize * 0.5f) * (CellSize * 0.5f);
			foreach (var moving in _movingSet)
			{
				float distSq = (EffectiveCenter(moving) - worldPos).LengthSquared();
				if (distSq < bestDistSq)
				{
					bestDistSq = distSq;
					nucleus = moving;
				}
			}
		}

		return nucleus;
	}

	private void PickNucleusUnderMouse()
	{
		// Звезда или ЧД под курсором (T006d) — их инструмент.
		var mouse = GetGlobalMousePosition();
		int mrow = Mathf.FloorToInt(mouse.Y / CellSize), mcol = Mathf.FloorToInt(mouse.X / CellSize);
		if (Stars.TryGetAt(mrow, mcol, out var star) && _starLayer != null)
		{
			_starLayer.SelectTool(star.Type);
			return;
		}
		if (BlackHoles.TryGetAt(mrow, mcol, out _) && _blackHoleLayer != null)
		{
			_blackHoleLayer.SelectTool();
			return;
		}

		var nucleus = FindNucleusUnderMouse(out int row, out int col);
		if (nucleus == null)
		{
			// Пустая клетка — снять любой инструмент (атом, звезда, ЧД, источник).
			ClearAllTools();
			GD.Print($"[NucleusLayer] пипетка: в клетке ({row},{col}) нет ядра — инструмент снят.");
			return;
		}

		int holeCount = 0;
		foreach (var slot in nucleus.Ring)
			if (slot.Exists) holeCount++;

		// Пипетка теперь подхватывает и направление вращения найденного ядра
		// (см. _currentSpinDirection/R) — выставляем ДО SelectSpawnPreset,
		// чтобы её сообщение в лог сразу показывало актуальное направление.
		_currentSpinDirection = nucleus.Dir;
		SelectSpawnPreset(nucleus.CoreTier, holeCount);
	}

	// _UnhandledInput (а не _Input) — намеренно: клик по кнопке на панели
	// спавна уже "съедается" GUI-системой и НЕ доходит сюда, так что нажатие
	// самой кнопки не ставит ещё и ядро под курсором заодно. ЛКМ — установка
	// (не только по мгновенному клику, но и всё время, пока зажата, см.
	// _leftMouseHeld/TryPlaceAtMouseIfSelected). ПКМ — удаление ЛЮБОГО объекта
	// под курсором (ядро, источник частиц — см. RemoveAllAtMouse),
	// тем же принципом удержания (см. _rightMouseHeld/TryRemoveAtMouse), и НЕ
	// требует выбранного пресета на панели.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mb) return;
		// На слое 2 клики обрабатывает только слой молекул (см. ViewLayer).
		if (ViewLayer.IsLayer2) return;

		if (mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				// ЛКМ по грузу — установить его на месте рабочим атомом (T006,
				// только песочница). В настоящем режиме груз — обломок (T009): ЛКМ
				// ничего не делает, забрать — ПКМ в инвентарь. Раньше инструмента:
				// груз не затирается установкой.
				var mouse = GetGlobalMousePosition();
				var cargoCell = (Mathf.FloorToInt(mouse.Y / CellSize), Mathf.FloorToInt(mouse.X / CellSize));
				if (_entAt.TryGetValue(cargoCell, out var cargo) && cargo.IsCargo)
				{
					if (Inventory.Sandbox) ActivateCargo(cargo);
					_lastPlacedCell = cargoCell; // удержание ЛКМ не ставит атом поверх только что установленного
					_leftMouseHeld = _selectedSpawnTier.HasValue;
					GetViewport().SetInputAsHandled();
					return;
				}
				// ЛКМ по атому-переносчику без инструмента — перекрёсток ↔ обычный
				// режим (T010).
				if (!AnyToolSelected() && _entAt.TryGetValue(cargoCell, out var atom) && CanBeCrossroad(atom))
				{
					ToggleCrossroad(atom);
					GetViewport().SetInputAsHandled();
					return;
				}
				if (!_selectedSpawnTier.HasValue) return;
				_leftMouseHeld = true;
				_lastPlacedCell = null; // разрешаем установку в клетку под курсором сразу же
				TryPlaceAtMouseIfSelected();
				GetViewport().SetInputAsHandled();
			}
			else
			{
				_leftMouseHeld = false;
			}
		}
		else if (mb.ButtonIndex == MouseButton.Right)
		{
			if (mb.Pressed)
			{
				_rightMouseHeld = true;
				_lastRemovedCell = null; // разрешаем удаление клетки под курсором сразу же
				_lastDenied = null;
				_removeHold.Reset();
				TryRemoveAtMouse(0f);
				GetViewport().SetInputAsHandled();
			}
			else
			{
				StopRemoveHold();
			}
		}
	}

	// Вызывается с панели спавна (NucleusSpawnPanel) при нажатии одной из
	// кнопок 3x3 (или кнопки серого ядра, которое теперь просто передаёт
	// tier=GrayCoreTier — см. NucleusSpawnPanel.OnGraySpawnPressed) —
	// запоминает, что ставить следующим ЛКМ (или удержанием ЛКМ) по полю.
	// Сбрасывает выбор энергии (см. EnergyLayer.SelectEnergyType) — выбор
	// ядра и энергии взаимоисключающий, иначе ЛКМ было бы не ясно, что именно
	// ставить.
	public void SelectSpawnPreset(int tier, int holeCount)
	{
		_selectedSpawnTier = tier;
		_selectedSpawnHoleCount = holeCount;
		_lastPlacedCell = null;
		_energyLayer?.ClearSelection();
		foreach (var clusterLayer in _energyClusterLayers) clusterLayer.ClearSelection();
		_blackHoleLayer?.ClearTool();
		_starLayer?.ClearTool();
		GD.Print($"[NucleusLayer] выбрано для установки: тир {tier}, дырок {holeCount}/8, направление {(_currentSpinDirection > 0 ? "по часовой" : "против часовой")} (R — переключить). Клик (или удержание ЛКМ) по полю — поставить.");
	}

	// Снять любой инструмент слоя 1 (атом, звезда, ЧД, источник) — Q по пустой клетке
	// и повторный клик по кнопке панели (T030).
	public void ClearAllTools()
	{
		ClearSelection();
		_energyLayer?.ClearSelection();
		foreach (var clusterLayer in _energyClusterLayers) clusterLayer.ClearSelection();
		GetViewport().GuiReleaseFocus(); // кнопка панели не остаётся подсвеченной
	}

	// Выбран ли ровно этот пресет атома (тир и число дырок) — для повторного клика по кнопке.
	public bool IsSpawnPresetSelected(int tier, int holeCount) =>
		_selectedSpawnTier == tier && _selectedSpawnHoleCount == holeCount;

	// Вызывается EnergyLayer при выборе типа энергии на панели — сбрасывает
	// выбор ядра (см. комментарий у SelectSpawnPreset).
	// Инструмент ЧД (BlackHoleLayer) тоже сбрасывается — его вызывают все
	// остальные инструменты.
	public void ClearSelection()
	{
		_selectedSpawnTier = null;
		_blackHoleLayer?.ClearTool();
		_starLayer?.ClearTool();
	}

	// Общая точка входа и для одиночного клика, и для каждого кадра при
	// удержании ЛКМ (см. _UnhandledInput/_Process) — не даёт повторно
	// пытаться поставить ядро в ту же самую клетку, пока курсор из неё не
	// ушёл (иначе при удержании на месте — спам одинаковых "уже занята").
	private void TryPlaceAtMouseIfSelected()
	{
		if (!_selectedSpawnTier.HasValue) return;

		// Раньше ставить новые ядра можно было только в режиме паузы — это
		// нужно было исключительно затем, чтобы ядро появлялось ровно в
		// фазе 0 (иначе BuildFixedRing расставляет дырки/частицы под
		// компасные направления, ПОДРАЗУМЕВАЯ фазу 0, а физическая ориентация
		// кольца в момент установки могла быть уже повёрнута — и дырки
		// визуально "уезжали" с тех направлений, куда их поставили). Теперь
		// вместо запрета — ядро просто "спит" до ближайшего момента, когда
		// ЕГО СОБСТВЕННАЯ фаза естественным образом станет 0 (см.
		// AsleepUntilTick/TryPlaceNucleus/WakeSleepingNuclei) — ставить можно
		// в любой момент, без паузы.
		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);
		if (_lastPlacedCell.HasValue && _lastPlacedCell.Value == (row, col)) return;

		_lastPlacedCell = (row, col);
		TryPlaceNucleus(worldPos, _selectedSpawnTier.Value, _selectedSpawnHoleCount);
	}

	// Общая точка входа и для одиночного ПКМ-клика, и для каждого кадра при
	// удержании ПКМ (см. _UnhandledInput/_Process) — тем же принципом, что и
	// установка: не повторяем попытку для той же самой клетки, пока курсор из
	// неё не ушёл. Дедупликация по (row,col) — в системе координат
	// NucleusLayer, просто как "не та же клетка, что в прошлый раз", реальные
	// стирания в RemoveAllAtMouse пересчитывают координаты для каждого слоя
	// заново по его собственному CellSize.
	private void TryRemoveAtMouse(float dt)
	{
		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);
		bool sameCell = _lastRemovedCell.HasValue && _lastRemovedCell.Value == (row, col);

		if (!IsCellOpen(row, col)) // закрытый чанк (T009): вспышка раз на клетку
		{
			_removeHold.Reset();
			_removeHoldLayer?.SetHold(null, 0f);
			if (!sameCell) { _lastRemovedCell = (row, col); DenyIfClosed(row, col); }
			return;
		}

		var target = GetRemoveTargetAt(row, col, out var denied);
		if (denied != _lastDenied)
		{
			_lastDenied = denied;
			if (denied is RemoveTarget d) _removeHoldLayer?.FlashDenied(d.Row, d.Col, d.Side);
		}

		if (target is RemoveTarget t && t.Side > 1)
		{
			// Крупный объект: удержание с нуля на каждом заходе, без дедупликации по клетке.
			var result = _removeHold.Update(t, dt, RemoveHoldSecondsPerCell);
			if (result == RemoveHoldResult.Remove)
			{
				RemoveAllAtMouse(worldPos, row, col);
				_lastRemovedCell = (row, col); // курсор остался на бывших клетках — не трогаем их заново
				_removeHoldLayer?.SetHold(null, 0f);
			}
			else _removeHoldLayer?.SetHold(t, _removeHold.Progress);
			return;
		}

		_removeHold.Reset();
		_removeHoldLayer?.SetHold(null, 0f);
		if (sameCell) return;
		_lastRemovedCell = (row, col);
		if (target != null) RemoveAllAtMouse(worldPos, row, col);
	}

	// Меню паузы (дерево на паузе) — сброс удержания без удаления.
	public override void _Notification(int what)
	{
		if (what == NotificationPaused) StopRemoveHold();
	}

	// Сброс удержания (отпускание ПКМ, слой 2, пауза меню).
	private void StopRemoveHold()
	{
		_rightMouseHeld = false;
		_removeHold.Reset();
		_lastDenied = null;
		_removeHoldLayer?.SetHold(null, 0f);
	}

	// Что ПКМ удалит в клетке (только запрос). Звезда и ЧД — по следу, атом и
	// источник — 1×1. denied — объект, который в настоящем режиме удалять нельзя
	// (ЧД, источник без атома): вспышка следа, не цель удержания.
	private RemoveTarget? GetRemoveTargetAt(int row, int col, out RemoveTarget? denied)
	{
		denied = null;
		if (_entAt.ContainsKey((row, col))) return new RemoveTarget(0, row, col, 1);
		if (Stars.TryGetAt(row, col, out var star))
			return new RemoveTarget(1, star.Row, star.Col, star.Size);
		if (BlackHoles.TryGetAt(row, col, out var hole))
		{
			var t = new RemoveTarget(2, hole.Row, hole.Col, hole.Size);
			if (Inventory.Sandbox) return t;
			denied = t;
			return null;
		}
		foreach (var layer in _energyClusterLayers)
		{
			int srcCol = Mathf.FloorToInt((col + 0.5f) * CellSize / layer.CellSize);
			int srcRow = Mathf.FloorToInt((row + 0.5f) * CellSize / layer.CellSize);
			if (!layer.HasClusterAt(srcRow, srcCol)) continue;
			var t = new RemoveTarget(3, row, col, 1);
			if (Inventory.Sandbox) return t;
			denied = t;
			return null;
		}
		return null;
	}

	// ПКМ убирает ЛЮБОЙ объект под курсором, а не только ядро — источники
	// частиц (EnergyClusterLayer, любой тир сразу, на случай если клетка
	// почему-то оказалась занята сразу на нескольких — штатно такого не
	// бывает, но лишняя проверка безвредна). Старый TileMapLayer/EnergyLayer сюда намеренно НЕ включён — он по
	// условиям задачи не используется и не трогается.
	//
	// row/col для ядра уже посчитаны вызывающей стороной в системе координат
	// NucleusLayer (this.CellSize); для остальных слоёв пересчитываем их
	// заново из worldPos СОБСТВЕННЫМ CellSize каждого слоя — тот же защитный
	// приём, что и в SimTick (шаг 3, мост к EnergyClusterLayer), на случай если чей-то CellSize когда-нибудь разъедется
	// с остальными, хотя сейчас все они читают одно и то же значение из
	// GridDraw.
	private void RemoveAllAtMouse(Vector2 worldPos, int row, int col)
	{
		if (DenyIfClosed(row, col)) return; // закрытый чанк (T009)
		RemoveNucleusAt(row, col);
		_starLayer?.RemoveAt(row, col);
		// ЧД и источники — только в песочнице (T026).
		if (!Inventory.Sandbox) return;
		_blackHoleLayer?.RemoveAt(row, col);

		foreach (var layer in _energyClusterLayers)
		{
			int srcCol = Mathf.FloorToInt(worldPos.X / layer.CellSize);
			int srcRow = Mathf.FloorToInt(worldPos.Y / layer.CellSize);
			layer.EraseClusterAt(srcRow, srcCol);
		}

	}

	// Удаляет ядро (если оно там есть) из клетки (row, col): убирает из
	// _entAt, _activeSet, из списка владеющего чанка и полностью пересобирает
	// его MultiMesh'и (см. RebuildChunkMeshes — Godot сбрасывает ВСЕ
	// инстансы при смене InstanceCount, точечно удалить один инстанс нельзя).
	// ВАЖНО: после удаления LocalIndex у ВСЕХ оставшихся ядер этого чанка
	// нужно пересчитать по их новой позиции в списке — иначе он будет
	// указывать на индексы инстансов, которых после пересборки уже не будет
	// (или которые сместились), и рендер/симуляция начнут путать слоты.
	private void RemoveNucleusAt(int row, int col)
	{
		if (!_entAt.TryGetValue((row, col), out var nucleus)) return;
		int refunded = RefundToInventory(nucleus);
		RemoveNucleusEntity(nucleus);
		GD.Print($"[NucleusLayer] удалено ядро из клетки ({row},{col}).{(refunded > 0 ? $" В инвентарь: {refunded}." : "")}");
	}

	// Настоящий режим (T008): снятый игроком атом Ж/К/С возвращается в
	// инвентарь, атомы-предметы из его дырок — тоже (у любого атома), частицы
	// пропадают. В песочнице ничего. Возвращает, сколько атомов ушло в инвентарь.
	// Только снятие игроком (ПКМ, замена) — захват ЧД и прочее сюда не идут.
	private int RefundToInventory(NucleusEntity nucleus)
	{
		if (Inventory.Sandbox) return 0;
		int count = 0;
		if (Inventory.IsAtomTier(nucleus.CoreTier)) { Inventory.Add(nucleus.CoreTier); count++; }
		return count + RefundItemsToInventory(nucleus);
	}

	// Атомы-предметы из дырок и с осей перекрёстка (T010) — в инвентарь
	// (настоящий режим). Частицы не возвращаются.
	private int RefundItemsToInventory(NucleusEntity nucleus)
	{
		if (Inventory.Sandbox) return 0;
		int count = 0;
		foreach (var slot in nucleus.Ring)
			if (slot.Exists && !slot.IsHole && slot.IsItem && Inventory.IsAtomTier(slot.ColorTier))
			{
				Inventory.Add(slot.ColorTier);
				count++;
			}
		if (nucleus.Cross != null)
			foreach (var axis in nucleus.Cross.Axes)
				for (int i = 0; i < axis.Count; i++)
					if (axis.Items[i].IsItem && Inventory.IsAtomTier(axis.Items[i].ColorTier))
					{
						Inventory.Add(axis.Items[i].ColorTier);
						count++;
					}
		return count;
	}

	// --- перекрёсток (T010) ---

	// Выбран ли какой-нибудь инструмент установки на панели слоя 1.
	private bool AnyToolSelected()
	{
		if (_selectedSpawnTier.HasValue) return true;
		if (_blackHoleLayer != null && _blackHoleLayer.HasTool) return true;
		if (_starLayer != null && _starLayer.HasTool) return true;
		if (_energyLayer != null && _energyLayer.IsPlacing) return true;
		foreach (var layer in _energyClusterLayers) if (layer.IsPlacing) return true;
		return false;
	}

	// Перекрёстком может стать рабочий атом-переносчик Ж/К/С или серый.
	private bool CanBeCrossroad(NucleusEntity n) =>
		!n.IsCargo && !IsSpinnerTier(n.CoreTier) && !(n.IsMoving && n.IsFlying);

	private static int HoleCountOf(NucleusEntity n)
	{
		int count = 0;
		foreach (var slot in n.Ring) if (slot.Exists) count++;
		return count;
	}

	// Обычный режим ↔ перекрёсток. Содержимое очищается (решение пользователя):
	// частицы пропадают, атомы-предметы в настоящем режиме — в инвентарь.
	private void ToggleCrossroad(NucleusEntity n)
	{
		int refunded = RefundItemsToInventory(n);
		int holeCount = HoleCountOf(n);
		n.Ring = BuildFixedRing(holeCount);
		n.Cross = n.Cross == null ? new Crossroad(holeCount) : null;
		if (n.Cross != null) _crossSet.Add(n); else _crossSet.Remove(n);
		Touch(n);
		GD.Print($"[NucleusLayer] атом ({n.Row},{n.Col}): {(n.Cross != null ? "перекрёсток" : "обычный режим")}.{(refunded > 0 ? $" В инвентарь: {refunded}." : "")}");
	}

	// Общая точка удаления живого ядра из ВСЕХ структур — вынесена из
	// RemoveNucleusAt (которая ищет ядро по клетке — годится для ПКМ, где
	// клетка и так уже под курсором) в отдельный переиспользуемый метод по
	// прямой ссылке на сущность — понадобился для бросателя (см.
	// ThrowerCoreTier/EvaluateFlightStep): исчерпавшее дистанцию ядро нужно удалить, а вызывающий код уже держит
	// ссылку на саму сущность, а не координаты клетки под курсором.
	private void RemoveNucleusEntity(NucleusEntity nucleus)
	{
		int row = nucleus.Row;
		int col = nucleus.Col;

		int cx = Mathf.FloorToInt((float)col / ChunkSize);
		int cy = Mathf.FloorToInt((float)row / ChunkSize);

		// Только если клетка числится за этим атомом: у едущего (захват ЧД на
		// прибытии, TryCaptureArrived) Row/Col — старая клетка, её мог уже занять другой.
		if (_entAt.TryGetValue((row, col), out var atCell) && atCell == nucleus) _entAt.Remove((row, col));
		RemoveActive(nucleus);
		UnregisterEntity(nucleus);
		_movingSet.Remove(nucleus); // защитная подстраховка — сюда не должны попадать едущие/летящие, но лишней не будет
		_sleepingSet.Remove(nucleus); // на случай удаления ещё не проснувшегося ядра (см. AsleepUntilTick)
		_cargoSet.Remove(nucleus);
		_crossSet.Remove(nucleus);
		_detachedSet.Remove(nucleus);

		if (!_chunks.TryGetValue((cx, cy), out var chunk))
		{
			GD.PrintErr($"[NucleusLayer] у ядра в клетке ({row},{col}) не найден владеющий чанк — удалено только из общих структур.");
			return;
		}

		chunk.Nuclei.Remove(nucleus);
		for (int i = 0; i < chunk.Nuclei.Count; i++) chunk.Nuclei[i].LocalIndex = i;
		RebuildChunkMeshes(chunk);
	}

	public override void _Process(double delta)
	{
		if (!_ready) return;

		var cam = GetViewport().GetCamera2D();
		if (cam == null) return;

		// Слой 2 (см. ViewLayer): детальная отрисовка слоя 1 скрыта целиком
		// (Visible узла гасит все чанки разом), обход видимых чанков не
		// делается вовсе — на дальнем зуме это были бы десятки тысяч пустых
		// чанков с MultiMesh. Симуляция (SimTick ниже) при этом тикает как
		// обычно — скрывается только рендер.
		bool layer1 = !ViewLayer.IsLayer2;
		Visible = layer1;
		if (layer1) UpdateVisibleChunks(cam);
		else
		{
			_leftMouseHeld = false;
			StopRemoveHold();
		}

		RunSimulationClock(delta);

		if (!layer1) return;

		long renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
		float zoom = cam.Zoom.X;
		float nebulaFade = NebulaFade(zoom);
		SetAtomsShown(zoom >= NebulaZoom);
		int visibleAtoms = 0;
		// Ниже NebulaZoom атомный рендер не выполняется совсем (T015).
		if (_atomsShown)
		{
			if (_dotFade != 1f - nebulaFade)
			{
				_dotFade = 1f - nebulaFade;
				_dotMaterial.SetShaderParameter("fade", _dotFade);
			}
			foreach (var coord in _visible)
			{
				if (!_chunks.TryGetValue(coord, out var chunk)) continue;
				// Зум мог измениться и без смены набора видимых чанков — держим
				// Visible в актуальном состоянии для уже показанных чанков тоже.
				chunk.HoleNode.Visible = _holesVisible;
				chunk.ParticleNode.Visible = _particlesVisible;
				if (chunk.Dots != _atomDots) SetChunkDots(chunk, _atomDots);
				UpdateChunkVisuals(chunk);
				visibleAtoms += chunk.Nuclei.Count;
			}
		}
		VisibleAtomCount = visibleAtoms;
		if (nebulaFade > 0f)
		{
			RecountCompositionSourcesIfNeeded();
			Composition.SourceWeightPercent = Mathf.RoundToInt(NebulaSourceWeight * 100f);
		}
		_nebulaLayer.Refresh(_visible, Composition, _tierPreviewColors, _chunkWorldSize,
			NebulaScale, NebulaMaxAlpha, NebulaSaturation, nebulaFade);
		double renderMs = System.Diagnostics.Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
		if (_benchFrames >= 0) StepRenderBench(renderMs, delta);
		_perfRenderMsSum += renderMs;
		if (renderMs > _perfRenderMsMax) _perfRenderMsMax = renderMs;

		if (_leftMouseHeld) TryPlaceAtMouseIfSelected();
		if (_rightMouseHeld)
		{
			// Отпускание ПКМ мог съесть GUI — сверяемся с реальным состоянием.
			if (!Input.IsMouseButtonPressed(MouseButton.Right)) StopRemoveHold();
			else TryRemoveAtMouse((float)delta);
		}
		UpdatePlacementPreview();
	}

	// Проявленность туманностей (T015): 1 — на NebulaZoom и дальше, 0 — от
	// NebulaZoom × NebulaFadeRatio и ближе, между — по логарифму зума.
	private float NebulaFade(float zoom)
	{
		if (zoom < NebulaZoom) return 1f;
		if (NebulaFadeRatio <= 1f) return 0f;
		float t = Mathf.Log(zoom / NebulaZoom) / Mathf.Log(NebulaFadeRatio);
		return 1f - Mathf.Clamp(t, 0f, 1f);
	}

	// Атомы видимых чанков показать или скрыть целиком (T015) — только при смене.
	private void SetAtomsShown(bool shown)
	{
		if (shown == _atomsShown) return;
		_atomsShown = shown;
		foreach (var coord in _visible)
		{
			if (!_chunks.TryGetValue(coord, out var chunk)) continue;
			chunk.Node.Visible = shown;
			chunk.HoleNode.Visible = shown && _holesVisible;
			chunk.ParticleNode.Visible = shown && _particlesVisible;
		}
	}

	// Какие чанки попадают в кадр камеры — только рендер, к симуляции не
	// относится (см. комментарий внутри).
	private void UpdateVisibleChunks(Camera2D cam)
	{
		var viewportSize = GetViewport().GetVisibleRect().Size;
		var visibleSize = viewportSize / cam.Zoom;
		var visiblePos = cam.GetScreenCenterPosition() - visibleSize / 2f;
		var visibleRect = new Rect2(visiblePos, visibleSize).Grow(CullMargin);

		int minCx = Mathf.FloorToInt(visibleRect.Position.X / _chunkWorldSize);
		int maxCx = Mathf.FloorToInt((visibleRect.Position.X + visibleRect.Size.X) / _chunkWorldSize);
		int minCy = Mathf.FloorToInt(visibleRect.Position.Y / _chunkWorldSize);
		int maxCy = Mathf.FloorToInt((visibleRect.Position.Y + visibleRect.Size.Y) / _chunkWorldSize);

		// T014: в видимые попадают только существующие чанки (пустых узлов на
		// каждую координату в кадре больше нет); новые создаёт только случайное
		// заполнение и только не дальше RandomFillMinZoom. На дальнем зуме, где
		// координат в кадре больше, чем чанков, перебираются сами чанки.
		var newVisible = _newVisible;
		newVisible.Clear();
		bool generate = RandomFillEnabled && cam.Zoom.X >= RandomFillMinZoom;
		long area = (long)(maxCx - minCx + 1) * (maxCy - minCy + 1);
		if (generate || area <= _chunks.Count)
		{
			for (int cy = minCy; cy <= maxCy; cy++)
				for (int cx = minCx; cx <= maxCx; cx++)
				{
					if (generate) GetOrCreateChunk(cx, cy);
					else if (!_chunks.ContainsKey((cx, cy))) continue;
					newVisible.Add((cx, cy));
				}
		}
		else
		{
			foreach (var key in _chunks.Keys)
				if (key.cx >= minCx && key.cx <= maxCx && key.cy >= minCy && key.cy <= maxCy)
					newVisible.Add(key);
		}

		// Ниже своего порога каждый слой — неразличимые точки, а видимых
		// чанков уже много, поэтому его рендер (и пересчёт каждый кадр) просто
		// выключается целиком, а не только прячется по чанку (см.
		// HoleHideZoom/ParticleHideZoom — сейчас оба 0.25, но пороги
		// независимые, так что могут снова разойтись). Тела ядер (chunk.Node)
		// это не касается — они остаются видимы всегда. H дополнительно
		// ручками гасит дырки поверх автоматики по зуму (см. _Input).
		//
		// ВАЖНО: этот блок и весь _visible ниже — ТОЛЬКО про рендер (что
		// показывать/пересчитывать в MultiMesh), симуляции он больше не
		// касается. Раньше сюда же было завязано наполнение _activeSet
		// (ядро добавлялось/убиралось из симуляции при входе/выходе чанка из
		// поля зрения камеры) — из-за этого ядра вне экрана полностью
		// переставали тикать (не вращались, не передавали частицы, не
		// захватывали энергию), пока камера не
		// возвращала их чанк в кадр. Теперь _activeSet — это ВСЕ живые ядра
		// (наполняется/чистится в PlaceNucleusAt/RemoveNucleusAt/генерации
		// чанка, см. комментарий у поля), а видимость чанка решает только,
		// рисовать ли его — как и должно быть у чисто рендерной оптимизации.
		_holesVisible = !_holesManuallyHidden && cam.Zoom.X >= HoleHideZoom;
		_particlesVisible = cam.Zoom.X >= ParticleHideZoom;
		_atomDots = cam.Zoom.X < AtomDotZoom;

		foreach (var coord in newVisible)
		{
			if (_visible.Contains(coord)) continue;
			var chunk = GetOrCreateChunk(coord.cx, coord.cy);
			chunk.Node.Visible = _atomsShown;
			chunk.HoleNode.Visible = _atomsShown && _holesVisible;
			chunk.ParticleNode.Visible = _atomsShown && _particlesVisible;
		}

		foreach (var coord in _visible)
		{
			if (newVisible.Contains(coord)) continue;
			if (_chunks.TryGetValue(coord, out var chunk))
			{
				chunk.Node.Visible = false;
				chunk.HoleNode.Visible = false;
				chunk.ParticleNode.Visible = false;
			}
		}

		(_visible, _newVisible) = (newVisible, _visible);
	}

	private void RunSimulationClock(double delta)
	{
		// Глобальные часы симуляции — фиксированный шаг, не зависящий от FPS.
		// Ограничиваем число "догоняющих" тиков за кадр, чтобы просадка FPS
		// не превратилась в спираль смерти. На паузе (_paused) весь этот блок
		// просто не выполняется — ни накопление времени, ни тики: _globalTick
		// и _subTickFraction остаются точно такими, какими были в момент
		// входа в паузу (см. TogglePause/ниже), поэтому камеру можно свободно
		// двигать, а ядра стоят неподвижно на выставленной фазе 0.
		int ticksThisFrame = 0;
		if (!_paused)
		{
			_tickAccumulatorMs += delta * 1000.0;
			int guard = 0;
			while (_tickAccumulatorMs >= PrototypeTickMs && guard < 10)
			{
				_tickAccumulatorMs -= PrototypeTickMs;
				_globalTick++;
				long tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
				SimTick();
				CheckGoals();
				// Слой 2 (перенос атомов молекулами) — на тех же часах, после
				// слоя 1: порты уже обменялись частицами на этом тике.
				_moleculeLayer?.SimTick(_globalTick);
				double tickMs = System.Diagnostics.Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
				_perfTickMsSum += tickMs;
				if (tickMs > _perfTickMsMax) _perfTickMsMax = tickMs;
				_perfTicks++;
				ticksThisFrame++;
				guard++;
				_upsWindowTicks++;

				// Запрошена пауза (пробел) — входим в неё не мгновенно, а
				// только на тике, где фаза поворота ВСЕХ ядер (любого тира)
				// ровно 0 (см. _pauseAlignTicks). Остаток накопленного
				// времени сознательно отбрасывается — иначе после снятия
				// паузы симуляция рывком доедет накопленный запас тиков.
				if (_pausePending && _globalTick % _pauseAlignTicks == 0)
				{
					_paused = true;
					_pausePending = false;
					_tickAccumulatorMs = 0;
					GD.Print($"[NucleusLayer] пауза включена на тике {_globalTick} (фаза 0).");
					break;
				}
			}
			// Доля пути до следующего тика — единая точка расчёта, см. поле.
			_subTickFraction = (float)(_tickAccumulatorMs / PrototypeTickMs);
		}

		// Обновляем CurrentUPS раз в ~секунду реального времени — сырое
		// количество тиков за кадр слишком дёргано для HUD.
		_perfFrames++;
		_perfFrameMsSum += delta * 1000.0;
		if (ticksThisFrame > _perfTicksPerFrameMax) _perfTicksPerFrameMax = ticksThisFrame;

		_upsWindowTimer += delta;
		if (_upsWindowTimer >= 1.0)
		{
			ClosePerfWindow();
			CurrentUPS = (float)(_upsWindowTicks / _upsWindowTimer);
			_upsWindowTimer = 0.0;
			_upsWindowTicks = 0;
		}
	}

	// Итог окна замера (T014) — в Perf для HUD, счётчики окна обнуляются.
	private void ClosePerfWindow()
	{
		Perf.TickMsAvg = _perfTicks > 0 ? _perfTickMsSum / _perfTicks : 0;
		Perf.TickMsMax = _perfTickMsMax;
		Perf.TicksPerFrameAvg = _perfFrames > 0 ? (double)_perfTicks / _perfFrames : 0;
		Perf.TicksPerFrameMax = _perfTicksPerFrameMax;
		Perf.FrameMsAvg = _perfFrames > 0 ? _perfFrameMsSum / _perfFrames : 0;
		Perf.RenderMsAvg = _perfFrames > 0 ? _perfRenderMsSum / _perfFrames : 0;
		Perf.RenderMsMax = _perfRenderMsMax;
		_perfTickMsSum = _perfTickMsMax = _perfRenderMsSum = _perfRenderMsMax = _perfFrameMsSum = 0;
		_perfTicks = _perfFrames = _perfTicksPerFrameMax = 0;
	}

	// Полупрозрачная "призрачная" копия выбранного ядра в клетке под
	// курсором — только если туда реально можно поставить (клетка свободна).
	// Цвет берём из той же самой палитровой текстуры, что красит настоящие
	// ядра (см. SampleTierColors), чтобы превью совпадало с итоговым видом.
	private void UpdatePlacementPreview()
	{
		if (!_selectedSpawnTier.HasValue)
		{
			_placementPreview.Visible = false;
			return;
		}

		var worldPos = GetGlobalMousePosition();
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);

		int tier = _selectedSpawnTier.Value;
		// Решение то же, что у клика (T030): на занятой клетке превью поверх атома,
		// если он отличается; такой же — скрыто; нельзя — красное.
		var decision = EvaluatePlace(row, col, tier, _selectedSpawnHoleCount);
		if (!decision.Draws || decision.Reason == PlaceDenyReason.Layer2) // красную подсветку молекулы рисует MoleculeLayer
		{
			_placementPreview.Visible = false;
			return;
		}

		var baseColor = (tier >= 0 && tier < _tierPreviewColors.Length) ? _tierPreviewColors[tier] : Colors.White;
		_placementPreview.Position = new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f);
		_placementPreview.Modulate = decision.Outcome == PlaceOutcome.Deny
			? DeniedPreviewColor
			: new Color(baseColor.R, baseColor.G, baseColor.B, 0.5f);
		_placementPreview.Visible = true;
	}

	// Сэмплирует по одному представительному цвету из каждой палитровой
	// текстуры тира (узкие 1-строчные полоски-градиенты, см.
	// BuildPaletteAtlas) — берём пиксель из середины полоски. Используется
	// только для превью установки (см. UpdatePlacementPreview), не влияет на
	// реальную покраску ядер/частиц шейдером.
	private static Color[] SampleTierColors(string[] paths)
	{
		var colors = new Color[paths.Length];
		for (int i = 0; i < paths.Length; i++)
		{
			var tex = GD.Load<Texture2D>(paths[i]);
			if (tex == null)
			{
				colors[i] = Colors.White;
				continue;
			}
			var img = tex.GetImage();
			img.Convert(Image.Format.Rgba8);
			int midX = img.GetWidth() / 2;
			colors[i] = img.GetPixel(midX, 0);
		}
		return colors;
	}

	// Случайный тир для процедурной генерации (T/RandomFillEnabled), с
	// исключённым GrayCoreTier — серый тир не настоящий цвет, а специальное
	// игровое поведение "принимает любую частицу" (см. ColorAccepted), и
	// должен появляться на поле только по явной установке с панели спавна, а
	// не случайно наравне с обычными цветными тирами. Реализовано как
	// равномерный выбор из _tierCount-1 вариантов со сдвигом, а не через
	// retry-цикл ("кинуть кубик ещё раз, если выпал серый") — так после
	// исключения одного индекса распределение остаётся строго равномерным по
	// оставшимся тирам, без лишних итераций.
	// Переименовал по смыслу (было RandomNonGrayTier) — теперь исключает из
	// случайного выбора GrayCoreTier, RotatorCoreTier И ThrowerCoreTier: все
	// три не настоящие цвета, а специальные "актёрские" тиры (принимает любой
	// цвет / двигает соседей / двигает и потом ещё бросает), им не место среди
	// случайно выпадающих тиров при процедурной генерации (см.
	// GetOrCreateChunk/RandomFillEnabled). Реализовано как равномерный выбор
	// из _tierCount-N вариантов со сдвигом индекса мимо каждого исключённого
	// (а не retry-цикл), чтобы распределение по оставшимся тирам оставалось
	// строго равномерным без лишних итераций.
	//
	// Раньше (для ровно двух исключаемых тиров) это была пара ручных
	// min/max-сравнений — с третьим исключаемым тиром (ThrowerCoreTier) тот
	// же приём пришлось бы утраивать вручную, поэтому обобщено на
	// произвольное число исключений: собираем уникальные валидные индексы,
	// сортируем по возрастанию и сдвигаем pick мимо каждого по очереди —
	// порядок (по возрастанию) обязателен ровно по той же причине, что и
	// раньше.
	private int RandomNormalTier()
	{
		int[] excluded = new int[3];
		int excludedCount = 0;
		foreach (int tier in new[] { GrayCoreTier, RotatorCoreTier, ThrowerCoreTier })
		{
			if (tier < 0 || tier >= _tierCount) continue;
			bool alreadyListed = false;
			for (int i = 0; i < excludedCount; i++)
				if (excluded[i] == tier) { alreadyListed = true; break; }
			if (!alreadyListed) excluded[excludedCount++] = tier;
		}

		// Сортировка по возрастанию — элементов не больше 3, обычная вставками
		// с лихвой достаточна и проще, чем тянуть Array.Sort ради трёх чисел.
		for (int i = 1; i < excludedCount; i++)
		{
			int key = excluded[i];
			int j = i - 1;
			while (j >= 0 && excluded[j] > key) { excluded[j + 1] = excluded[j]; j--; }
			excluded[j + 1] = key;
		}

		if (_tierCount - excludedCount <= 0) return 0; // все тиры особые — деваться некуда

		int pick = _rng.RandiRange(0, _tierCount - excludedCount - 1);
		for (int i = 0; i < excludedCount; i++)
			if (pick >= excluded[i]) pick++;
		return pick;
	}

	// --- симуляция ---

	private void SimTick()
	{
		// Будим "спящих" (см. NucleusEntity.AsleepUntilTick/TryPlaceNucleus) —
		// ДО раннего выхода по пустому _activeSet ниже: если на поле пока
		// вообще ничего активного нет, а только что поставленное (спящее)
		// первое ядро ждёт своей фазы 0, ранний выход не должен мешать ему
		// вообще когда-либо проснуться.
		if (_sleepingSet.Count > 0) WakeSleepingNuclei();

		// Производство звёзд (T006) — до раннего выхода: звезда с набранным
		// рецептом работает, даже если рабочих атомов нет. Готовый атом уходит
		// в дырку на выходе — шаг 2е (T007).
		if (Stars.Count > 0) TickStars();

		if (_activeSet.Count == 0) return;

		// Шаг 0: прибытие уже едущих ядер (см. StartMove/FinishArrivedMoves) —
		// раньше всего остального, чтобы только что прибывшее ядро в этом же
		// тике уже участвовало в обычной симуляции на новом месте (снова
		// видно в _entAt), а не осталось "невидимым" ещё один лишний тик.
		if (_movingSet.Count > 0) FinishArrivedMoves();

		// Шаг 1: поворот колец — только у ядер, чей тир как раз "щёлкает" на
		// этом глобальном тике. Сама ориентация теперь не хранится и не
		// сдвигается тут — она чистая функция (_globalTick, тир), см.
		// DiscreteRotationOffset. Этот шаг только снимает блокировку со всех
		// слотов ядра (антидребезг завершён к границе тика поворота).
		//
		// По заданию клиента — пропускаем целиком только по-настоящему
		// ЛЕТЯЩЕЕ ядро (IsFlying, см. ThrowerCoreTier/BeginFlight): пока оно
		// летит, у него нет устойчивой клетки, от которой считать соседей,
		// так что обычная логика тут не применима. А вот ядро, которое
		// просто ЕДЕТ на шаг дальше (толчок обычного вращателя/передача
		// между вращателями, IsMoving=true, IsFlying=false) — должно вести
		// себя как обычно все время переезда: раньше оно тут ЦЕЛИКОМ
		// замирало (в т.ч. собственный поворот кольца/разблокировка слотов)
		// на весь moveDuration, что могло быть заметно дольше одного тика —
		// отсюда и баг "ядро в процессе поворота вращателем замирает, фаза
		// не меняется". Row/Col у такого ядра остаются его СТАРОЙ (ещё не
		// прибывшей) клеткой до самого FinishArrivedMoves, поэтому вся
		// обычная логика по n.Row/n.Col/n.Center ниже (Шаги 1-4) продолжает
		// работать корректно и во время переезда — считает от той клетки,
		// откуда ядро выехало, ровно как для неподвижного.
		if (FullScanDebug)
		{
			// Полный обход (отладка, T014): поворот — по всем рабочим атомам.
			_spinnerScratch.Clear();
			foreach (var n in _activeSet)
			{
				if (IsSpinnerTier(n.CoreTier)) _spinnerScratch.Add(n);
				if (n.IsMoving && n.IsFlying) continue;

				int ticks = n.CoreTier < TierTicks.Length ? TierTicks[n.CoreTier] : TierTicks[TierTicks.Length - 1];
				if (ticks > 0 && _globalTick % ticks == 0) OnRotationTick(n);
			}
			RotateDueTiers(unlock: false); // только отметки — чтобы можно было переключить режим на ходу
		}
		else
		{
			// По событиям (T014): только тиры, у которых на этом тике шаг поворота.
			RotateDueTiers(unlock: true);
			_spinnerScratch.Clear();
			foreach (var n in _spinners)
				if (n.IsActiveSim) _spinnerScratch.Add(n);
		}

		// Вращатели и бросатели (T014) — отдельным проходом после поворота всех
		// атомов, в том же фиксированном порядке (раньше — в общем цикле вперемешку
		// с поворотом, и результат поимки зависел от места атома в обходе).
		// Поимка меняет клетку атома (порядок _activeSet), поэтому обход — по копии.
		foreach (var n in _spinnerScratch)
		{
			if (n.IsMoving && n.IsFlying) continue;
			if (n.CoreTier == RotatorCoreTier || n.CoreTier == ThrowerCoreTier)
			{
				// Захват пролетающего мимо ядра (см. TryCatchFlyingNeighbor) —
				// КАЖДЫЙ тик, а не только в момент собственного срабатывания
				// толчка (MaybeTriggerRotatorRotation ниже): между двумя
				// срабатываниями толчка обычно проходит куда больше тиков, чем
				// летящее ядро тратит на весь пролёт мимо (ThrowTicksPerCell на
				// клетку) — если проверять только на редких моментах толчка,
				// пролетающее ядро почти всегда успевает проскочить мимо
				// незамеченным (собственно и был баг — "вращатель не
				// захватывает пролетающее ядро").
				//
				// Мгновенная передача занятого ядра соседнему вращателю/
				// бросателю (см. TryHandOffSharedOccupant) — тоже КАЖДЫЙ тик,
				// а не по расписанию, и раньше обычного толчка: если этот же
				// сосед одновременно активен и для другого вращателя, и оба
				// они одного holeCount (а значит триггерятся синхронно, см.
				// MaybeTriggerRotatorRotation), без этой проверки сосед
				// доставался бы навсегда только тому, кто раньше встретится в
				// переборе _activeSet — ядро никогда бы не попало ко второму.
				TryHandOffSharedOccupant(n);
				TryCatchFlyingNeighbor(n);
				MaybeTriggerRotatorRotation(n);
			}
		}

		// Шаг 2: передача частиц, два прохода с "захватом" слотов, чтобы один
		// и тот же физический перенос не был учтён дважды (один раз со стороны
		// дырки, которая "тянет", и один раз со стороны частицы, которая
		// "толкает" в ту же дырку). Проход A — PullPass, проход B — PushPass.
		// По событиям (T014) — только изменившиеся атомы и соседи (RunEventPasses),
		// в отладке — все рабочие атомы в том же порядке.
		_claimEpoch++;
		BeginChangeEpoch();
		if (FullScanDebug)
		{
			foreach (var n in _activeSet) if (TakesPartInPasses(n)) PullPass(n);
			foreach (var n in _activeSet) if (TakesPartInPasses(n)) PushPass(n);
		}
		else
		{
			RunEventPasses();
		}

		// Шаг 2в: обмен частицами с портами чанков (см. PortSet/PortLayer).
		// Порт — неподвижное серое ядро без спина (TransferRules.NoSpin): объект
		// в соседней клетке, с учётом _claimed; обход идёт от портов в порядке
		// PortSet, чтобы спор
		// нескольких ядер за один порт решался детерминированно.
		if (Ports != null && PortTicksPerParticle > 0 && _globalTick % PortTicksPerParticle == 0)
			ExchangeWithPorts();

		// Шаг 2г: ЧД высасывает частицы из атомов, стоящих на горизонте (T005).
		if (BlackHoles.Count > 0) AbsorbFromHorizon();

		// Шаг 2д: звёзды берут нужные рецепту частицы у атомов вокруг (T006).
		if (Stars.Count > 0) FeedStarsFromNeighbors();

		// Шаг 2е: звёзды отдают готовые атомы-предметы в дырки на выходе (T007).
		if (Stars.Count > 0) OutputStarsToHoles();

		// Шаг 3: захват энергии из источников частиц (EnergyClusterLayer).
		// ВАЖНО: раньше здесь была одна общая проверка на ВСЮ симуляцию сразу —
		// "_globalTick % _energyCaptureTicks == 0" — в надежде, что она будет
		// срабатывать раз в _energyCaptureTicks тиков для всех ядер разом. Но
		// _energyCaptureTicks (=TierTicks[0]*8=128) кратен полному периоду
		// вращения кольца ЛЮБОГО тира (8*16=128, 8*8=64, 8*4=32 — все делят
		// 128 нацело), поэтому единственная фаза поворота, которую видела эта
		// проверка, была одной и той же НАВСЕГДА — стробоскопический эффект:
		// захват мог сработать только для той одной дырки, что вечно стоит в
		// эту фазу лицом к источнику, а все остальные дырки, которым для этого
		// нужна другая фаза, не сэмплировались никогда. Отсюда и баг "захватило
		// один раз и больше никогда". Чиним индивидуальным кулдауном на КАЖДОЕ
		// ядро (NucleusEntity.NextCaptureTick) — проверяем и пытаемся захватить
		// каждый тик, но пропускаем ядро, если для него кулдаун ещё не истёк;
		// при успешном захвате взводим кулдаун на _energyCaptureTicks вперёд И
		// сразу прекращаем перебор направлений для этого ядра на этом тике —
		// один захват за одну попытку, как и раньше, но без привязки к фазе.
		// Захват — по кандидатам рядом с месторождениями (T014), в отладке — по всем.
		if (_energyClusterLayers.Count > 0 && _energyCaptureTicks > 0)
		{
			RefreshCaptureCandidatesIfNeeded();
			if (FullScanDebug)
			{
				foreach (var n in _activeSet) TryCaptureEnergy(n);
			}
			else
			{
				foreach (var n in _captureCandidates)
					if (n.IsActiveSim) TryCaptureEnergy(n);
			}
		}
	}

	// Проход A ("pull"): дырка атома n тянет частицу из соседа напротив. k —
	// сторона света (компас), а не индекс в Ring: у каждого из двух атомов своя
	// ориентация (DiscreteRotationOffset), физический слот — через PhysicalSlotForCompass.
	private void PullPass(NucleusEntity n)
	{
		for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
		{
			int k = OrthogonalSlots[idx];
			if (!CanReceive(n, k)) continue;
			int p = SideKey(n, k);
			if (IsClaimed(n, p)) continue;

			var (dr, dc) = Adj8[k];
			if (!_entAt.TryGetValue((n.Row + dr, n.Col + dc), out var neighbor)) continue;
			if (neighbor.IsCargo) continue; // груз частиц не отдаёт (T006)

			int k2 = Opposite(k);
			if (!TryPeekGive(neighbor, k2, out var giverSlot)) continue;
			int p2 = SideKey(neighbor, k2);
			if (IsClaimed(neighbor, p2)) continue;
			if (!TransferAllowed(receiver: n, giver: neighbor, giverSlot)) continue;

			PutReceived(n, k, giverSlot);
			TakeGiven(neighbor, k2);
			Claim(n, p);
			Claim(neighbor, p2);
		}
	}

	// Проход B ("push"): свободная (не заблокированная) частица атома n толкается
	// в дырку соседа, если её ещё не разобрали в проходе A.
	private void PushPass(NucleusEntity n)
	{
		for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
		{
			int k = OrthogonalSlots[idx];
			if (!TryPeekGive(n, k, out var giverSlot)) continue;
			int p = SideKey(n, k);
			if (IsClaimed(n, p)) continue;

			var (dr, dc) = Adj8[k];
			if (!_entAt.TryGetValue((n.Row + dr, n.Col + dc), out var neighbor)) continue;
			if (neighbor.IsCargo) continue; // груз частиц не берёт (T006)

			int k2 = Opposite(k);
			if (!CanReceive(neighbor, k2)) continue;
			int p2 = SideKey(neighbor, k2);
			if (IsClaimed(neighbor, p2)) continue;
			if (!TransferAllowed(receiver: neighbor, giver: n, giverSlot)) continue;

			PutReceived(neighbor, k2, giverSlot);
			TakeGiven(n, k);
			Claim(n, p);
			Claim(neighbor, p2);
		}
	}

	// Шаг 3: захват энергии из источника частиц (EnergyClusterLayer) атомом n.
	// Индивидуальный кулдаун на атом (NextCaptureTick), а не общий модуль тика:
	// общий период кратен полному обороту кольца любого тира, и захват видел бы
	// всегда одну и ту же фазу (стробоскоп). Одна попытка захвата на атом за тик.
	private void TryCaptureEnergy(NucleusEntity n)
	{
		// Пропускаем только летящее (IsFlying) — едущее захватывает как обычно.
		if (n.IsMoving && n.IsFlying) return;
		// Вращатель и бросатель ничего не копят (правило — в ColorAccepted; здесь быстрый выход).
		if (n.CoreTier == RotatorCoreTier || n.CoreTier == ThrowerCoreTier) return;
		if (n.Cross != null) return; // перекрёсток из месторождений не берёт (T010)
		if (_globalTick < n.NextCaptureTick) return;

		for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
		{
			int k = OrthogonalSlots[idx];
			int p = PhysicalSlotForCompass(n, k);
			if (!n.Ring[p].Exists || !n.Ring[p].IsHole) continue;
			if (IsClaimed(n, p)) continue;

			// Мост между сетками: соседняя клетка атома → мировые пиксели → row/col
			// каждого слоя частиц по его собственному CellSize.
			var (dr, dc) = Adj8[k];
			Vector2 neighborWorld = n.Center + new Vector2(dc, dr) * CellSize;

			foreach (var layer in _energyClusterLayers)
			{
				int srcCol = Mathf.FloorToInt(neighborWorld.X / layer.CellSize);
				int srcRow = Mathf.FloorToInt(neighborWorld.Y / layer.CellSize);
				if (!layer.HasClusterAt(srcRow, srcCol)) continue;
				if (DepositGivesOwnTierOnly && n.CoreTier != GrayCoreTier && n.CoreTier != layer.Tier) continue;
				// Та же проверка цвета, что у передачи между атомами (ColorAccepted).
				if (!ColorAccepted(n, layer.Tier)) continue;

				long got = layer.ConsumeAt(srcRow, srcCol, EnergyCaptureAmount);
				if (got <= 0) continue;

				n.Ring[p] = new RingSlot { Exists = true, IsHole = false, ColorTier = layer.Tier, Locked = true,
					Variant = ParticleVariant(srcRow, srcCol, _globalTick) };
				Touch(n);
				Claim(n, p);
				n.NextCaptureTick = _globalTick + _energyCaptureTicks;
				return; // одна попытка захвата на атом за тик
			}
		}
	}

	// Горизонт ЧД (T005, GDD «Встроенные объекты»): атом, который стоит на
	// клетке горизонта (поставлен игроком — доставленные захватываются сразу,
	// см. FinishArrivedMoves), отдаёт ЧД частицы. ЧД — как серое без спина
	// (TransferRules.NoSpin: любой тир и спин, любой цвет), дырки у неё всегда
	// свободны: забирается незаблокированная частица из гнезда, смотрящего на
	// ЧД, с учётом _claimed — то есть на шаге поворота атома, как при передаче
	// между атомами. Обход — ЧД по порядку BlackHoleSet, стороны N/E/S/W.
	// T028: единственное место правила «что берёт ЧД» — спрашивает цепочку заданий.
	// Песочница и нет активного этапа — всё.
	private bool BlackHoleAccepts(in RingSlot slot) =>
		!GoalsActive || Inventory.Sandbox
		|| Goals.Accepts(slot.IsItem ? GoalKind.Atom : GoalKind.Particle, slot.ColorTier);

	private void AbsorbFromHorizon()
	{
		foreach (var hole in BlackHoles.Enumerate())
		{
			for (int side = 0; side < 4; side++)
			{
				for (int i = 0; i < hole.Size; i++)
				{
					// Клетка горизонта и сторона света из неё на ЧД (компас Adj8).
					var (row, col, k) = side switch
					{
						0 => (hole.Row - 1, hole.Col + i, 4),         // сверху, смотрит на юг
						1 => (hole.Row + i, hole.Col + hole.Size, 6), // справа, смотрит на запад
						2 => (hole.Row + hole.Size, hole.Col + i, 0), // снизу, смотрит на север
						_ => (hole.Row + i, hole.Col - 1, 2),         // слева, смотрит на восток
					};
					if (!_entAt.TryGetValue((row, col), out var n)) continue;
					if (n.IsCargo) continue; // груз частиц не отдаёт (T006)
					if (_globalTick < n.AsleepUntilTick) continue; // ещё не проснулся — не крутится
					// У перекрёстка (T010) — частица у выхода на сторону ЧД.
					if (!TryPeekGive(n, k, out var slot)) continue;
					// T028: ненужное этапу остаётся в дырке (затор), сторона не занимается.
					if (!BlackHoleAccepts(slot)) continue;
					int p = SideKey(n, k);
					if (IsClaimed(n, p)) continue;
					if (!TierSpinAllowed(GrayCoreTier, TransferRules.NoSpin, n.CoreTier, n.Dir)) continue;

					TakeGiven(n, k);
					Claim(n, p);
					var (dr, dc) = Adj8[k];
					var from = n.Center + new Vector2(dc, dr) * _orbitRadius;
					if (slot.IsItem)
					{
						// Атом-предмет (T007) — в счётчик атомов по тиру, как при захвате.
						BlackHoles.AbsorbAtom(slot.ColorTier);
						_blackHoleLayer?.OnItemAbsorbed(hole, from, slot.ColorTier);
						continue;
					}
					BlackHoles.AbsorbParticle(slot.ColorTier);
					_blackHoleLayer?.OnParticleAbsorbed(hole, from, slot.ColorTier);
				}
			}
		}
	}

	// Атом доставлен на горизонт ЧД (или внутрь неё) вращателем или
	// бросателем — захватывается целиком в том же тике: +1 атом его тира,
	// частицы из его дырок — в счётчик по цветам, эффект падения из клетки
	// прибытия. Вызывается из FinishArrivedMoves до завершения переезда:
	// Row/Col у атома ещё старые (клетка, откуда выехал), он в списке старого
	// чанка и не в _entAt — RemoveNucleusEntity это учитывает.
	private bool TryCaptureArrived(NucleusEntity n)
	{
		int row = n.MoveToRow, col = n.MoveToCol;
		if (!BlackHoles.TryGetAt(row, col, out var hole) && !BlackHoles.TryGetHorizon(row, col, out hole, out _, out _))
			return false;

		var particles = new Atom();
		foreach (var slot in n.Ring)
		{
			if (!slot.Exists || slot.IsHole) continue;
			if (slot.IsItem) { BlackHoles.AbsorbAtom(slot.ColorTier); continue; } // предмет — атом (T007)
			BlackHoles.AbsorbParticle(slot.ColorTier);
			particles.Push(slot.ColorTier);
		}
		if (n.Cross != null) // частицы в пути перекрёстка (T010)
			foreach (var axis in n.Cross.Axes)
				for (int i = 0; i < axis.Count; i++)
				{
					var cp = axis.Items[i];
					if (cp.IsItem) { BlackHoles.AbsorbAtom(cp.ColorTier); continue; }
					BlackHoles.AbsorbParticle(cp.ColorTier);
					particles.Push(cp.ColorTier);
				}
		BlackHoles.AbsorbAtom(n.CoreTier);
		_blackHoleLayer?.OnAtomCaptured(hole, n.MoveToCenter, n.CoreTier, particles);
		RemoveNucleusEntity(n);
		return true;
	}

	// Звезда (T006, GDD «Звезда — сборщик») берёт частицы у атомов в 12
	// клетках вокруг — как горизонт ЧД (AbsorbFromHorizon): звезда как серое без
	// спина, незаблокированная частица из гнезда, смотрящего на звезду, с учётом
	// _claimed. Берёт только цвет, нужный рецепту, пока в буфере есть место;
	// остальное не трогает. Груз частиц не отдаёт. Обход — звёзды по порядку
	// StarSet, клетки по порядку Star.RingCells.
	private void FeedStarsFromNeighbors()
	{
		foreach (var star in Stars.All)
		{
			if (star.Choice == StarChoice.None) continue; // С ничего не принимает
			foreach (var (row, col, k) in star.RingCells())
			{
				if (!_entAt.TryGetValue((row, col), out var n)) continue;
				if (n.IsCargo || _globalTick < n.AsleepUntilTick) continue;
				// У перекрёстка (T010) — частица у выхода на сторону звезды.
				if (!TryPeekGive(n, k, out var slot)) continue;
				int p = SideKey(n, k);
				if (IsClaimed(n, p)) continue;
				// Атом-предмет (T007) — ингредиент-атом его тира, частица — по цвету.
				var kind = slot.IsItem ? IngredientKind.Atom : IngredientKind.Particle;
				if (!star.Accepts(kind, slot.ColorTier, RecipeAvailable)) continue;
				if (!TierSpinAllowed(GrayCoreTier, TransferRules.NoSpin, n.CoreTier, n.Dir)) continue;

				TakeGiven(n, k);
				Claim(n, p);
				star.Put(kind, slot.ColorTier);
				var (dr, dc) = Adj8[k];
				var from = n.Center + new Vector2(dc, dr) * _orbitRadius;
				if (slot.IsItem) _starLayer?.OnItemTaken(star, from, slot.ColorTier);
				else _starLayer?.OnParticleTaken(star, from, slot.ColorTier);
			}
		}
	}

	// Один тик производства всех звёзд (T006): полный буфер уходит в работу,
	// работа длится StarDuration тиков. Готовый атом — в выходной буфер (T008);
	// буфер полон — звезда стоит. Из буфера атомы отдаёт OutputStarsToHoles
	// (T007); груз на клетку выхода больше не кладётся (PlaceCargo заморожен).
	private void TickStars()
	{
		foreach (var star in Stars.All)
		{
			star.Advance();
			star.TryFinishToOutput();
		}
	}

	// Звезда отдаёт готовый атом-предмет (T007, GDD «Звезда — сборщик») из
	// головы выходного буфера (T008) в пустую дырку атома в клетке выхода,
	// обращённую к звезде, — на шаге атома-получателя, с учётом _claimed; звезда
	// как серое без спина (любой тир и спин). Нет такой дырки — атомы копятся в
	// буфере, попытка — каждый тик. Предмет блокируется до поворота атома.
	private void OutputStarsToHoles()
	{
		foreach (var star in Stars.All)
		{
			if (star.Output.Count == 0 || star.Choice == StarChoice.None) continue; // С в линию не отдаёт (T027)
			// Звезда-предмет (T011) по дыркам не едет — ждёт в буфере, пока игрок
			// не заберёт её в инвентарь; до тех пор выдача в линию стоит.
			if (StarItem.IsStar(star.Output.Peek())) continue;
			var (row, col) = star.OutputCell;
			if (!_entAt.TryGetValue((row, col), out var n)) continue;
			if (n.IsCargo || _globalTick < n.AsleepUntilTick || (n.IsMoving && n.IsFlying)) continue;
			if (IsSpinnerTier(n.CoreTier)) continue; // вращатели и бросатели ничего не хранят
			int k = Opposite(star.OutputSide * 2); // из клетки выхода на звезду
			// Перекрёсток (T010) принимает со стороны звезды и везёт на противоположную.
			if (!CanReceive(n, k)) continue;
			int p = SideKey(n, k);
			if (IsClaimed(n, p)) continue;
			if (!TierSpinAllowed(n.CoreTier, n.Dir, GrayCoreTier, TransferRules.NoSpin)) continue;

			PutReceived(n, k, new RingSlot { Exists = true, IsHole = false, ColorTier = star.Output.Dequeue(), IsItem = true });
			Claim(n, p);
			// Место в буфере освободилось — ждущая готовая работа уходит туда же.
			star.TryFinishToOutput();
		}
	}

	// T007: атомы-ингредиенты звезда берёт только предметами из дырок
	// (FeedStarsFromNeighbors); приём атомов, доставленных вращателем или
	// бросателем, выключен — код ниже заморожен.
	private static readonly bool StarAcceptsDeliveredAtoms = false;

	// Атом доставлен вращателем или бросателем в клетку вокруг звезды (или в
	// саму звезду) — поглощается целиком, если нужен рецепту и в буфере есть
	// место (частицы в его гнёздах сгорают). Иначе вызывающий оставляет его
	// лежать (из звезды — возвращает назад). Атом, поставленный игроком, сюда
	// не попадает — он не приезжает. Вызывается из FinishArrivedMoves, как
	// TryCaptureArrived.
	private bool TryFeedArrivedToStar(NucleusEntity n)
	{
		if (!StarAcceptsDeliveredAtoms) return false;
		if (IsSpinnerTier(n.CoreTier) || n.CoreTier == GrayCoreTier) return false;
		int row = n.MoveToRow, col = n.MoveToCol;
		foreach (var star in Stars.All)
		{
			if (!star.ContainsCell(row, col) && !star.IsRingCell(row, col)) continue;
			if (!star.Accepts(IngredientKind.Atom, n.CoreTier, RecipeAvailable)) continue;
			star.Put(IngredientKind.Atom, n.CoreTier);
			if (_starLayer != null)
			{
				var particles = new Atom();
				foreach (var slot in n.Ring)
					if (slot.Exists && !slot.IsHole) particles.Push(slot.ColorTier);
				_starLayer.OnAtomTaken(star, n.MoveToCenter, n.CoreTier, particles);
			}
			RemoveNucleusEntity(n);
			return true;
		}
		return false;
	}

	private readonly List<PortKey> _portKeys = new();

	// Один обмен на порт за вызов (потолок скорости — PortTicksPerParticle).
	// Выход забирает незаблокированную частицу из гнезда соседнего ядра,
	// смотрящего на клетку порта; вход кладёт следующую частицу своего атома в
	// пустое гнездо, частица блокируется до поворота ядра, как при обычной
	// передаче. Соседи — только внутри своего чанка (сосед через границу —
	// половина порта другого чанка, блок ↔ блок в T002 не передают).
	private void ExchangeWithPorts()
	{
		_portKeys.Clear();
		foreach (var pair in Ports.Enumerate())
			if (pair.Value.Mode != PortMode.Closed) _portKeys.Add(pair.Key);

		foreach (var key in _portKeys)
		{
			var mode = Ports.ModeOf(key);
			if (mode == PortMode.Output && !Ports.CanAcceptParticle(key)) continue;
			int emitColor = mode == PortMode.Input ? Ports.PeekParticle(key) : -1;
			if (mode == PortMode.Input && emitColor < 0) continue;

			bool done = false;
			for (int i = 0; i < 2 && !done; i++)
			{
				var (row, col) = Ports.Cell(key, i);
				for (int idx = 0; idx < OrthogonalSlots.Length && !done; idx++)
				{
					int k = OrthogonalSlots[idx];
					var (dr, dc) = Adj8[k];
					int nr = row + dr, nc = col + dc;
					if (!Ports.SameChunk(key, nr, nc) || Ports.IsPortCell(nr, nc)) continue;
					if (!_entAt.TryGetValue((nr, nc), out var n) || !_activeSet.Contains(n)) continue;
					if (n.IsMoving && n.IsFlying) continue;
					if (n.Cross != null) continue; // перекрёсток с портами не работает (T010, слой 2 заморожен)

					int p = PhysicalSlotForCompass(n, Opposite(k));
					if (IsClaimed(n, p)) continue;
					var slot = n.Ring[p];
					if (!slot.Exists) continue;

					if (mode == PortMode.Output)
					{
						if (slot.IsHole || slot.Locked || slot.IsItem) continue; // предметы порт не берёт (слой 2 заморожен)
						if (!TransferRules.TierSpinAllowed(GrayCoreTier, TransferRules.NoSpin, n.CoreTier, n.Dir,
								GrayCoreTier, GrayAcceptsAnySpin, RequireSameCoreTier)) continue;
						Ports.AcceptParticle(key, slot.ColorTier);
						n.Ring[p] = new RingSlot { Exists = true, IsHole = true };
						Touch(n);
					}
					else
					{
						if (!slot.IsHole) continue;
						if (!TransferRules.TierSpinAllowed(n.CoreTier, n.Dir, GrayCoreTier, TransferRules.NoSpin,
								GrayCoreTier, GrayAcceptsAnySpin, RequireSameCoreTier)) continue;
						if (!ColorAccepted(n, emitColor)) continue;
						Ports.EmitParticle(key);
						n.Ring[p] = new RingSlot { Exists = true, IsHole = false, ColorTier = emitColor, Locked = true };
						Touch(n);
					}
					Claim(n, p);
					done = true;
				}
			}
		}
	}

	// --- перемещение ядер по сетке (клеточный автомат) ---

	// Проверяет, наступил ли для ЭТОГО ротатора расчётный момент следующего
	// поворота, и если да — вызывает TriggerRotatorRotation. Период
	// срабатывания — ЧИСТАЯ функция (_globalTick, holeCount ЭТОГО ротатора),
	// без какого-либо состояния на ядро, а НЕ общее для всех ротаторов поле:
	// у ротатора с 2 гнёздами (сдвиг на 4 позиции — самый долгий переезд)
	// период дольше, чем у ротатора с 8 гнёздами, и это правильно — иначе
	// проверка происходила бы намного чаще, чем реально успевает завершиться
	// один полный переезд+ожидание, кольцо за это время успевало бы
	// провернуться на "чужое" число шагов, и активные направления съезжали
	// бы на диагональ (см. обсуждение "почему поворотники 2 и 4 захватывают
	// по диагонали").
	//
	// Формула (см. актуальный код в MaybeTriggerRotatorRotation): period =
	// OwnRotationTicks(тир) * shift + PauseTicksAfterStep, где shift =
	// 8/holeCount — то есть длительность одного переезда (moveDuration, см.
	// StartMove) плюс отдельно настраиваемая пауза сверху (по умолчанию 0 —
	// без паузы, крутится непрерывно; раньше здесь была жёстко зашитая
	// пауза "ещё один moveDuration простоя", то есть period=moveDuration*2,
	// но по заданию клиента задержку убрали из поведения по умолчанию и
	// вынесли в PauseTicksAfterStep). На доказательство ниже сама пауза не
	// влияет — она не поворачивает кольцо, а просто отодвигает момент
	// следующего срабатывания: за period тиков кольцо ВСЁ РАВНО провернётся
	// ровно на shift шагов по 45° (поворот происходит только внутри самого
	// TriggerRotatorRotation, а не "размазан" по тикам ожидания), а гнёзда у
	// BuildFixedRing расставлены равномерно с шагом shift — значит после
	// каждого срабатывания активные гнёзда гарантированно возвращаются в ТЕ
	// ЖЕ САМЫЕ направления, что и были, без дрейфа, независимо от паузы.
	//
	// holeCount берём как сырое количество Exists в Ring — оно НЕ зависит от
	// текущей фазы поворота кольца (расставлено раз и навсегда в
	// BuildFixedRing), поэтому его можно просто посчитать, без
	// PhysicalSlotForCompass (та развёртка нужна уже внутри
	// TriggerRotatorRotation — там важно, КАКИЕ именно направления сейчас
	// активны, а не сколько их).
	// Мгновенная передача целого ядра между двумя СОСЕДНИМИ активными
	// вращателями/бросателями — по заданию клиента, "по принципу как у
	// частиц" (см. Шаг 2 в SimTick), только для целого ядра, а не для
	// содержимого кольца, и КАЖДЫЙ тик, а не по расписанию.
	//
	// Зачем это вообще нужно: два вращателя с ОДИНАКОВЫМ holeCount
	// триггерятся АБСОЛЮТНО синхронно — MaybeTriggerRotatorRotation зависит
	// только от (_globalTick, holeCount, тир), без какой-либо привязки к
	// конкретному экземпляру. Если один и тот же сосед одновременно активен
	// у ДВУХ таких вращателей (т.е. стоит в клетке, которая для каждого из
	// них — направление с физической дыркой), то в момент их общего
	// срабатывания оба одновременно пытаются толкнуть этого же соседа — и
	// выигрывает всегда только тот, кто раньше встретился в переборе
	// _activeSet (см. проверку occ.IsMoving в TriggerRotatorRotation) —
	// НАВСЕГДА один и тот же, ядро никогда не попадает ко второму. Эта
	// функция решает гонку явно и сразу: если сосед, которого держит
	// rotator, ОДНОВРЕМЕННО активен и для другого активного вращателя/
	// бросателя — ядро немедленно продвигается на шаг вокруг ЧУЖОГО кольца
	// (по формуле ЧУЖОГО толчка), не дожидаясь вообще ничьего периода.
	//
	// HandoffLastGiver — блокировка обратного захвата (по заданию): ядро
	// нельзя тем же способом вернуть тому, кто последним его так отдал,
	// иначе получился бы бесконечный пинг-понг между двумя соседними
	// вращателями. Дальше по цепочке (третьему вращателю) передавать можно
	// без ограничений.
	private void TryHandOffSharedOccupant(NucleusEntity rotator)
	{
		bool[] active = CanonicalActiveDirections(rotator, out int activeCount);
		if (activeCount == 0) return;

		for (int k = 0; k < 8; k++)
		{
			if (!active[k]) continue;
			var (dr, dc) = Adj8[k];
			int occRow = rotator.Row + dr, occCol = rotator.Col + dc;
			if (!_entAt.TryGetValue((occRow, occCol), out var occ)) continue;
			if (occ.IsMoving) continue;
			// Вращатель/бросатель не участвует в этом механизме как ЗАХВАЧЕННОЕ
			// ядро — по заданию (см. IsSpinnerTier), эти два тира друг с другом
			// вообще не взаимодействуют.
			if (IsSpinnerTier(occ.CoreTier)) continue;

			// Ищем ДРУГОГО активного вращателя/бросателя, для которого ЭТА ЖЕ
			// самая клетка тоже активна (см. комментарий выше) — перебираем 8
			// соседей УЖЕ ЗАХВАЧЕННОГО ядра, а не всех активных вращателей на
			// поле, это дёшево и достаточно (два вращателя, претендующих на
			// одного соседа, всегда сами соседи этого соседа).
			NucleusEntity other = null;
			int otherK = -1;
			for (int od = 0; od < 8; od++)
			{
				var (odr, odc) = Adj8[od];
				if (!_entAt.TryGetValue((occRow + odr, occCol + odc), out var cand)) continue;
				if (cand == rotator || !IsSpinnerTier(cand.CoreTier)) continue;
				int backK = Opposite(od); // направление ОТ cand К occ
				bool[] candActive = CanonicalActiveDirections(cand, out _);
				if (!candActive[backK]) continue;
				other = cand;
				otherK = backK;
				break;
			}
			if (other == null) continue;

			// Блокировка обратного захвата — не отдаём тому, кто только что
			// сам отдал нам это ядро.
			if (occ.HandoffLastGiver == other) continue;

			int otherHoleCount = 0;
			foreach (var slot in other.Ring) if (slot.Exists) otherHoleCount++;
			if (otherHoleCount == 0 || 8 % otherHoleCount != 0) continue;
			int otherShift = 8 / otherHoleCount;

			if (other.CoreTier == ThrowerCoreTier)
			{
				// Тот же формат броска, что и в TriggerRotatorRotation —
				// касательная от направления otherK, повёрнутая на 90° в
				// сторону собственного вращения other.
				int throwDir = ((otherK + 2 * other.Dir) % 8 + 8) % 8;
				var (tdr, tdc) = Adj8[throwDir];
				int throwRow = occ.Row + tdr;
				int throwCol = occ.Col + tdc;
				if (_entAt.ContainsKey((throwRow, throwCol))) continue;

				occ.HandoffLastGiver = rotator;
				occ.PendingFlightDir = throwDir;
				StartMove(occ, throwRow, throwCol, ThrowTicksPerCell);
				continue;
			}

			int otherSignedShift = otherShift * other.Dir;
			int destK = ((otherK + otherSignedShift) % 8 + 8) % 8;
			var (destDr, destDc) = Adj8[destK];
			int destRow = other.Row + destDr;
			int destCol = other.Col + destDc;
			if (_entAt.ContainsKey((destRow, destCol))) continue;

			occ.HandoffLastGiver = rotator;
			int otherMoveDuration = OwnRotationTicks(other.CoreTier) * otherShift;
			if (otherShift != 1)
			{
				float startAngle = otherK * (Mathf.Pi / 4f) - (Mathf.Pi / 2f);
				float sweepAngle = otherSignedShift * (Mathf.Pi / 4f);
				StartMove(occ, destRow, destCol, otherMoveDuration, other.Center, startAngle, sweepAngle);
			}
			else
			{
				StartMove(occ, destRow, destCol, otherMoveDuration);
			}
		}
	}

	// По заданию клиента "задержка ожидания" в точке назначения убрана из
	// поведения по умолчанию: раньше период между двумя толчками (period)
	// был РОВНО в 2 раза длиннее самого переезда (moveDuration), то есть
	// ядро, доехав, ещё столько же времени просто стояло без дела, прежде
	// чем ехать дальше (см. историю в комментарии у StartMove). Теперь
	// период = длительность самого переезда (moveDuration) + отдельно
	// настраиваемая пауза сверху (см. PauseTicksAfterStep) — при
	// PauseTicksAfterStep=0 (значение по умолчанию) это ровно moveDuration,
	// и ядро, доехав до новой фазы, тут же (на следующем шаге проверки)
	// снова становится кандидатом на толчок, т.е. крутится непрерывно, без
	// какой-либо паузы. Если клиенту снова понадобится пауза — она теперь
	// просто настраивается через PauseTicksAfterStep, а не переоткрывается
	// заново в формуле.
	private void MaybeTriggerRotatorRotation(NucleusEntity rotator)
	{
		int holeCount = 0;
		foreach (var slot in rotator.Ring) if (slot.Exists) holeCount++;
		if (holeCount <= 0 || 8 % holeCount != 0) return; // пусто, либо нестандартный holeCount — через панель спавна не бывает, но на всякий случай не ломаемся

		int shift = 8 / holeCount;
		long period = (long)OwnRotationTicks(rotator.CoreTier) * shift + PauseTicksAfterStep;
		if (period <= 0 || _globalTick % period != 0) return;

		TriggerRotatorRotation(rotator);
	}

	// Вызывается из MaybeTriggerRotatorRotation, когда для ротатора наступил
	// расчётный момент поворота.
	//
	// По заданию поворачиваются НЕ все 8 соседей всегда, а только те, что
	// стоят в направлениях, где у САМОГО поворачивателя физически есть гнездо
	// (Ring[...].Exists — та же раскладка, что и у обычного ядра с той же
	// панели спавна, см. BuildFixedRing/HolePriority): 2 гнезда — только
	// верх/низ, 4 — верх/право/низ/лево (все ортогонали), 8 — все 8
	// направлений. Компас→физический слот берём через PhysicalSlotForCompass
	// (та же функция, что и everywhere в SimTick), поэтому если у
	// поворачивателя ещё и крутится собственное кольцо (TierTicks[RotatorCoreTier]),
	// набор активных направлений тоже честно вращается вместе с ним, а не
	// стоит намертво в исходной ориентации спавна.
	//
	// Шаг поворота напрямую следует из числа активных направлений: 2 (180°)
	// — сдвиг на 4 позиции по кругу Adj8, 4 (90°) — на 2, 8 (45°) — на 1.
	// Общая формула — 8/activeCount. Это работает без исключений именно
	// потому, что активные позиции при 2/4/8 гнёздах у BuildFixedRing всегда
	// разнесены РАВНОМЕРНО по кругу — сдвиг на 8/activeCount переводит любую
	// активную позицию строго в другую активную же, круг замкнут сам на себя,
	// и соседей в неактивных направлениях эффект вообще не касается (они не
	// участвуют ни как источник, ни как цель перемещения).
	//
	// ВАЖНО (баг "поворотники 2/4 захватывают/бросают по диагонали"):
	// активные направления берутся ЗДЕСЬ и в TryCatchFlyingNeighbor через
	// CanonicalActiveDirections — т.е. напрямую из Ring[k].Exists, БЕЗ
	// разворачивания через живой PhysicalSlotForCompass. Причина — кольцо
	// физически крутится непрерывно (DiscreteRotationOffset проходит все 8
	// положений по 45°), и у ротатора с 2/4 гнёздами (гнёзда расставлены
	// РАВНОМЕРНО с шагом shift=8/holeCount) ровно половину времени живой
	// разворот показывает гнёзда, "уехавшие" на диагональ, хотя канонически
	// (и в момент, когда MaybeTriggerRotatorRotation вообще разрешает сюда
	// зайти — период у неё нарочно подобран кратным shift*2, см. её
	// комментарий) они всегда возвращаются РОВНО в исходные направления
	// (для 2/4/8 гнёзд, равномерно расставленных с шагом shift, сдвиг набора
	// на любое кратное shift переводит набор сам в себя — математически
	// совпадает с offset=0, т.е. с сырым Ring[k].Exists). Раньше эта функция
	// вызывалась ТОЛЬКО в такие выровненные тики, поэтому разница была не
	// видна; TryCatchFlyingNeighbor же теперь вызывается КАЖДЫЙ тик (см. её
	// комментарий) — в "невыровненные" тики живой разворот отдавал диагональ,
	// и поимка/перезапуск полёта уезжали по диагонали. Раз канонический и
	// выровненный живой расчёты доказуемо совпадают, а PhysicalSlotForCompass
	// больше нигде в этих двух методах не нужен — просто всегда берём
	// канонический набор, без выравнивания по тику.
	private void TriggerRotatorRotation(NucleusEntity rotator)
	{
		bool[] active = CanonicalActiveDirections(rotator, out int activeCount);
		if (activeCount == 0 || 8 % activeCount != 0) return; // пусто, либо нестандартный holeCount — через панель спавна не бывает, но на всякий случай не ломаемся

		int shift = 8 / activeCount;
		// Длительность самого движения — по заданию, собственная скорость
		// ЭТОГО тира (тиков на одну фазу) × число сдвигаемых фаз: чем больше
		// поворот (2/4 фазы вместо 1), тем дольше едет, с одной и той же
		// угловой скоростью, а не с одной и той же длительностью на любое
		// расстояние. Ожидание в точке назначения (ещё столько же) считается
		// уже снаружи, из периода срабатывания (см. MaybeTriggerRotatorRotation).
		int moveDuration = OwnRotationTicks(rotator.CoreTier) * shift;

		// Направление толчка соседей (по часовой/против) теперь берётся из
		// СОБСТВЕННОГО направления вращения этого ротатора/бросателя
		// (rotator.Dir, см. R/_currentSpinDirection) — та же величина, что
		// уже определяет направление вращения его же кольца в
		// DiscreteRotationOffset, чтобы визуально толчок соседей всегда шёл
		// в ту же сторону, в которую крутится само кольцо ротатора (иначе
		// кольцо крутилось бы, например, по часовой, а толкало бы против —
		// выглядело бы рассинхронизированным). shift сам по себе остаётся
		// БЕЗ знака (используется как модуль везде, где важно только
		// расстояние — период, длительность движения), а signedShift — уже
		// с учётом направления, только для вычисления самой целевой фазы.
		int signedShift = shift * rotator.Dir;

		// При сдвиге на 2 или 4 фазы (90°/180°) — по заданию, интерполяция
		// должна идти по дуге окружности вокруг поворачивателя, а не по
		// прямой (которая на 90°/180° срезала бы совсем рядом с ним или прямо
		// через него). При сдвиге на 1 фазу (45°, holeCount=8) оставляем
		// обычную прямую — угол между соседними компасами мал, дуга там
		// визуально не нужна. Угол компаса k в мировых координатах —
		// k*(π/4) - π/2 (та же формула, что и всюду для AngleVec/Adj8 — см.
		// комментарий у UpdateChunkVisuals), сдвиг — по часовой или против,
		// см. signedShift выше.
		bool circular = shift != 1;

		var occupants = new NucleusEntity[8];
		for (int k = 0; k < 8; k++)
		{
			if (!active[k]) continue;
			var (dr, dc) = Adj8[k];
			_entAt.TryGetValue((rotator.Row + dr, rotator.Col + dc), out occupants[k]);
		}

		if (rotator.CoreTier == ThrowerCoreTier)
		{
			// Бросатель — независимая обработка каждого соседа, без общей
			// цепочки ниже: он не толкает соседа на новую фазу, а сразу
			// срывает его в полёт по касательной с ТЕКУЩЕГО места (см.
			// комментарий у ThrowerCoreTier), так что тут нет проблемы "кто
			// кому мешает у общей клетки назначения" — каждый улетает в
			// СВОЮ сторону, а не встаёт в чужую физическую фазу этого же
			// кольца.
			for (int k = 0; k < 8; k++)
			{
				if (!active[k]) continue;
				var occ = occupants[k];
				if (occ == null || occ.IsMoving) continue;
				// По заданию — вращатель и бросатель не взаимодействуют друг
				// с другом ни в каком сочетании (см. IsSpinnerTier).
				if (IsSpinnerTier(occ.CoreTier)) continue;

				// Касательная — направление k (радиус от бросателя к ядру),
				// сдвинутое на 2 позиции компаса (90°) в сторону
				// СОБСТВЕННОГО направления вращения бросателя (rotator.Dir).
				// Первый же "прыжок" полёта — уже на скорости
				// ThrowTicksPerCell, см. PendingFlightDir/BeginFlight.
				int throwDir = ((k + 2 * rotator.Dir) % 8 + 8) % 8;
				var (tdr, tdc) = Adj8[throwDir];
				int throwRow = occ.Row + tdr;
				int throwCol = occ.Col + tdc;

				occ.PendingFlightDir = throwDir;
				StartMove(occ, throwRow, throwCol, ThrowTicksPerCell);
			}
			return;
		}

		// --- обычный вращатель: толчок соседей на одну фазу вперёд ---
		//
		// Активные позиции всегда образуют РОВНО ОДНУ циклическую цепочку
		// длиной activeCount (см. доказательство у CanonicalActiveDirections:
		// они расставлены равномерно с шагом shift, и k+shift снова попадает
		// в этот же набор) — k0, k0+shift, k0+2*shift, ..., по кругу обратно
		// в k0. Раньше тут стояла наивная проверка "cвободна ли клетка
		// назначения" по живому _entAt для КАЖДОГО k независимо — и именно
		// эта цикличность её ломала: если кольцо соседей занято ПОЛНОСТЬЮ
		// (ни одной пустой фазы), каждый сосед хочет встать на место
		// следующего, а тот ещё физически там стоит (просто до него не
		// дошла очередь в переборе k=0..7) — в итоге блокировались вообще
		// ВСЕ, хотя это обычное синхронное вращение полностью укомплектован-
		// ного кольца (все едут одновременно на шаг дальше, как один жёсткий
		// диск) — отсюда баг "вращатель перестал вращать ядра" на
		// симметричной раскладке (крест из 4 ядер вокруг 4-дырочного
		// вращателя, см. отчёт в чате).
		//
		// Собираем цепочку в порядке толчка и решаем её как единое целое:
		// пустая фаза и сосед-Spinner (вращатель/бросатель, сам никогда не
		// подвинется, см. IsSpinnerTier) — это "стены" цепочки; обычный
		// сосед едет ровно тогда, когда стена, в которую он упирается по
		// ходу толчка, — пустая фаза, а не Spinner. Особый случай — кольцо
		// вообще без единой стены (заполнено только обычными соседями): тогда
		// это единая замкнутая петля без разрывов, и она проворачивается
		// целиком (иначе не провернулась бы вообще никогда, раз свободного
		// места формально "нет" — при этом мест ровно хватает всем).
		// holeCount=1 — вырожденный случай: shift=8, то есть "толчок" привёл
		// бы соседа обратно в ЕГО ЖЕ клетку (destK==k) — реального перемещения
		// тут нет никогда, поэтому просто ничего не делаем (как и раньше —
		// старая проверка занятости всегда блокировала это же самое, только
		// как побочный эффект, а не явно).
		if (activeCount <= 1) return;

		int firstActive = -1;
		for (int k = 0; k < 8; k++) { if (active[k]) { firstActive = k; break; } }

		var cycleK = new int[activeCount];
		{
			int k = firstActive;
			for (int i = 0; i < activeCount; i++)
			{
				cycleK[i] = k;
				k = ((k + signedShift) % 8 + 8) % 8;
			}
		}

		bool anyGap = false, anySpinner = false;
		for (int i = 0; i < activeCount; i++)
		{
			var occ = occupants[cycleK[i]];
			if (occ == null) anyGap = true;
			else if (IsSpinnerTier(occ.CoreTier)) anySpinner = true;
		}

		var moves = new bool[activeCount];
		if (!anyGap && !anySpinner)
		{
			for (int i = 0; i < activeCount; i++) moves[i] = true;
		}
		else
		{
			for (int i = 0; i < activeCount; i++)
			{
				var occ = occupants[cycleK[i]];
				if (occ == null || IsSpinnerTier(occ.CoreTier)) continue; // сама пустая фаза/Spinner никуда не едет

				// Идём вперёд по цепочке до первой "стены" — раз anyGap ||
				// anySpinner тут гарантированно true, цикл не может пройти
				// полный круг обратно на i, не встретив ни одной.
				int j = (i + 1) % activeCount;
				while (occupants[cycleK[j]] != null && !IsSpinnerTier(occupants[cycleK[j]].CoreTier))
					j = (j + 1) % activeCount;

				moves[i] = occupants[cycleK[j]] == null; // стена пустая — едем; Spinner — стоим
			}
		}

		for (int i = 0; i < activeCount; i++)
		{
			if (!moves[i]) continue;
			var occ = occupants[cycleK[i]];
			int k = cycleK[i];
			int destK = cycleK[(i + 1) % activeCount];
			var (destDr, destDc) = Adj8[destK];
			int destRow = rotator.Row + destDr;
			int destCol = rotator.Col + destDc;

			if (circular)
			{
				float startAngle = k * (Mathf.Pi / 4f) - (Mathf.Pi / 2f);
				float sweepAngle = signedShift * (Mathf.Pi / 4f);
				StartMove(occ, destRow, destCol, moveDuration, rotator.Center, startAngle, sweepAngle);
			}
			else
			{
				StartMove(occ, destRow, destCol, moveDuration);
			}
		}
	}

	// Захват ПРОЛЕТАЮЩЕГО мимо ядра (см. ThrowerCoreTier/EvaluateFlightStep)
	// --- по заданию клиента, вращатель и бросатель ловят пролетающее ядро
	// ровно по тем же активным направлениям, по которым толкают статичных
	// соседей (active[k] — прямые для 2/4 гнёзд, плюс диагонали для 8).
	//
	// ВАЖНО: вызывается из SimTick КАЖДЫЙ тик для каждого активного
	// вращателя/бросателя, а НЕ только в момент его собственного
	// срабатывания толчка (MaybeTriggerRotatorRotation) — раньше эта
	// проверка была частью TriggerRotatorRotation и потому срабатывала так
	// же редко, как сам толчок (период = OwnRotationTicks*shift*2), тогда
	// как пролетающее ядро проходит мимо всего за ThrowTicksPerCell тиков на
	// клетку — почти всегда успевало проскочить мимо между двумя редкими
	// проверками, отсюда и баг "вращатель не захватывает пролетающее ядро".
	//
	// Ищем среди _movingSet (летящее ядро на всё время полёта полностью
	// изъято из _entAt, см. StartMove, поэтому обычный occupants[]-поиск его
	// никогда не найдёт — тот же приём, что уже понадобился для починки
	// пипетки, см. PickNucleusUnderMouse) — сравниваем по клетке, ближайшей
	// к текущей интерполированной позиции (EffectiveCenter).
	//
	// "Ближайшая свободная фаза" (по заданию, дословно — задача этим местом
	// сформулирована не до конца однозначно, реализовано по собственному
	// разумению): ближайшая по кругу от точки поимки активная фаза, чья
	// клетка прямо сейчас пуста в _entAt.
	private void TryCatchFlyingNeighbor(NucleusEntity rotator)
	{
		if (_movingSet.Count == 0) return; // нечего ловить — не тратим время на active[] ниже

		// Канонический набор (см. CanonicalActiveDirections/TriggerRotatorRotation),
		// а НЕ живой PhysicalSlotForCompass — этот метод, в отличие от
		// TriggerRotatorRotation, вызывается КАЖДЫЙ тик, а не только в
		// выровненные моменты, поэтому живой разворот у 2/4-гнёздных
		// поворотников половину времени показывал бы диагональ (баг
		// "останавливает и запускает по диагонали").
		bool[] active = CanonicalActiveDirections(rotator, out int activeCount);
		if (activeCount == 0) return;

		for (int k = 0; k < 8; k++)
		{
			if (!active[k]) continue;
			var (dr, dc) = Adj8[k];
			int nr = rotator.Row + dr, nc = rotator.Col + dc;

			NucleusEntity caught = null;
			foreach (var f in _movingSet)
			{
				if (!f.IsFlying || f == rotator) continue;
				// Вращатель/бросатель не ловит пролетающего вращатель/бросатель
				// (см. IsSpinnerTier) — по заданию, эти два тира друг с другом
				// не взаимодействуют вообще; на практике летящий вращатель/
				// бросатель сейчас и так не может возникнуть (см. запрет ниже
				// в TriggerRotatorRotation), проверка тут — на случай будущих
				// путей попадания в полёт.
				if (IsSpinnerTier(f.CoreTier)) continue;
				// НЕ ловим ядро, чей ТЕКУЩИЙ прыжок полёта стартовал ИЗ ЭТОЙ ЖЕ
				// самой соседней клетки (nr,nc) — иначе получается бесконечный
				// цикл "поймали → тут же отпустили (relaunch) → в тот же миг
				// поймали заново", потому что этот метод проверяется КАЖДЫЙ тик
				// по ТЕКУЩЕЙ интерполированной позиции (EffectiveCenter), а не
				// только по факту прибытия — и всё то время, что только что
				// отпущенное ядро визуально ещё не покинуло клетку nr,nc (первая
				// половина анимации нового прыжка), CellOf(EffectiveCenter)
				// продолжает возвращать nr,nc, и мы бы ловили СОБСТВЕННЫЙ же
				// только что состоявшийся бросок повторно — каждый раз заново
				// сбрасывая MoveStartTick на "сейчас", отчего IsMoving так и не
				// становится false НИКОГДА (баг из отчёта клиента: "клетку
				// нельзя удалить, а если удалить бросатель — она улетает в
				// сторону, где была захвачена", т.е. в тот самый момент
				// зависания как раз стоял направлением на исходный catch).
				// У ДЕЙСТВИТЕЛЬНО пролетающего мимо ядра клетка отправления его
				// текущего прыжка (MoveFromRow/Col) — какая-то ДРУГАЯ, более
				// дальняя клетка, поэтому настоящий "пролёт мимо" эта проверка
				// не задевает.
				if (f.MoveFromRow == nr && f.MoveFromCol == nc) continue;
				// Позиция на границе тика (T014): без доли кадра, иначе поимка зависит от FPS.
				var cell = CellOf(EffectiveCenterAt(f, 0f));
				if (cell.row == nr && cell.col == nc) { caught = f; break; }
			}
			if (caught == null) continue;

			int destK = -1;
			for (int dist = 0; dist < 8 && destK == -1; dist++)
			{
				int kPlus = (k + dist) % 8;
				if (active[kPlus])
				{
					var (pdr, pdc) = Adj8[kPlus];
					if (!_entAt.ContainsKey((rotator.Row + pdr, rotator.Col + pdc))) { destK = kPlus; break; }
				}
				if (dist == 0) continue;
				int kMinus = (k - dist + 8) % 8;
				if (active[kMinus])
				{
					var (mdr, mdc) = Adj8[kMinus];
					if (!_entAt.ContainsKey((rotator.Row + mdr, rotator.Col + mdc))) { destK = kMinus; break; }
				}
			}
			if (destK == -1) continue; // все активные фазы заняты прямо сейчас — не ловим, летит дальше как летело

			// Снимаем с полёта и телепортируем "логически" в клетку поимки —
			// StartMove ниже читает n.Row/n.Col/n.Center как СТАРТ переезда,
			// а у летящего ядра они всё ещё указывают на клетку ДО броска
			// (см. комментарий у StartMove/IsMoving), поэтому обязательно
			// выставляем их на клетку поимки ДО вызова StartMove/прямой записи
			// в _entAt ниже.
			_movingSet.Remove(caught);
			caught.IsFlying = false;
			caught.PendingFlightDir = null;
			SetEntityCell(caught, nr, nc);
			caught.Center = new Vector2(nc * CellSize + CellSize / 2f, nr * CellSize + CellSize / 2f);

			var (destDr2, destDc2) = Adj8[destK];
			int destRow2 = rotator.Row + destDr2;
			int destCol2 = rotator.Col + destDc2;

			int rawDiff = destK - k;
			if (rawDiff > 4) rawDiff -= 8;
			else if (rawDiff < -4) rawDiff += 8;

			// Всегда через StartMove (даже когда destK==k, т.е. пойманное
			// ядро остаётся в той же клетке, где его поймали) — НАМЕРЕННО, а
			// не отдельной веткой "мгновенно приземлилось" напрямую в
			// _entAt: только штатный конвейер прибытия (FinishArrivedMoves)
			// корректно переносит ядро между чанками (LocalIndex/Nuclei) и
			// подхватывает PendingFlightDir для цепного перезапуска полёта —
			// дублировать эту логику здесь ещё раз было бы лишним источником
			// рассинхрона. Вырожденный "переезд на месте" — минимальная 1
			// тиковая длительность (StartMove сама не даёт уйти в 0).
			//
			// Направление повторного броска — та же самая касательная формула,
			// что и у первого, самого первого броска в TriggerRotatorRotation
			// (radius-направление, повёрнутое на 90° в сторону rotator.Dir), а
			// НЕ просто destK (радиально наружу, вдоль той же линии, откуда
			// ядро прилетело) — по заданию клиента бросатель всегда бросает по
			// касательной собственного вращения, и это должно быть верно для
			// ЛЮБОГО броска этим бросателем, включая повторный бросок только
			// что пойманного ядра, а не только для самого первого.
			if (rotator.CoreTier == ThrowerCoreTier)
				caught.PendingFlightDir = ((destK + 2 * rotator.Dir) % 8 + 8) % 8;

			int steps = Mathf.Abs(rawDiff);
			int catchMoveDuration = steps == 0 ? 1 : OwnRotationTicks(rotator.CoreTier) * steps;
			if (steps <= 1)
			{
				StartMove(caught, destRow2, destCol2, catchMoveDuration);
			}
			else
			{
				float startAngle = k * (Mathf.Pi / 4f) - (Mathf.Pi / 2f);
				float sweepAngle = rawDiff * (Mathf.Pi / 4f);
				StartMove(caught, destRow2, destCol2, catchMoveDuration, rotator.Center, startAngle, sweepAngle);
			}
		}
	}

	// Запускает плавный переезд ядра n в клетку (toRow, toCol) за moveTicks
	// тиков — это ВСЯ длительность анимации, без внутреннего удвоения.
	// "Ждать в точке столько же, сколько занял поворот" (по заданию) теперь
	// получается САМО, снаружи, а не за счёт растягивания самого переезда:
	// MaybeTriggerRotatorRotation проверяет свой ротатор раз в
	// moveTicks*2 тиков (см. её комментарий), а сам переезд занимает
	// moveTicks — то есть ядро доезжает ровно за первую половину периода
	// между двумя срабатываниями и потом просто стоит без дела (никем не
	// тронутое) ровно вторую половину — столько же, сколько ехало — вплоть
	// до следующего срабатывания того же ротатора. Раньше это же
	// пытались получить, удваивая MoveDurationTicks и держа IsMoving=true
	// весь удвоенный срок — но тогда ядро оказывалось изъято из _entAt (не
	// удаляется ПКМ, не участвует в передаче/захвате) на ВЕСЬ период между
	// срабатываниями, включая момент, когда следующее срабатывание уже
	// подоспело — на практике ядро почти никогда не бывало "свободным"
	// (сообщалось как "повёрнутые ядра не удаляются правой кнопкой").
	// Теперь ядро изъято из _entAt ТОЛЬКО пока реально едет — как и
	// требовалось изначально ("не взаимодействует ни с кем на время
	// переезда"), а не на время переезда плюс ожидание.
	//
	// Сразу изымает n из _entAt — на время самого переезда оно физически
	// ещё "нигде" с точки зрения соседей (не приёмник и не отдающий, не
	// находится ПКМ-удалением), а Row/Col/Center у него остаются СТАРЫМИ до
	// самого прибытия (см. FinishArrivedMoves).
	//
	// arcPivot — необязательный центр дуги: если задан, интерполяция идёт по
	// окружности вокруг него (радиус берётся из фактического расстояния до
	// стартовой позиции — не параметр, а измеряется, чтобы гарантированно
	// совпасть с реальной клеткой старта) от arcStartAngle на arcSweepAngle
	// радиан, вместо обычной прямой (см. EffectiveCenter). Используется
	// поворачивателем при сдвиге на 2/4 фазы (см. TriggerRotatorRotation);
	// без arcPivot (null) — обычная линейная интерполяция, как и раньше.
	private void StartMove(NucleusEntity n, int toRow, int toCol, int moveTicks, Vector2? arcPivot = null, float arcStartAngle = 0f, float arcSweepAngle = 0f)
	{
		_entAt.Remove((n.Row, n.Col));
		_movingSet.Add(n);
		Touch(n);
		n.IsMoving = true;
		n.MoveFromRow = n.Row;
		n.MoveFromCol = n.Col;
		n.MoveToRow = toRow;
		n.MoveToCol = toCol;
		n.MoveFromCenter = n.Center;
		n.MoveToCenter = new Vector2(toCol * CellSize + CellSize / 2f, toRow * CellSize + CellSize / 2f);
		n.MoveStartTick = _globalTick;
		n.MoveDurationTicks = Mathf.Max(1, moveTicks);

		n.MoveIsCircular = arcPivot.HasValue;
		if (arcPivot.HasValue)
		{
			n.MoveArcPivot = arcPivot.Value;
			n.MoveArcRadius = (n.MoveFromCenter - arcPivot.Value).Length();
			n.MoveArcStartAngle = arcStartAngle;
			n.MoveArcSweepAngle = arcSweepAngle;
		}
	}

	// Переводит из _sleepingSet в _activeSet всех, для кого уже наступил
	// AsleepUntilTick (см. NucleusEntity.AsleepUntilTick/TryPlaceNucleus) —
	// тем же принципом, что и FinishArrivedMoves для _movingSet: собираем
	// список готовых заранее, мутировать множество во время его же перебора
	// нельзя. Само пробуждение — просто смена набора, никакого отдельного
	// "визуального выравнивания" не нужно: раз AsleepUntilTick — это именно
	// тот тик, где живая формула DiscreteRotationOffset для этого тира сама
	// даёт 0, рендер (см. UpdateChunkVisuals) естественно продолжит ровно с
	// того же угла, на котором до сих пор рисовал спящее ядро.
	private void WakeSleepingNuclei()
	{
		List<NucleusEntity> woken = null;
		foreach (var n in _sleepingSet)
		{
			if (_globalTick < n.AsleepUntilTick) continue;
			(woken ??= new List<NucleusEntity>()).Add(n);
		}
		if (woken == null) return;

		foreach (var n in woken)
		{
			_sleepingSet.Remove(n);
			AddActive(n);
		}
	}

	// Проверяет всех сейчас едущих ядер и завершает переезд тем, для кого
	// вышло MoveDurationTicks тиков: обновляет Row/Col/Center на целевые,
	// возвращает ядро в _entAt (снова видимо соседям) и, если целевая клетка
	// оказалась в ДРУГОМ чанке — переносит ядро между списками Nuclei чанков
	// и перестраивает MultiMesh обоих (тот же приём, что и в
	// RemoveNucleusAt/TryPlaceNucleus). Собирает список прибывших заранее —
	// мутировать _movingSet во время его же перебора нельзя.
	private void FinishArrivedMoves()
	{
		List<NucleusEntity> arrived = null;
		foreach (var n in _movingSet)
		{
			if (_globalTick < n.MoveStartTick + n.MoveDurationTicks) continue;
			(arrived ??= new List<NucleusEntity>()).Add(n);
		}
		if (arrived == null) return;

		foreach (var n in arrived)
		{
			_movingSet.Remove(n);
			n.IsMoving = false;

			// Доставлен на горизонт ЧД (переезд от вращателя, толчок или шаг
			// полёта бросателя) — захвачен целиком (T005), дальше не едет.
			if (BlackHoles.Count > 0 && TryCaptureArrived(n)) continue;
			// Доставлен к звезде и нужен рецепту — поглощён (T006).
			if (Stars.Count > 0 && TryFeedArrivedToStar(n)) continue;

			int destRow = n.MoveToRow;
			int destCol = n.MoveToCol;

			// Защита от редкого конфликта: пока ядро ехало, кто-то вручную
			// поставил новое ядро прямо в его клетку назначения (единственный
			// способ это сделать — TryPlaceNucleus видит клетку свободной,
			// т.к. на время переезда она не числится в _entAt). Переписывать
			// чужое ядро нельзя — откатываем переезд, возвращая ядро на ту
			// клетку, откуда оно выехало (она гарантированно всё ещё свободна,
			// никто другой её не занимал, пока n оттуда числилось выехавшим).
			// То же — если вращатель довёз атом в клетку звезды, а звезде он не
			// нужен (T006): внутри звезды атом лежать не может.
			// То же — клетка назначения в закрытом чанке (T009).
			if (_entAt.ContainsKey((destRow, destCol)) || Stars.TryGetAt(destRow, destCol, out _) || !IsCellOpen(destRow, destCol))
			{
				if (_entAt.ContainsKey((destRow, destCol)))
					GD.PrintErr($"[NucleusLayer] переезд в клетку ({destRow},{destCol}) отменён — она уже занята; ядро возвращено в ({n.MoveFromRow},{n.MoveFromCol}).");
				destRow = n.MoveFromRow;
				destCol = n.MoveFromCol;
			}

			// Смена клетки и, если нужно, чанка (списки Nuclei и MultiMesh) — SetEntityCell.
			SetEntityCell(n, destRow, destCol);
			// Откат в клетку, которую уже занял другой атом (старая ошибка отката,
			// поведение не меняем): прежний хозяин клетки пропадает из _entAt, но
			// остаётся рабочим — соседи его не видят, а он их видит, как атом в пути.
			if (_entAt.TryGetValue((n.Row, n.Col), out var displaced) && displaced != n)
				_detachedSet.Add(displaced);
			_entAt[(n.Row, n.Col)] = n;

			// --- бросатель: подхват/продолжение полёта (см. ThrowerCoreTier/
			// EvaluateFlightStep) --- ПОСЛЕ того, как переезд физически
			// завершён и ядро гарантированно уже в _entAt и в правильном
			// чанке, чтобы соседские проверки внутри EvaluateFlightStep
			// (обычное ядро рядом, занятость следующей клетки)
			// видели консистентное состояние поля, а не промежуточное.
			if (n.PendingFlightDir.HasValue)
			{
				int dir = n.PendingFlightDir.Value;
				n.PendingFlightDir = null;
				// Сам толчок бросателем уже был первой клеткой полёта — она
				// вычитается из бюджета дистанции сразу, а не "бесплатно".
				BeginFlight(n, dir, Mathf.Max(0, ThrowMaxDistance - 1));
			}
			else if (n.IsFlying)
			{
				EvaluateFlightStep(n);
			}
		}
	}

	// Клетка (row,col), в которую попадает мировая позиция worldPos — общий
	// приём, уже использованный для починки пипетки (см.
	// PickNucleusUnderMouse) и теперь ещё и для поимки летящего ядра (см.
	// TriggerRotatorRotation) — оба места ищут ядро НЕ через _entAt (либо
	// ядро едет и физически изъято оттуда, либо мы ищем именно "что творится
	// вот в этой клетке" по факту текущей интерполированной позиции).
	private (int row, int col) CellOf(Vector2 worldPos) =>
		(Mathf.FloorToInt(worldPos.Y / CellSize), Mathf.FloorToInt(worldPos.X / CellSize));

	// Переводит ядро в режим самостоятельного полёта в направлении dir на
	// cellsRemaining клеток (см. NucleusEntity.IsFlying/FlightDir/
	// FlightCellsRemaining) и сразу проверяет условия окончания полёта на
	// ТЕКУЩЕЙ клетке (EvaluateFlightStep) — общая точка входа и для запуска
	// после толчка бросателем (см. FinishArrivedMoves/PendingFlightDir), и
	// для цепного перезапуска, когда пойманное в полёте ядро поймал СНОВА
	// бросатель (см. TriggerRotatorRotation) — в обоих случаях логика "начать
	// лететь" одна и та же.
	private void BeginFlight(NucleusEntity n, int dir, int cellsRemaining)
	{
		n.IsFlying = true;
		n.FlightDir = dir;
		n.FlightCellsRemaining = cellsRemaining;
		EvaluateFlightStep(n);
	}

	// Вызывается сразу по прибытии летящего ядра в новую клетку (как сразу
	// после толчка бросателем, так и после каждого следующего перелётного
	// шага) — решает, что дальше, строго в этом порядке (по заданию):
	// 1) (горизонт ЧД проверяется раньше, в FinishArrivedMoves: атом,
	//    прилетевший на горизонт, захватывается и сюда не попадает, T005);
	// 2) любое ОБЫЧНОЕ ядро (Ж/К/С/Сер — явно НЕ вращатель/бросатель) СТРОГО
	//    ПОД ПРЯМЫМ УГЛОМ (см. OrthogonalSlots — только 4 ортогонали, БЕЗ
	//    диагоналей, по заданию) — "прилипание": полёт останавливается прямо
	//    тут, ядро остаётся на месте и продолжает работать как обычное;
	// 3) дистанция исчерпана (FlightCellsRemaining<=0) — исчезает;
	// 4) следующая клетка по курсу чем-либо занята (атом, ЧД или её чанк —
	//    молекула, см. IsCellBlockedForLayer1) — не влетаем, тот же исход, что и
	//    (2) — прилипаем на текущем месте;
	// 5) иначе — летим ещё на одну клетку в том же направлении.
	private void EvaluateFlightStep(NucleusEntity n)
	{
		// Только 4 ортогонали (см. OrthogonalSlots — N/E/S/W, без диагоналей)
		// — по заданию, "приклеивание" к обычному ядру должно срабатывать
		// строго под прямым углом, по диагонали НЕ прилипает (раньше тут
		// перебирались все 8 направлений Adj8).
		for (int idx = 0; idx < OrthogonalSlots.Length; idx++)
		{
			int k = OrthogonalSlots[idx];
			var (dr, dc) = Adj8[k];
			if (_entAt.TryGetValue((n.Row + dr, n.Col + dc), out var neighbor) &&
				neighbor.CoreTier != RotatorCoreTier && neighbor.CoreTier != ThrowerCoreTier)
			{
				AttachFlyingNucleus(n);
				return;
			}
		}

		if (n.FlightCellsRemaining <= 0)
		{
			RemoveNucleusEntity(n);
			return;
		}

		var (fdr, fdc) = Adj8[n.FlightDir];
		int nextRow = n.Row + fdr;
		int nextCol = n.Col + fdc;
		if (_entAt.ContainsKey((nextRow, nextCol)) || IsCellBlockedForLayer1(nextRow, nextCol))
		{
			AttachFlyingNucleus(n);
			return;
		}

		n.FlightCellsRemaining--;
		StartMove(n, nextRow, nextCol, ThrowTicksPerCell);
	}

	// Останавливает полёт летящего ядра прямо в текущей клетке ("прилипает",
	// см. EvaluateFlightStep) — снимает флаг IsFlying, дальше ядро ведёт себя
	// как обычное установленное ядро (кольцо/частицы/поворот — всё как есть,
	// ничего в нём не сбрасывается).
	private static void AttachFlyingNucleus(NucleusEntity n)
	{
		n.IsFlying = false;
		n.FlightDir = 0;
		n.FlightCellsRemaining = 0;
	}

	// Позиция ядра для рендера ПРЯМО СЕЙЧАС: обычно просто n.Center, но пока
	// оно едет (IsMoving) — плавная интерполяция (линейная или по дуге) от 0
	// до 1 на протяжении всех MoveDurationTicks (см. StartMove — там же
	// объяснение, почему "ожидание" сюда больше не подмешано: оно теперь
	// получается снаружи, из периода срабатывания ротатора, а не из
	// искусственного удвоения этой длительности). Целые тики + текущая доля
	// до следующего (_subTickFraction) — тот же принцип, что уже
	// используется для угла поворота кольца ниже (DiscreteRotationOffset +
	// continuousOffset).
	private Vector2 EffectiveCenter(NucleusEntity n) => EffectiveCenterAt(n, _subTickFraction);

	private Vector2 EffectiveCenterAt(NucleusEntity n, float subTickFraction)
	{
		if (!n.IsMoving) return n.Center;

		float ticksElapsed = (_globalTick - n.MoveStartTick) + subTickFraction;
		float t = n.MoveDurationTicks > 0 ? Mathf.Clamp(ticksElapsed / n.MoveDurationTicks, 0f, 1f) : 1f;

		if (n.MoveIsCircular)
		{
			float angle = n.MoveArcStartAngle + n.MoveArcSweepAngle * t;
			return n.MoveArcPivot + n.MoveArcRadius * AngleVec(angle);
		}

		return n.MoveFromCenter.Lerp(n.MoveToCenter, t);
	}

	// ВАЖНО: содержимое Ring физически НЕ переставляется — меняется только то,
	// какая сторона света считается соответствующей какому физическому слоту
	// (см. DiscreteRotationOffset). Раньше здесь переставлялся сам массив, из-за
	// чего частица одновременно и плавно вращалась (по непрерывному углу
	// рендера), и мгновенно прыгала на +45° при каждом тике поворота (индекс
	// менялся) — это и была "телепортация в соседнюю дырку". Угол рендера слота
	// k (baseAngle + k*45°) и физический индекс k в Ring — одно и то же, никогда
	// не расходятся, поворот просто переопределяет, какой компас у какого k.
	// Этот метод вызывается на границе тика поворота этого тира — единственное,
	// что тут нужно физически сделать: снять блокировку "остывания" со всех
	// слотов (сама ориентация не хранится, см. комментарий у Ring выше).
	private static void OnRotationTick(NucleusEntity n)
	{
		for (int i = 0; i < 8; i++)
		{
			var slot = n.Ring[i];
			slot.Locked = false; // поворот завершил "остывание" — можно снова отдавать
			n.Ring[i] = slot;
		}
		n.Cross?.Step(); // перекрёсток (T010): частицы в пути — на шаг по полукольцу
	}

	// НОД/НОК — вспомогательные для _pauseAlignTicks (см. поле и _Ready):
	// нужно найти период, на котором ВСЕ тиры одновременно оказываются в
	// позиции поворота 0, а не только каждый по отдельности.
	private static long Gcd(long a, long b)
	{
		while (b != 0) { (a, b) = (b, a % b); }
		return a == 0 ? 1 : a;
	}

	private static long Lcm(long a, long b) => a / Gcd(a, b) * b;

	// Сколько шагов по 45° уже провернулось кольцо ЭТОГО тира к текущему
	// _globalTick, приведено по модулю 8 и с учётом направления вращения —
	// ЧИСТАЯ функция (_globalTick, CoreTier, Dir), без своего состояния на
	// ядро. Это принципиально: если бы вместо этого хранился накопительный
	// счётчик на каждом ядре (как было раньше), два ядра одного тира,
	// появившиеся на поле в разные моменты, синхронизировались бы только
	// случайно (если разница в тиках между их спавном кратна TierTicks[tier]),
	// а так — все ядра тира всегда в одной фазе, независимо от истории.
	private int DiscreteRotationOffset(NucleusEntity n)
	{
		// Кэш на тик (T014): та же формула RingMath.RotationStep по тиру и спину ±1.
		if (_rotCacheTick != _globalTick || _rotCache.Length != TierTicks.Length * 2) RebuildRotationCache();
		if (n.Dir != 1 && n.Dir != -1)
		{
			int t = n.CoreTier < TierTicks.Length ? TierTicks[n.CoreTier] : TierTicks[TierTicks.Length - 1];
			return RingMath.RotationStep(_globalTick, t, n.Dir);
		}
		int tier = n.CoreTier < TierTicks.Length ? n.CoreTier : TierTicks.Length - 1;
		return _rotCache[tier * 2 + (n.Dir > 0 ? 0 : 1)];
	}

	private long _rotCacheTick = long.MinValue;
	private int[] _rotCache = System.Array.Empty<int>();

	private void RebuildRotationCache()
	{
		if (_rotCache.Length != TierTicks.Length * 2) _rotCache = new int[TierTicks.Length * 2];
		for (int tier = 0; tier < TierTicks.Length; tier++)
		{
			_rotCache[tier * 2] = RingMath.RotationStep(_globalTick, TierTicks[tier], 1);
			_rotCache[tier * 2 + 1] = RingMath.RotationStep(_globalTick, TierTicks[tier], -1);
		}
		_rotCacheTick = _globalTick;
	}

	// Физический слот (индекс в Ring, он же индекс рендера), который у ЭТОГО
	// ядра прямо сейчас смотрит на сторону света compassIndex — учитывая, что
	// кольцо уже провернулось на DiscreteRotationOffset шагов по 45°.
	private int PhysicalSlotForCompass(NucleusEntity n, int compassIndex) =>
		((compassIndex - DiscreteRotationOffset(n)) % 8 + 8) % 8;

	// --- сторона атома для передачи (T010): у обычного атома — физический слот,
	// смотрящий на сторону k; у перекрёстка — вход или выход оси (k / 2 = N/E/S/W).

	// Ключ стороны в _claimed: слот 0..7 или 8 + сторона у перекрёстка.
	private int SideKey(NucleusEntity n, int k) =>
		n.Cross != null ? 8 + k / 2 : PhysicalSlotForCompass(n, k);

	// Спин для законов передачи: у перекрёстка вертикальные кольца — спина нет.
	private static int SpinOf(NucleusEntity n) => n.Cross != null ? TransferRules.NoSpin : n.Dir;

	// Незаблокированная частица или предмет, готовые уйти через сторону k.
	private bool TryPeekGive(NucleusEntity n, int k, out RingSlot content)
	{
		if (n.Cross != null)
		{
			bool ready = n.Cross.TryPeekExit(k / 2, out var cp);
			content = ready ? new RingSlot { Exists = true, IsHole = false, ColorTier = cp.ColorTier, IsItem = cp.IsItem, Variant = cp.Variant } : default;
			return ready;
		}
		content = n.Ring[PhysicalSlotForCompass(n, k)];
		return content.Exists && !content.IsHole && !content.Locked;
	}

	// Есть ли место для частицы, входящей через сторону k.
	private bool CanReceive(NucleusEntity n, int k)
	{
		if (n.Cross != null) return n.Cross.CanEnter(k / 2);
		var slot = n.Ring[PhysicalSlotForCompass(n, k)];
		return slot.Exists && slot.IsHole;
	}

	// Убрать отданное через сторону k (после TryPeekGive).
	private void TakeGiven(NucleusEntity n, int k)
	{
		Touch(n);
		if (n.Cross != null) { n.Cross.TakeExit(k / 2); return; }
		n.Ring[PhysicalSlotForCompass(n, k)] = new RingSlot { Exists = true, IsHole = true };
	}

	// Положить принятое через сторону k (после CanReceive); частица блокируется
	// до поворота, у перекрёстка — занимает вход до следующего шага.
	private void PutReceived(NucleusEntity n, int k, RingSlot content)
	{
		Touch(n);
		if (n.Cross != null) { n.Cross.Enter(k / 2, content.ColorTier, content.IsItem, content.Variant); return; }
		n.Ring[PhysicalSlotForCompass(n, k)] = new RingSlot
			{ Exists = true, IsHole = false, ColorTier = content.ColorTier, Locked = true, IsItem = content.IsItem, Variant = content.Variant };
	}

	// requireMatchingSpin=true — раньше не блокировало ничего (все ядра
	// ставились с одним и тем же направлением, см. SpinDirection), теперь же
	// направление можно переключить у любого уже стоящего ядра по R (см.
	// ToggleSpinDirectionUnderMouse) — так что два соседних ядра с РАЗНЫМ
	// направлением вращения не будут передавать друг другу частицы, как и
	// задумано этим правилом изначально.
	private bool TransferAllowed(NucleusEntity receiver, NucleusEntity giver, RingSlot giverSlot)
	{
		// Тир и спин — общие законы слоёв 1 и 2 (см. TransferRules): серый
		// получатель при GrayAcceptsAnySpin не проверяет спин; при
		// RequireSameCoreTier разные цветные тиры не взаимодействуют, серое с
		// любой стороны — исключение. Dir у ядер всегда ±1.
		if (!TransferRules.TierSpinAllowed(
				receiver.CoreTier, SpinOf(receiver), giver.CoreTier, SpinOf(giver),
				GrayCoreTier, GrayAcceptsAnySpin, RequireSameCoreTier))
			return false;

		// Атом-предмет (T007): материал на перенос не влияет — флаги цвета
		// не применяются; вращатели и бросатели не хранят ничего, как и частицы.
		if (giverSlot.IsItem) return !IsSpinnerTier(receiver.CoreTier);

		return ColorAccepted(receiver, giverSlot.ColorTier);
	}

	// Для MoleculeLayer: те же флаги законов тира/спина, что у ядер слоя 1.
	public bool TierSpinAllowed(int receiverTier, int receiverDir, int giverTier, int giverDir) =>
		TransferRules.TierSpinAllowed(receiverTier, receiverDir, giverTier, giverDir,
			GrayCoreTier, GrayAcceptsAnySpin, RequireSameCoreTier);

	// Общая проверка "может ли receiver принять частицу цвета color" —
	// используется и при передаче между ядрами (TransferAllowed), и при
	// захвате из источника (SimTick, шаг 3), чтобы обе точки входа всегда
	// применяли одни и те же правила.
	//
	// Серое ядро (CoreTier == GrayCoreTier) — безусловное исключение из ВСЕХ
	// ограничений ниже: по заданию эксперимента оно принимает любой цвет
	// всегда, независимо от обоих флагов (RequireColorMatch/RequireOwnColorTier).
	//
	// RequireOwnColorTier (включено по вашему запросу, по умолчанию выключено
	// — это ЭКСПЕРИМЕНТ, см. [Export] выше): цвет должен совпадать с
	// собственным CoreTier ядра — то есть ядро тира "Ж" НИКОГДА не примет
	// К/С, даже если оно ещё совсем пустое. Это отдельная, более строгая
	// проверка, чем RequireColorMatch ниже.
	//
	// RequireColorMatch (включено по вашему более раннему запросу, в
	// прототипе выключено по умолчанию): ядро, уже держащее хоть один цвет в
	// кольце, отказывается принимать частицу другого цвета — пока полностью
	// не опустеет. Держит именно то, что уже физически лежит в кольце
	// (NucleusColorOrNull), а не CoreTier — до первой принятой частицы ядро с
	// этим флагом (но без RequireOwnColorTier) примет любой цвет.
	private bool ColorAccepted(NucleusEntity receiver, int color)
	{
		// Поворачиватель и бросатель (см. IsSpinnerTier) никогда не копят
		// энергию в кольце — их слоты Ring это чисто структурные "дырки"
		// механизма (см. CanonicalActiveDirections/holeCount), а не хранилище
		// частиц (см. комментарий у SimTick, шаг 3). Раньше это исключение
		// стояло только на прямом захвате из источника (шаг 3, отдельная
		// проверка n.CoreTier перед циклом) — сюда, в общую точку, которую
		// вызывают ОБА входа (шаг 3 и передача частицы между соседями, шаг 2 —
		// TransferAllowed), оно не попадало, поэтому зелёное/фиолетовое ядро
		// всё ещё могло получить частицу не с земли, а от обычного соседа.
		// Проверяется раньше GrayCoreTier — серое исключение не должно его
		// перебивать (хотя оба тира разные, и коллизии тут в принципе не
		// бывает, но порядок важен для читаемости правила).
		if (IsSpinnerTier(receiver.CoreTier)) return false;

		if (receiver.CoreTier == GrayCoreTier) return true;

		if (RequireOwnColorTier && receiver.CoreTier != color) return false;

		if (RequireColorMatch)
		{
			int? haveColor = NucleusColorOrNull(receiver);
			if (haveColor.HasValue && haveColor.Value != color) return false;
		}

		return true;
	}

	// Цвет, которым ядро уже "заняло" своё кольцо — цвет первой попавшейся
	// частицы (не дырки) в Ring, либо null, если в кольце частиц вообще нет
	// (ядро ещё ничем не "закоммитилось" и примет любой цвет первым).
	private static int? NucleusColorOrNull(NucleusEntity n)
	{
		foreach (var slot in n.Ring)
			if (slot.Exists && !slot.IsHole && !slot.IsItem) return slot.ColorTier;
		return null;
	}

	// --- рендер ---

	// ВАЖНО: угол рендера теперь считается НАПРЯМУЮ из того же тикового
	// счётчика (_globalTick/_tickAccumulatorMs), что и логика передачи, а не
	// из отдельной случайной "фазы" ядра. Раньше рендер крутился независимо
	// от накопительного RotationOffset (случайный старт + свободный ход по
	// игровому времени), из-за чего экранное "дырка напротив частицы" не имело
	// вообще никакой связи с тем, что логика передачи считает выровненным по
	// стороне света — отсюда рассинхронизация ("совпали на экране — ничего не
	// произошло, а потом частица дёрнулась сама по себе"). Теперь физический
	// слот k рисуется строго под углом (k + DiscreteRotationOffset +
	// доля_до_следующего_тика*Dir)*45°-90° — то есть ровно та ориентация,
	// которую в этот момент использует PhysicalSlotForCompass для проверки
	// передачи (это ЧИСТАЯ функция времени, см. её комментарий — поэтому все
	// ядра одного тира всегда синхронны, когда бы они ни появились на поле),
	// плюс плавная интерполяция
	// между тиками поворота (никакого скачка — см. RotateRing). -90° — поправка,
	// чтобы компас-индекс 0 (N, см. Adj8) визуально указывал вверх/на соседа
	// сверху, а не в произвольную сторону.
	private void UpdateChunkVisuals(WorldChunk chunk)
	{
		// T014: буферы MultiMesh пишутся целиком одним вызовом на слой
		// (RenderingServer.MultimeshSetBuffer, как в BlackHoleLayer), а не
		// поштучно ~17 вызовами SetInstanceTransform2D на атом.
		if (chunk.Nuclei.Count == 0) return;

		// Тела едущих ядер (см. IsMoving/StartMove) обновляются КАЖДЫЙ кадр
		// независимо от зума — слой тел (chunk.Node) всегда видим. Стоящие на
		// месте не трогаем: их позиция записана в RebuildChunkMeshes.
		bool anyMoving = false;
		foreach (var n in chunk.Nuclei)
		{
			if (!n.IsMoving) continue;
			PutTransform(chunk.BodyBuf, n.LocalIndex * BodyStride, EffectiveCenter(n), 1f);
			anyMoving = true;
		}
		if (anyMoving) RenderingServer.MultimeshSetBuffer(chunk.Node.Multimesh.GetRid(), chunk.BodyBuf);

		// У дырок и частиц СВОИ независимые пороги отключения (HoleHideZoom /
		// ParticleHideZoom) — ниже порога слой не только прячется через
		// Visible, но и вообще не пересчитывается (экономия CPU).
		if (!_holesVisible && !_particlesVisible) return;

		var holeBuf = chunk.HoleBuf;
		var particleBuf = chunk.ParticleBuf;

		foreach (var n in chunk.Nuclei)
		{
			// Спящее ядро (см. AsleepUntilTick/TryPlaceNucleus/
			// WakeSleepingNuclei) рисуем "замороженным" на фазе 0 — офсет 0,
			// без анимации; просыпается оно ровно тогда, когда живая формула
			// ниже сама даёт офсет 0, — без визуального скачка.
			float continuousOffset;
			if (_globalTick < n.AsleepUntilTick || n.IsCargo) // груз не вращается (T006)
			{
				continuousOffset = 0f;
			}
			else
			{
				int ticks = n.CoreTier < TierTicks.Length ? TierTicks[n.CoreTier] : TierTicks[TierTicks.Length - 1];
				continuousOffset = DiscreteRotationOffset(n);
				if (ticks > 0)
				{
					float ticksSinceRotation = RingMath.TicksIntoStep(_globalTick, ticks);
					float fraction = (ticksSinceRotation + _subTickFraction) / ticks; // 0..1 до следующего шага поворота
					continuousOffset += fraction * n.Dir;
				}
			}

			var effCenter = EffectiveCenter(n);
			if (n.Cross != null)
			{
				UpdateCrossroadVisuals(n, effCenter, holeBuf, particleBuf);
				continue;
			}
			for (int k = 0; k < 8; k++)
			{
				int instanceIdx = n.LocalIndex * 8 + k;
				int ho = instanceIdx * HoleStride;
				int po = instanceIdx * ParticleStride;
				var slot = n.Ring[k];

				if (!slot.Exists)
				{
					// Слота физически нет (кольцо на 2/4 гнезда) — не рисуется ничего.
					HideTransform(holeBuf, ho);
					HideTransform(particleBuf, po);
					continue;
				}

				float angle = (k + continuousOffset) * (Mathf.Pi / 4f) - (Mathf.Pi / 2f);
				var pos = effCenter + _orbitRadius * AngleVec(angle);
				if (slot.IsHole)
				{
					PutTransform(holeBuf, ho, pos, 1f);
					HideTransform(particleBuf, po);
				}
				else
				{
					PutTransform(particleBuf, po, pos, 1f);
					// .w — кадр атласа частиц (T024): вариант осколка или шар атома-предмета.
					PutCustom(particleBuf, po + 8, (slot.ColorTier + 0.5f) / _tierCount, 0f, 0f, ParticleFrame(slot.IsItem, slot.Variant));
					HideTransform(holeBuf, ho);
				}
			}
		}

		if (_holesVisible) RenderingServer.MultimeshSetBuffer(chunk.HoleNode.Multimesh.GetRid(), holeBuf);
		if (_particlesVisible) RenderingServer.MultimeshSetBuffer(chunk.ParticleNode.Multimesh.GetRid(), particleBuf);
	}

	// Раскладка буфера MultiMesh (Transform2D): 8 чисел — [x.x, y.x, 0, o.x, x.y, y.y, 0, o.y],
	// за ними 4 числа custom data, если она включена.
	private const int BodyStride = 12;     // transform + custom (тир, свечение, приглушение, предмет)
	private const int HoleStride = 8;      // только transform
	private const int ParticleStride = 12; // transform + custom

	private static void PutTransform(float[] b, int o, Vector2 pos, float scale)
	{
		b[o] = scale; b[o + 1] = 0f; b[o + 2] = 0f; b[o + 3] = pos.X;
		b[o + 4] = 0f; b[o + 5] = scale; b[o + 6] = 0f; b[o + 7] = pos.Y;
	}

	// Нулевой масштаб — инстанс не виден (как прежний HiddenTransform).
	private static void HideTransform(float[] b, int o)
	{
		for (int i = 0; i < 8; i++) b[o + i] = 0f;
	}

	private static void PutCustom(float[] b, int o, float x, float y, float z, float w)
	{
		b[o] = x; b[o + 1] = y; b[o + 2] = z; b[o + 3] = w;
	}

	// Перекрёсток (T010, вид T029): 4 неподвижные дырки на N/E/S/W (инстансы дырок 0..3,
	// порядок как у Crossroad.ExitSide), дырка выхода занятой оси скрыта (на её месте
	// CrossroadLayer рисует шеврон). Частицы в пути (ось 0 — инстансы 0..3, ось 1 — 4..7)
	// идут по прямой своей оси от входа к выходу, размер постоянный, без затемнения.
	private void UpdateCrossroadVisuals(NucleusEntity n, Vector2 center, float[] holeBuf, float[] particleBuf)
	{
		for (int k = 0; k < 8; k++)
		{
			HideTransform(holeBuf, (n.LocalIndex * 8 + k) * HoleStride);
			HideTransform(particleBuf, (n.LocalIndex * 8 + k) * ParticleStride);
		}

		int exitMask = n.Cross.ExitMask;
		for (int side = 0; side < 4; side++)
		{
			if ((exitMask & (1 << side)) != 0) continue;
			PutTransform(holeBuf, (n.LocalIndex * 8 + side) * HoleStride, center + SideDir(side) * _orbitRadius, 1f);
		}

		int ticks = n.CoreTier < TierTicks.Length ? TierTicks[n.CoreTier] : TierTicks[TierTicks.Length - 1];
		float fraction = ticks > 0 && _globalTick >= n.AsleepUntilTick
			? (RingMath.TicksIntoStep(_globalTick, ticks) + _subTickFraction) / ticks
			: 0f;
		for (int a = 0; a < 2; a++)
		{
			var axis = n.Cross.Axes[a];
			for (int i = 0; i < axis.Count; i++)
			{
				var cp = axis.Items[i];
				// Та же очередь, что в Crossroad.Step: едет, только если впереди свободно.
				int limit = i == 0 ? Crossroad.ExitPos : axis.Items[i - 1].Pos - 1;
				float u = (cp.Pos + (cp.Pos < limit ? fraction : 0f)) / Crossroad.ExitPos;
				float along = (2f * u - 1f) * _orbitRadius * axis.Dir;
				var pos = center + (a == 0 ? new Vector2(along, 0f) : new Vector2(0f, along));
				int po = (n.LocalIndex * 8 + a * 4 + i) * ParticleStride;
				PutTransform(particleBuf, po, pos, 1f);
				PutCustom(particleBuf, po + 8, (cp.ColorTier + 0.5f) / _tierCount, 0f, 0f, ParticleFrame(cp.IsItem, cp.Variant));
			}
		}
	}

	// Единичный вектор стороны: 0 N, 1 E, 2 S, 3 W.
	public static Vector2 SideDir(int side) => side switch
	{
		0 => new Vector2(0f, -1f),
		1 => new Vector2(1f, 0f),
		2 => new Vector2(0f, 1f),
		_ => new Vector2(-1f, 0f),
	};

	private WorldChunk GetOrCreateChunk(int cx, int cy)
	{
		var key = (cx, cy);
		if (_chunks.TryGetValue(key, out var existing)) return existing;

		var nuclei = new List<NucleusEntity>();

		int cellRowStart = cy * ChunkSize;
		int cellColStart = cx * ChunkSize;

		// RandomFillEnabled=false (умолчание) — чанк создаётся ПУСТЫМ, клетки
		// заполняются только вручную (см. TryPlaceNucleus). Переключается по T.
		if (RandomFillEnabled)
		{
			for (int r = 0; r < ChunkSize; r++)
			{
				for (int c = 0; c < ChunkSize; c++)
				{
					if (_rng.Randf() >= FillDensity) continue; // клетка остаётся пустой

					int worldRow = cellRowStart + r;
					int worldCol = cellColStart + c;
					if (Ports != null && Ports.IsPortCell(worldRow, worldCol)) continue; // клетки портов — не для ядер (см. PortSet)
					var center = new Vector2(
						worldCol * CellSize + CellSize / 2f,
						worldRow * CellSize + CellSize / 2f);

					// CoreTier — сначала, отдельно от цвета частиц: при
					// RequireOwnColorTier цвет частиц ниже обязан совпасть именно
					// с ним (а не быть выбран независимо). RandomNormalTier —
					// и серый (GrayCoreTier), и поворачиватель (RotatorCoreTier)
					// исключены из случайного выбора: оба не настоящие цвета, а
					// специальные тиры (см. ColorAccepted/TriggerRotatorRotation),
					// им не место среди случайно выпадающих тиров при
					// процедурной генерации.
					int coreTier = RandomNormalTier();

					// Один случайный цвет частиц на всё кольцо ПРИ ГЕНЕРАЦИИ — это
					// только стартовое состояние. После первой же передачи частицы
					// цвета в кольце одного ядра вполне могут стать разными — это
					// ожидаемое следствие настоящей передачи между ядрами.
					// При включённом RequireOwnColorTier ядро и так никогда не
					// примет чужой цвет через SimTick — но процедурная генерация
					// создаёт частицы напрямую, в обход ColorAccepted, поэтому без
					// этой проверки на поле тут же появлялись бы "нелегальные"
					// ядра с чужим цветом в кольце с самого спавна. Раз CoreTier
					// уже гарантированно не особый (см. выше), просто берём его же.
					int particleTier = RequireOwnColorTier ? coreTier : RandomNormalTier();
					var ring = new RingSlot[8];
					for (int k = 0; k < 8; k++)
					{
						bool isParticle = _rng.Randf() < ParticleFillChance;
						ring[k] = new RingSlot { Exists = true, IsHole = !isParticle, ColorTier = particleTier, Locked = false,
							Variant = ParticleVariant(worldRow, worldCol, k) };
					}

					var nucleus = new NucleusEntity
					{
						Id = _nextEntityId++,
						Row = worldRow,
						Col = worldCol,
						Center = center,
						CoreTier = coreTier,
						Dir = _currentSpinDirection,
						Ring = ring,
						LocalIndex = nuclei.Count
					};

					nuclei.Add(nucleus);
					_entAt[(worldRow, worldCol)] = nucleus;
					RegisterEntity(nucleus);
					// Симуляция теперь не завязана на видимость чанка (см.
					// комментарий у _activeSet в шапке файла) — ядро начинает
					// тикать сразу же, а не только когда чанк попадёт в кадр.
					AddActive(nucleus);
				}
			}
		}

		var node = new MultiMeshInstance2D
		{
			Name = $"Chunk_{cx}_{cy}",
			Texture = _coreTexture,
			Material = _material,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Visible = false
		};
		AddChild(node);

		var holeNode = new MultiMeshInstance2D
		{
			Name = $"Holes_{cx}_{cy}",
			Texture = _holeTexture,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Modulate = new Color(1f, 1f, 1f, HoleOpacity),
			Visible = false
		};
		AddChild(holeNode);

		var particleNode = new MultiMeshInstance2D
		{
			Name = $"Particles_{cx}_{cy}",
			Texture = _particleTexture,
			Material = _particleMaterial,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
			Visible = false
		};
		AddChild(particleNode);

		var worldRect = new Rect2(
			cellColStart * CellSize, cellRowStart * CellSize,
			ChunkSize * CellSize, ChunkSize * CellSize);

		var chunk = new WorldChunk
		{
			Node = node,
			HoleNode = holeNode,
			ParticleNode = particleNode,
			WorldRect = worldRect,
			Nuclei = nuclei,
			Cx = cx,
			Cy = cy
		};

		_chunks[key] = chunk;

		// Строит все 3 MultiMesh с нуля из chunk.Nuclei и сразу отрисовывает
		// (иначе до первого UpdateChunkVisuals слоты стояли бы в (0,0)).
		RebuildChunkMeshes(chunk);

		GD.Print($"[NucleusLayer] новый чанк ({cx},{cy}): ядер {nuclei.Count}, чанков всего: {_chunks.Count}");

		return chunk;
	}

	// Пересоздаёт все 3 MultiMesh чанка с нуля из ТЕКУЩЕГО chunk.Nuclei.
	// Нужно не только при первой генерации, но и при ручной установке ядра
	// (TryPlaceNucleus) — у Godot MultiMesh.InstanceCount при изменении
	// сбрасывает ВСЕ инстансы, поэтому просто "добавить один" нельзя, каждый
	// раз перестраиваем целиком (для размеров чанка это дёшево).
	private void RebuildChunkMeshes(WorldChunk chunk)
	{
		chunk.Version++;
		RecountChunkAtoms(chunk);
		int nucleusCount = chunk.Nuclei.Count;
		int slotCount = nucleusCount * 8;
		// Границы отсечения — чанк с запасом (едущие атомы выходят за край на
		// клетку-две): canvas item не пересчитывает их при MultimeshSetBuffer
		// (см. BlackHoleLayer).
		var r = chunk.WorldRect.Grow(CellSize * 3);
		var aabb = new Aabb(new Vector3(r.Position.X, r.Position.Y, -1f), new Vector3(r.Size.X, r.Size.Y, 2f));

		// --- ядра: одна позиция на ядро, custom data = тир (строка в атласе палитр) ---
		var mm = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = _coreQuad,
			CustomAabb = aabb,
			InstanceCount = nucleusCount
		};
		chunk.BodyBuf = new float[nucleusCount * BodyStride];
		for (int i = 0; i < nucleusCount; i++)
		{
			var n = chunk.Nuclei[i];
			int o = i * BodyStride;
			PutTransform(chunk.BodyBuf, o, n.IsMoving ? EffectiveCenter(n) : n.Center, 1f);
			// z — приглушение (шейдер палитр): груз тусклый, без свечения (T006).
			PutCustom(chunk.BodyBuf, o + 8, (n.CoreTier + 0.5f) / _tierCount, 0f, n.IsCargo ? CargoDim : 0f, 0f);
		}
		if (nucleusCount > 0) RenderingServer.MultimeshSetBuffer(mm.GetRid(), chunk.BodyBuf);
		chunk.Node.Multimesh = mm;

		// --- дырки и частицы: ФИКСИРОВАННЫЕ nucleusCount*8 инстансов у обоих
		// мешей (по одному представлению на каждый слот кольца каждого ядра).
		// В любой момент активен ровно один из двух представлений слота —
		// какой именно, решает live Ring[k].IsHole в UpdateChunkVisuals.
		chunk.HoleNode.Multimesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			Mesh = _holeQuad,
			CustomAabb = aabb,
			InstanceCount = slotCount
		};
		chunk.ParticleNode.Multimesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseCustomData = true,
			Mesh = _particleQuad,
			CustomAabb = aabb,
			InstanceCount = slotCount
		};
		chunk.HoleBuf = new float[slotCount * HoleStride];
		chunk.ParticleBuf = new float[slotCount * ParticleStride];

		// Дырки и частицы пишутся, только если их слой сейчас виден; иначе —
		// нулевые (невидимые) инстансы до первого обновления на ближнем зуме.
		if (slotCount > 0)
		{
			RenderingServer.MultimeshSetBuffer(chunk.HoleNode.Multimesh.GetRid(), chunk.HoleBuf);
			RenderingServer.MultimeshSetBuffer(chunk.ParticleNode.Multimesh.GetRid(), chunk.ParticleBuf);
		}
		UpdateChunkVisuals(chunk);
	}

	// Состав чанка (T015) заново из chunk.Nuclei: рабочие атомы Ж/К/С и серые
	// (перекрёстки — по своему тиру); обломки, вращатели и бросатели не считаются.
	// Через RebuildChunkMeshes проходит любое изменение списка атомов чанка.
	private void RecountChunkAtoms(WorldChunk chunk)
	{
		System.Array.Clear(_compScratch);
		foreach (var n in chunk.Nuclei)
			if (!n.IsCargo && ChunkComposition.IsCountedTier(n.CoreTier)) _compScratch[n.CoreTier]++;
		Composition.SetAtoms(chunk.Cx, chunk.Cy, _compScratch);
	}

	// Источники в составе чанков (T015) — полный пересчёт, только когда
	// месторождения поменялись (установка, удаление, исчерпание, загрузка).
	private void RecountCompositionSourcesIfNeeded()
	{
		int version = 0;
		foreach (var layer in _energyClusterLayers) version += layer.CellsVersion;
		if (version == _compSourceVersion) return;
		_compSourceVersion = version;
		Composition.ClearSources();
		foreach (var layer in _energyClusterLayers)
			foreach (var (row, col) in layer.EnumerateCells())
				Composition.AddSource(CanonicalOrder.FloorDiv(col, ChunkSize), CanonicalOrder.FloorDiv(row, ChunkSize), layer.Tier);
	}

	// Строит кольцо ровно с holeCount реальными гнёздами (из 8 возможных
	// физических позиций) — и ВСЕ они дырки, частиц нет вообще. Остальные
	// 8-holeCount позиций физически не существуют (Exists=false — не
	// рисуются и не участвуют в передаче, см. UpdateChunkVisuals/SimTick).
	// Позиции гнёзд берутся по RingMath.SlotMask, чтобы получались симметричные
	// фигуры: 2 — строго друг напротив друга, 4 — крестом по всем сторонам
	// света, 8 — всё кольцо.
	private static RingSlot[] BuildFixedRing(int holeCount)
	{
		var exists = RingMath.SlotMask(holeCount);

		var ring = new RingSlot[8];
		for (int k = 0; k < 8; k++)
			ring[k] = new RingSlot { Exists = exists[k], IsHole = true, ColorTier = 0, Locked = false };
		return ring;
	}

	// Описание клетки для PlaceDecision (T030): единое место для клика и превью.
	private PlaceResult EvaluatePlace(int row, int col, int tier, int holeCount)
	{
		_entAt.TryGetValue((row, col), out var e);
		var tool = new PlaceTool(tier, holeCount, _currentSpinDirection, !IsSpinnerTier(tier));
		var atom = e == null
			? default
			: new PlaceCellAtom(true, e.CoreTier, HoleCountOf(e), e.Dir, e.Cross != null, e.IsCargo, IsNormalTier(e.CoreTier));
		var cell = new PlaceCellFlags(
			Open: IsCellOpen(row, col),
			BlackHole: BlackHoles.TryGetAt(row, col, out _),
			Star: Stars.TryGetAt(row, col, out _),
			Layer2: _moleculeLayer != null && _moleculeLayer.IsCellTakenByLayer2(row, col),
			Port: Ports != null && Ports.TryGetPortAtCell(row, col, out _),
			CanAfford: Inventory.CanAfford(tier));
		return PlaceDecision.Decide(tool, atom, cell);
	}

	// Красная вспышка отказа установки: чанк, след звезды/ЧД, порт, клетка.
	// «Нет атома» и слой 2 — без вспышки (красное превью / подсветка молекулы).
	private void FlashPlaceDenied(int row, int col, PlaceDenyReason reason)
	{
		switch (reason)
		{
			case PlaceDenyReason.Closed:
				DenyIfClosed(row, col);
				break;
			case PlaceDenyReason.BlackHole:
				if (BlackHoles.TryGetAt(row, col, out var hole)) _removeHoldLayer?.FlashDenied(hole.Row, hole.Col, hole.Size);
				break;
			case PlaceDenyReason.Star:
				if (Stars.TryGetAt(row, col, out var star)) _removeHoldLayer?.FlashDenied(star.Row, star.Col, star.Size);
				break;
			case PlaceDenyReason.Port:
				if (Ports != null && Ports.TryGetPortAtCell(row, col, out var portKey)) _portLayer?.FlashPort(portKey);
				break;
			case PlaceDenyReason.Cargo:
			case PlaceDenyReason.Occupied:
				_removeHoldLayer?.FlashDenied(row, col, 1);
				break;
		}
	}

	// Ставит одно ядро в клетку под worldPos (см. _UnhandledInput/ЛКМ). Если
	// клетка уже занята — по умолчанию ничего не делает, КРОМЕ одного случая
	// (по заданию клиента): если и старое ядро в клетке, и новое, которое
	// сейчас ставится, оба "обычного" тира (см. IsNormalTier — Ж/К/С и любой
	// будущий цветной тир, но НЕ серое/поворачиватель/бросатель), старое ядро
	// снимается и на его месте сразу встаёт новое — удобно, чтобы просто
	// зажатой ЛКМ "перекрасить" уже стоящие обычные ядра в выбранный (в т.ч.
	// пипеткой) тир, не удаляя их предварительно ПКМ по одному. Особые тиры
	// в этой замене не участвуют ни с той, ни с другой стороны — для них
	// клетка по-прежнему просто "занята". Чанк-владелец создаётся лениво, как
	// обычно (пустым, если RandomFillEnabled выключен).
	private void TryPlaceNucleus(Vector2 worldPos, int tier, int holeCount)
	{
		int col = Mathf.FloorToInt(worldPos.X / CellSize);
		int row = Mathf.FloorToInt(worldPos.Y / CellSize);

		// Единое решение для клика и превью (T030, PlaceDecision): пусто / заменить /
		// такой же / отказ. Проверка инвентаря — до удаления: без атома старый остаётся.
		var decision = EvaluatePlace(row, col, tier, holeCount);
		if (decision.Outcome == PlaceOutcome.Deny)
		{
			FlashPlaceDenied(row, col, decision.Reason);
			GD.Print($"[NucleusLayer] установка в ({row},{col}) отклонена: {decision.Reason}.");
			return;
		}
		if (decision.Outcome == PlaceOutcome.Same) return;

		bool wasCrossroad = false;
		if (decision.Outcome == PlaceOutcome.Replace && _entAt.TryGetValue((row, col), out var existingNucleus))
		{
			GD.Print($"[NucleusLayer] клетка ({row},{col}) занята атомом тира {existingNucleus.CoreTier} — заменяю на тир {tier}.");
			wasCrossroad = existingNucleus.Cross != null; // режим перекрёстка сохраняется (T030)
			RefundToInventory(existingNucleus); // замена = снять старый + поставить новый
			RemoveNucleusEntity(existingNucleus);
		}
		Inventory.TrySpend(tier);

		int cx = Mathf.FloorToInt((float)col / ChunkSize);
		int cy = Mathf.FloorToInt((float)row / ChunkSize);
		var chunk = GetOrCreateChunk(cx, cy);

		var center = new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f);
		var nucleus = new NucleusEntity
		{
			Id = _nextEntityId++,
			Row = row,
			Col = col,
			Center = center,
			CoreTier = tier,
			Dir = _currentSpinDirection,
			Ring = BuildFixedRing(holeCount),
			LocalIndex = chunk.Nuclei.Count
		};

		if (wasCrossroad)
		{
			nucleus.Cross = new Crossroad(holeCount);
			_crossSet.Add(nucleus);
		}
		chunk.Nuclei.Add(nucleus);
		_entAt[(row, col)] = nucleus;
		RegisterEntity(nucleus);
		if (wasCrossroad) Touch(nucleus);
		RebuildChunkMeshes(chunk);

		// Симуляция не завязана на видимость чанка (см. комментарий у
		// _activeSet в шапке файла) — но включается в неё не всегда СРАЗУ:
		// если сейчас не ровно фаза 0 для ЭТОГО тира (см. AsleepUntilTick),
		// ядро сначала "спит" (см. _sleepingSet/WakeSleepingNuclei) — пока
		// спит, рендерится "замороженным" на фазе 0 (см. UpdateChunkVisuals),
		// то есть ровно так, как его дырки/частицы расставил BuildFixedRing,
		// и просыпается без единого визуального скачка, как только живая
		// формула DiscreteRotationOffset сама естественным образом дойдёт до
		// 0 для этого тира. Во время паузы _globalTick всегда стоит ровно на
		// такой границе (см. _pauseAlignTicks/TogglePause — она кратна
		// периоду КАЖДОГО тира), поэтому установка на паузе, как и раньше,
		// всегда активна немедленно, без сна.
		long ownPeriod = (long)OwnRotationTicks(tier) * 8;
		nucleus.AsleepUntilTick = (ownPeriod > 0 && _globalTick % ownPeriod != 0)
			? ((_globalTick / ownPeriod) + 1) * ownPeriod
			: _globalTick;
		if (_globalTick >= nucleus.AsleepUntilTick)
			AddActive(nucleus);
		else
			_sleepingSet.Add(nucleus);

		int actualSlots = 0;
		int actualParticles = 0;
		foreach (var s in nucleus.Ring)
		{
			if (!s.Exists) continue;
			actualSlots++;
			if (!s.IsHole) actualParticles++;
		}
		string sleepNote = _sleepingSet.Contains(nucleus) ? $", спит до тика {nucleus.AsleepUntilTick}" : "";
		GD.Print($"[NucleusLayer] установлено ядро тира {tier} в клетке ({row},{col}), запрошено гнёзд {holeCount}/8, реально гнёзд {actualSlots}/8 (частиц среди них: {actualParticles}){sleepNote}.");
	}

	// --- груз (T006, см. NucleusEntity.IsCargo) ---

	// Клетка свободна для нового атома: нет атома, ЧД, звезды, молекулы и порта.
	public bool IsCellFreeForAtom(int row, int col) =>
		!_entAt.ContainsKey((row, col)) && !IsCellBlockedForLayer1(row, col)
		&& (Ports == null || !Ports.IsPortCell(row, col));

	// Кладёт груз (переносчик tier на holeCount пустых гнёзд) в свободную клетку.
	// false — клетка занята, ничего не сделано.
	public bool PlaceCargo(int row, int col, int tier, int holeCount, int dir = 1)
	{
		if (!IsCellFreeForAtom(row, col)) return false;
		var chunk = GetOrCreateChunk(Mathf.FloorToInt((float)col / ChunkSize), Mathf.FloorToInt((float)row / ChunkSize));
		if (_entAt.ContainsKey((row, col))) return false; // RandomFillEnabled мог поставить сюда атом
		var cargo = new NucleusEntity
		{
			Id = _nextEntityId++,
			Row = row,
			Col = col,
			Center = new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f),
			CoreTier = tier,
			Dir = dir >= 0 ? 1 : -1,
			Ring = BuildFixedRing(holeCount),
			LocalIndex = chunk.Nuclei.Count,
			IsCargo = true,
		};
		chunk.Nuclei.Add(cargo);
		_entAt[(row, col)] = cargo;
		RegisterEntity(cargo);
		_cargoSet.Add(cargo);
		RebuildChunkMeshes(chunk);
		return true;
	}

	// Груз → рабочий атом на месте. Включается в симуляцию так же, как
	// только что поставленный атом: спит до фазы 0 своего тира (см.
	// TryPlaceNucleus), чтобы кольцо не прыгнуло. Частицы в гнёздах сохраняются.
	private void ActivateCargo(NucleusEntity cargo)
	{
		cargo.IsCargo = false;
		_cargoSet.Remove(cargo);
		Touch(cargo);
		long ownPeriod = (long)OwnRotationTicks(cargo.CoreTier) * 8;
		cargo.AsleepUntilTick = (ownPeriod > 0 && _globalTick % ownPeriod != 0)
			? ((_globalTick / ownPeriod) + 1) * ownPeriod
			: _globalTick;
		if (_globalTick >= cargo.AsleepUntilTick) AddActive(cargo);
		else _sleepingSet.Add(cargo);
		if (_chunks.TryGetValue((Mathf.FloorToInt((float)cargo.Col / ChunkSize), Mathf.FloorToInt((float)cargo.Row / ChunkSize)), out var chunk))
			RebuildChunkMeshes(chunk);
		GD.Print($"[NucleusLayer] груз в клетке ({cargo.Row},{cargo.Col}) установлен рабочим атомом тира {cargo.CoreTier}.");
	}

	// --- сохранение/загрузка поля в/из JSON-строки (см. SaveLoadPanel) ---
	// По заданию клиента: сохраняются ТОЛЬКО ядра (позиция/тир/направление/
	// число гнёзд кольца) и источники частиц (EnergyClusterLayer — только
	// клетки; тир каждой клетки восстанавливается по тому, какому узлу-тир-
	// слою она принадлежит). НЕ сохраняются: частицы в кольцах ядер (сами
	// RingSlot.ColorTier/IsHole — при загрузке кольцо строится заново через
	// BuildFixedRing, как у только что поставленного ядра), текущая фаза
	// поворота (она и так не хранится по ядру, а чистая функция от
	// (_globalTick, тир) — см. DiscreteRotationOffset — поэтому сама собой
	// "обнуляется" вместе со сбросом _globalTick), количество частиц в
	// источниках (Amount/MaxAmount — источник при загрузке ставится заново,
	// как обычной ручной установкой, и копит свежий бак с нуля). Старый
	// EnergyLayer (Terrain) в сохранение/загрузку не входит вовсе.
	private static readonly JsonSerializerOptions FieldJsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
	};

	private class FieldSaveData
	{
		public List<SavedNucleus> Nuclei { get; set; } = new();
		public List<SavedSource> Sources { get; set; } = new();
		// Молекулы слоя 2 (см. MoleculeLayer). В старых сохранениях поля нет —
		// остаётся пустой список из инициализатора.
		public List<MoleculeLayer.SavedMolecule> Molecules { get; set; } = new();
		// Порты чанков (T002): режим и содержимое. В старых сохранениях поля нет.
		public List<SavedPort> Ports { get; set; } = new();
		// Чёрные дыры слоя 2 из T003 (L2-клетки) — только для чтения старых
		// сохранений: с T005 ЧД — объект слоя 1, такие записи пропускаются.
		public List<SavedLayer2BlackHole> BlackHoles { get; set; } = new();
		// Чёрные дыры слоя 1 (T005): верхняя левая клетка и размер.
		public List<SavedBlackHole> BlackHoleCells { get; set; } = new();
		public SavedAbsorbed Absorbed { get; set; }
		// Звёзды-сборщики (T006). В старых сохранениях поля нет.
		public List<SavedStar> Stars { get; set; } = new();
		// Инвентарь и режим (T008). В старых сохранениях нет: пустой инвентарь,
		// режим — настройка SandboxMode.
		public List<SavedTierCount> Inventory { get; set; } = new();
		// Звёзды-предметы в инвентаре (T011). В старых сохранениях нет — пусто.
		public List<SavedTierCount> InventoryStars { get; set; } = new();
		public bool? Sandbox { get; set; }
		// Открытые чанки (T009). null — вся карта открыта (старые сохранения).
		public List<SavedChunk> OpenChunks { get; set; }
		// Задания ЧД (T011). null — старое сохранение: этап 1, прогресс с нуля.
		public SavedGoals Goals { get; set; }
		// Подсказка управления (T012). null — старое сохранение: пройдена.
		public SavedHints Hints { get; set; }
	}

	private class SavedHints
	{
		public bool Camera { get; set; } // W, A, S, D выполнено
		public bool Grid { get; set; }   // G выполнено
	}

	private class SavedGoals
	{
		public int Stage { get; set; }             // индекс этапа (0 — первый)
		public List<long> Baseline { get; set; } = new(); // счётчики ЧД на начало этапа, по пунктам
		public List<string> Recipes { get; set; } = new(); // открытые рецепты (StarRecipe.Id)
		public ulong Seed { get; set; }
	}

	private class SavedChunk
	{
		public int Cx { get; set; }
		public int Cy { get; set; }
	}

	// Звезда (T027). Новый формат — есть Type; Row/Col — верхняя левая клетка.
	// Старый формат (до T027) — Tier и Recipe (индекс StarCatalog.LegacyRecipeIds),
	// Row/Col — верхняя левая клетка следа 3×3; переводится в ApplySavedStar.
	private class SavedStar
	{
		public int Row { get; set; }
		public int Col { get; set; }
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public int? Type { get; set; }
		// Выбранный рецепт фабрики (StarRecipe.Id); у печи и С — нет.
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public string RecipeId { get; set; }
		// Буфер ингредиентов по ячейкам (StarCatalog.SlotOf: частицы 0..2, атомы 0..2).
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public List<int> Items { get; set; }
		// Рецепт в работе (Id) или нет.
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public string ActiveRecipe { get; set; }
		public int OutputSide { get; set; } = Star.DefaultOutputSide; // 0 N, 1 E, 2 S, 3 W
		public int Elapsed { get; set; }
		// Выходной буфер (T008): коды предметов (StarItem: тир атома или звезда, T011), первый — самый старый. В старых сохранениях нет.
		public List<int> Output { get; set; } = new();
		// Старый формат (только чтение).
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public int? Tier { get; set; }
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public int? Recipe { get; set; }
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public bool? Producing { get; set; }
	}

	private static SavedStar ToSavedStar(Star star, int dRow, int dCol, bool withState) => new()
	{
		Row = star.Row + dRow, Col = star.Col + dCol, Type = star.Type, RecipeId = star.RecipeId,
		OutputSide = star.OutputSide,
		Items = withState ? new List<int>(star.Buffer) : null,
		ActiveRecipe = withState ? star.ActiveRecipe?.Id : null,
		Elapsed = withState ? star.Elapsed : 0,
		Output = withState ? new List<int>(star.Output) : new List<int>(),
	};

	private class SavedLayer2BlackHole
	{
		public int Cx { get; set; }
		public int Cy { get; set; }
	}

	private class SavedBlackHole
	{
		public int Row { get; set; }
		public int Col { get; set; }
		public int Size { get; set; }
	}

	private class SavedAbsorbed
	{
		// Атомы ЧД слоя 2 (T003), без тира — только чтение старых сохранений.
		public long Atoms { get; set; }
		// Атомы по тиру (T005): пары (тир, количество), только ненулевые.
		public List<SavedTierCount> AtomsByTier { get; set; } = new();
		// Частицы по цветам: пары (цвет, количество), только ненулевые.
		public List<SavedColorCount> Particles { get; set; } = new();
	}

	private class SavedTierCount
	{
		public int Tier { get; set; }
		public long Count { get; set; }
	}

	private class SavedColorCount
	{
		public int Color { get; set; }
		public long Count { get; set; }
	}

	private class SavedPort
	{
		public int Cx { get; set; }
		public int Cy { get; set; }
		public int Side { get; set; }  // PortSide: 0=N, 1=E, 2=S, 3=W
		public int Mode { get; set; }  // PortMode: 0=закрыт, 1=выход, 2=вход
		public List<int> Colors { get; set; } = new(); // частицы в порту по порядку (8 — готовый атом)
	}

	private class SavedNucleus
	{
		public int Row { get; set; }
		public int Col { get; set; }
		public int CoreTier { get; set; }
		public int Dir { get; set; }
		// Число реально существующих гнёзд кольца (0..8) — этого достаточно,
		// чтобы воссоздать ТОЧНО такое же кольцо через BuildFixedRing (см. её
		// комментарий): позиции гнёзд — чистая функция одного этого числа, без
		// надобности хранить весь массив Ring целиком.
		public int HoleCount { get; set; }
		// Груз (T006). В старых сохранениях поля нет — false, рабочий атом.
		public bool Cargo { get; set; }
		// Перекрёсток (T010). В старых сохранениях поля нет — false, обычный режим.
		public bool Crossroad { get; set; }
	}

	private class SavedSource
	{
		// Тир слоя-источника (EnergyClusterLayer.Tier — 0/1/2, СВОЯ нумерация,
		// не CoreTier ядер, см. её комментарий в EnergyClusterLayer.cs) — по
		// нему при загрузке выбирается, в какой из узлов-тиров класть клетку.
		public int Tier { get; set; }
		public int Row { get; set; }
		public int Col { get; set; }
		// Запас клетки (шаблоны, T009). В сохранениях нет — null, InitialAmount слоя.
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public long? Amount { get; set; }
	}

	// Шаблон чанков (T009): подмножество формата сохранения — атомы (и груз-
	// обломки), источники с запасом, ЧД, звёзды. Без территории, режима,
	// инвентаря и состояния (частицы, буферы звёзд, счётчики ЧД). Клетки —
	// относительно левого верхнего угла шаблона; размер — целые чанки.
	// Строится в песочнице (клавиша B, ExportTemplateJson), ставится с любого
	// чанка — PlaceTemplate. Стартовая зона новой игры — StartZoneTemplatePath.
	private class FieldTemplate
	{
		public int WidthChunks { get; set; } = 1;
		public int HeightChunks { get; set; } = 1;
		public List<SavedNucleus> Nuclei { get; set; } = new();
		public List<SavedSource> Sources { get; set; } = new();
		public List<SavedBlackHole> BlackHoleCells { get; set; } = new();
		public List<SavedStar> Stars { get; set; } = new();
	}

	// Собирает текущее поле (ядра + источники) в JSON-строку — см. комментарий
	// в начале раздела о том, что НЕ сохраняется.
	public string ExportFieldJson()
	{
		var data = new FieldSaveData
		{
			Sandbox = Inventory.Sandbox,
			Goals = new SavedGoals
			{
				Stage = Goals.Stage, Baseline = new List<long>(Goals.Baseline),
				Recipes = Goals.SortedRecipes(Catalog), Seed = Goals.Seed,
			},
			Hints = new SavedHints { Camera = Hints.CameraDone, Grid = Hints.GridDone },
		};
		if (!Territory.AllOpen)
		{
			data.OpenChunks = new List<SavedChunk>();
			foreach (var (cx, cy) in Territory.SortedOpenChunks())
				data.OpenChunks.Add(new SavedChunk { Cx = cx, Cy = cy });
		}
		for (int t = 0; t < Inventory.TierCount; t++)
			if (Inventory.Count(t) != 0)
				data.Inventory.Add(new SavedTierCount { Tier = t, Count = Inventory.Count(t) });
		for (int t = 0; t < Inventory.TierCount; t++)
			if (Inventory.StarCount(t) != 0)
				data.InventoryStars.Add(new SavedTierCount { Tier = t, Count = Inventory.StarCount(t) });

		foreach (var n in _activeSet) data.Nuclei.Add(ToSavedNucleus(n));
		foreach (var n in _sleepingSet) data.Nuclei.Add(ToSavedNucleus(n));
		foreach (var n in _cargoSet) data.Nuclei.Add(ToSavedNucleus(n));

		foreach (var layer in _energyClusterLayers)
			foreach (var (row, col) in layer.EnumerateCells())
				data.Sources.Add(new SavedSource { Tier = layer.Tier, Row = row, Col = col });

		if (_moleculeLayer != null) data.Molecules = _moleculeLayer.ExportMolecules();

		if (BlackHoles != null)
		{
			foreach (var hole in BlackHoles.Enumerate())
				data.BlackHoleCells.Add(new SavedBlackHole { Row = hole.Row, Col = hole.Col, Size = hole.Size });
			data.Absorbed = new SavedAbsorbed();
			for (int t = 0; t < BlackHoleSet.TierCount; t++)
				if (BlackHoles.AtomsAbsorbed[t] != 0)
					data.Absorbed.AtomsByTier.Add(new SavedTierCount { Tier = t, Count = BlackHoles.AtomsAbsorbed[t] });
			for (int c = 0; c < BlackHoleSet.ColorCount; c++)
				if (BlackHoles.ParticlesAbsorbed[c] != 0)
					data.Absorbed.Particles.Add(new SavedColorCount { Color = c, Count = BlackHoles.ParticlesAbsorbed[c] });
		}

		foreach (var star in Stars.All)
			data.Stars.Add(ToSavedStar(star, 0, 0, withState: true));

		if (Ports != null)
			foreach (var pair in Ports.Enumerate())
				data.Ports.Add(new SavedPort
				{
					Cx = pair.Key.Cx, Cy = pair.Key.Cy, Side = pair.Key.Side,
					Mode = (int)pair.Value.Mode, Colors = pair.Value.Atom.ToColorList(),
				});

		return JsonSerializer.Serialize(data, FieldJsonOptions);
	}

	// n.Row/n.Col у едущего/летящего ядра (IsMoving) — это клетка, ИЗ которой
	// оно выехало (см. комментарий у NucleusEntity.IsMoving): она уже
	// физически свободна (ядро изъято из _entAt на время переезда, см.
	// StartMove), так что сохранение именно этой пары координат — не потеря
	// данных, а сознательное упрощение под то же самое задание (не сохранять
	// фазу/текущее движение) — снимок поля фиксирует ядро в его последней
	// стабильной клетке.
	private static SavedNucleus ToSavedNucleus(NucleusEntity n)
	{
		int holeCount = 0;
		foreach (var slot in n.Ring) if (slot.Exists) holeCount++;
		return new SavedNucleus { Row = n.Row, Col = n.Col, CoreTier = n.CoreTier, Dir = n.Dir, HoleCount = holeCount, Cargo = n.IsCargo, Crossroad = n.Cross != null };
	}

	// Разбирает JSON и полностью заменяет им текущее поле (ядра, источники
	// частиц, порты, чёрные дыры, молекулы — старый EnergyLayer НЕ трогается,
	// он и не сохранялся). При успехе возвращает true; при любой ошибке разбора —
	// false и текст ошибки в error, а поле остаётся НЕТРОНУТЫМ (сам разбор
	// JSON целиком происходит ДО очистки текущего поля).
	public bool ImportFieldJson(string json, out string error)
	{
		FieldSaveData data;
		try
		{
			data = JsonSerializer.Deserialize<FieldSaveData>(json, FieldJsonOptions);
		}
		catch (System.Exception e)
		{
			error = e.Message;
			return false;
		}

		if (data == null)
		{
			error = "пустой JSON.";
			return false;
		}

		ClearFieldForImport();

		foreach (var tc in data.Inventory ?? new List<SavedTierCount>())
			Inventory.Set(tc.Tier, tc.Count);
		foreach (var tc in data.InventoryStars ?? new List<SavedTierCount>())
			Inventory.SetStars(tc.Tier, tc.Count);
		Inventory.Sandbox = data.Sandbox ?? SandboxMode;
		// Территория — до объектов: в закрытый чанк ничего не ставится.
		if (data.OpenChunks == null) Territory.OpenAll();
		else
		{
			var open = new List<(int cx, int cy)>();
			foreach (var sc in data.OpenChunks) open.Add((sc.Cx, sc.Cy));
			Territory.Reset(open);
		}

		ApplyFieldData(data, 0, 0, "поле загружено из JSON, тик сброшен в 0");
		// Задания — после объектов: счётчики ЧД уже восстановлены.
		RestoreGoals(data.Goals, Inventory.Sandbox);
		// Подсказка (T012): нет поля — пройдена.
		Hints.Set(data.Hints?.Camera ?? true, data.Hints?.Grid ?? true);
		error = null;
		return true;
	}

	// Задания из сохранения (T011). Нет данных (старое сохранение) — этап 1,
	// прогресс с текущих счётчиков ЧД, рецепты: песочница — все, иначе нет.
	private void RestoreGoals(SavedGoals saved, bool sandbox)
	{
		Goals.ClearRecipes();
		if (saved == null)
		{
			Goals.Seed = Goals.Data.Seed;
			if (sandbox) Goals.OpenAllRecipes(Catalog);
			Goals.BeginStage(0, BlackHoles);
			return;
		}
		Goals.Seed = saved.Seed;
		foreach (var id in saved.Recipes ?? new List<string>())
		{
			if (Catalog.IndexOf(id) >= 0) Goals.OpenRecipe(id);
			else GD.PushWarning($"[NucleusLayer] импорт: неизвестный рецепт «{id}» — пропущен.");
		}
		Goals.Restore(saved.Stage, saved.Baseline);
		GD.Print($"[NucleusLayer] задания: этап {System.Math.Min(Goals.Stage + 1, Goals.StageCount)}/{Goals.StageCount}{(Goals.AllDone ? " (все выполнены)" : "")}, рецептов открыто {Goals.SortedRecipes(Catalog).Count}.");
	}

	// Звезда из сохранения или шаблона (T027). Старый формат (нет Type): тир →
	// тип с тем же номером, центр следа 3×3 остаётся центром, рецепт — индекс
	// прежней таблицы → Id (чужой типу — рецепт по умолчанию, работа сгорает),
	// буфер ингредиентов сгорает. Выходной буфер восстанавливается всегда
	// (предметы уже сделаны). Не помещается — пропуск с предупреждением.
	private bool ApplySavedStar(SavedStar ss, int dRow, int dCol)
	{
		bool legacy = ss.Type == null;
		int type = ss.Type ?? ss.Tier ?? 0;
		if (!StarCatalog.IsType(type))
		{
			GD.PushWarning($"[NucleusLayer] импорт: звезда в клетке ({ss.Row},{ss.Col}) неизвестного типа {type} — пропущена.");
			return false;
		}
		int size = Catalog.TypeOf(type).Size;
		int row = ss.Row + dRow, col = ss.Col + dCol;
		string recipeId = ss.RecipeId;
		StarRecipe active = null;
		if (legacy)
		{
			// Старый след 3×3: центр = верхняя левая + 1.
			row = row + 1 - size / 2;
			col = col + 1 - size / 2;
			int index = ss.Recipe ?? 0;
			var old = index >= 0 && index < StarCatalog.LegacyRecipeIds.Length ? Catalog.Get(StarCatalog.LegacyRecipeIds[index]) : null;
			bool own = old != null && old.Producer == type;
			recipeId = own ? old.Id : null;
			if ((ss.Producing ?? false) && own) active = old;
		}
		else if (ss.ActiveRecipe != null)
		{
			active = Catalog.Get(ss.ActiveRecipe);
			if (active == null || active.Producer != type)
				GD.PushWarning($"[NucleusLayer] импорт: у звезды ({ss.Row},{ss.Col}) работа «{ss.ActiveRecipe}» не из каталога её типа — сгорела.");
		}
		var recipe = Catalog.Get(recipeId);
		if (Catalog.TypeOf(type).Choice == StarChoice.Player && (recipe == null || recipe.Producer != type))
		{
			if (recipeId != null) GD.PushWarning($"[NucleusLayer] импорт: у звезды ({ss.Row},{ss.Col}) рецепт «{recipeId}» не её типа — рецепт по умолчанию.");
			recipeId = null; // TryPlace поставит рецепт по умолчанию
		}
		var star = _starLayer?.TryPlace(row, col, type, log: false, recipe: recipeId);
		if (star == null)
		{
			GD.PushWarning($"[NucleusLayer] импорт: звезда типа {type} {size}×{size} в клетке ({row},{col}) не ставится (занято или закрыто) — пропущена.");
			return false;
		}
		star.OutputSide = ((ss.OutputSide % Star.SideCount) + Star.SideCount) % Star.SideCount;
		if (!legacy) star.RestoreBuffer(ss.Items);
		star.RestoreWork(active, ss.Elapsed);
		foreach (int code in ss.Output ?? new List<int>())
		{
			if (star.Output.Count >= star.OutputCapacity) break;
			int id = StarItem.Tier(code);
			if (StarItem.IsStar(code) ? StarCatalog.IsType(id) : Inventory.IsAtomTier(id)) star.Output.Enqueue(code);
		}
		return true;
	}

	// Расставляет объекты из данных сохранения (или шаблона, T009) поверх
	// текущего поля со сдвигом (dRow, dCol) клеток — кратным чанку, если в
	// данных есть порты. Территорию, режим и инвентарь не трогает — их задаёт
	// вызывающий. Занятые клетки пропускаются с сообщением.
	private void ApplyFieldData(FieldSaveData data, int dRow, int dCol, string what)
	{
		int placed = 0;
		var nucleiList = data.Nuclei ?? new List<SavedNucleus>();
		foreach (var sn in nucleiList)
		{
			if (Ports != null && Ports.IsPortCell(sn.Row + dRow, sn.Col + dCol))
			{
				GD.PrintErr($"[NucleusLayer] импорт: клетка ({sn.Row + dRow},{sn.Col + dCol}) — порт чанка, ядро пропущено.");
				continue;
			}
			bool ok = sn.Cargo
				? PlaceCargo(sn.Row + dRow, sn.Col + dCol, sn.CoreTier, sn.HoleCount, sn.Dir)
				: PlaceNucleusForImport(sn.Row + dRow, sn.Col + dCol, sn.CoreTier, sn.Dir, sn.HoleCount);
			if (ok && sn.Crossroad && !sn.Cargo
				&& _entAt.TryGetValue((sn.Row + dRow, sn.Col + dCol), out var imported) && CanBeCrossroad(imported))
			{
				imported.Cross = new Crossroad(HoleCountOf(imported));
				_crossSet.Add(imported);
				Touch(imported);
			}
			if (ok) placed++;
			else GD.PrintErr($"[NucleusLayer] импорт: клетка ({sn.Row + dRow},{sn.Col + dCol}) уже занята — ядро пропущено.");
		}

		int sourcesPlaced = 0;
		var sourcesList = data.Sources ?? new List<SavedSource>();
		foreach (var ss in sourcesList)
		{
			bool found = false;
			foreach (var layer in _energyClusterLayers)
			{
				if (layer.Tier != ss.Tier) continue;
				layer.PlaceClusterAt(ss.Row + dRow, ss.Col + dCol, ss.Amount);
				sourcesPlaced++;
				found = true;
				break;
			}
			if (!found) GD.PrintErr($"[NucleusLayer] импорт: не найден слой-источник тира {ss.Tier} — клетка ({ss.Row},{ss.Col}) пропущена.");
		}

		// Порты — до молекул: чанк с открытым портом — блок, молекула туда не ставится.
		int portsPlaced = 0;
		var portsList = data.Ports ?? new List<SavedPort>();
		if (Ports == null && portsList.Count > 0)
			GD.PushWarning($"[NucleusLayer] импорт: слой 2 выключен — порты ({portsList.Count}) пропущены.");
		foreach (var sp in portsList)
		{
			if (Ports == null) break;
			if (sp.Side < 0 || sp.Side >= PortSet.SideCount || sp.Mode < 0 || sp.Mode > (int)PortMode.Input)
			{
				GD.PrintErr($"[NucleusLayer] импорт: порт чанка ({sp.Cx},{sp.Cy}) с неверной стороной/режимом ({sp.Side}/{sp.Mode}) — пропущен.");
				continue;
			}
			var atom = sp.Mode == (int)PortMode.Closed ? default : Atom.FromColors(sp.Colors);
			Ports.Restore(new PortKey(sp.Cx + dCol / ChunkSize, sp.Cy + dRow / ChunkSize, sp.Side), (PortMode)sp.Mode, atom);
			portsPlaced++;
		}

		// Чёрные дыры слоя 1 (T005) — после атомов, источников и портов: те же
		// проверки, что при установке инструментом (BlackHoleLayer.TryPlace).
		int holesPlaced = 0;
		var holesList = data.BlackHoleCells ?? new List<SavedBlackHole>();
		foreach (var sh in holesList)
		{
			if (_blackHoleLayer != null && _blackHoleLayer.TryPlace(sh.Row + dRow, sh.Col + dCol, sh.Size, log: false)) holesPlaced++;
			else GD.PushWarning($"[NucleusLayer] импорт: ЧД в клетке ({sh.Row},{sh.Col}) размером {sh.Size} не ставится (занято) — пропущена.");
		}
		// Звёзды (T006) — после атомов, источников и ЧД, с проверками инструмента.
		int starsPlaced = 0;
		var starsList = data.Stars ?? new List<SavedStar>();
		foreach (var ss in starsList)
			if (ApplySavedStar(ss, dRow, dCol)) starsPlaced++;

		// ЧД слоя 2 (T003) больше нет — старые записи пропускаются.
		var oldHoles = data.BlackHoles ?? new List<SavedLayer2BlackHole>();
		if (oldHoles.Count > 0)
			GD.PushWarning($"[NucleusLayer] импорт: ЧД слоя 2 из старого сохранения ({oldHoles.Count}) пропущены — с T005 ЧД ставится на слое 1.");

		if (data.Absorbed != null)
		{
			foreach (var tc in data.Absorbed.AtomsByTier ?? new List<SavedTierCount>())
				if (tc.Tier >= 0 && tc.Tier < BlackHoleSet.TierCount) BlackHoles.AtomsAbsorbed[tc.Tier] = tc.Count;
			if (data.Absorbed.Atoms > 0)
				GD.PushWarning($"[NucleusLayer] импорт: счётчик атомов ЧД слоя 2 ({data.Absorbed.Atoms}, без тира) пропущен.");
			foreach (var pc in data.Absorbed.Particles ?? new List<SavedColorCount>())
				if (pc.Color >= 0 && pc.Color < BlackHoleSet.ColorCount) BlackHoles.ParticlesAbsorbed[pc.Color] = pc.Count;
		}

		// Молекулы — после слоя 1: в чанк с содержимым слоя 1 они не ставятся.
		var moleculesList = data.Molecules ?? new List<MoleculeLayer.SavedMolecule>();
		if (_moleculeLayer == null && moleculesList.Count > 0)
			GD.PushWarning($"[NucleusLayer] импорт: слой 2 выключен — молекулы ({moleculesList.Count}) пропущены.");
		int moleculesPlaced = _moleculeLayer?.ImportMolecules(moleculesList) ?? 0;

		GD.Print($"[NucleusLayer] {what}: ядер {placed}/{nucleiList.Count}, источников {sourcesPlaced}/{sourcesList.Count}, портов {portsPlaced}/{portsList.Count}, ЧД {holesPlaced}/{holesList.Count}, звёзд {starsPlaced}/{starsList.Count}, молекул {moleculesPlaced}/{moleculesList.Count}.");
	}

	// --- шаблоны чанков и новая игра (T009) ---

	// Стартовая зона новой игры: шаблон 2×2 чанка, ставится с чанка (-1,-1),
	// то есть покрывает Territory.StartChunks (ЧД — в начале координат).
	[Export] public string StartZoneTemplatePath = "res://Data/Templates/start_zone.json";
	private const int StartZoneChunkX = -1, StartZoneChunkY = -1;
	// Куда пишет экспорт шаблона (клавиша B); строка ещё и в буфер обмена.
	private const string TemplateExportPath = "user://template_export.json";

	// Первый угол прямоугольника чанков для экспорта шаблона (клавиша B).
	private (int cx, int cy)? _templateCorner;

	// B — первый раз запоминает чанк под курсором, второй раз экспортирует
	// прямоугольник чанков между ними в шаблон.
	private void TemplateCornerAtMouse()
	{
		var mouse = GetGlobalMousePosition();
		var chunk = (cx: ChunkOf(Mathf.FloorToInt(mouse.X / CellSize)), cy: ChunkOf(Mathf.FloorToInt(mouse.Y / CellSize)));
		if (!_templateCorner.HasValue)
		{
			_templateCorner = chunk;
			GD.Print($"[NucleusLayer] шаблон: первый угол — чанк ({chunk.cx},{chunk.cy}). B на втором углу — экспорт.");
			return;
		}
		var a = _templateCorner.Value;
		_templateCorner = null;
		int cx0 = System.Math.Min(a.cx, chunk.cx), cx1 = System.Math.Max(a.cx, chunk.cx);
		int cy0 = System.Math.Min(a.cy, chunk.cy), cy1 = System.Math.Max(a.cy, chunk.cy);
		string json = ExportTemplateJson(cx0, cy0, cx1, cy1);
		using (var file = FileAccess.Open(TemplateExportPath, FileAccess.ModeFlags.Write))
		{
			if (file == null) GD.PrintErr($"[NucleusLayer] шаблон: не удалось записать {TemplateExportPath}: {FileAccess.GetOpenError()}");
			else file.StoreString(json);
		}
		DisplayServer.ClipboardSet(json);
		GD.Print($"[NucleusLayer] шаблон чанков ({cx0},{cy0})–({cx1},{cy1}) ({cx1 - cx0 + 1}×{cy1 - cy0 + 1}) сохранён в {ProjectSettings.GlobalizePath(TemplateExportPath)} и скопирован в буфер обмена.");
	}

	// Шаблон из прямоугольника чанков (cx0,cy0)–(cx1,cy1) включительно.
	// Берутся объекты, целиком лежащие внутри (ЧД и звёзды на границе —
	// пропускаются с предупреждением). Запас источника — доля клетки в
	// текущем остатке её кластера.
	public string ExportTemplateJson(int cx0, int cy0, int cx1, int cy1)
	{
		int row0 = cy0 * ChunkSize, col0 = cx0 * ChunkSize;
		int rowEnd = (cy1 + 1) * ChunkSize, colEnd = (cx1 + 1) * ChunkSize;
		bool Inside(int r, int c) => r >= row0 && r < rowEnd && c >= col0 && c < colEnd;

		var t = new FieldTemplate { WidthChunks = cx1 - cx0 + 1, HeightChunks = cy1 - cy0 + 1 };
		var atoms = new List<NucleusEntity>();
		atoms.AddRange(_activeSet);
		atoms.AddRange(_sleepingSet);
		atoms.AddRange(_cargoSet);
		foreach (var n in atoms)
		{
			if (!Inside(n.Row, n.Col)) continue;
			var sn = ToSavedNucleus(n);
			sn.Row -= row0;
			sn.Col -= col0;
			t.Nuclei.Add(sn);
		}
		// Порядок — по клетке, чтобы одно и то же поле давало одну и ту же строку.
		t.Nuclei.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));

		foreach (var layer in _energyClusterLayers)
			foreach (var (row, col) in layer.EnumerateCells())
			{
				if (!Inside(row, col)) continue;
				int cells = System.Math.Max(1, layer.CellCountAt(row, col));
				long amount = layer.AmountAt(row, col) / cells;
				if (amount <= 0) continue;
				t.Sources.Add(new SavedSource { Tier = layer.Tier, Row = row - row0, Col = col - col0, Amount = amount });
			}
		t.Sources.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col != b.Col ? a.Col.CompareTo(b.Col) : a.Tier.CompareTo(b.Tier));

		foreach (var hole in BlackHoles.Enumerate())
		{
			bool first = Inside(hole.Row, hole.Col), last = Inside(hole.Row + hole.Size - 1, hole.Col + hole.Size - 1);
			if (first && last)
				t.BlackHoleCells.Add(new SavedBlackHole { Row = hole.Row - row0, Col = hole.Col - col0, Size = hole.Size });
			else if (first || last)
				GD.PushWarning($"[NucleusLayer] шаблон: ЧД в клетке ({hole.Row},{hole.Col}) выходит за границу — пропущена.");
		}

		foreach (var star in Stars.All)
		{
			bool first = Inside(star.Row, star.Col), last = Inside(star.Row + star.Size - 1, star.Col + star.Size - 1);
			if (first && last)
				t.Stars.Add(ToSavedStar(star, -row0, -col0, withState: false));
			else if (first || last)
				GD.PushWarning($"[NucleusLayer] шаблон: звезда в клетке ({star.Row},{star.Col}) выходит за границу — пропущена.");
		}

		return JsonSerializer.Serialize(t, FieldJsonOptions);
	}

	// Читает шаблон из файла (res:// или user://). null — ошибка в error.
	private static FieldTemplate LoadTemplate(string path, out string error)
	{
		if (!FileAccess.FileExists(path))
		{
			error = $"файл шаблона {path} не найден.";
			return null;
		}
		using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			error = $"не удалось открыть {path}: {FileAccess.GetOpenError()}.";
			return null;
		}
		try
		{
			var t = JsonSerializer.Deserialize<FieldTemplate>(file.GetAsText(), FieldJsonOptions);
			error = t == null ? $"пустой шаблон {path}." : null;
			return t;
		}
		catch (System.Exception e)
		{
			error = $"шаблон {path}: {e.Message}";
			return null;
		}
	}

	// Ставит шаблон из файла левым верхним углом в чанк (cx, cy) поверх
	// текущего поля. Занятые клетки и клетки закрытых чанков пропускаются —
	// при расширении территории сначала открыть чанки, потом ставить шаблон.
	public bool PlaceTemplate(string path, int cx, int cy, out string error)
	{
		var t = LoadTemplate(path, out error);
		if (t == null) return false;
		PlaceTemplate(t, cx, cy, path);
		return true;
	}

	private void PlaceTemplate(FieldTemplate t, int cx, int cy, string name)
	{
		var data = new FieldSaveData
		{
			Nuclei = t.Nuclei ?? new List<SavedNucleus>(),
			Sources = t.Sources ?? new List<SavedSource>(),
			BlackHoleCells = t.BlackHoleCells ?? new List<SavedBlackHole>(),
			Stars = t.Stars ?? new List<SavedStar>(),
		};
		ApplyFieldData(data, cy * ChunkSize, cx * ChunkSize, $"шаблон {name} ({t.WidthChunks}×{t.HeightChunks}) поставлен с чанка ({cx},{cy})");
	}

	// Новая игра (T009, GDD «Прогрессия → Старт»): поле очищается, настоящий
	// режим, пустой инвентарь, открыты только стартовые 2×2 чанка, в них —
	// шаблон стартовой зоны. Шаблон читается до очистки: нет файла — поле
	// не тронуто, false и ошибка.
	public bool NewGame(out string error)
	{
		var t = LoadTemplate(StartZoneTemplatePath, out error);
		if (t == null)
		{
			GD.PrintErr($"[NucleusLayer] новая игра: {error}");
			return false;
		}
		ClearFieldForImport();
		Inventory.Sandbox = false;
		Territory.Reset(Territory.StartChunks);
		// Задания (T011): этап 1, рецептов нет, seed — из цепочки.
		Goals.Seed = Goals.Data.Seed;
		Goals.ClearRecipes();
		Goals.BeginStage(0, BlackHoles);
		Hints.Reset();
		_templateCorner = null;
		PlaceTemplate(t, StartZoneChunkX, StartZoneChunkY, StartZoneTemplatePath);
		// Камера — на все стартовые чанки (T012).
		ShowTerritory();
		GD.Print("[NucleusLayer] новая игра: настоящий режим, открыты стартовые 2×2 чанка.");
		return true;
	}

	// Камера сразу на всю открытую территорию (новая игра, «Продолжить» в меню T013);
	// вся карта открыта — камера не трогается.
	public void ShowTerritory()
	{
		if (_camera != null && Territory.TryGetBounds(out int x0, out int y0, out int x1, out int y1))
			_camera.ShowRect(ChunkRectWorld(x0, y0, x1, y1));
	}

	// Очищает ВСЁ текущее поле ядер и источников частиц перед загрузкой новых
	// (см. ImportFieldJson) — старый EnergyLayer намеренно не трогает (он по
	// заданию не участвует в сохранении/загрузке). Ядра
	// удаляются не через RemoveNucleusEntity по одному (это пересобирало бы
	// меши чанка на каждое отдельное удаление — дорого при большом поле), а
	// разом: чанк просто получает пустой список и одну пересборку мешей.
	private void ClearFieldForImport()
	{
		foreach (var chunk in _chunks.Values)
		{
			if (chunk.Nuclei.Count == 0) continue;
			chunk.Nuclei.Clear();
			RebuildChunkMeshes(chunk);
		}
		_entAt.Clear();
		foreach (var n in _activeSet) n.IsActiveSim = false;
		_activeSet.Clear();
		foreach (var list in _byTier) list.Clear();
		_spinners.Clear();
		_captureCandidates.Clear();
		_changed.Clear();
		_changedPrev.Clear();
		_detachedSet.Clear();
		_movingSet.Clear();
		_sleepingSet.Clear();
		_cargoSet.Clear();
		_crossSet.Clear();
		_claimEpoch++;

		// Показ расширения прежнего поля (T012) прерывается.
		_showSession++;
		_camera?.CancelFlight();
		_territoryLayer?.ClearEffects();

		foreach (var layer in _energyClusterLayers) layer.ClearAll();
		_moleculeLayer?.ClearAll();
		Ports?.Clear();
		BlackHoles?.Clear();
		Stars?.Clear();
		Inventory?.Clear();

		// По заданию — при загрузке тик должен быть 0, а вместе с ним и все
		// производные величины часов симуляции, чтобы не осталось дробного
		// "довеска" от предыдущего запуска.
		_globalTick = 0;
		_tickAccumulatorMs = 0;
		_subTickFraction = 0f;
	}

	// То же самое ядро установки, что и в TryPlaceNucleus (см. её комментарий
	// и BuildFixedRing), но для загрузки из JSON: направление (Dir) — часть
	// сохранённых данных этого конкретного ядра, а не текущий глобальный
	// выбор с панели спавна (_currentSpinDirection), поэтому принимается
	// параметром, а клетка задана напрямую (row, col), а не мышью (worldPos).
	// Возвращает false и ничего не делает, если клетка уже занята (например,
	// испорченный/задвоенный JSON).
	private bool PlaceNucleusForImport(int row, int col, int tier, int dir, int holeCount)
	{
		if (_entAt.ContainsKey((row, col))) return false;

		int cx = Mathf.FloorToInt((float)col / ChunkSize);
		int cy = Mathf.FloorToInt((float)row / ChunkSize);
		var chunk = GetOrCreateChunk(cx, cy);

		if (_entAt.ContainsKey((row, col))) return false; // на всякий случай — вдруг чанк только что сгенерировал ядро именно сюда (RandomFillEnabled)

		var center = new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f);
		var nucleus = new NucleusEntity
		{
			Id = _nextEntityId++,
			Row = row,
			Col = col,
			Center = center,
			CoreTier = tier,
			Dir = dir,
			Ring = BuildFixedRing(holeCount),
			LocalIndex = chunk.Nuclei.Count
		};

		chunk.Nuclei.Add(nucleus);
		_entAt[(row, col)] = nucleus;
		RegisterEntity(nucleus);
		RebuildChunkMeshes(chunk);

		// При загрузке _globalTick только что сброшен в 0 (ClearFieldForImport) —
		// атом активен сразу. Шаблон (T009) может ставиться посреди игры — тогда
		// атом спит до фазы 0 своего тира, как при ручной установке.
		long ownPeriod = (long)OwnRotationTicks(tier) * 8;
		nucleus.AsleepUntilTick = (ownPeriod > 0 && _globalTick % ownPeriod != 0)
			? ((_globalTick / ownPeriod) + 1) * ownPeriod
			: _globalTick;
		if (_globalTick >= nucleus.AsleepUntilTick) AddActive(nucleus);
		else _sleepingSet.Add(nucleus);

		return true;
	}

	// --- сверка работы по событиям с полным обходом (T014) ---
	// Запуск: Godot --headless --path <проект> -- --sim-selfcheck [--ticks=N] [--seed=S]
	// Одно и то же поле с seed (все тиры, серые, вращатели, бросатели,
	// перекрёстки, ЧД, звезда, месторождения) прогоняется N тиков полным
	// обходом и по событиям; хеши состояния сравниваются каждые 100 тиков.
	// Текущее поле при этом стирается — только для командной строки.
	private void RunSimSelfCheck()
	{
		int ticks = 2000, every = 100;
		ulong seed = 14;
		string modes = "fe"; // f — полный обход, e — по событиям; первый прогон — эталон
		foreach (var arg in OS.GetCmdlineUserArgs())
		{
			if (arg.StartsWith("--ticks=")) ticks = int.Parse(arg.Substring(8));
			else if (arg.StartsWith("--seed=")) seed = ulong.Parse(arg.Substring(7));
			else if (arg.StartsWith("--every=")) every = int.Parse(arg.Substring(8));
			else if (arg.StartsWith("--modes=")) modes = arg.Substring(8);
			else if (arg.StartsWith("--dump=")) _selfCheckDumpTick = long.Parse(arg.Substring(7));
			else if (arg.StartsWith("--plain=")) _selfCheckPlainChunks = int.Parse(arg.Substring(8));
		}

		bool savedMode = FullScanDebug;
		var hashes = new List<ulong>[2];
		var ms = new double[2];
		int atoms = 0;
		for (int run = 0; run < 2; run++)
		{
			FullScanDebug = modes[run] == 'f';
			ClearFieldForImport();
			if (_selfCheckPlainChunks > 0) BuildPlainField(seed, _selfCheckPlainChunks);
			else BuildSelfCheckField(seed);
			atoms = TotalAtomCount;
			hashes[run] = new List<ulong>();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			for (int i = 1; i <= ticks; i++)
			{
				_globalTick++;
				SimTick();
				if (i == _selfCheckDumpTick) DumpState(modes[run]);
				if (i % every == 0)
				{
					sw.Stop();
					hashes[run].Add(StateHash());
					sw.Start();
				}
			}
			ms[run] = sw.Elapsed.TotalMilliseconds / ticks;
		}
		FullScanDebug = savedMode;

		int firstDiff = -1;
		for (int i = 0; i < hashes[0].Count; i++)
			if (hashes[0][i] != hashes[1][i]) { firstDiff = i; break; }
		GD.Print($"[SimSelfCheck] seed {seed}, атомов {atoms}, звёзд {Stars.Count}, тиков {ticks}");
		for (int run = 0; run < 2; run++)
			GD.Print($"[SimSelfCheck] {(modes[run] == 'f' ? "полный обход" : "по событиям ")}: {ms[run]:0.000} мс/тик, хеш {hashes[run][^1]:X16}");
		GD.Print(firstDiff < 0
			? "[SimSelfCheck] СОВПАДАЕТ"
			: $"[SimSelfCheck] РАСХОЖДЕНИЕ на тике {(firstDiff + 1) * every} (впервые видно при шаге сверки {every})");
		GetTree().Quit(firstDiff < 0 ? 0 : 1);
	}

	private long _selfCheckDumpTick = -1;

	// Короткий замер отрисовки (T014): --render-bench=<зум> — поле как у
	// случайного заполнения (13×13 чанков, seed 14), камера в центре на этом
	// зуме, 60 кадров разогрева и 300 кадров замера, затем выход.
	private float _benchZoom;
	private int _benchFrames = -1;
	private double _benchRenderMs, _benchFrameMs;

	private void StartRenderBench()
	{
		ClearFieldForImport();
		BuildPlainField(14, 13);
		var cam = GetViewport().GetCamera2D();
		cam.Position = new Vector2(1, 1) * (13 * ChunkSize * CellSize / 2f);
		cam.Zoom = new Vector2(_benchZoom, _benchZoom);
		ProcessMode = ProcessModeEnum.Always; // главное меню ставит дерево на паузу
		if (GetNodeOrNull("/root/Main/GameMenu") is CanvasLayer menu) menu.Visible = false; // не закрывать поле на снимке
		_benchFrames = 0;
	}

	private void StepRenderBench(double renderMs, double delta)
	{
		_benchFrames++;
		if (_benchFrames <= 60) return;
		_benchRenderMs += renderMs;
		_benchFrameMs += delta * 1000.0;
		if (_benchFrames < 360) return;
		GD.Print($"[RenderBench] зум {_benchZoom}, атомов видно {VisibleAtomCount} из {TotalAtomCount}: отрисовка атомов {_benchRenderMs / 300:0.00} мс, кадр {_benchFrameMs / 300:0.0} мс");
		foreach (var arg in OS.GetCmdlineUserArgs())
			if (arg.StartsWith("--bench-shot=")) GetViewport().GetTexture().GetImage().SavePng(arg.Substring(13));
		GetTree().Quit();
	}
	private int _selfCheckPlainChunks; // --plain=N: поле как у случайного заполнения (T), N×N чанков

	// Как GetOrCreateChunk при RandomFillEnabled: тиры Ж/К/С, FillDensity, частицы цвета тира.
	private void BuildPlainField(ulong seed, int chunks)
	{
		var rng = new RandomNumberGenerator { Seed = seed };
		int size = chunks * ChunkSize;
		for (int row = 0; row < size; row++)
			for (int col = 0; col < size; col++)
			{
				if (rng.Randf() >= FillDensity) continue;
				int tier = rng.RandiRange(0, 2);
				if (!PlaceNucleusForImport(row, col, tier, 1, 8)) continue;
				var n = _entAt[(row, col)];
				for (int k = 0; k < 8; k++)
					if (rng.Randf() < ParticleFillChance)
						n.Ring[k] = new RingSlot { Exists = true, IsHole = false, ColorTier = tier, Variant = ParticleVariant(row, col, k) };
				Touch(n);
			}
	}

	private void DumpState(char mode)
	{
		var all = new List<NucleusEntity>(_activeSet);
		all.AddRange(_sleepingSet);
		all.AddRange(_cargoSet);
		all.Sort(_canonical);
		foreach (var n in all)
		{
			var sb = new System.Text.StringBuilder();
			foreach (var slot in n.Ring) sb.Append(!slot.Exists ? '.' : slot.IsHole ? 'o' : (char)('0' + slot.ColorTier + (slot.Locked ? 10 : 0)));
			string cross = n.Cross == null ? "" : $" X{n.Cross.Axes[0].Dir}/{n.Cross.Axes[0].Count} {n.Cross.Axes[1].Dir}/{n.Cross.Axes[1].Count}";
			GD.Print($"[Dump{mode}] {n.Row},{n.Col} t{n.CoreTier} d{n.Dir} m{(n.IsMoving ? 1 : 0)}{(n.IsFlying ? 1 : 0)} {sb}{cross} cap{n.NextCaptureTick}");
		}
	}

	private void PlaceSelfCheckStar(int row0, int col0, int type, string recipe, int fieldSize)
	{
		if (_starLayer == null) return;
		int side = Catalog.TypeOf(type).Size;
		for (int i = 0; i < fieldSize * fieldSize; i++)
		{
			int row = (row0 + i / fieldSize) % (fieldSize - side), col = (col0 + i % fieldSize) % (fieldSize - side);
			if (_starLayer.TryPlace(row, col, type, log: false, recipe: recipe) != null) return;
		}
	}

	private void BuildSelfCheckField(ulong seed)
	{
		var rng = new RandomNumberGenerator { Seed = seed };
		bool savedFill = RandomFillEnabled;
		RandomFillEnabled = false;
		Inventory.Sandbox = true;
		const int size = 96; // 6×6 чанков

		foreach (var layer in _energyClusterLayers)
			for (int i = 0; i < 30; i++)
				layer.PlaceClusterAt(rng.RandiRange(0, size - 1), rng.RandiRange(0, size - 1), 40);
		_blackHoleLayer?.TryPlace(40, 40, 4, log: false);
		// Звёзды всех трёх типов (T027): печь, фабрика (звезда Ж), С.
		// Место — первое свободное по фиксированному обходу (месторождения случайны).
		PlaceSelfCheckStar(20, 70, 0, null, size);
		PlaceSelfCheckStar(60, 10, 1, "star_y", size);
		PlaceSelfCheckStar(70, 70, 2, null, size);


		int[] holeCounts = { 2, 4, 8 };
		for (int row = 0; row < size; row++)
			for (int col = 0; col < size; col++)
			{
				if (rng.Randf() >= 0.55f) continue;
				if (IsCellBlockedForLayer1(row, col)) continue;
				foreach (var layer in _energyClusterLayers)
					if (layer.HasClusterAt(row, col)) goto next;

				float r = rng.Randf();
				// Вращатели и бросатели заморожены — на поле проверки их нет.
				int tier = r < 0.24f ? 0 : r < 0.48f ? 1 : r < 0.72f ? 2 : GrayCoreTier;
				int holes = holeCounts[rng.RandiRange(0, 2)];
				int dir = rng.Randf() < 0.5f ? 1 : -1;
				if (!PlaceNucleusForImport(row, col, tier, dir, holes)) continue;
				var n = _entAt[(row, col)];

				if (rng.Randf() < 0.08f)
				{
					n.Cross = new Crossroad(HoleCountOf(n));
					_crossSet.Add(n);
					continue;
				}
				for (int k = 0; k < 8; k++)
				{
					if (!n.Ring[k].Exists || rng.Randf() >= 0.5f) continue;
					int color = tier == GrayCoreTier ? rng.RandiRange(0, 2) : tier;
					n.Ring[k] = new RingSlot { Exists = true, IsHole = false, ColorTier = color, Variant = ParticleVariant(row, col, k) };
				}
				Touch(n);
			next:;
			}
		RandomFillEnabled = savedFill;
	}

	// Хеш состояния симуляции (FNV-1a 64): атомы в порядке CanonicalOrder (без Id —
	// он зависит от истории сессии), счётчики ЧД, звёзды, запасы месторождений.
	private ulong StateHash()
	{
		ulong h = 14695981039346656037UL;
		void Mix(long v)
		{
			for (int i = 0; i < 8; i++) { h ^= (ulong)(v & 0xFF); h *= 1099511628211UL; v >>= 8; }
		}

		var all = new List<NucleusEntity>(_activeSet);
		all.AddRange(_sleepingSet);
		all.AddRange(_cargoSet);
		all.Sort(_canonical);
		Mix(_globalTick);
		Mix(all.Count);
		foreach (var n in all)
		{
			Mix(n.Row); Mix(n.Col); Mix(n.CoreTier); Mix(n.Dir);
			Mix(n.IsMoving ? 1 : 0); Mix(n.IsFlying ? 1 : 0); Mix(n.IsCargo ? 1 : 0);
			if (n.IsMoving) { Mix(n.MoveToRow); Mix(n.MoveToCol); Mix(n.MoveStartTick); Mix(n.MoveDurationTicks); }
			if (n.IsFlying) { Mix(n.FlightDir); Mix(n.FlightCellsRemaining); }
			Mix(n.PendingFlightDir ?? -1);
			Mix(n.NextCaptureTick); Mix(n.AsleepUntilTick);
			// RingSlot.Variant / CrossParticle.Variant (T024) — только вид, в хеш не входят:
			// хеш = законы, selfcheck до и после правок вида обязан совпадать.
			foreach (var slot in n.Ring)
				Mix((slot.Exists ? 1 : 0) | (slot.IsHole ? 2 : 0) | (slot.Locked ? 4 : 0) | (slot.IsItem ? 8 : 0) | (slot.ColorTier << 4));
			if (n.Cross != null)
				foreach (var axis in n.Cross.Axes)
				{
					Mix(axis.Dir); Mix(axis.Count);
					for (int i = 0; i < axis.Count; i++)
						Mix(axis.Items[i].Pos | (axis.Items[i].ColorTier << 8) | (axis.Items[i].IsItem ? 1 << 16 : 0));
				}
		}
		foreach (long v in BlackHoles.AtomsAbsorbed) Mix(v);
		foreach (long v in BlackHoles.ParticlesAbsorbed) Mix(v);
		foreach (var star in Stars.All)
		{
			Mix(star.Type); Mix(Catalog.IndexOf(star.RecipeId)); Mix(Catalog.IndexOf(star.ActiveRecipe?.Id));
			foreach (int b in star.Buffer) Mix(b);
			Mix(star.Elapsed); Mix(star.Producing ? 1 : 0); Mix(star.Output.Count);
			foreach (int code in star.Output) Mix(code);
		}
		foreach (var layer in _energyClusterLayers)
		{
			long sum = 0, count = 0;
			foreach (var (row, col) in layer.EnumerateCells()) { sum += layer.AmountAt(row, col); count++; }
			Mix(sum); Mix(count);
		}
		return h;
	}

	// Уровень детализации (T014): тела чанка — точки цвета тира или спрайт атома.
	private void SetChunkDots(WorldChunk chunk, bool dots)
	{
		chunk.Dots = dots;
		chunk.Node.Texture = dots ? _dotTexture : _coreTexture;
		chunk.Node.Material = dots ? _dotMaterial : _material;
		chunk.Node.TextureFilter = dots ? CanvasItem.TextureFilterEnum.Linear : CanvasItem.TextureFilterEnum.Nearest;
	}

	// Кружок для шейдера палитр: красный канал — индекс оттенка (средний столбец
	// палитры, тот же цвет, что у превью — SampleTierColors), альфа — форма.
	private static Texture2D BuildDotTexture()
	{
		const int size = 32;
		var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		float c = (size - 1) / 2f, radius = size * 0.45f;
		for (int y = 0; y < size; y++)
			for (int x = 0; x < size; x++)
			{
				float d = new Vector2(x - c, y - c).Length();
				float a = Mathf.Clamp(radius - d + 0.5f, 0f, 1f);
				img.SetPixel(x, y, new Color(0.5f, 0.5f, 0.5f, a));
			}
		return ImageTexture.CreateFromImage(img);
	}

	private static Vector2 AngleVec(float angle) => new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

	// Склеивает N узких (5x1) палитр в одну атлас-текстуру (N строк x 5
	// колонок) по сырым RGBA8-байтам.
	private Texture2D BuildPaletteAtlas(string[] paths)
	{
		if (paths == null || paths.Length == 0)
		{
			GD.PrintErr("NucleusLayer: PalettePaths пуст — задайте хотя бы одну палитру.");
			return null;
		}

		var rowImages = new List<Image>();
		int width = -1;

		foreach (var path in paths)
		{
			var tex = GD.Load<Texture2D>(path);
			if (tex == null)
			{
				GD.PrintErr($"NucleusLayer: не загрузилась палитра {path}");
				return null;
			}

			var img = tex.GetImage();
			img.Convert(Image.Format.Rgba8);

			if (img.GetHeight() != 1)
			{
				GD.PrintErr($"NucleusLayer: палитра {path} высотой {img.GetHeight()}px, ожидалась ровно 1 строка.");
				return null;
			}

			if (width == -1) width = img.GetWidth();
			else if (img.GetWidth() != width)
			{
				GD.PrintErr($"NucleusLayer: палитра {path} шириной {img.GetWidth()}px, ожидалось {width}px как у остальных.");
				return null;
			}

			rowImages.Add(img);
		}

		int height = rowImages.Count;
		var combined = new byte[width * height * 4];
		for (int row = 0; row < rowImages.Count; row++)
		{
			var rowBytes = rowImages[row].GetData();
			System.Array.Copy(rowBytes, 0, combined, row * width * 4, width * 4);
		}

		var atlasImage = Image.CreateFromData(width, height, false, Image.Format.Rgba8, combined);
		return ImageTexture.CreateFromImage(atlasImage);
	}
}
