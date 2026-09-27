@tool
extends Node2D
class_name SpriteCookDualGrid

const FRAME_BY_MASK := [-1, 15, 8, 9, 0, 11, 14, 7, 13, 4, 1, 10, 3, 2, 5, 6]
const DATA_LAYER_NAME := "Data"
const DISPLAY_LAYER_NAME := "Display"

var _display_tile_set: TileSet
var _last_signature := ""
var _syncing := false

@export var display_tile_set: TileSet:
  get:
    return _display_tile_set
  set(value):
    _display_tile_set = value
    if is_inside_tree():
      _configure_layers()
      sync_display()

@export var source_id := 0
@export var filled_atlas_coords := Vector2i(2, 1)
@export var sync_in_editor := true
@export var show_data_layer := false:
  set(value):
    show_data_layer = value
    if is_inside_tree():
      _configure_layers()

func _ready() -> void:
  _configure_layers()
  sync_display()
  set_process(Engine.is_editor_hint())

func _process(_delta: float) -> void:
  if not Engine.is_editor_hint() or not sync_in_editor:
    return
  var data_layer := _get_data_layer()
  if data_layer == null:
    return
  var signature := _data_signature(data_layer)
  if signature != _last_signature:
    _last_signature = signature
    sync_display()

func sync_display() -> void:
  if _syncing:
    return
  _syncing = true
  _configure_layers()
  var data_layer := _get_data_layer()
  var display_layer := _get_display_layer()
  if data_layer == null or display_layer == null:
    _syncing = false
    return

  display_layer.clear()
  var display_cells := {}
  for data_cell in data_layer.get_used_cells():
    display_cells[data_cell] = true
    display_cells[data_cell + Vector2i(1, 0)] = true
    display_cells[data_cell + Vector2i(0, 1)] = true
    display_cells[data_cell + Vector2i(1, 1)] = true

  for display_cell in display_cells.keys():
    var mask := _mask_for_display_cell(data_layer, display_cell)
    var atlas_coords := _atlas_coords_for_mask(mask)
    if atlas_coords.x >= 0:
      display_layer.set_cell(display_cell, source_id, atlas_coords)

  _last_signature = _data_signature(data_layer)
  _syncing = false

func _configure_layers() -> void:
  var data_layer := _ensure_layer(DATA_LAYER_NAME)
  var display_layer := _ensure_layer(DISPLAY_LAYER_NAME)
  if data_layer == null or display_layer == null:
    return

  if data_layer.tile_set == null and _display_tile_set != null:
    data_layer.tile_set = _display_tile_set
  if _display_tile_set != null:
    display_layer.tile_set = _display_tile_set

  data_layer.z_index = 0
  data_layer.visible = true
  data_layer.modulate = Color(1, 1, 1, 0.28 if show_data_layer else 0.0)
  display_layer.z_index = 1
  display_layer.position = -_tile_size() * 0.5

func _ensure_layer(layer_name: String) -> TileMapLayer:
  var layer := get_node_or_null(layer_name) as TileMapLayer
  if layer != null:
    return layer
  layer = TileMapLayer.new()
  layer.name = layer_name
  add_child(layer)
  if Engine.is_editor_hint() and get_tree() != null and get_tree().edited_scene_root != null:
    layer.owner = get_tree().edited_scene_root
  return layer

func _get_data_layer() -> TileMapLayer:
  return get_node_or_null(DATA_LAYER_NAME) as TileMapLayer

func _get_display_layer() -> TileMapLayer:
  return get_node_or_null(DISPLAY_LAYER_NAME) as TileMapLayer

func _is_filled(data_layer: TileMapLayer, coords: Vector2i) -> bool:
  return data_layer.get_cell_source_id(coords) != -1

func _mask_for_display_cell(data_layer: TileMapLayer, coords: Vector2i) -> int:
  var mask := 0
  if _is_filled(data_layer, coords + Vector2i(-1, -1)):
    mask |= 1
  if _is_filled(data_layer, coords + Vector2i(0, -1)):
    mask |= 2
  if _is_filled(data_layer, coords + Vector2i(-1, 0)):
    mask |= 4
  if _is_filled(data_layer, coords):
    mask |= 8
  return mask

func _atlas_coords_for_mask(mask: int) -> Vector2i:
  if mask <= 0 or mask >= FRAME_BY_MASK.size():
    return Vector2i(-1, -1)
  var frame: int = FRAME_BY_MASK[mask]
  if frame < 0:
    return Vector2i(-1, -1)
  return Vector2i(frame % 4, int(frame / 4))

func _data_signature(data_layer: TileMapLayer) -> String:
  var cells := data_layer.get_used_cells()
  cells.sort()
  return var_to_str(cells)

func _tile_size() -> Vector2:
  if _display_tile_set != null:
    return Vector2(_display_tile_set.tile_size.x, _display_tile_set.tile_size.y)
  return Vector2(16, 16)
