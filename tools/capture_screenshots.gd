extends SceneTree
# Screenshot helper for the paper (not part of the game). Run by
# tools/capture_screenshots.py, which serves the encounter design.
#
# Loads the real Main.tscn (with `-- --autoplay`, so the bot plays), then:
#  1. once the dungeon exists, saves a fog-free overhead view of the whole map
#     (overview.png), rendered off-screen at 3200x3200;
#  2. restores the fog and HUD and, during play, periodically saves the
#     player's view re-rendered at 2560x1440 through a camera that copies
#     the game camera every frame (g_<frame>.png; the HUD is not included).
#
#   godot --path <project> --script tools/capture_screenshots.gd -- --out=DIR --frames=N --autoplay

var frames := 0
var out_dir := "."
var max_frames := 6000
var sv_over: SubViewport
var sv_game: SubViewport
var game_cam: Camera3D
var mirror: Camera3D
var hidden := []

func _initialize() -> void:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--out="):
			out_dir = a.substr(6)
		elif a.begins_with("--frames="):
			max_frames = int(a.substr(9))
	change_scene_to_file("res://Main.tscn")

func _find_all(node: Node, acc: Array) -> void:
	acc.append(node)
	for c in node.get_children():
		_find_all(c, acc)

func _world() -> World3D:
	return root.world_3d if root.world_3d != null else root.find_world_3d()

func _process(_delta: float) -> bool:
	frames += 1
	if frames == 60:
		var nodes := []
		_find_all(root, nodes)
		for n in nodes:
			# The fog of war is one large quad over the map; the HUD is a CanvasLayer.
			if (n is MeshInstance3D and n.mesh is QuadMesh and n.mesh.size.x > 100) or n is CanvasLayer:
				if n.visible:
					n.visible = false
					hidden.append(n)
		sv_over = SubViewport.new()
		sv_over.size = Vector2i(3200, 3200)
		sv_over.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		sv_over.world_3d = _world()
		root.add_child(sv_over)
		var c := Camera3D.new()
		c.projection = Camera3D.PROJECTION_ORTHOGONAL
		c.size = 166.0
		c.rotation_degrees = Vector3(-90, 0, 0)
		sv_over.add_child(c)
		c.position = Vector3(0, 120, 0)  # the map is centred on the origin
		c.current = true
	if frames == 70:
		sv_over.get_texture().get_image().save_png(out_dir + "/overview.png")
		sv_over.queue_free()
		for n in hidden:
			n.visible = true
		game_cam = root.get_camera_3d()
		sv_game = SubViewport.new()
		sv_game.size = Vector2i(2560, 1440)
		sv_game.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		sv_game.world_3d = _world()
		root.add_child(sv_game)
		mirror = Camera3D.new()
		mirror.projection = Camera3D.PROJECTION_ORTHOGONAL
		sv_game.add_child(mirror)
		mirror.current = true
	if frames > 70:
		var cur := root.get_camera_3d()
		if cur != null and cur != mirror:
			game_cam = cur
		if game_cam != null and is_instance_valid(game_cam):
			mirror.global_transform = game_cam.global_transform
			mirror.size = game_cam.size
	if frames > 100 and frames % 12 == 0:
		sv_game.get_texture().get_image().save_png("%s/g_%05d.png" % [out_dir, frames])
	return frames >= max_frames
