shader_type canvas_item;

// Фон-туманность (T016): плотность из бесшовной серой текстуры, освещённая
// светом ЧД, переведённая в 4 ступени палитры стабилизированным дизерингом
// Байера 4×4. Один полноэкранный проход, uniform'ы задаёт NebulaBackground.
//
// Экранная точка p (в единицах холста, от левого верхнего угла):
//   мир (для света)  = cam_pos + (p − screen/2) / zoom
//   фон (для текстуры) = cam_pos·parallax + (p − screen/2) / zoom
// Клетка дизеринга — dither_pixel единиц экрана, сетка отсчитывается от
// экранной позиции начала фона, поэтому узор едет вместе с фоном и не рябит.
//
// soft (T016c, F5) — «Мягкий свет»: уровень считается в каждом пикселе и
// плавно интерполируется по color0..3 без ступеней и Байера (цвета вне
// палитры — исключение только для фона); soft_noise — шум против полос.
//
// Свечение (T020, финальный проход): буфер GlowLayer (R/G/B — Ж/К/С, A — серое;
// 1 пиксель = клетка дизеринга, glow_origin — номер клетки его левого верхнего
// пикселя, сетка клеток от начала мира). Тиры смешиваются по долям (T020c),
// яркость = сумма, не больше glow_clamp → 3 тона рампы (серое — только тон 0). Свет ЧД
// считается здесь же (немного источников): (1 − (d/R)²)², тёплая рампа; при
// sing_t → 1 (дальний зум) — Сингулярность, акцентная рампа (в дизеринге смена
// рампы — тоже Байером). Из двух источников побеждает более яркий; с фоном —
// «светлее из двух».

uniform sampler2D density : filter_linear, repeat_enable;
uniform vec2 screen_size = vec2(1920.0, 1080.0);
uniform vec2 cam_pos = vec2(0.0);
uniform float zoom = 1.0;
uniform float parallax = 1.0;
uniform float texel_world_size = 15.0;
uniform float texture_size = 512.0;
uniform float dither_pixel = 3.0;
uniform float gain = 1.35;
uniform float black_point = 0.12;
uniform bool soft = false;
uniform float soft_noise = 0.004;
uniform float fade = 1.0;
uniform bool nebula_on = true;
uniform bool glow_on = false;
uniform sampler2D glow_tex : filter_linear;
uniform vec2 glow_origin = vec2(0.0);
uniform vec2 glow_size = vec2(1.0);
uniform float glow_clamp = 1.0;
uniform vec4 tier_tones[12];
uniform vec4 bh_tones[3];
uniform vec4 sing_tones[3];
uniform float bh_glow_radius = 288.0;
uniform float bh_glow_strength = 0.8;
uniform float sing_t = 0.0;
// Свет ЧД: xy — центр ЧД относительно cam_pos (мир), z — радиус R (мир).
uniform int light_count = 0;
uniform vec3 lights[8];
uniform vec4 color0 : source_color = vec4(0.039, 0.031, 0.071, 1.0);
uniform vec4 color1 : source_color = vec4(0.071, 0.055, 0.114, 1.0);
uniform vec4 color2 : source_color = vec4(0.106, 0.086, 0.161, 1.0);
uniform vec4 color3 : source_color = vec4(0.153, 0.125, 0.220, 1.0);

const float BAYER[16] = float[16](
	0.0, 8.0, 2.0, 10.0,
	12.0, 4.0, 14.0, 6.0,
	3.0, 11.0, 1.0, 9.0,
	15.0, 7.0, 13.0, 5.0);

float luma(vec4 c) {
	return dot(c.rgb, vec3(0.299, 0.587, 0.114));
}

float bayer_at(vec2 cell) {
	ivec2 b = ivec2(mod(cell, 4.0));
	return (BAYER[b.y * 4 + b.x] + 0.5) / 16.0;
}

vec4 apply_glow(vec4 c, vec2 p, vec2 half_screen) {
	// Клетки свечения — от экранной позиции начала мира (без параллакса).
	vec2 origin_w = half_screen - cam_pos * zoom;
	vec2 cell_w = floor((p - origin_w) / dither_pixel);
	float bayer = bayer_at(cell_w);
	vec2 at = soft ? p : origin_w + (cell_w + 0.5) * dither_pixel;
	vec2 off = (at - half_screen) / zoom;

	vec4 g = soft
		? texture(glow_tex, ((p - origin_w) / dither_pixel - glow_origin) / glow_size)
		: texelFetch(glow_tex, ivec2(cell_w - glow_origin), 0);
	g = max(g, vec4(0.0));
	// Смешение тиров (T020c): доли w_k = g_k / Σg. Дизеринг — тир пикселя порогом
	// Байера (своя фаза) по накопленным долям; мягкий свет — взвешенная сумма
	// цветов тиров. Серое — только тон 0. Тон — по общей яркости.
	float sum = g.r + g.g + g.b + g.a;
	vec4 w = sum > 0.0 ? g / sum : vec4(0.0);
	float lt = min(sum, glow_clamp) * 3.0;
	vec4 t0, t1, t2;
	if (soft) {
		t0 = w.r * tier_tones[0] + w.g * tier_tones[3] + w.b * tier_tones[6] + w.a * tier_tones[9];
		t1 = w.r * tier_tones[1] + w.g * tier_tones[4] + w.b * tier_tones[7] + w.a * tier_tones[9];
		t2 = w.r * tier_tones[2] + w.g * tier_tones[5] + w.b * tier_tones[8] + w.a * tier_tones[9];
		lt = min(lt, 3.0 - 2.0 * w.a);
	} else {
		float u = bayer_at(cell_w + vec2(1.0, 3.0));
		int tier = u < w.r ? 0 : (u < w.r + w.g ? 1 : (u < w.r + w.g + w.b ? 2 : 3));
		t0 = tier_tones[tier * 3];
		t1 = tier_tones[tier * 3 + 1];
		t2 = tier_tones[tier * 3 + 2];
		if (tier == 3) lt = min(lt, 1.0);
	}

	float bh = 0.0;
	for (int i = 0; i < 8; i++) {
		if (i >= light_count) break;
		vec2 dd = off - lights[i].xy;
		float f = max(0.0, 1.0 - dot(dd, dd) / (bh_glow_radius * bh_glow_radius));
		bh += f * f;
	}
	float lb = min(bh * bh_glow_strength, 1.0) * 3.0;

	float level = max(lt, lb);
	if (level <= 0.0) return c;
	if (lb > lt) {
		if (soft) {
			t0 = mix(bh_tones[0], sing_tones[0], sing_t);
			t1 = mix(bh_tones[1], sing_tones[1], sing_t);
			t2 = mix(bh_tones[2], sing_tones[2], sing_t);
		} else {
			bool accent = sing_t > bayer_at(cell_w + vec2(2.0, 1.0));
			t0 = accent ? sing_tones[0] : bh_tones[0];
			t1 = accent ? sing_tones[1] : bh_tones[1];
			t2 = accent ? sing_tones[2] : bh_tones[2];
		}
	}
	vec4 gc;
	if (soft) {
		if (level < 1.0) gc = mix(c, t0, level);
		else if (level < 2.0) gc = mix(t0, t1, level - 1.0);
		else gc = mix(t1, t2, min(level - 2.0, 1.0));
	} else {
		int l = min(int(floor(level + bayer)), 3);
		if (l == 0) return c;
		gc = l == 1 ? t0 : (l == 2 ? t1 : t2);
	}
	return luma(gc) > luma(c) ? gc : c;
}

void fragment() {
	vec2 p = UV * screen_size;
	vec2 half_screen = screen_size * 0.5;
	// Экранная позиция начала фона и клетка, в которой лежит p.
	vec2 origin = half_screen - cam_pos * parallax * zoom;
	vec2 cell = floor((p - origin) / dither_pixel);
	vec2 center = origin + (cell + 0.5) * dither_pixel;
	// Мягкий свет — без клеток: уровень в самом пикселе.
	if (soft) center = p;

	vec2 offset = (center - half_screen) / zoom;
	vec2 bg = cam_pos * parallax + offset;
	float d = texture(density, bg / (texel_world_size * texture_size)).r;

	float light = 0.0;
	for (int i = 0; i < 8; i++) {
		if (i >= light_count) break;
		vec2 delta = offset - lights[i].xy;
		float q = dot(delta, delta) / (lights[i].z * lights[i].z);
		light += 1.0 / (1.0 + q);
	}
	light = min(light, 1.0);

	float v = nebula_on ? min(d * light * gain * fade, 1.0) : 0.0;
	// Чёрная точка (T016b): слабый свет — чистая тьма color0.
	v = max(0.0, v - black_point) / (1.0 - black_point);
	vec4 c;
	if (soft) {
		float t = v * 3.0;
		c = mix(color0, color1, clamp(t, 0.0, 1.0));
		c = mix(c, color2, clamp(t - 1.0, 0.0, 1.0));
		c = mix(c, color3, clamp(t - 2.0, 0.0, 1.0));
		// Шум по пикселям экрана — против полос в тёмных тонах.
		float n = fract(sin(dot(floor(p), vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
		c.rgb += n * soft_noise;
	} else {
		ivec2 b = ivec2(mod(cell, 4.0));
		float bayer = (BAYER[b.y * 4 + b.x] + 0.5) / 16.0;
		int level = min(int(floor(v * 3.0 + bayer)), 3);

		c = color0;
		if (level == 1) c = color1;
		else if (level == 2) c = color2;
		else if (level == 3) c = color3;
	}
	if (glow_on) c = apply_glow(c, p, half_screen);
	COLOR = c;
}
