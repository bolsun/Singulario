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
// T007/T023/T024: при atlas_frames > 0 текстура — атлас из atlas_frames кадров в ряд (осколки, последним —
// шар атома-предмета), INSTANCE_CUSTOM.w — номер кадра. 0 (по умолчанию) — вся текстура (тела, дырки, точки).
varying float v_frame;
// T038: перспектива объектов в дырках. INSTANCE_CUSTOM.w = frame + 16·step + 64·shift: step 0/1/2 — блок
// 1/2/4 (квад уже уменьшен на CPU), shift 0..3 — на сколько тонов темнее (сдвиг тона, как Dark у месторождений).
varying float v_step;
varying float v_shift;
uniform float atlas_frames = 0.0;
// T015: общая проявленность (материал точек дальнего зума гасит их к туманностям).
uniform float fade = 1.0;
// T038: тон 0 тиров Ж/К/С (общий с DepositLayer.DefaultTone0) и число строк палитры (тир = строка).
uniform vec3 tone0[3];
uniform float tier_count = 6.0;

void vertex() {
	v_palette_row = INSTANCE_CUSTOM.x;
	v_glow = INSTANCE_CUSTOM.y;
	v_dim = INSTANCE_CUSTOM.z;
	// +0.5 и floor — против погрешности float при разборе упаковки.
	float packed = floor(INSTANCE_CUSTOM.w + 0.5);
	v_frame = mod(packed, 16.0);
	v_step = mod(floor(packed / 16.0), 4.0);
	v_shift = floor(packed / 64.0);
}

void fragment() {
	// QuadMesh (на нём стоит MultiMeshInstance2D) генерирует UV "вверх ногами"
	// относительно 2D-экрана — подтверждённая особенность движка (issue #44347
	// в трекере Godot), а не наша ошибка. Без этой строки спрайт будет
	// отрисован перевёрнутым по вертикали.
	vec2 uv = vec2(UV.x, 1.0 - UV.y);

	vec4 src;
	float idx;
	vec3 final_color;
	if (atlas_frames > 0.5) {
		float frame = floor(v_frame + 0.5);
		if (v_step < 0.5 && v_shift < 0.5) {
			uv.x = (clamp(uv.x, 0.0, 0.999) + frame) / atlas_frames;
			src = texture(TEXTURE, uv);
		} else {
			// T038: выходной тексель сетки side/B смотрит блок B×B кадра; берём самый тёмный непрозрачный пиксель.
			ivec2 tsz = textureSize(TEXTURE, 0);
			int side = tsz.y;
			int B = v_step < 0.5 ? 1 : (v_step < 1.5 ? 2 : 4);
			int cells = side / B;
			ivec2 p = ivec2(floor(clamp(uv, vec2(0.0), vec2(0.999)) * float(cells)));
			src = vec4(0.0);
			float best = 2.0;
			for (int j = 0; j < B; j++) {
				for (int i = 0; i < B; i++) {
					vec4 t = texelFetch(TEXTURE, ivec2(int(frame) * side + p.x * B + i, p.y * B + j), 0);
					if (t.a > 0.5 && t.r < best) { best = t.r; src = t; }
				}
			}
		}
	} else {
		src = texture(TEXTURE, uv);
	}
	idx = src.r; // 0..1 — квантованный маркер уровня из index-спрайта
	if (v_shift < 0.5) {
		final_color = texture(palette_tex, vec2(idx, v_palette_row)).rgb;
	} else {
		// Палитра — 5 колонок: 0 — обводка, 1..4 — тоны; уровень L берёт колонку L. Тон L − shift;
		// ниже тона 1 — тон 0 тира Ж/К/С, у остальных строк — не темнее тона 1. Обводка не трогается.
		float level = floor(idx * 4.0 + 0.5);
		float t = level - floor(v_shift + 0.5);
		int row = int(floor(v_palette_row * tier_count));
		if (level < 0.5) final_color = texture(palette_tex, vec2(0.1, v_palette_row)).rgb;
		else if (t >= 0.5) final_color = texture(palette_tex, vec2((t + 0.5) / 5.0, v_palette_row)).rgb;
		else if (row >= 0 && row < 3) final_color = tone0[row];
		else final_color = texture(palette_tex, vec2(1.5 / 5.0, v_palette_row)).rgb;
	}
	// Свечение (T006c): ярче с сохранением оттенка — умножение, но не выше
	// 1 / max(r,g,b), чтобы ни один канал не упёрся в 1 и цвет не выцвел.
	float peak = max(max(final_color.r, final_color.g), max(final_color.b, 0.001));
	final_color *= min(1.0 + max(v_glow, 0.0), 1.0 / peak);
	float luma = dot(final_color, vec3(0.299, 0.587, 0.114));
	float dim = clamp(v_dim, 0.0, 1.0);
	final_color = mix(final_color, vec3(luma), dim * 0.6) * (1.0 - dim * 0.5);
	float alpha = src.a;
	COLOR = vec4(final_color, alpha * fade);
}
