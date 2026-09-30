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

uniform sampler2D density : filter_linear, repeat_enable;
uniform vec2 screen_size = vec2(1920.0, 1080.0);
uniform vec2 cam_pos = vec2(0.0);
uniform float zoom = 1.0;
uniform float parallax = 0.4;
uniform float texel_world_size = 15.0;
uniform float texture_size = 512.0;
uniform float dither_pixel = 3.0;
uniform float gain = 1.3;
uniform float fade = 1.0;
// Свет ЧД: xy — центр ЧД относительно cam_pos (мир), z — радиус R (мир).
uniform int light_count = 0;
uniform vec3 lights[8];
uniform vec4 color0 : source_color = vec4(0.106, 0.086, 0.161, 1.0);
uniform vec4 color1 : source_color = vec4(0.153, 0.125, 0.220, 1.0);
uniform vec4 color2 : source_color = vec4(0.216, 0.176, 0.302, 1.0);
uniform vec4 color3 : source_color = vec4(0.302, 0.259, 0.408, 1.0);

const float BAYER[16] = float[16](
	0.0, 8.0, 2.0, 10.0,
	12.0, 4.0, 14.0, 6.0,
	3.0, 11.0, 1.0, 9.0,
	15.0, 7.0, 13.0, 5.0);

void fragment() {
	vec2 p = UV * screen_size;
	vec2 half_screen = screen_size * 0.5;
	// Экранная позиция начала фона и клетка, в которой лежит p.
	vec2 origin = half_screen - cam_pos * parallax * zoom;
	vec2 cell = floor((p - origin) / dither_pixel);
	vec2 center = origin + (cell + 0.5) * dither_pixel;

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

	float v = min(d * light * gain * fade, 1.0);
	ivec2 b = ivec2(mod(cell, 4.0));
	float bayer = (BAYER[b.y * 4 + b.x] + 0.5) / 16.0;
	int level = min(int(floor(v * 3.0 + bayer)), 3);

	vec4 c = color0;
	if (level == 1) c = color1;
	else if (level == 2) c = color2;
	else if (level == 3) c = color3;
	COLOR = c;
}
