shader_type canvas_item;
render_mode world_vertex_coords;

// Частицы месторождения (T022): движение целиком здесь, от uniform тика — CPU каждый
// кадр экземпляры не трогает. Постоянные данные экземпляра (DepositLayer):
//   MODEL_MATRIX[3].xy — точка A в мире (ядро клетки или начало мостика), масштаб = 1;
//   INSTANCE_CUSTOM: x — строка палитры тира, y — класс размера (0/1/2 = 16/8/4 px),
//                    z — фаза 0..1, w — множитель периода (период = 256 · w тиков);
//   COLOR (цвет экземпляра): kind = a (0 — у ядра, 1 — мостик);
//     у ядра r — угол, g — расстояние (клеток); у мостика (r, g) — вектор A→B (клеток), b — изгиб.
// Позиция округляется до пикселя мира; спрайт — атлас 16+8+4 по горизонтали, 1 арт-пиксель
// = 1 пиксель мира. Цвет — таблица палитр тира (как nucleus_palette.gdshader).
uniform sampler2D atlas_tex : filter_nearest, repeat_disable;
uniform sampler2D palette_tex : filter_nearest, repeat_disable;
uniform float tick = 0.0;     // GlobalTick (mod кратного всем периодам) + доля тика
uniform float cell = 96.0;    // пикселей мира на клетку
uniform vec2 atlas_size = vec2(28.0, 16.0);
uniform float sway_angle = 0.3;     // рад
uniform float sway_dist = 0.04;     // клеток
uniform float squash = 0.85;        // сжатие по вертикали

varying float v_row;
varying vec2 v_region;   // x0 и сторона спрайта в пикселях атласа

void vertex() {
	vec2 a = MODEL_MATRIX[3].xy;
	vec2 corner = VERTEX - a;                 // ±0.5 (масштаб экземпляра = 1)
	float sz = INSTANCE_CUSTOM.y < 0.5 ? 16.0 : (INSTANCE_CUSTOM.y < 1.5 ? 8.0 : 4.0);
	float x0 = INSTANCE_CUSTOM.y < 0.5 ? 0.0 : (INSTANCE_CUSTOM.y < 1.5 ? 16.0 : 24.0);
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
	vec3 col = texture(palette_tex, vec2(src.r, v_row)).rgb;
	COLOR = vec4(col, src.a);
}
