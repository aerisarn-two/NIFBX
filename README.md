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
