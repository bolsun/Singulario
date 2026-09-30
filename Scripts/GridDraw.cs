using Godot;

// Чисто визуальный ориентир масштаба — сетка с шагом CellSize (единственный
// источник истины для всего проекта, см. NucleusLayer._Ready). Раньше сетка
// рисовалась один раз в _Ready на фиксированный диапазон Cols x Rows вокруг
// начала координат — при удалении камеры от старта область переставала
// покрываться сеткой. Теперь диапазон линий каждый кадр пересчитывается из
// текущей видимой области камеры (тот же принцип, что и видимые чанки в
// NucleusLayer._Process), с запасом CullMargin, так что сетка всегда
// покрывает то, что реально видно.
//
// Толщина линии раньше передавалась как 1.0 в МИРОВЫХ единицах — при зуме
// сильнее 1x линия визуально толще экранного пикселя, а при отдалении
// (зум < 1x) мировая толщина 1.0 становится долей экранного пикселя, из-за
// сглаживания часть линий пропадает или мерцает. Чтобы линия всегда была
// РОВНО 1 экранный пиксель, мировая толщина берётся как 1/zoom.
//
// G — переключает видимость (без пересчёта — сетка просто перестаёт
// рисоваться, Godot не вызывает _Draw для невидимых нод).
//
// На слое 2 (см. ViewLayer) клетка — это чанк слоя 1, поэтому сетка рисуется
// по границам ЧАНКОВ (шаг CellSize*ChunkSize), той же ширины в 1 экранный
// пиксель; на слое 1 — обычная сетка клеток. При отдалении ниже CellGridHideZoom
// сетка клеток пропадает и остаются только границы чанков (серые, как клетки),
// ниже GridHideZoom сетка не рисуется совсем.
// T019: линии рисует шейдер Resources/Shaders/grid.gdshader — один прямоугольник
// на видимый диапазон (_Draw), линия 1 пиксель экрана считается в шейдере по
// мировым координатам (вид тот же, что у прежних DrawLine шириной 1/zoom).
// На дальнем зуме (шаг = чанк) линии — цвет ChunkLineColor: граница чанка
// одного цвета при любом зуме.
// ChunkSize берём у NucleusLayer (единственный источник истины для него, как
// CellSize — у GridDraw, см. NucleusLayer._Ready) в _Ready(), это обычное
// [Export]-поле, так что порядок вызова _Ready() между нодами не важен.
public partial class GridDraw : Node2D
{
    public const string ShaderPath = "res://Resources/Shaders/grid.gdshader";

    [Export] public int CellSize = 96;
    [Export] public float CullMargin = 128f; // запас вокруг видимой области — как у NucleusLayer
    // Зум, ниже которого сетка клеток не рисуется: остаются только границы чанков,
    // тем же серым цветом, что и линии клеток.
    [Export] public float CellGridHideZoom = 0.2f;
    // Зум, ниже которого сетка не рисуется совсем.
    [Export] public float GridHideZoom = 0.025f;

    // Цвета из палитры Singulario 32 (T018): тише на тёмном фоне «Бездна».
    [Export] public Color LineColor = new Color("#272038");
    // По заданию клиента: на близком зуме, когда рисуется сетка КЛЕТОК (шаг
    // CellSize), линии, совпадающие с границей чанка, должны быть заметно
    // светлее обычных — иначе на частой сетке клеток границу чанка не видно
    // вообще. На дальнем зуме, когда сетка уже сама ЧАНКОВ (шаг
    // CellSize*ChunkSize, см. _Process), разделять нечего — там и так каждая
    // линия это граница чанка, красится как раньше, одним LineColor.
    [Export] public Color ChunkLineColor = new Color("#4d4268");

    // Прогиб сетки под ЧД и звёздами (T019, только вид): сила — доля клетки,
    // отдельно для ЧД и звезды; в шейдер — не больше MaxWarpObjects ближайших к
    // центру экрана (из тех, чей след с запасом виден). Вкл/выкл — Shift+G.
    [Export] public float BlackHoleWarp = 0.25f;
    [Export] public float StarWarp = 0.25f;
    [Export] public int MaxWarpObjects = 16;
    private const int WarpCapacity = 16; // размер массива warp_objects в grid.gdshader

    private int _chunkSize = 16;
    private int _minCol, _maxCol, _minRow, _maxRow;
    private int _step = 1;
    private float _lineWidth = 1f;
    private bool _haveRange;
    private bool _hidden;
    private ShaderMaterial _material;
    private NucleusLayer _nucleusLayer;
    private readonly System.Collections.Generic.List<(float dist, int order, Vector4 obj)> _warpScratch = new();
    private readonly Vector4[] _warpObjects = new Vector4[WarpCapacity];

    // Включена ли сетка (G) — для других узлов: подробный вид портов (T004,
    // PortLayer, MoleculeLayer, BlackHoleLayer) показывается только с сеткой.
    public static bool Shown { get; private set; }

    public override void _Ready()
    {
        Shown = Visible;
        SetProcessInput(true);

        var nucleusLayer = _nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
        if (nucleusLayer != null) _chunkSize = nucleusLayer.ChunkSize;

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) GD.PrintErr($"[GridDraw] не загрузился шейдер {ShaderPath}.");
        Material = _material = new ShaderMaterial { Shader = shader };
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.G)
        {
            Visible = !Visible;
            Shown = Visible;
            // На случай если пока сетка была скрыта, диапазон не менялся (и
            // поэтому не переcчитывался) — форсируем перерисовку сразу при
            // включении, а не ждём следующего движения камеры.
            if (Visible) QueueRedraw();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        // Считаем диапазон всегда (даже пока скрыто по G), чтобы при
        // включении сетка сразу была актуальна, а не ждала следующего
        // движения камеры.
        var cam = GetViewport().GetCamera2D();
        if (cam == null) return;

        var viewportSize = GetViewport().GetVisibleRect().Size;
        var visibleSize = viewportSize / cam.Zoom;
        var visiblePos = cam.GetScreenCenterPosition() - visibleSize / 2f;
        var visibleRect = new Rect2(visiblePos, visibleSize).Grow(CullMargin);

        float zoom = cam.Zoom.X;
        bool hidden = zoom < GridHideZoom;
        int step = ViewLayer.IsLayer2 || zoom < CellGridHideZoom ? CellSize * _chunkSize : CellSize;
        if (hidden != _hidden)
        {
            _hidden = hidden;
            QueueRedraw();
        }
        if (hidden) return;
        UpdateShader(step);
        UpdateWarp(visibleRect, cam.GetScreenCenterPosition());

        int minCol = Mathf.FloorToInt(visibleRect.Position.X / step);
        int maxCol = Mathf.CeilToInt((visibleRect.Position.X + visibleRect.Size.X) / step);
        int minRow = Mathf.FloorToInt(visibleRect.Position.Y / step);
        int maxRow = Mathf.CeilToInt((visibleRect.Position.Y + visibleRect.Size.Y) / step);
        float lineWidth = cam.Zoom.X > 0f ? 1f / cam.Zoom.X : 1f;

        if (_haveRange && step == _step && minCol == _minCol && maxCol == _maxCol && minRow == _minRow && maxRow == _maxRow
            && Mathf.IsEqualApprox(lineWidth, _lineWidth))
        {
            return; // ничего не изменилось — не гоняем _Draw зря
        }

        _step = step;
        _minCol = minCol;
        _maxCol = maxCol;
        _minRow = minRow;
        _maxRow = maxRow;
        _lineWidth = lineWidth;
        _haveRange = true;
        QueueRedraw();
    }

    private void UpdateShader(int step)
    {
        _material.SetShaderParameter("step_size", (float)step);
        _material.SetShaderParameter("cell_size", (float)CellSize);
        _material.SetShaderParameter("chunk_size", _chunkSize);
        _material.SetShaderParameter("cell_mode", step == CellSize);
        _material.SetShaderParameter("line_color", LineColor);
        _material.SetShaderParameter("chunk_line_color", ChunkLineColor);
    }

    // Объекты прогиба: центр следа, полусторона, сила (мировые единицы). Порядок —
    // по расстоянию до центра экрана, при равенстве — по порядку обхода наборов
    // (ЧД и звёзды хранятся упорядоченно), так что выбор детерминирован.
    private void UpdateWarp(Rect2 visibleRect, Vector2 screenCenter)
    {
        _warpScratch.Clear();
        if (_nucleusLayer != null && _nucleusLayer.IsReady && !ViewLayer.IsLayer2)
        {
            var view = visibleRect.Grow(CellSize);
            if (_nucleusLayer.BlackHoles != null)
                foreach (var hole in _nucleusLayer.BlackHoles.Enumerate())
                    AddWarp(view, screenCenter, hole.Row, hole.Col, hole.Size, BlackHoleWarp);
            if (_nucleusLayer.Stars != null)
                foreach (var star in _nucleusLayer.Stars.All)
                    AddWarp(view, screenCenter, star.Row, star.Col, Star.Size, StarWarp);
        }
        _warpScratch.Sort((a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.order.CompareTo(b.order));
        int count = System.Math.Min(_warpScratch.Count, System.Math.Clamp(MaxWarpObjects, 0, WarpCapacity));
        for (int i = 0; i < count; i++) _warpObjects[i] = _warpScratch[i].obj;
        _material.SetShaderParameter("warp_count", count);
        _material.SetShaderParameter("warp_objects", _warpObjects);
    }

    private void AddWarp(Rect2 view, Vector2 screenCenter, int row, int col, int size, float strength)
    {
        if (strength == 0f || size <= 0) return;
        var rect = new Rect2(col * CellSize, row * CellSize, size * CellSize, size * CellSize);
        if (!view.Intersects(rect)) return;
        var center = rect.GetCenter();
        _warpScratch.Add((center.DistanceSquaredTo(screenCenter), _warpScratch.Count,
            new Vector4(center.X, center.Y, size * CellSize / 2f, strength * CellSize)));
    }

    // Один прямоугольник на видимый диапазон — линии (и подсветку границ чанков,
    // см. IsChunkBoundary: та же floor-логика, в шейдере — mod) рисует шейдер.
    public override void _Draw()
    {
        if (!_haveRange || _hidden) return;
        float left = _minCol * _step;
        float top = _minRow * _step;
        DrawRect(new Rect2(left, top, (_maxCol - _minCol) * _step, (_maxRow - _minRow) * _step), Colors.White);
    }

    // true, если номер клетки cellIndex лежит РОВНО на границе чанка — то
    // есть на той же линии, где NucleusLayer.GetOrCreateChunk проводит
    // границу между чанками (cx = floor(col / ChunkSize), см. её комментарий):
    // такое floor-деление даёт границы на каждом кратном ChunkSize числе в
    // ОБЕ стороны от нуля, включая отрицательные (мир не ограничен началом
    // координат — камеру можно свободно увести в минус). Обычный "%" в C# для
    // отрицательного cellIndex вернёт отрицательный остаток (например,
    // -1 % 16 == -1, а не 15) — поэтому остаток приводится к настоящему
    // математическому модулю вручную, иначе часть границ на отрицательных
    // координатах осталась бы неподсвеченной.
    private bool IsChunkBoundary(int cellIndex)
    {
        if (_chunkSize <= 0) return false;
        int m = cellIndex % _chunkSize;
        if (m < 0) m += _chunkSize;
        return m == 0;
    }
}
