# Read an FBX into Blender, weld every mesh's coincident vertices, and write it out.
#
# The shape a mesh has when it was made in Blender rather than converted from a NIF.
# A NIF vertex is one position with one normal and one UV, so NifToFbx writes a
# vertex for every side of a UV seam and each is its own control point. Blender's
# meshes share one control point across a seam and keep the two UVs on the polygon
# corners, and a converter has to split that point back into two vertices -- and
# give both of them the bones that move it. Welding here is what makes the file
# ask for that.
#
#   blender -b --python weld.py -- in.fbx out.fbx

import bpy
import bmesh
import sys

argv = sys.argv[sys.argv.index("--") + 1:]
source, destination = argv[0], argv[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=source, use_custom_props=True, ignore_leaf_bones=False)

welded = 0
for o in bpy.data.objects:
    if o.type != 'MESH':
        continue
    bm = bmesh.new()
    bm.from_mesh(o.data)
    before = len(bm.verts)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
    welded += before - len(bm.verts)
    bm.to_mesh(o.data)
    bm.free()

print("WELDED", welded)
bpy.ops.export_scene.fbx(filepath=destination, use_custom_props=True, add_leaf_bones=False,
                         axis_forward='Y', axis_up='Z', bake_anim=False)
