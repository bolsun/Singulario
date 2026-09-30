shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

// Пятно света (T020) в буфер свечения GlowLayer: мягкое конечное пятно
// (1 − r²)², r — от центра квада до края. Сила по каналам — INSTANCE_CUSTOM
// (не цвет экземпляра: цвета в HDR-буфере переводятся в линейное пространство):
// R/G/B — Ж/К/С, A — серое.
//
// Смешение premultiplied: dst.rgb = src.rgb + dst.rgb·(1 − src.a),
// dst.a = src.a + dst.a·(1 − src.a). У цветных пятен src.a = 0 — R/G/B просто
// складываются; у серых src.rgb = 0 — A копится мягким насыщением. Серые пятна
// рисуются раньше цветных, чтобы не ослаблять их.

uniform float fade = 1.0;

varying vec4 weights;

void vertex() {
	weights = INSTANCE_CUSTOM;
}

void fragment() {
	vec2 q = UV * 2.0 - 1.0;
	float f = max(0.0, 1.0 - dot(q, q));
	COLOR = weights * (f * f * fade);
}
