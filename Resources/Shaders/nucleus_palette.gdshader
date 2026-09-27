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

void vertex() {
	v_palette_row = INSTANCE_CUSTOM.x;
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
	COLOR = vec4(final_color, src.a);
}
