SpriteCook Godot tileset export

Copy the SpriteCook folder into your Godot project.
Assign SpriteCook/73c91aaf-dd04-4be1-ae2e-c1c533fbe660.tres to a TileMapLayer Tile Set.
Paint from TileMap > Terrains > Ground 0 > Connect.

Dual-grid editor helper for 15-piece tilesets:
1. Keep the whole exported SpriteCook folder together in your project.
2. Instance SpriteCook/scenes/SpriteCookDualGrid.tscn in your scene.
3. Select its Data TileMapLayer child.
4. Paint filled cells with the center tile from the atlas.
5. The Display TileMapLayer updates to the half-cell-offset dual-grid result.
6. Data is hidden by default so only the final Display layer is visible.
7. Toggle show_data_layer on the SpriteCookDualGrid root if you need to inspect painted source cells.
