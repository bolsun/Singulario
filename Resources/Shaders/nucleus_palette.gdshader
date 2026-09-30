shader_type canvas_item;

// Атлас палитр: по строкам — разные цветовые тиры (row 0 = первый тир и
// т.д.), по столбцам — уровни яркости (5 колонок: 4 оттенка + чёрная
// обводка). Какую строку использовать для конкретного инстанса — приходит
// через INSTANCE_CUSTOM.x с CPU (MultiMesh custom_data).
//
// Серое ядро (NucleusLayer.GrayCoreTier) — просто ещё одна строка в этом же
// атласе (PalettePaths[GrayCoreTier] = реально серая палитра), а не особый
// случай: никакого ветвления по цвету тут не нужно, шейдер всегда красит по
// таблице одинаково для всех тиров.
//
// ВАЖНО: INSTANCE_CUSTOM в canvas_item-шейдерах — vertex-only билтин, в
// fragment() он недоступен напрямую (это и было причиной "все ядра серые" —
// шейдер не компилировался, движок откатывался на показ сырой текстуры).
// Поэтому прокидываем его через varying из vertex() в fragment().
uniform sampler2D palette_tex : filter_nearest, repeat_disable;

varying float v_palette_row;
// T006: INSTANCE_CUSTOM.y — свечение (звезда во время производства, доля
// прибавки яркости: 0.5 — в 1.5 раза ярче),
// INSTANCE_CUSTOM.z — приглушение (груз, звезда в простое, 0..1). У всех,
// кто их не задаёт, там 0 — цвет не меняется.
varying float v_glow;
varying float v_dim;
// T007: INSTANCE_CUSTOM.w > 0.5 — атом-предмет в дырке: спрайт частицы с
// вырезанным центром (полое кольцо цвета тира). Вырез считается по пиксельной
// сетке спрайта (item_pixels), чтобы край остался пиксельным.
varying float v_item;
uniform float item_pixels = 16.0;
uniform float item_hole_radius = 0.22;  // доля стороны квада — прозрачный центр
uniform float item_edge_width = 0.07;   // тёмная обводка внутреннего края
// T015: общая проявленность (материал точек дальнего зума гасит их к туманностям).
uniform float fade = 1.0;

void vertex() {
	v_palette_row = INSTANCE_CUSTOM.x;
	v_glow = INSTANCE_CUSTOM.y;
	v_dim = INSTANCE_CUSTOM.z;
	v_item = INSTANCE_CUSTOM.w;
}

void fragment() {
	// QuadMesh (на нём стоит MultiMeshInstance2D) генерирует UV "вверх ногами"
	// относительно 2D-экрана — подтверждённая особенность движка (issue #44347
	// в трекере Godot), а не наша ошибка. Без этой строки спрайт будет
	// отрисован перевёрнутым по вертикали.
	vec2 uv = vec2(UV.x, 1.0 - UV.y);

	vec4 src = texture(TEXTURE, uv);
	float idx = src.r; // 0..1 — квантованный маркер уровня из index-спрайта
	vec3 final_color = texture(palette_tex, vec2(idx, v_palette_row)).rgb;
	// Свечение (T006c): ярче с сохранением оттенка — умножение, но не выше
	// 1 / max(r,g,b), чтобы ни один канал не упёрся в 1 и цвет не выцвел.
	float peak = max(max(final_color.r, final_color.g), max(final_color.b, 0.001));
	final_color *= min(1.0 + max(v_glow, 0.0), 1.0 / peak);
	float luma = dot(final_color, vec3(0.299, 0.587, 0.114));
	float dim = clamp(v_dim, 0.0, 1.0);
	final_color = mix(final_color, vec3(luma), dim * 0.6) * (1.0 - dim * 0.5);
	float alpha = src.a;
	if (v_item > 0.5) {
		vec2 cell = (floor(uv * item_pixels) + 0.5) / item_pixels;
		float r = length(cell - vec2(0.5));
		if (r < item_hole_radius) alpha = 0.0;
		else if (r < item_hole_radius + item_edge_width) final_color *= 0.15;
	}
	COLOR = vec4(final_color, alpha * fade);
}
