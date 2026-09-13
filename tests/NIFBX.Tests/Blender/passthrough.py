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
import os
import sys

argv = sys.argv[sys.argv.index("--") + 1:]
source, destination = argv[0], argv[1]

# SKHK_ADDON points at the directory holding SKDcc's `skyrim_havok_constraints`
# add-on. With it set, the pass is what a person using that add-on actually does:
# the Havok properties become Blender constraints on the way in and are written
# back on the way out. Without it, the properties ride through untouched as
# properties. The two should agree, and the test that runs both is what says so.
addon = os.environ.get("SKHK_ADDON", "")

bpy.ops.wm.read_factory_settings(use_empty=True)

bpy.ops.import_scene.fbx(
    filepath=source,
    automatic_bone_orientation=True,
    use_custom_props=True,
    use_anim=True,
    ignore_leaf_bones=False)

if addon:
    sys.path.insert(0, addon)

    import skyrim_havok_constraints

    skyrim_havok_constraints.register()
    bpy.ops.skhk.build_constraints()
    bpy.ops.skhk.bake()

bpy.ops.export_scene.fbx(
    filepath=destination,
    use_selection=False,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_NONE',
    # The axes NIFBX declares, so the file comes back as the file it was. These
    # are Blender's description of the conversion rather than of the file, and
    # the two are not the same sentence: the default `-Z`/`Y` returns the whole
    # scene rotated -90 degrees about X, and `-Y`/`Z` returns it spun 180 about
    # Z. Only `Y`/`Z` gives back what went in.
    #
    # Either wrong answer is geometrically perfect -- across 47 skeleton nodes
    # the worst pairwise distance changed by 0.0001 units -- and in the wrong
    # place, which reads exactly like a conversion fault until the distances are
    # measured rather than the positions.
    axis_forward='Y',
    axis_up='Z',
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
