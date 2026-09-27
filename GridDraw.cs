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
// Ниже ChunkGridZoomThreshold сетка клеток (шаг CellSize) превращается в
// нечитаемый частокол линий — на таком отдалении переключаемся на сетку
// ЧАНКОВ (шаг CellSize*ChunkSize), той же ширины в 1 экранный пиксель.
// ChunkSize берём у NucleusLayer (единственный источник истины для него, как
// CellSize — у GridDraw, см. NucleusLayer._Ready) в _Ready(), это обычное
// [Export]-поле, так что порядок вызова _Ready() между нодами не важен.
public partial class GridDraw : Node2D
{
    [Export] public int CellSize = 96;
    [Export] public float CullMargin = 128f; // запас вокруг видимой области — как у NucleusLayer
    [Export] public float ChunkGridZoomThreshold = 0.1f; // ниже этого зума — сетка чанков вместо клеток

    private static readonly Color LineColor = new Color(0.35f, 0.35f, 0.4f);

    private int _chunkSize = 16;
    private int _minCol, _maxCol, _minRow, _maxRow;
    private int _step = 1;
    private float _lineWidth = 1f;
    private bool _haveRange;

    public override void _Ready()
    {
        SetProcessInput(true);

        var nucleusLayer = GetNodeOrNull<NucleusLayer>("../NucleusLayer");
        if (nucleusLayer != null) _chunkSize = nucleusLayer.ChunkSize;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.G)
        {
            Visible = !Visible;
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

        int step = cam.Zoom.X < ChunkGridZoomThreshold ? CellSize * _chunkSize : CellSize;

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

    public override void _Draw()
    {
        if (!_haveRange) return;

        float top = _minRow * _step;
        float bottom = _maxRow * _step;
        for (int c = _minCol; c <= _maxCol; c++)
        {
            float x = c * _step;
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), LineColor, _lineWidth);
        }

        float left = _minCol * _step;
        float right = _maxCol * _step;
        for (int r = _minRow; r <= _maxRow; r++)
        {
            float y = r * _step;
            DrawLine(new Vector2(left, y), new Vector2(right, y), LineColor, _lineWidth);
        }
    }
}
