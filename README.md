# NIFBX

Converts Bethesda **NIF** models to **FBX** and back.

```csharp
// NIF -> FBX
NifXmlDatabase schema = NifXmlDatabase.LoadEmbedded();
NifModel model = NifModel.Load("meshes/clutter/apple.nif", schema);

FbxDocument document = new NifToFbx(model).Convert();
document.Save("apple.fbx");

// FBX -> NIF
var scene = new FbxScene(FbxDocument.Load("apple.fbx"));
new FbxToNif(scene).Convert(schema).Save("apple.nif");
```

Meshes, materials, skinning, animation, Havok collision, constraints and
particle systems all cross in both directions.

## The bar is the round trip, not the import

A converter that produces something a viewer will open is easy. This aims at
something harder: NIF → FBX → NIF giving back the file it started from.

That standard is what finds the bugs. A vertex format read as though it were a
different one still displays. A skin partition rebuilt with the bones in another
order still deforms. A collision shape whose convex radius was dropped still
looks right. None of them survives a byte comparison, and each was a real defect
found that way rather than by looking at a preview.

1,450 tests run against committed fixtures. Two more suites convert the game's
own meshes, thousands of them, and report what came back different — those need
an installed copy and skip without one.

## Opening the result in Blender

Blender needs telling two things about a Bethesda model, and there is one place
it is easy to import from and get neither.

Use **File ▸ Import ▸ FBX**, the "FBX format" add-on. Blender 5.0 also ships a
newer built-in importer, and it is the one on the drag-and-drop path; it has no
bone-axis setting, so a skeleton always arrives ninety degrees across the limbs
and there is nothing you can set to correct it.

In that dialog:

| | |
| --- | --- |
| **Armature ▸ Automatic Bone Orientation** | on |
| **Transform ▸ Scale** | 100, if you want the model at NIF scale |

A NIF's bones run down their node's local **+X**. Blender points a bone along
local **+Y** and does not read a bone's direction from the file, because FBX does
not record one — so without automatic orientation every bone is drawn square
across the limb it belongs to. The skinning is right either way; only the
octahedra point the wrong way, which is enough to make a correct rig look broken.

The scale is a unit question. These files declare `UnitScaleFactor = 1.0`, which
says a unit is a centimetre, so Blender — which works in metres — divides by a
hundred and a five-hundred-unit waterwheel comes in five metres wide. Everything
stays in proportion; a scale of 100 undoes it.

Nothing else needs setting. The export marks the node above each bone chain as a
skeleton root on purpose: Blender builds the armature object out of the topmost
bone it finds and turns only what hangs below into bones, so a file whose bones
are flat siblings of one another — most of the game's characters — would
otherwise arrive as one empty armature per bone, with no armature modifier on
the mesh and nothing deforming at all.

The Autodesk SDK and FBX Review need none of this. They read the clusters
directly and never ask what a chain hangs from or which way a bone points, which
is exactly why a file can look perfect there and wrong in Blender.

### If the import dialog has no panels

On a Blender built against Python 3.14 — distribution packages do this, the
blender.org builds ship their own older Python — the add-on registers with
nothing but its two axis settings, and the Transform and Armature panels are
simply absent. `bpy_extras.io_utils.orientation_helper` gives the class it
decorates its own annotations, and tests for them with:

```python
if "__annotations__" not in cls.__dict__:
    setattr(cls, "__annotations__", {})
```

Since PEP 649 a class body's annotations are held as a thunk in
`__annotate_func__`, and an assigned dict lands in `__annotations_cache__`;
neither is that key, so the test passes for a class that does have annotations
and every declared property is thrown away. A start-up script that re-applies
them after the decorator runs, and reloads `io_scene_fbx`, restores the dialog.
The add-on also imports numpy, which such builds do not always provide.

## What it is built on

Deliberately little of this is about parsing files:

| | |
| --- | --- |
| [NIFSharp](https://github.com/aerisarn-two/NIFSharp) | the NIF itself — nif.xml, the item tree, both stream directions |
| [LeanMeshIO](https://github.com/aerisarn-two/LeanMeshIO) | the FBX as its raw node tree |
| [Mopper.Native](https://github.com/aerisarn/mopper-fork) | mopper.exe, the only encoder for Havok MOPP collision code |

What is left here is the part that has an opinion: what a `BSXFlags` should say,
which order a NIF wants its blocks in, how a dismember partition maps onto an FBX
skin, where a constraint's frames go, how a particle system survives a format
that has no idea what one is.

## Layout

- `Conversion/` — the two directions, and the geometry that serves them:
  tangents, tessellation, shape fitting, inertia.
- `Fbx/` — FBX semantics over the raw node tree: scenes, meshes, skins,
  materials, animation curves, collision objects.
- `Nif/` — NIF semantics: skin and animation writers, constraints, particles,
  and the flags a file has to declare about itself.
- `Havok/` — MOPP generation, out-of-process through mopper.

## Documentation

`docs/` carries what was learned taking the formats apart, most of it written
while chasing a specific difference to ground:

- `fbx-nif-conversion-spec.md` — the long one, field by field.
- `nif-fbx-knowledge-base.md` — what the formats do, and what they lie about.
- `bsxflags-spec.md`, `hkx-constraint-spec.md`, `nif-particle-spec.md`.

## Licence

**GPL-3.0-or-later.** It descends from se-cmd and depends on NIFSharp, both GPL.

## Installing

Published to GitHub Packages; see NIFSharp's README for the `nuget.config` shape.
Then `<PackageReference Include="NIFBX" Version="1.0.0" />` with
`GITHUB_USERNAME` and `GITHUB_TOKEN` set.
