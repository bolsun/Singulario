shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

// Пятно света (T020) в буфер свечения GlowLayer. T020b: скруглённый квадрат в
// границах следа объекта — n = (|x|⁴ + |y|⁴)^¼, x, y в долях полустороны следа,
// спад 1 − smoothstep(inner, reach, n); квад = след × reach. Сила по каналам — INSTANCE_CUSTOM
// (не цвет экземпляра: цвета в HDR-буфере переводятся в линейное пространство):
// R/G/B — Ж/К/С, A — серое.
//
// Смешение premultiplied: dst.rgb = src.rgb + dst.rgb·(1 − src.a),
// dst.a = src.a + dst.a·(1 − src.a). У цветных пятен src.a = 0 — R/G/B просто
// складываются; у серых src.rgb = 0 — A копится мягким насыщением. Серые пятна
// рисуются раньше цветных, чтобы не ослаблять их.

uniform float fade = 1.0;
uniform float inner = 0.6;
uniform float reach = 1.15;

varying vec4 weights;

void vertex() {
	weights = INSTANCE_CUSTOM;
}

void fragment() {
	vec2 q = abs(UV * 2.0 - 1.0) * reach;
	float n = pow(pow(q.x, 4.0) + pow(q.y, 4.0), 0.25);
	float f = 1.0 - smoothstep(inner, reach, n);
	COLOR = weights * (f * fade);
}
