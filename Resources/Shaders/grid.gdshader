shader_type canvas_item;

// Сетка G (T019): один прямоугольник на видимую область (GridDraw._Draw), линии
// ищутся в мировых координатах пикселя. Толщина — 1 пиксель экрана при любом
// зуме: пиксель закрашен, если его центр ближе к линии, чем на полпикселя (как
// прямоугольник DrawLine шириной 1/zoom, которым сетка рисовалась раньше).
//
// Прогиб под ЧД и звёздами (только вид): точка p показывает линию, найденную по
// q = p + dir·s, dir = normalize(p − c) — линии втягиваются к центру следа.
//   s = strength·g·fall·near (strength уже в мировых единицах: доля клетки × клетка);
//   g = 1 − 0.45·min(u, 1), u — чебышёвское расстояние до центра / h;
//   fall = 1 внутри следа, спадает до 0 за полклетки снаружи по скруглённому квадрату;
//   near = min(1, |p − c| / (0.35·h)).
// Линия соседних клеток (1 клетка от следа) — прямая.

uniform float step_size = 96.0;       // шаг линий (клетка или чанк)
uniform float cell_size = 96.0;
uniform int chunk_size = 16;
uniform bool cell_mode = true;        // шаг = клетка: границы чанков подсвечены
uniform vec4 line_color : source_color;
uniform vec4 chunk_line_color : source_color;
uniform bool warp_on = true;
uniform int warp_count = 0;
uniform vec4 warp_objects[16];        // центр (x, y), полусторона h, сила в мировых единицах

varying vec2 v_world;

void vertex() {
	v_world = VERTEX; // узел сетки — в начале координат мира
}

vec2 warped(vec2 p) {
	vec2 offset = vec2(0.0);
	for (int i = 0; i < warp_count; i++) {
		vec4 o = warp_objects[i];
		vec2 d = p - o.xy;
		float h = o.z;
		float e = length(max(abs(d) - vec2(h), vec2(0.0)));
		float fall = 1.0 - smoothstep(0.0, 0.5 * cell_size, e);
		if (fall <= 0.0) continue;
		float len = length(d);
		if (len < 1e-4) continue;
		float u = max(abs(d.x), abs(d.y)) / h;
		float g = 1.0 - 0.45 * min(u, 1.0);
		float near = min(1.0, len / (0.35 * h));
		offset += d / len * (o.w * g * fall * near);
	}
	return p + offset;
}

// Номер ближайшей линии по оси и попадание пикселя в неё. Линия на границе двух
// пикселей (ничья) — верхний/левый, как у прежних DrawLine; смещение tie_bias
// (доля пикселя) делает выбор устойчивым к погрешности координат.
const float tie_bias = 0.002;
bool on_line(float q, float half_width, out float index) {
	index = round(q / step_size);
	float o = (q - index * step_size) / max(2.0 * half_width, 1e-6); // в пикселях экрана
	return o >= -0.5 - tie_bias && o < 0.5 - tie_bias;
}

bool is_chunk_boundary(float index) {
	if (!cell_mode) return true; // шаг = чанк: каждая линия — граница чанка
	float m = mod(index, float(chunk_size));
	return m < 0.5 || m > float(chunk_size) - 0.5;
}

void fragment() {
	vec2 q = warp_on && warp_count > 0 ? warped(v_world) : v_world;
	// Полпикселя экрана в координатах q вдоль нормали к линии.
	float hx = 0.5 * length(vec2(dFdx(q.x), dFdy(q.x)));
	float hy = 0.5 * length(vec2(dFdx(q.y), dFdy(q.y)));
	float ix, iy;
	bool vx = on_line(q.x, hx, ix);
	bool hz = on_line(q.y, hy, iy);
	// Горизонтальные линии рисовались после вертикальных — на пересечении их цвет.
	if (hz) COLOR = is_chunk_boundary(iy) ? chunk_line_color : line_color;
	else if (vx) COLOR = is_chunk_boundary(ix) ? chunk_line_color : line_color;
	else discard;
}
