shader_type canvas_item;
render_mode world_vertex_coords;

// Частицы месторождения (T022): движение целиком здесь, от uniform тика — CPU каждый
// кадр экземпляры не трогает. Постоянные данные экземпляра (DepositLayer):
//   MODEL_MATRIX[3].xy — точка A в мире (ядро клетки или начало мостика), масштаб = 1;
//   INSTANCE_CUSTOM: x — строка палитры тира,
//                    y — упаковка size + 4·variant + 16·dark (T024): size 0..3 = 16/8/4/2 px,
//                        variant — кадр в полосе своего размера, dark 0/1 — глубина (на тон темнее);
//                    z — фаза 0..1, w — множитель периода (период = 256 · w тиков);
//   COLOR (цвет экземпляра): kind = a (0 — у ядра, 1 — мостик);
//     у ядра r — угол, g — расстояние (клеток); у мостика (r, g) — вектор A→B (клеток), b — изгиб.
// Позиция округляется до пикселя мира; спрайт — атлас из четырёх полос кадров (16/8/4/2 px) в один
// ряд, начало полос — strip_x0; 1 арт-пиксель = 1 пиксель мира. Цвет — таблица палитр тира
// (как nucleus_palette.gdshader); у dark уровень яркости на тон ниже (контур и тон 1 не трогаются).
uniform sampler2D atlas_tex : filter_nearest, repeat_disable;
uniform sampler2D palette_tex : filter_nearest, repeat_disable;
uniform float tick = 0.0;     // GlobalTick (mod кратного всем периодам) + доля тика
uniform float cell = 96.0;    // пикселей мира на клетку
uniform vec2 atlas_size = vec2(86.0, 16.0);
uniform vec4 strip_x0 = vec4(0.0, 48.0, 72.0, 84.0); // начало полосы 16/8/4/2 px (задаёт DepositLayer)
uniform float sway_angle = 0.3;     // рад
uniform float sway_dist = 0.04;     // клеток
uniform float squash = 0.85;        // сжатие по вертикали

varying float v_row;
varying vec2 v_region;   // x0 и сторона спрайта в пикселях атласа
varying float v_dark;

void vertex() {
	vec2 a = MODEL_MATRIX[3].xy;
	vec2 corner = VERTEX - a;                 // ±0.5 (масштаб экземпляра = 1)
	// +0.5 и floor — против погрешности float при разборе упаковки.
	float packed = floor(INSTANCE_CUSTOM.y + 0.5);
	float size = mod(packed, 4.0);
	float variant = mod(floor(packed / 4.0), 4.0);
	float sz = size < 0.5 ? 16.0 : (size < 1.5 ? 8.0 : (size < 2.5 ? 4.0 : 2.0));
	float strip = size < 0.5 ? strip_x0.x : (size < 1.5 ? strip_x0.y : (size < 2.5 ? strip_x0.z : strip_x0.w));
	float x0 = strip + variant * sz;
	v_dark = packed >= 15.5 ? 1.0 : 0.0;
	float period = 256.0 * INSTANCE_CUSTOM.w;
	float w = fract(tick / period + INSTANCE_CUSTOM.z) * 6.28318530718;
	vec2 pos;
	if (COLOR.a < 0.5) {
		float ang = COLOR.r + sway_angle * sin(w);
		float r = COLOR.g + sway_dist * sin(2.0 * w);
		pos = a + vec2(cos(ang), sin(ang) * squash) * r * cell;
	} else {
		vec2 d = COLOR.rg;
		float u = (sin(w) + 1.0) * 0.5;
		vec2 perp = normalize(vec2(-d.y, d.x) + vec2(1e-6));
		pos = a + (d * u + perp * COLOR.b * sin(3.14159265359 * u)) * cell;
	}
	pos = floor(pos + 0.5);
	VERTEX = pos + corner * sz;
	v_row = INSTANCE_CUSTOM.x;
	v_region = vec2(x0, sz);
}

void fragment() {
	// QuadMesh: UV вверх ногами (см. nucleus_palette.gdshader).
	vec2 uv = vec2(UV.x, 1.0 - UV.y);
	vec2 px = min(uv * v_region.y, vec2(v_region.y - 0.01));
	vec4 src = texture(atlas_tex, (vec2(v_region.x, 0.0) + px) / atlas_size);
	float idx = src.r;
	if (v_dark > 0.5) {
		float level = floor(src.r * 4.0 + 0.5);
		// Палитра — 5 колонок, уровень L (src.r ≈ L/4) берёт колонку L: центр колонки L−1.
		if (level >= 2.0) idx = (level - 0.5) / 5.0;
	}
	vec3 col = texture(palette_tex, vec2(idx, v_row)).rgb;
	COLOR = vec4(col, src.a);
}
