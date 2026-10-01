# PixelPlanets (Deep-Fold) — исходники для шейдерных объектов

Источник: https://github.com/Deep-Fold/PixelPlanets, лицензия MIT (`LICENSE` рядом — хранить вместе с
любым кодом, производным от этих шейдеров).

- `star_body_calm.gdshader` — тело звезды, производное от `Planets/Star/Star.gdshader`: крупность ячеек
  грануляции — параметры `cells_a`/`cells_b` (в оригинале зашиты 10/20), добавлено потемнение к краю
  (`limb`, `limb_start`). Утверждённый вид «станка»: `cells_a = 4`, `cells_b = 8`, `limb = 0.6`,
  `limb_start = 0.5`, сид 753, дизеринг включён.
- `star_blobs.gdshader` — корона (без изменений).

Это эталон вида, а не готовый игровой шейдер: перенос в MultiMesh, палитра тиров, время от тика симуляции —
задача T017. Мастерская генератора (пакетный экспорт, эксперименты с сидами): `D:\Projects\Tools\PixelPlanets`.
- `black_hole_body.gdshader` — тело ЧД (оригинал `Planets/BlackHole/BlackHole.gdshader`, без изменений).
- `black_hole_disk_loop.gdshader` — аккреционный диск, производное от `Planets/BlackHole/BlackHoleRing.gdshader`:
  время заменено на `phase` 0..1 (`loop_turns`, `wobble_cycles` — целые, петля бесшовная). Утверждённый вид — вариант F,
  параметры и цвета — `Docs/Art/black-hole-visual-spec.md`.
