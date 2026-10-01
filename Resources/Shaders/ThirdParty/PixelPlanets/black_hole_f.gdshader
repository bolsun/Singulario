// Чёрная дыра Singulario (T025, вариант F «Сингулярность»). Производная от PixelPlanets (Deep-Fold, MIT,
// см. LICENSE рядом): тело — black_hole_body.gdshader (Planets/BlackHole/BlackHole.gdshader), диск —
// black_hole_disk_loop.gdshader (Planets/BlackHole/BlackHoleRing.gdshader, время заменено на phase).
//
// Изменения относительно оригинала:
//  - один квад на ЧД (MultiMesh): диск на весь квад, тело — средняя треть, диск поверх тела
//    (пиксель диска с disk_a = 1 перекрывает тело, как в сцене оригинала);
//  - данные инстанса — INSTANCE_CUSTOM: x — пикселей на сторону квада (целое), y — альфа (превью);
//  - пиксельная сетка — целые пиксели квада, общая у тела и диска; координаты — центры пикселей;
//    дизеринг — шахматка по целым пикселям (без подпиксельных полос при крупном зуме);
//  - время — phase ∈ [0,1) от тика симуляции; один оборот шума и одно покачивание за петлю (бесшовно);
//  - толщина колец тела не меньше арт-пикселя (иначе на мелких ступенях зума кольцо пропадает);
//  - lod = true — упрощённый вид без шума и анимации: ядро, кольцо, горизонтальная полоса диска.
shader_type canvas_item;
render_mode blend_mix;

uniform float phase = 0.0;
uniform bool lod = false;

// Тело.
uniform vec4 body_colors[3] : source_color;  // ядро, внутреннее кольцо, край
uniform float radius = 0.247;
uniform float light_width = 0.028;

// Диск. Цвета — по возрастанию яркости: 0 — самый тёмный, n_colors-1 — самый яркий.
uniform vec4 disk_colors[5] : source_color;
uniform int n_colors = 5;
uniform float rotation = 0.0;
uniform float disk_width = 0.065;
uniform float ring_perspective = 14.0;
uniform bool should_dither = true;
uniform float size = 6.598;
uniform int OCTAVES = 3;
uniform float seed = 7.53;
uniform float loop_turns = 1.0;       // оборотов шума диска за петлю (целое)
uniform float wobble_cycles = 1.0;    // покачиваний диска за петлю (целое)

varying float v_quad_px;
varying float v_alpha;

void vertex() {
	v_quad_px = INSTANCE_CUSTOM.x;
	v_alpha = INSTANCE_CUSTOM.y;
}

float rand(vec2 coord) {
	coord = mod(coord, vec2(2.0, 1.0) * round(size));
	return fract(sin(dot(coord.xy, vec2(12.9898, 78.233))) * 15.5453 * seed);
}

float noise(vec2 coord) {
	vec2 i = floor(coord);
	vec2 f = fract(coord);

	float a = rand(i);
	float b = rand(i + vec2(1.0, 0.0));
	float c = rand(i + vec2(0.0, 1.0));
	float d = rand(i + vec2(1.0, 1.0));

	vec2 cubic = f * f * (3.0 - 2.0 * f);

	return mix(a, b, cubic.x) + (c - a) * cubic.y * (1.0 - cubic.x) + (d - b) * cubic.x * cubic.y;
}

float fbm(vec2 coord) {
	float value = 0.0;
	float scale = 0.5;

	for (int i = 0; i < OCTAVES; i++) {
		value += noise(coord) * scale;
		coord *= 2.0;
		scale *= 0.5;
	}
	return value;
}

vec2 rotate(vec2 coord, float angle) {
	coord -= 0.5;
	coord *= mat2(vec2(cos(angle), -sin(angle)), vec2(sin(angle), cos(angle)));
	return coord + 0.5;
}

// Диск: цвет и покрытие (a) для пикселя; uv — центр пикселя в долях квада.
vec4 disk_pixel(vec2 uv, bool dith) {
	uv = rotate(uv, rotation);

	// keep an undistored version of the current uvs
	vec2 uv2 = uv;

	// compress uv along the x axis, or the accretion disk will look to stretched out
	uv.x -= 0.5;
	uv.x *= 1.3;
	uv.x += 0.5;

	// wobble of the accretion disk
	uv = rotate(uv, sin(phase * 6.2831853 * wobble_cycles) * 0.01);

	// l_origin will be used to determine how to color the pixels
	vec2 l_origin = vec2(0.5);
	// d_width will be the final width of the accretion disk
	float d_width = disk_width;

	// here we distort the uvs to achieve the shape of the accretion disk
	if (uv.y < 0.5) {
		uv.y += smoothstep(distance(vec2(0.5), uv), 0.5, 0.2);
		d_width += smoothstep(distance(vec2(0.5), uv), 0.5, 0.3);
		l_origin.y -= smoothstep(distance(vec2(0.5), uv), 0.5, 0.2);
	}
	// we don't check for exactly uv.y > 0.5 because we want a small area where the ring
	// is unaffected by stretching, the middle part that goes over the black hole.
	else if (uv.y > 0.53) {
		uv.y -= smoothstep(distance(vec2(0.5), uv), 0.4, 0.17);
		d_width += smoothstep(distance(vec2(0.5), uv), 0.5, 0.2);
		l_origin.y += smoothstep(distance(vec2(0.5), uv), 0.5, 0.2);
	}

	// get distance to light origin based on unaltered uv's we saved earlier, some math to account for perspective
	float light_d = distance(uv2 * vec2(1.0, ring_perspective), l_origin * vec2(1.0, ring_perspective)) * 0.3;

	// center is used to determine ring position
	vec2 uv_center = uv - vec2(0.0, 0.5);

	// tilt ring
	uv_center *= vec2(1.0, ring_perspective);
	float center_d = distance(uv_center, vec2(0.5, 0.0));

	// cut out 2 circles of different sizes and only intersection of the 2.
	float disk = smoothstep(0.1 - d_width * 2.0, 0.5 - d_width, center_d);
	disk *= smoothstep(center_d - d_width, center_d, 0.4);

	// rotate noise in the disk
	uv_center = rotate(uv_center + vec2(0.0, 0.5), phase * 6.2831853 * loop_turns);

	// some noise
	disk *= pow(fbm(uv_center * size), 0.5);

	// apply dithering
	if (dith || !should_dither) {
		disk *= 1.2;
	}

	// apply some colors based on final value
	float n_posterized = float(n_colors - 1);
	float posterized = floor((disk + light_d) * n_posterized);
	posterized = min(posterized, n_posterized);
	vec4 col = disk_colors[int(posterized)];

	return vec4(col.rgb, step(0.15, disk) * col.a);
}

void fragment() {
	// QuadMesh в MultiMesh даёт UV вверх ногами (см. nucleus_palette.gdshader).
	vec2 st = vec2(UV.x, 1.0 - UV.y);
	float qp = max(v_quad_px, 3.0);
	vec2 ip = min(floor(st * qp), vec2(qp - 1.0)); // целый пиксель квада
	vec2 pc = (ip + 0.5) / qp;                      // его центр в долях квада

	// Тело — средняя треть квада: свои координаты 0..1 по тому же пикселю.
	float bp = qp / 3.0;
	vec2 body = (ip + 0.5 - bp) / bp;
	float bd = distance(body, vec2(0.5));
	float ring_w = max(light_width, 2.0 / bp);

	vec4 res = vec4(0.0);
	if (lod) {
		float rw = max(light_width, 1.0 / bp);
		if (bd <= radius) res = bd > radius - rw ? body_colors[1] : body_colors[0];
		// Горизонтальная полоса диска (не уже пикселя).
		if (abs(pc.x - 0.5) < 0.45 && abs(pc.y - 0.5) <= max(0.012, 0.5 / qp) + 0.0001) res = disk_colors[2];
	} else {
		if (bd <= radius) {
			res = body_colors[0];
			if (bd > radius - ring_w) res = body_colors[1];
			if (bd > radius - ring_w * 0.5) res = body_colors[2];
		}
		bool dith = mod(ip.x + ip.y, 2.0) < 1.0;
		vec4 d = disk_pixel(pc, dith);
		if (d.a > 0.5) res = d;
	}
	if (res.a < 0.5) discard;
	COLOR = vec4(res.rgb, v_alpha);
}
