# Read an FBX into Blender and write it out again, changing nothing on purpose.
#
# The middle of `BlenderRoundTripTests`: what a modder actually does with one of
# these files. Anything that differs after this is something Blender does not
# carry, which is a different and larger set than what the conversion itself
# loses -- a NIF -> FBX -> NIF trip keeps everything the stack properties hold,
# and Blender writes none of them back.
#
# The legacy importer, not the built-in one: only it has an Armature panel, and
# the setting on that panel is the whole of this.
#
# Automatic Bone Orientation aims each bone at the bone below it, which is the
# only way Blender draws a Skyrim rig as a skeleton rather than as sticks all
# pointing one way -- and it rewrites the rest pose to do it, with no per-bone
# inverse on the way out: 44 of skeleton_cow's 48 nodes come back turned, 82 of
# a draugr's 93. SKEX_ADDON points at SKDcc's `skyrim_export`, which turns the
# aiming off and has `skyrim_rig` draw the bones down their chains instead, by
# turning a shape while the bone stays where the file put it. Without it set,
# this is Blender bare, and the two numbers together are what say it works.
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
rigging = os.environ.get("SKEX_ADDON", "")

bpy.ops.wm.read_factory_settings(use_empty=True)

if not rigging:
    bpy.ops.import_scene.fbx(
        filepath=source,
        automatic_bone_orientation=True,
        use_custom_props=True,
        use_anim=True,
        ignore_leaf_bones=False)

settings = None

if rigging:
    sys.path.insert(0, rigging)

    from skyrim_export import load, settings

    load.read(source)

if addon:
    sys.path.insert(0, addon)

    import skyrim_havok_constraints

    skyrim_havok_constraints.register()
    bpy.ops.skhk.build_constraints()
    bpy.ops.skhk.bake()

# The rig has to stand at rest while its skeleton is written, or the pose is
# written into it -- and it must not be told to *ignore* poses, or every action
# bakes flat. `at_rest` empties the channels, which is both.
resting = settings.at_rest(bpy.context.scene) if settings else None

if resting:
    resting.__enter__()

bpy.ops.export_scene.fbx(
    filepath=destination,
    use_selection=False,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_NONE',
    # Z up and +Y forward, which is Skyrim's convention and NIFBX's.
    #
    # The file says this as `UpAxis=Z, FrontAxis=Y, FrontAxisSign=-1`, which is
    # `FbxAxisSystem::eMax`, and looks like it disagrees until you read what FBX
    # means by front: "vector with origin at the screen pointing toward the
    # camera", where -1 is "back relative to observer". A model facing +Y is
    # looked at down -Y, so -Y front *is* +Y forward. Blender names the facing
    # direction instead, so the same convention is spelled `Y` here.
    #
    # Getting it wrong is quiet: the default `-Z`/`Y` returns the whole scene
    # rotated -90 degrees about X, and `-Y`/`Z` returns it spun 180 about Z.
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

if resting:
    resting.__exit__(None, None, None)
