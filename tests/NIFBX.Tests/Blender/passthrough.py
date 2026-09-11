# Read an FBX into Blender and write it out again, changing nothing on purpose.
#
# The middle of `BlenderRoundTripTests`: what a modder actually does with one of
# these files. Anything that differs after this is something Blender does not
# carry, which is a different and larger set than what the conversion itself
# loses -- a NIF -> FBX -> NIF trip keeps everything the stack properties hold,
# and Blender writes none of them back.
#
# The add-on's importer rather than the built-in one: only it has the Armature
# panel, and Skyrim's bones run down their local +X where Blender assumes +Y, so
# without Automatic Bone Orientation every rig arrives ninety degrees across its
# own limbs.
#
# `bake_anim_use_all_actions` is the expensive one, and it is on deliberately. A
# NIF's sequences each become an action, and only one of them can be the active
# one, so exporting only the active action would drop every sequence but one.
# The cost is that Blender's exporter bakes every action against every object:
# `blacksmithforgemarker` is 285 objects, and it goes from 1.1 seconds with no
# baking, to 7.9 with the active action only, to over ten minutes with all of
# them. A file that large is left to the caller's timeout, which counts it as too
# slow rather than as a failure.
#
#   blender -b --python passthrough.py -- in.fbx out.fbx

import bpy
import sys

argv = sys.argv[sys.argv.index("--") + 1:]
source, destination = argv[0], argv[1]

bpy.ops.wm.read_factory_settings(use_empty=True)

bpy.ops.import_scene.fbx(
    filepath=source,
    automatic_bone_orientation=True,
    use_custom_props=True,
    use_anim=True,
    ignore_leaf_bones=False)

bpy.ops.export_scene.fbx(
    filepath=destination,
    use_selection=False,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_NONE',
    axis_forward='-Z',
    axis_up='Y',
    bake_space_transform=False,
    object_types={'EMPTY', 'ARMATURE', 'MESH', 'OTHER'},
    use_custom_props=True,
    add_leaf_bones=False,
    bake_anim=True,
    bake_anim_use_all_bones=True,
    bake_anim_use_nla_strips=False,
    bake_anim_use_all_actions=True,
    bake_anim_force_startend_keying=True,
    path_mode='AUTO')
