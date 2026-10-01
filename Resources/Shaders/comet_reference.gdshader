// ЭТАЛОН ВИДА кометы (вариант А «Капля», утверждён 2026-10-01), не игровой шейдер.
// Значения по умолчанию = утверждённый вариант (style 0). Источник и пакетный рендер — мастерская
// D:\Projects\Tools\PixelPlanets, SingularioTools/Comet. Спецификация — Docs/Art/comet-visual-spec.md.
// Комета для Singulario (концепт). Своя, в духе PixelPlanets (Deep-Fold, MIT): пиксельные UV, fbm, дизеринг.
// Анимация — петля по phase 0..1 (шум хвоста периодичен вдоль хвоста, сдвиг на целый период за петлю).
// Стили: 0 — «капля» (плотная кома, короткий конус), 1 — два хвоста (прямой ионный + изогнутый пылевой),
// 2 — осколки (хвост из частиц), 3 — штрихи (огранённое ядро, хвост полосами).
shader_type canvas_item;
render_mode blend_mix;

uniform float pixels = 192.0;
uniform float phase = 0.0;
uniform float angle = -0.785398;         // направление движения (рад, экран: x вправо, y вниз)
uniform vec2 head_pos = vec2(0.72, 0.28);
uniform float head_radius = 0.065;        // ядро, доли квада
uniform float coma_radius = 0.17;
uniform float coma_strength = 1.0;
uniform float tail_length = 0.62;
uniform float tail_width = 0.13;         // полуширина в конце хвоста
uniform float tail_curve = 0.0;          // изгиб пылевого хвоста
uniform float tail_strength = 1.0;
uniform float gamma = 1.0;               // кривая яркости → тоны
uniform int style = 0;
uniform float facets = 0.0;              // > 0 — ядро-многогранник
uniform float shard_cell = 7.0;          // стиль 2: шаг решётки осколков, арт-пиксели
uniform float shard_density = 0.55;
uniform float seed = 7.53;
uniform bool should_dither = true;
uniform vec4[7] colors : source_color; // ярче → темнее: #ffffff #c2ecff #cfcde0 #9d9ab3 #6e6a88 #4d4268 #372d4d


float hash(vec2 c) {
	return fract(sin(dot(c + seed * 17.13, vec2(12.9898, 78.233))) * 43758.5453);
}

// шум, периодичный по x с периодом per (в клетках шума)
float noise_p(vec2 c, float per) {
	vec2 i = floor(c);
	vec2 f = fract(c);
	float x0 = mod(i.x, per);
	float x1 = mod(i.x + 1.0, per);
	float a = hash(vec2(x0, i.y));
	float b = hash(vec2(x1, i.y));
	float cc = hash(vec2(x0, i.y + 1.0));
	float d = hash(vec2(x1, i.y + 1.0));
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(a, b, u.x), mix(cc, d, u.x), u.y);
}

float fbm_p(vec2 c, float per) {
	float v = 0.0;
	float s = 0.5;
	for (int i = 0; i < 3; i++) {
		v += noise_p(c, per) * s;
		c *= 2.0;
		per *= 2.0;
		s *= 0.5;
	}
	return v / 0.875;
}

float bayer4(vec2 p) {
	int x = int(mod(p.x, 4.0));
	int y = int(mod(p.y, 4.0));
	int m[16] = int[](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
	return (float(m[y * 4 + x]) + 0.5) / 16.0;
}

// яркость 0..1 → номер тона (0 — прозрачно, 7 — самый яркий)
int level(float I, vec2 pix) {
	I = pow(clamp(I, 0.0, 1.2), gamma);
	float b = should_dither ? bayer4(pix) - 0.5 : 0.0;
	return int(clamp(floor(I * 7.0 + b * 0.9 + 0.5), 0.0, 7.0));
}

void fragment() {
	vec2 pix = floor(UV * pixels);
	vec2 uv = (pix + 0.5) / pixels;
	vec2 dir = vec2(cos(angle), sin(angle));
	vec2 d = uv - head_pos;
	float x = dot(d, dir);                    // вперёд по движению
	float y = dot(d, vec2(-dir.y, dir.x));    // поперёк
	float s = -x;                             // расстояние назад, в хвост
	float px1 = 1.0 / pixels;

	// --- ядро ---
	float a = atan(y, x);
	float rn = head_radius * (0.85 + 0.3 * noise_p(vec2(a / (2.0 * PI) * 6.0, seed), 6.0));
	if (facets > 0.0) {
		float seg = 2.0 * PI / facets;
		float am = mod(a + seed, seg) - seg * 0.5;
		rn = head_radius * cos(seg * 0.5) / cos(am);
	}
	float r = length(vec2(x, y));
	if (r < rn) {
		vec2 q = vec2(x, y) / rn;
		float nz = sqrt(max(0.0, 1.0 - dot(q, q)));
		vec3 n = vec3(q, nz);
		vec3 L = normalize(vec3(0.7, -0.35, 0.65)); // свет спереди (со стороны движения)
		float lum = dot(n, L);
		if (facets > 0.0) {
			float seg = 2.0 * PI / facets;
			float fa = floor((a + seed) / seg);
			lum = 0.35 + 0.65 * max(0.0, cos(fa * seg - seed + 0.4)) * (0.6 + 0.4 * nz);
		}
		lum += (noise_p(uv * pixels * 0.18, 64.0) - 0.5) * 0.25;
		int lv = level(clamp(lum * 0.75 + 0.3, 0.0, 1.0), pix);
		lv = max(lv, 2);
		COLOR = vec4(colors[7 - lv].rgb, 1.0);
	} else {

	// --- кома ---
	float I = 0.0;
	float c = clamp(1.0 - r / coma_radius, 0.0, 1.0);
	I = max(I, pow(c, 1.6) * coma_strength);

	// --- хвост ---
	if (s > 0.0) {
		float t = s / tail_length;
		float P = 5.0; // период шума вдоль хвоста
		if (style == 0 || style == 1) {
			// широкий хвост (у стиля 1 — пылевой, изогнутый)
			float len = style == 1 ? 0.75 : 1.0;
			float tt = t / len;
			if (tt < 1.0) {
				float yy = y - tail_curve * tt * tt * tail_length;
				float w = head_radius * 0.9 + (tail_width - head_radius * 0.9) * pow(tt, 0.7);
				float prof = clamp(1.0 - (yy / w) * (yy / w), 0.0, 1.0);
				float n = fbm_p(vec2(tt * P - phase * P, yy * 22.0), P);
				float fade = pow(1.0 - tt, 1.4);
				I = max(I, pow(prof, 0.8) * fade * (0.45 + 0.75 * n) * tail_strength);
			}
			if (style == 1 && t < 1.0) {
				// ионный: прямой, узкий, полосатый
				float w = head_radius * 0.45 + 0.025 * t;
				float prof = clamp(1.0 - abs(y) / w, 0.0, 1.0);
				float n = noise_p(vec2(t * P * 2.0 - phase * P * 2.0, y * 90.0), P * 2.0);
				I = max(I, prof * pow(1.0 - t, 0.9) * (0.35 + 0.85 * n) * tail_strength);
			}
		} else if (style == 2) {
			// осколки: решётка в системе кометы, сдвиг на целый период решётки за петлю
			float cell = shard_cell * px1;
			float per = ceil(tail_length / cell) + 2.0;
			vec2 g = vec2(s / cell - phase * per, y / cell);
			vec2 gi = floor(g);
			vec2 gf = fract(g);
			vec2 key = vec2(mod(gi.x, per), gi.y);
			float hp = hash(key);
			vec2 off = vec2(hash(key + 3.1), hash(key + 7.7)) * 0.6 + 0.2;
			float w = head_radius + (tail_width - head_radius) * pow(t, 0.7);
			float prof = clamp(1.0 - abs(y) / w, 0.0, 1.0);
			float pres = shard_density * pow(max(0.0, 1.0 - t), 0.8) * prof;
			float sz = (1.0 + floor(hash(key + 11.3) * 2.0)) / shard_cell; // 1–2 арт-пикселя
			vec2 fd = abs(gf - off);
			if (hp < pres && fd.x < sz * 1.4 && fd.y < sz * 0.8 && t < 1.0) {
				I = max(I, 0.45 + 0.55 * hash(key + 5.5) * (1.0 - t));
			}
			// слабая дымка под осколками
			float wh = head_radius * 0.8 + (tail_width * 0.6 - head_radius * 0.8) * pow(t, 0.7);
			float ph = clamp(1.0 - (y / wh) * (y / wh), 0.0, 1.0);
			I = max(I, ph * pow(max(0.0, 1.0 - t), 2.0) * 0.32 * tail_strength);
		} else {
			// штрихи: ряды по 2 арт-пикселя, отрезки бегут назад
			float row = floor(y / (2.0 * px1));
			float w = head_radius + (tail_width - head_radius) * pow(t, 0.8);
			float prof = clamp(1.0 - abs(y) / w, 0.0, 1.0);
			float n = noise_p(vec2(t * P * 3.0 - phase * P * 3.0, row * 1.37), P * 3.0);
			float on = step(0.55 - 0.25 * prof, n) * prof * pow(max(0.0, 1.0 - t), 1.1);
			I = max(I, on * (0.5 + 0.5 * n) * tail_strength);
		}
	}

	int lv = level(I, pix);
	if (lv <= 0) {
		COLOR = vec4(0.0);
	} else {
		COLOR = vec4(colors[7 - lv].rgb, 1.0);
	}
	}
}
