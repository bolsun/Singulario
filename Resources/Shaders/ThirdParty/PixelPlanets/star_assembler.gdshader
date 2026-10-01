// Звезда-сборщик Singulario (T017). Производная от PixelPlanets (Deep-Fold, MIT, см. LICENSE рядом):
// тело — star_body_calm.gdshader (Planets/Star/Star.gdshader), корона — star_blobs.gdshader
// (Planets/Star/StarBlobs.gdshader). Вспышки (StarFlares) не перенесены.
//
// Изменения относительно оригинала:
//  - один квад на звезду (MultiMesh): в центре тело, вокруг корона; данные инстанса —
//    INSTANCE_CUSTOM: x — тир, y — состояние (0 работает, 1 простаивает, 2 выход забит),
//    z — пикселей на диаметр тела (чётное), w — пикселей на сторону квада;
//  - пиксельная сетка — целые пиксели квада, общая у тела и короны; дизеринг по центрам
//    пикселей (шахматка), без подпиксельных полос при крупном зуме;
//  - время — phase ∈ [0,1) от тика симуляции; сдвиги за цикл целые, цикл бесшовный;
//  - цвета — тоны рамп тиров Singulario 32 (tones[tier*5 + тон 0..4]), без белого;
//  - lod = true — диск тона 3 с краем тона 1, без поверхности и короны.
shader_type canvas_item;
render_mode blend_mix;

uniform float phase = 0.0;
uniform float idle_phase = 0.0; // простой: тот же дрейф тела, цикл в IdleSlowdown раз длиннее
uniform bool lod = false;
uniform vec4 tones[20] : source_color; // 4 тира × тоны 0..4 (0 — самый тёмный)
uniform float seeds[4];                 // сид на тир (как в PixelPlanets: sd % 1000 / 100)

// Тело (star_body_calm, утверждённый вид «станка»).
uniform float body_size = 4.463;
uniform float TILES = 1.0;
uniform float cells_a = 4.0;
uniform float cells_b = 8.0;
uniform float limb = 0.6;
uniform float limb_start = 0.5;
uniform bool should_dither = true;
uniform float body_shift_a = 2.0; // сдвиг ячеек за цикл — целый
uniform float body_shift_b = 1.0;

// Корона (star_blobs). Координаты короны — как в оригинале: квад в 2 раза больше тела.
uniform float corona_size = 4.93;
uniform float circle_amount = 2.0;
uniform float circle_size = 1.0;
uniform float corona_shift = 10.0;   // за цикл, кратно round(corona_size)
uniform float breath = 0.02;         // «дыхание» короны в работе (порог радиуса)
uniform float breath_cycles = 2.0;   // вдохов за цикл — целое

varying float v_tier;
varying float v_state;
varying float v_body_px;
varying float v_quad_px;

void vertex() {
	v_tier = INSTANCE_CUSTOM.x;
	v_state = INSTANCE_CUSTOM.y;
	v_body_px = INSTANCE_CUSTOM.z;
	v_quad_px = INSTANCE_CUSTOM.w;
}

float seed_of_tier() {
	int t = clamp(int(v_tier + 0.5), 0, 3);
	return seeds[t];
}

vec4 tone(int t) {
	int tier = clamp(int(v_tier + 0.5), 0, 3);
	return tones[tier * 5 + clamp(t, 0, 4)];
}

float rand(vec2 co, float size, float seed) {
	co = mod(co, vec2(1.0, 1.0) * round(size));
	return fract(sin(dot(co.xy, vec2(12.9898, 78.233))) * 15.5453 * seed);
}

vec2 Hash2(vec2 p) {
	float r = 523.0 * sin(dot(p, vec2(53.3158, 43.6143)));
	return vec2(fract(15.32354 * r), fract(17.25865 * r));
}

// Tileable cell noise by Dave_Hoskins from shadertoy: https://www.shadertoy.com/view/4djGRh
float Cells(in vec2 p, in float numCells) {
	p *= numCells;
	float d = 1.0e10;
	for (int xo = -1; xo <= 1; xo++) {
		for (int yo = -1; yo <= 1; yo++) {
			vec2 tp = floor(p) + vec2(float(xo), float(yo));
			tp = p - tp - Hash2(mod(tp, numCells / TILES));
			d = min(d, dot(tp, tp));
		}
	}
	return sqrt(d);
}

vec2 spherify(vec2 uv) {
	vec2 centered = uv * 2.0 - 1.0;
	float z = sqrt(max(0.0, 1.0 - dot(centered.xy, centered.xy)));
	vec2 sphere = centered / (z + 1.0);
	return sphere * 0.5 + 0.5;
}

float circle(vec2 uv, float seed) {
	float invert = 1.0 / circle_amount;
	if (mod(uv.y, invert * 2.0) < invert) {
		uv.x += invert * 0.5;
	}
	vec2 rand_co = floor(uv * circle_amount) / circle_amount;
	uv = mod(uv, invert) * circle_amount;
	float r = rand(rand_co, corona_size, seed);
	r = clamp(r, invert, 1.0 - invert);
	float c = distance(uv, vec2(r));
	return smoothstep(c, c + 0.5, invert * circle_size * rand(rand_co * 1.5, corona_size, seed));
}

void fragment() {
	// QuadMesh в MultiMesh даёт UV вверх ногами (см. nucleus_palette.gdshader).
	vec2 uv = vec2(UV.x, 1.0 - UV.y);
	float qp = max(v_quad_px, 1.0);
	float bp = max(v_body_px, 2.0);
	vec2 ip = min(floor(uv * qp), vec2(qp - 1.0)); // целый пиксель квада
	float off = (qp - bp) * 0.5;                    // целое: квад = тело + по k пикселей с каждой стороны
	vec2 body = (ip - off) / bp;                    // = floor(UV_тела * bp) / bp, как в оригинале
	bool working = v_state < 0.5;
	bool idle = v_state > 0.5 && v_state < 1.5;
	float t = working ? phase : (idle ? idle_phase : 0.0);
	float seed = seed_of_tier();

	float bd = distance(body, vec2(0.5));
	if (lod) {
		if (bd >= 0.49999) discard;
		COLOR = bd >= 0.5 - 1.0 / bp ? tone(1) : tone(3);
		COLOR.a = 1.0;
	} else if (bd < 0.49999) {
		// Тело.
		float r = bd * 2.0;
		bool dith = mod(ip.x + ip.y, 2.0) < 1.0;
		vec2 sp = spherify(body);
		float n = Cells(sp - vec2(t * body_shift_a, 0.0), cells_a);
		n *= Cells(sp - vec2(t * body_shift_b, 0.0), cells_b);
		n *= 2.0;
		n += limb * smoothstep(limb_start, 1.0, r);
		n = clamp(n, 0.0, 1.0);
		if (dith || !should_dither) n *= 1.3;
		int idx = min(int(floor(n * 3.0)), 3);   // 0 — светлее всего
		int tn = 4 - idx - (idle ? 1 : 0);       // тоны 4 → 1; в простое на ступень темнее (3 → 0)
		COLOR = vec4(tone(tn).rgb, 1.0);
	} else {
		// Корона: в простое погашена; при забитом выходе — стоит на фазе 0.
		if (idle) discard;
		vec2 cu = (ip - (qp - 2.0 * bp) * 0.5) / (2.0 * bp); // пиксель в координатах короны (квад 2× тела)
		float angle = atan(cu.x - 0.5, cu.y - 0.5);
		float d = max(distance(cu, vec2(0.5)), 0.001);
		float c = 0.0;
		for (int i = 0; i < 15; i++) {
			float rr = rand(vec2(float(i)), corona_size, seed);
			vec2 circleUV = vec2(d, angle);
			c += circle(circleUV * corona_size - t * corona_shift - (1.0 / d) * 0.1 + rr, seed);
		}
		float b = working ? breath * sin(6.28318530718 * phase * breath_cycles) : 0.0;
		c *= 0.37 + b - d;
		c = step(0.07, c - d);
		if (c < 0.5) discard;
		COLOR = vec4(tone(1).rgb, 1.0);
	}
}
