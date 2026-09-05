# NIF and FBX in this codebase

Orientation for working on the NIF and FBX layers of `se-cmd`. The three specs in the
repository's `docs/` directory describe *what ck-cmd does*; this describes *what this
codebase does*, and the things that have already gone wrong.

Read this first, then the spec for whatever you are touching (all under `docs/`):

- `fbx-nif-conversion-spec.md` — the whole conversion, both directions, extracted from
  ck-cmd's FBXWrangler
- `hkx-constraint-spec.md` — constraints, extracted from FBXWrangler and HKXWrangler
- `nif-particle-spec.md` — particle systems, and what a scene format can hold

---

## 1. The map

| Layer | Directory | What it is |
| --- | --- | --- |
| NIF | `Nif/` | Reading, writing and querying a NIF. No FBX, no conversion. |
| FBX | `Fbx/` | Reading, writing and querying an FBX. Knows NIF *types* (`NifVector3`) but not files. |
| Conversion | `Conversion/` | The two directions, plus format-neutral models (`MeshGeometry`, `SkinData`, `AnimSequence`). |
| Havok | `Havok/` | MOPP generation via external binaries. No Havok code in this repo. |
| Commands | `Commands/` | `exportfbx`, `importfbx`. |

The two conversion entry points are `Conversion/NifToFbx.cs` and
`Conversion/FbxToNif.cs`. Everything else is reached from one of those.

`Nif/` must not reference `Fbx/`. The reverse is allowed and used — `Fbx/` writes NIF
values into FBX properties. When a helper looked like it belonged in `Fbx/` but was
really a NIF question (`ConstraintDescriptor`), it moved to `Nif/`.

---

## 2. The NIF layer

### 2.1 It is data-driven

`External/nifxml/nif.xml` (0.9.1.0) is vendored and embedded as a resource. It
describes every block and field, and `NifModel` reads by walking those descriptors —
there is no class per block type, and adding support for a block usually means adding
nothing.

Consequences worth internalising:

- **Reading is ordered, not indexed.** A field's condition may name a field read a
  moment earlier, so conditions are invalidated and re-evaluated as the walk reaches
  them. Nothing can be read out of order.
- **You address fields by name**, through `model.FindItem(block, "Some Field")` or a
  backslash path: `FindItem(entry, @"Scales\Num Keys")`. Paths are not recursive
  searches; each segment is a direct child.
- **`FindItem` respects conditions.** A field whose condition is false is invisible,
  and a *stale cached* condition makes it invisible too. See §2.3.

### 2.2 Conditions

Four things decide whether a field exists, and they are not interchangeable:

- `cond` resolves against **siblings** (`Has Vertex Colors`).
- `vercond` resolves against the **header** (`#BS_GTE_SSE#`).
- `onlyT` / `excludeT` are **block-type** tests. Missing this once dropped
  `NiObjectNET`'s `Shader Type` and misaligned every `BSLightingShaderProperty` by four
  bytes.
- Version tokens like `#BS202#` expand to expressions over `#VER#` and `#BSVER#`.
  `#BS202#` is *any* Bethesda 20.2 file — Fallout 3 and both Skyrims.

The same field name often appears several times in one block with different version
conditions. Only one is live. Anything walking a block's children must filter on
`model.EvalCondition(child)`, or it will see this file's values beside another
version's zeroes under names that look equally real.

### 2.3 The invariant that has caused the most bugs

**Reading sizes arrays as it walks. Writing must do the same.**

`NifModel.PrepareForOutput` enforces it now: `SaveItem` and `MeasureItem` both
invalidate each field's condition and resize each array before descending. Before that
existed, four separate bugs came from the same asymmetry — binary padding blobs,
stale cached conditions, unsized weight rows, and Special Edition's `Triangles Copy`.

The damage is never local. Reading a NIF is sequential, so a block written one byte
short takes every block after it down with it, and the error surfaces somewhere
unrelated: *"failed to read block 12 (BSDismemberSkinInstance): array Bones has
implausible size 20938752"* was actually a short skin partition three blocks earlier.

If you see an implausible array size on load, **look at the block before it**, not the
one that failed.

### 2.4 Writing blocks

- `model.InsertBlock("NiNode")` creates a block with arrays initialised and conditions
  invalidated.
- `model.SetArraySize(block, "Num X", "X", n)` sets the count **and** sizes the array.
  Use this rather than setting the count by hand.
- **After changing a field that some condition names, invalidate.**
  `item.InvalidateConditionsRecursive()`. Otherwise `FindItem` will keep returning the
  answer from before.
- **Two-dimensional arrays**: sizing the outer array creates rows but leaves them
  empty. Each row must be sized as well before anything can be written into it. See
  `NifSkinWriter.SizeGrid`.
- **Call `model.UpdateHeader()` before `Save()`**, and `SetRoots()` if the footer
  matters. Without `UpdateHeader` the header says zero blocks and the file reloads
  empty. This is not enforced and will not warn you.
- **`AddString` interns; `SetStringTable` copies.** `AddString` folds duplicates and
  refuses empty strings, which is right when authoring — but Bethesda's files contain
  empty entries, and dropping one shifts every name after it onto the wrong index.
  When the indices already exist in blocks you are about to write, adopt the table
  whole.
- **`UpdateHeader` keeps a block-type order the header already has**, appending only
  types it does not name. First-use order is what it produces from scratch, which is
  what Bethesda's exporter does — 2,500 of 2,500 vanilla meshes are ordered that way —
  but re-saving a file written by another tool must not renumber its table.

### 2.5 Traps that are not obvious from the code

- **A fresh link is null, and null is -1.** Left at zero a link points at block 0,
  which for a model built from scratch is the root — so every ref nobody assigned
  quietly claimed the root as its target. Fixed in `InsertType`, but if you add a value
  type with a similar "empty is not zero" rule, it needs the same treatment.
- **Strings**: `SetString` decides index-versus-inline from the *file version*, not the
  item's current type, because a fresh model's `Name` is still `String` until a reader
  converts it. `ResolveString` reads the model's own string list, not the header's
  array, because the header's is only filled in by `UpdateHeader` on the way out.

  The reading half of that costs an hour if you meet it from the wrong end. **Read a
  string field with `model.GetString(block, "Field")`, never `FindItem(...).Value` and
  a cast.** In an SSE file the field holds an *index* into the string table, so the raw
  value reads as empty or as a number and a probe over the whole corpus comes back with
  nothing at all — for every file, which looks far more like a wrong field name or a
  dead version condition than like a resolution step you skipped. It is neither of
  those: `FindItem` respects conditions (§2.1) and will have handed you the right
  field. Chasing `Node Name` on `ControlledBlock` this way cost two rewrites of a probe
  and a wrong diagnosis before `NifAnimAccess.ReadTargetName` showed how it is done.
- **Descriptors are often inlined.** `bhkRagdollConstraint`'s pivots and limits are
  fields of the block itself; there is no `Constraint` child to descend into. Only the
  polymorphic wrappers (`bhkWrappedConstraintData`) are children, and they are told
  apart by having a `Type` field. `model.ConstraintDescriptor(block)` handles this.
- **Fixed compounds** such as `BSVertexData` have their conditions evaluated once
  against element 0, not per element.
- **A ragged array's width names a sibling array, indexed by the row.**
  `length="Num Strips" width="Strip Lengths"` means row *n* holds `Strip Lengths[n]`
  entries — unlike every other length expression in the format, which names a scalar.
  The condition resolver handles it, but the array branch has to be tested *before* the
  count branch: an array item carries no value of its own, and an unset value reads as
  a perfectly good count of zero.
- **A half's bit pattern must survive, not just its value.** Converting through `float`
  and back loses a NaN's payload, and vanilla Skyrim ships meshes with NaNs in their
  vertex data. `NifPack.HalfToFloat`/`FloatToHalf` carry the payload across by hand. A
  NIF is a file rather than a computation: whatever is in it comes back out, whether or
  not it means a number.
- `BSVertexDesc` is a `<bitfield>` in nif.xml, not a `<struct>`. Searching for
  `<struct name=` will not find it. Nibble 1 is `Dynamic Vertex Size`, not a position
  offset, and the attribute flags are 12 bits wide, not 16.
- **A skinned shape holds its weights three times, and a limit on one is not a limit on
  the others.** `NiSkinData\Bone List` holds the authored binding; the partition's rows
  and the vertex buffer hold what the renderer draws, four influences each, normalised.
  Four is *their* limit. `NiSkinData` regularly names more — over a 3,000-mesh sample it
  names a bone that no partition of the same shape renders on 4,319 vertices, and
  `nordcuirassm_0.nif` weights vertex 1728 with five bones while rendering four.

  Enforcing four before writing any copy therefore drops authored influences, and then
  renormalising the survivors moves *every other weight on that vertex* — so bones that
  lost nothing still come back wrong, which is what makes it hard to read. `dragon.nif`
  changed 32 of its 86 bones this way. Each copy caps and rescales itself as it is
  written; nothing should cap the skin ahead of them.

  Two things make this hard to see from a probe. A vertex in two partitions legitimately
  carries four from each, so counting influences per vertex *globally* conflates the two
  causes and reports maxima of five and six that are neither wrong nor over the limit.
  And a partition's `Bones` list is not the answer to "which bones weight this vertex" —
  `dragon.nif`'s first partition lists 53 of them. The per-vertex answer is in the
  partition's own `Bone Indices` and `Vertex Weights` rows.

### 2.6 Carrying blocks FBX has no place for

`Nif/NifFieldCodec.cs` flattens any block subtree to name/value text and reads it back,
walking both directions in declaration order so the names line up by construction. It
takes a skip predicate and an optional link callback.

Used by constraints and particle systems. If you need to carry something else, use it
rather than writing a third walk.

Links are never carried as values — a block index means nothing once exported. Carry
the *name* of what it pointed at, and resolve by name (§4.4).

---

## 3. The FBX layer

### 3.1 Shape of the format

An FBX is a flat pool of objects plus a list of connections. Nothing about the
hierarchy is implied by object order.

- `OO` connections are object-to-object: parent/child and ownership. Written
  **child first**: `C: "OO", child, parent`.
- `OP` connections are object-to-property: an animation curve driving a named property.

`Fbx/FbxScene.cs` wraps this. `scene.AddObject`, `scene.Connect`,
`scene.ConnectToProperty`, `scene.ChildrenOf`, `scene.OfClass`. Call `scene.Flush()`
before saving — it writes the objects and connections back into the document and
updates the `Definitions` counts, which importers use to size their pools and which
will silently drop objects if wrong.

### 3.2 MeshIO quirks

`External/MeshIO` is a vendored FBX reader/writer with rough edges already worked
around. Do not undo these:

- **Writing ASCII is unsupported.** Its ASCII writer emits `\x01` for booleans, which
  its own parser rejects. `FbxDocument.Save` throws for ASCII deliberately. Reading
  ASCII works.
- **FBX's one-byte boolean is property type `C`, which MeshIO models as `char`.**
  Pass `(char)1`, not `true`, or the save fails.
- `CreationTimeStamp` is required or MeshIO rejects the document.
- **A polygon index is not a triangle index.** MeshIO gives polygons; a NIF holds
  triangles, and `FbxMeshReader` fans an n-gon into several. Anything reading a
  per-polygon layer element has to come back through `MeshGeometry.TrianglePolygons`
  rather than indexing the triangle list. Degenerate triangles are dropped during the
  fan, so the two lists drift even for a mesh that was all triangles going in.
- **A mesh has one material layer, not several.** Every mesh gets an `AllSame`
  `LayerElementMaterial` pointing at index 0; `AddPerPolygonMaterialElement` *replaces*
  it rather than adding beside it, because two would leave the `Layer` record naming
  one of them arbitrarily. The `BSLODTriShape` levels are the only user of it — see
  §5.2.4 of the conversion spec for why the levels ride there and are resolved by
  material name rather than by index.

### 3.3 Animation

Four levels, and the binding is by connection, not containment:

```
AnimationStack  (the take)
  AnimationLayer  ("Default")
    AnimationCurveNode  -- OP --> Model."Lcl Translation"
      AnimationCurve    -- OP --> curve node."d|X"
```

- Vector properties are addressed by axis: `d|X`, `d|Y`, `d|Z`.
- Scalar properties are addressed by their own name: `d|<property>`.
- The declared property **type** is load-bearing and is the only record of what a track
  means: `bool`, `ColorRGB`, `Visibility`, `Number`. A colour read back as a scalar
  loses two of its three channels with nothing looking wrong.
- Key times are `KTime`, 46186158000 units per second.
- Key attribute arrays are run-length encoded against the keys.

Miss a connection and the file loads with every stack, layer and curve present and no
animation at all.

### 3.4 Custom properties as a carrier

`Properties70` entries with the `U` (user) flag are how foreign data rides through FBX.
Blender surfaces them as custom properties, Maya as extra attributes. This is what
constraints, particle systems and flipbook controllers use.

Conventions already established, worth following:

- A marker property names the block type and identifies the node's role
  (`constraint_type`, `particle_system`, `particle_modifier`, `particle_collider`).
- Field properties are named from the NIF field, lowercased with spaces to underscores
  (`NifFieldCodec.Key`).
- A link is carried as `<field>_ref` holding the target's **name**.
- Structure the tree already expresses is *not* carried. Naming it too would give two
  sources for one fact.

---

## 4. Conversion conventions

### 4.1 Matrices and units

- **NIF matrices are row-vector.** `NifTransform.Apply` computes `p * M + t`, so a
  matrix's *rows* are the images of the basis vectors. Getting this backwards mirrors
  every rotation, and it has happened twice.
- `NifTransform.ToEulerDegrees()` is the one rotation convention — FBX XYZ degrees.
  Anything animating a rotation must use it, or an animated node jumps the moment its
  first key takes effect.
- `NifTransform.RotationFromQuaternion` is the transpose of the usual column-vector
  form, for the same reason.
- `ShapeTessellator.BhkScaleFactor` is `69.99125`: Havok works in metres, the rest of a
  NIF in Skyrim units. Multiply going NIF to FBX, **divide** coming back — and mean it.
  ck-cmd multiplies by a literal `0.01428` instead, which is not the reciprocal, and the
  code followed ck-cmd here while this line said otherwise. The pair keeps 99.9475% of
  every coordinate, which is invisible on a small shape and three hundredths of a unit on
  a large one, so no collision geometry survived a round trip and no fixture could show
  it. `BhkScaleFactorInverse` is now `1f / BhkScaleFactor`.

**One deliberate exception.** Constraint attachment points carry the *transpose* of the
joint frame, because that is what ck-cmd writes and its importer inverts on the way
back. See `hkx-constraint-spec.md` §1.2 and §3.2. Do not "fix" it.

### 4.2 The tangent frame is carried, and used not to be

A tangent space an FBX arrives with is read by `FbxMeshReader` and kept. `TangentSpace`
generates one only for a mesh that has none — never over one that does — and only when the
geometry carries no `nif_shape_no_tangents` marker saying the NIF it came from had none
either. So a DCC export with normals and UVs and no tangent layer gets a frame built for
it, and a NIF that shipped without one does not gain one: `Bitangent X` and `Unused W`
share a slot, and a shape that gains a frame loses that word.

It used to be otherwise: `FbxToNif.BuildShape` called `TangentSpace.Generate` for every
mesh with UVs, and that method clears both arrays first, so a frame that had been read,
carried correctly through the vertex weld and matched to the right vertices was thrown
away at the last step. ck-cmd never did this: its `GenerateTangentsDataForAllUVSets()`
defaults to `pOverwrite = false`, which the SDK header defines as "left untouched if
exist".

**This is the one to remember, because the reasoning that kept it was checkable and
nobody checked it.** The note that used to sit here said the corpus sweep was blind to
the regeneration, on the grounds that neither converter writes tangents *into* an FBX so
every round trip regenerates at both ends. Both halves are false: `FbxMeshWriter` emits
`LayerElementTangent` and `LayerElementBinormal`, and `FbxMeshReader` reads them. Far
from being blind to it, the sweep was reporting almost nothing else — the regenerated
frame was the bulk of the residue, and it had been filed as vertex *welding* because the
field names it moved (`Triangles`, `Index`, `Weight`) looked like renumbering.

The measurement that settled it, and the shape of measurement worth copying: take a
vanilla mesh, round trip it, and compare the `NiSkinPartition` vertex rows **component by
component** rather than row by row. A whole row differs if any one component does, which
is what made this look like reordering. Split out, on `giant01`, `hmdaedra`, `dragon` and
`dragon_purple`: `Vertex`, `UV`, `Normal`, `Vertex Colors`, `Bone Weights`,
`Bone Indices`, `Unused W` and `Eye Data` were exact on all 31,684 vertices, and
`Tangent` and `Bitangent X` differed on all 31,684. Bethesda's tangents are not
NifSkope's.

The handedness question this note used to leave open — whether a preserved FBX tangent
needs the swap ck-cmd applies to SDK-convention vectors — is answered by the same
measurement: carried straight through, the frame comes back bit-exact against vanilla.

What is still deliberate is that a mesh arriving *without* tangents is not given any.
nif.xml puts `Bitangent X` and `Unused W` in the same slot and picks between them by the
Tangents flag, so introducing a frame moves every offset in `Vertex Desc` and loses
whatever that word held. Recorded in `fbx-nif-conversion-spec.md` §5.3.1.

### 4.2a Comparing against ck-cmd, in practice

ck-cmd runs under wine and is worth reaching for when a question is "what does the tool
we follow actually do":

```
wine /home/ecanepa/Dev/ck-cmd/bin/ck-cmd.exe exportfbx <nif> -e .
wine /home/ecanepa/Dev/ck-cmd/bin/ck-cmd.exe importfbx <fbx> -e .
```

**It only reads Skyrim LE.** Hand it an SE mesh and it does not fail — it writes an FBX
of null nodes named after the NIF's own block types (`NiSkinData`, `NiSkinPartition`,
`BSLightingShaderProperty`), and importing that back gives a file of `NiNode`s with no
geometry at all. That looks exactly like a converter that lost the mesh, and it is really
ck-cmd not knowing `BSTriShape`. Convert to LE first — `FbxToNifOptions.LegendaryEdition`
does it — and feed both tools the same LE file.

Done that way, on `giant01`, `hmdaedra`, `dragon` and `dragon_purple`: this port returns
every vertex and triangle exactly, and ck-cmd **splits** vertices — +75, +198, +293 and
+303 — while keeping triangle counts. Neither tool welds. If a difference is ever again
diagnosed as welding, measure before believing it.

### 4.2b A skin partition's triangles are in the shape's numbering

Not the partition's. nif.xml says what `Vertex Map` is for in as many words: it "maps the
weight/influence lists in this submesh to the vertices in the shape being skinned" — the
weights, not the faces. The partitions divide the vertices for bone influence and slice
the triangle list, and both halves go on naming the shape's own vertices.

`NifToFbx.ReadSkinnedGeometry` has known this since the prisoner-rags investigation and
has the evidence in a comment beside it. `NifSkinWriter.WriteOnePartition` did not: it put
every triangle through the map on the way out, so the two halves of the round trip
disagreed and a rebuilt multi-partition mesh addressed the map instead of the shape. That
is a broken mesh, not a fidelity difference.

Vanilla settles which is right, and one measurement is enough:

| mesh | partition maps | triangles reach | shape has |
| --- | --- | --- | --- |
| `0000282d` part 0 | 108 vertices | index 878 | 996 vertices |
| `hair13` part 1 | 142 vertices | index 963 | 964 vertices |

Neither is a local index into a map that small.

**Why nothing caught it, which is the part worth remembering.** Every skinned fixture in
the suite has a single partition whose map covers the whole shape — and there the
partition's numbering and the shape's are the same numbering, so the bug is invisible. The
one test that did build several partitions asserted the writer's own convention rather
than the format's, and so agreed with the bug rather than catching it. A test written
from the code it tests will do that.

If a rule only shows itself when two things differ, the fixture has to make them differ.
A single-partition skin cannot tell local numbering from global, in the same way a cube
with equal sides cannot tell width from height.

**Still open: the `Vertex Map` order itself.** Ours is a `SortedSet`, so always ascending.
Vanilla's is ascending in about a ninth of partitions, first-reference order over the
partition's triangles in about half, and neither in the rest — measured over 998
partitions in 1,200 meshes. There is no single rule to copy, and the order carries no
meaning: it is an indirection, and any permutation renders the same provided the weights
are written in terms of it consistently. If it turns out to matter for the comparison,
that is the same ruling as the skin bone list — the contents have to be right and the
order does not — and it belongs in the comparer, not the writer.

### 4.2c A mesh emitter needs a second copy of its geometry

`BSTriShape` carries the vertex buffer the renderer draws and, beside it, a plainer copy
that a `NiPSysMeshEmitter` scatters particles over — `Particle Vertices`,
`Particle Normals`, `Particle Triangles`: positions, normals and faces, no tangents, no
UVs, no skinning. Nothing wrote it, so every rebuilt emitter mesh had
`Particle Data Size` zero and the particle system had no surface to emit from.

None of it is carried, because none of it is a choice:

- **Which shapes get it** follows from the block graph. Over 8,080 shapes in a 3,000-mesh
  sample, all 76 with particle data are named by a `NiPSysMeshEmitter` and not one of the
  8,004 without it is. No exceptions either way.
- **How big it is** follows from nif.xml, which states the formula:
  `calc="(Num Vertices #MUL# 6) #ADD# (Num Triangles #MUL# 3)"` — six words a vertex for
  position and normal, three a triangle.
- **What is in it** is the shape's own geometry, at half precision.

**The rounding trap, which cost longer than the feature.** The copy is `HalfVector3`.
Vanilla rounded it once, from the authoring data. A rebuild can only round what the
vertex buffer holds, so comparing the two copies compares one rounding against two and
they disagree in the last digit — for no reason either side can fix. The comparison has to
go back to the thing the copy is a copy of: the source's own full-precision vertex,
transformed and rounded once. Then it lands exactly.

The general form, worth keeping: **when two values are derived from a common original at
different precisions, compare each against the original, never against each other.**

### 4.2d Two routes rebuild controllers, and each thinks the other owns the fields

A controller reaches a rebuilt file by one of two routes, and which one decides what
survives:

- **the structural carrier** (`FbxNodeControllers`), for a controller holding no keys —
  it writes the whole block, field by field, through `NifFieldCodec`;
- **the animation route** (`NifAnimWriter`), for a controller a sequence drives — it
  rebuilds the block from the track, and a track carries keys.

Anything a class declares beyond `NiTimeController` therefore survives the first route
and is lost on the second. `BSPSysMultiTargetEmitterCtlr` came back with `Max Emitters`
zero against files holding anything from 2 to 99.

`NifAnimWriter` already expects a carrier to exist for this — its comment says it looks
for a controller "rebuilt by a carrier that owns more of it than its keys" before making
one — but only the flipbook had one written. `FbxNodeControllers.WriteAnimatedFields`
and `ReadAnimatedFields` are that carrier for every other class, asked of the schema
rather than listed, and applied after the animation because until then the controllers do
not exist.

**Look for this shape whenever two routes can produce the same block.** Each will handle
the part it knows about and assume the other covers the rest.

### 4.2e Links are not carried, and mostly should not be

A `Ref` or `Ptr` means nothing outside the file it was written in, so no carrier writes
one. That is right, and it means every link has to be re-established on the way back —
by name, or by derivation from the graph. The master particle system needed both halves
of one relationship restored and neither was:

| link | restored by |
| --- | --- |
| `BSMasterParticleSystem` → `Particle Systems` | every `NiParticleSystem` below it, in tree order — 93 masters of 93 across the whole game |
| `BSPSysMultiTargetEmitterCtlr` → `Master Particle System` | the file's only master — 142 of 142, and no vanilla file has two |

Both were measured over all 22,047 meshes before being relied on, and the second is
guarded: a scene with more than one master is left alone rather than guessed at.

Prefer derivation to carrying when the graph already says the answer. It cannot go stale,
it costs no property, and it works for a scene authored in a DCC that never had the
carrier on it.

### 4.2f A tree's skin is partitioned per level of detail

Trees are the only meshes in the game that use this, and everything odd about their
skinning follows from one fact: **a tree is skinned once and sliced per LOD**, and each
slice gets its own share of the bone list.

`treepineforest05` has three partitions at levels 1, 2 and 0, and a bone list of nine
entries for four distinct bones — `[Trunk] [Trunk, C02, C04, Mid01] [Trunk, C02, C04,
Mid01]`, one set per level. Over the whole game: of 26,940 skins, **72 name a bone more
than once, and those 72 are exactly the 72 with a partition above LOD 0**. The
correlation is perfect in both directions.

Three things follow, and each was wrong in its own way:

| | why it could not be derived |
| --- | --- |
| `LOD Level` | nothing in the geometry says which slice is which level |
| which entry a cluster is | name and bind pose collapse, because a tree repeats a bone at the *same* pose in two levels |
| how many entries there are | not one per cluster either — `treepineforestash01` has 22 clusters against 19 entries |

All three now travel: the level on the skin deformer that stands for the partition, and
the entry index on the cluster. No new mechanism was needed for the first — the deformer
already carried the partition's index and its body slots.

**Two traps worth keeping.** Restricting the entry-carrying to "skins that repeat a bone"
misses every tree, because a tree's clusters each name a *distinct* entry — the repetition
is of bones across entries, not entries across clusters. And the vertex buffer maps a skin
bone to a written one; doing that by name sends every reference to the first entry of that
name, which is one level's bones moving all three levels' vertices. Walk the two lists
together and use the name only as a check.

**The partitions do not share vertices, which is the thing to know.** It is tempting to
assume the LODs overlap in the shared vertex buffer; they do not. `treepineforest05` holds
274 vertices, and the three partitions take 0–51, 52–107 and 108–273 — disjoint ranges,
every vertex in exactly one partition, each level a separate copy of the geometry
referencing only its own slice of the bone list. Assuming otherwise sent one investigation
down a blind alley.

What was left after the entries were carried was two encodings and one real bug:

- **A partition's bone list may be padded with repeats.** `treepineforest05`'s LOD 1 lists
  `[0,0,0,0]` for one distinct bone. There is no rule: partition bone lists across the
  game run 4, 5, 6 … 59 entries.
- **An unused influence slot's bone index is arbitrary.** Of 1,140,976 slots carrying no
  weight, 873,289 hold zero, 267,253 repeat the first index and 434 hold something else.
  A slot with weight zero is ignored, so none of it means anything.
- **`Num Vertices` on a switched-off skin-data entry was being invented**, which was real
  and is fixed — see below.

Both encodings are inert and neither is reproducible; the comparison already passes over
them.

### 4.2g A count beside a switched-off array says whatever the file likes

`NiSkinData`'s `Num Vertices` sits next to a `Vertex Weights` array that
`Has Vertex Weights` can switch off, and nif.xml makes only the array conditional. So the
count is written either way, and there is no derivable answer for what it should hold.

The writer chose one, from a single fixture: it wrote the number of weights the bone
actually has, reasoning that the renderer's copy still honours the count. The game
disagrees — of 26,913 `NiSkinData` blocks, the 108 clearing the flag have every count at
zero, without exception — so every rebuilt tree wrote 52, 56 and 166 where the file said
nothing.

Writing zero instead would have been choosing again in the other direction, since nifly's
`TestNifFile_Skinned_NoNiSkinDataWeights` says 76 and 60 and has to come back unchanged
too. **Where two inputs disagree and neither number is derivable, carry it rather than
picking a winner.** The fallback is what the game writes, for a scene that never stated
one.

Worth noticing how this was found: the test asserting the old behaviour asserted it *from
the fixture it was written against*. A test written from one file records that file, not
the format.

### 4.2h Two accepted gaps that interfere with each other

The skin partition's `Vertex Map` order is an accepted gap (§4.2b): the order carries no
meaning, and ours is ascending where vanilla's follows no reproducible rule. Everything
inside a partition is indexed by that order — `Vertex Weights`, `Bone Indices`, the lot.

So **a partition's rows cannot be compared row by row** once the two sides order their
maps differently: row 823 on one side is a different vertex from row 823 on the other.
`hair13` looks as though two vertices have swapped weights, and they have — the vertices
are in different places in the two maps.

This matters because the self-contradictory-weight exception (§ commit "Excuse a cached
weight only where the file contradicts itself") reaches the authored weight through the
*source's* map and vertex index, then compares it against *our* value at the same row.
Where the orders agree that is exact; where they do not, it is comparing two different
vertices and its verdict means nothing either way.

The residue is small — 15 meshes in 2,000, at one to six weights each — but it is not
"nearly right weights". It is an artefact of comparing across two orderings, and the way
to settle it is to translate both sides through their own `Vertex Map` and compare in
global vertex numbers, as was done for the partition triangles. Until then, do not read
these counts as a measure of weight accuracy.

**The general lesson: an accepted gap is not inert.** Anything indexed by the thing you
excused is now unsafe to compare positionally, and a later exception written in terms of
that index inherits the problem quietly.

### 4.2i2 Compare against what the writer does, not against the file's other copy

A skinned mesh states its weights twice — `NiSkinData` holds what was authored, the
partition holds the four-slot copy the renderer reads — and **the two disagree in real
files**. `hair13` has 44 cached weights contradicting its own authored ones, `0000cfbd` has
210, `falmervampireferal` has 3,425. A rebuild has one of the two to work from, so where
they disagree it cannot match both.

Two wrong framings were tried before the right one, and both are instructive:

1. **"Renormalisation of vertices with more than four influences."** Wrong: `hair13`,
   `0000cfbd` and `torso05_1` have *no* vertex with more than four influences at all. One
   count refuted it.
2. **"Then it must be a vertex shared between partitions"** — a NIF row holds four, so five
   authored influences would mean two partitions. Also wrong: of the 141 five-influence
   vertices in `falmervampireferal`, **zero** are in more than one partition. The file
   simply authors more than a row can hold.

The right question is not "does our weight equal the authored one" — it equals it only when
the authored weights already total one and number four or fewer. It is **"did the writer do
what it says it does"**: keep the four heaviest and scale them to total one. That is
checkable, and checking it took the sample from 18 divergent meshes to 10.

What genuinely cannot be checked is which four a tool kept when it had to choose.
`falmervampireferal`'s cache keeps `L UpperArm` as a slot at **weight zero** while dropping
a heavier influence, and moves `Spine1` from 0.37286 to 0.48826 — ratios of 1.0846, 1.0949,
1.0498 and 0.9546 against its own authored weights, so no scaling of any kind reaches it.
That decision was taken before the file was written and recorded nowhere in it, so a row the
file was *forced* to trim is passed over. A vertex whose influences all fit is still held to
the scaled value exactly.

**The general rule, which this session reached four separate times:** when a value passes
through a known transformation — a renormalisation, an Euler decomposition, a half-float
rounding, a byte quantisation — compare against the transformation's own output, not
against the input and not within a tolerance. It is exact, it needs no threshold, and it
still fails when the result is genuinely wrong.

### 4.2j Rotation goes through Euler, and that is checkable rather than tolerable

**FBX has no quaternion.** A NIF stores a rotation track as quaternion keys or as three
`XYZ Rotations` groups; FBX has only the second. A node's transform is the same story —
it rides on `Lcl Rotation`, Euler XYZ in degrees. So a rotation is decomposed on the way
out and rebuilt on the way back, and what returns is the same rotation to fewer digits:
`blacksmithforgemarker` sends `(0.500559, 0.501334, 0.499604, -0.498498)` and gets
`(0.500082, 0.499918, 0.500082, -0.499918)`; `boarriekling_varianta`'s node matrix sends
`-9.04376E-05` and gets `-4.37114E-08`.

It reads like precision noise and invites a tolerance. **Don't reach for one.** Put the
source's own rotation through the same two conversions and demand an exact match: a
rotation that came back as a *different* rotation still fails however close, and nobody has
to invent a threshold for how close is close enough. The same technique settles the
particle copy's half-float rounding (§4.2c) and the baked transform's byte-quantised
normals — it is the general answer whenever a value passes through a lossy representation
on a known path.

### 4.2k An unset transform is not an identity one

`NiQuatTransform` marks an unset component with **−FLT_MAX**, and unset is the *normal*
state: of the 17,647 interpolator transforms the game ships, 17,067 have an unset rotation,
15,750 an unset scale and 11,480 an unset translation. The keys supply the value; the
static transform beside them says nothing.

`NiTransformInterpolator` is written that way correctly — 17,642 of those blocks. The
remaining five are `NiLookAtInterpolator`, and they come back with an identity rotation
where the file had the sentinel. Two meshes in a 2,000-mesh sample report it.

**All five are the same shape**, and it is worth stating because it is §6A.2 again — a
thing falling between two routes. Every one is a `NiTransformController` on a
`NiNode` called `PreviewCam`, holding a `NiLookAtInterpolator`, in a file with sequences:

- `FbxNodeControllers.Write` skips it, because a sequence names it and the animation route
  owns those;
- the animation route declines it, because a look-at drives nothing a curve on an FBX
  property can express — which `FbxNodeControllers` says outright a few lines above
  `IsStructural`, noting that it "used to fall between the two routes, carried by neither".

The class survives, so something rebuilds the block; the transform inside it does not, and
no `Transform_Rotation` property is written into the scene at all. Whatever closed the gap
for the interpolator itself did not close it for what the interpolator holds.

Left as it is deliberately: five blocks in 22,047 meshes, against an interaction between
two carriers that would want understanding rather than guessing. Recorded because the shape
of the mistake is worth more than the fix — writing a plausible default where the format
has a way of saying "nothing here" is a different error from writing the wrong value, and
it does not look wrong in a viewer.

**A cylinder refit that was not close**, since fixed and worth remembering how it read:
`sewerentrancecollision01`'s `bhkCylinderShape` is 0.0143 long and 0.4572 across — a disc —
and came back 1.61 long. The cause was in `FitCapsule`, and §6A.2's shape: the degenerate
clamp for "a cloud shorter than it is wide" could never fire, because the two pulled-in
ends were taken through `Max` and `Min` which swap a crossed pair back into an uncrossed
one. For any cloud longer than it is wide the two forms agree exactly, so only flat shapes
ever showed it — and the code contradicted the comment sitting beside it.

### 4.2i A collision shape SSE does not support

`bhkNiTriStripsShape` is the older games' packed-strips collision, and **Skyrim SE does not
read it**. The 31 blocks the game still ships are left over in files that carry a working
shape beside them, so the geometry inside one — `Material CRC`, `BS Data Flags`,
`Has Normals`, the vertex colours, the UV set, the per-strips collision filter — is
vestigial. A rebuild dropping it costs nothing the engine would have read.

That fact came from outside the data, and it is the whole of the answer. Measuring alone
had reached the wrong conclusion: only `Has Normals` follows a rule across the 31 blocks
(1 on every one) while `Material CRC`, `BS Data Flags`, `Has Vertex Colors` and the UV set
all vary, so the numbers said "carry these, they are real". They are real bytes and dead
data, and no amount of counting them would have said so.

**Knowing which parts of a format the engine ignores is not something a corpus sweep can
tell you.** It is worth asking before building a carrier for something.

Excused keyed to the shape, so an `NiTriStripsData` doing an LE mesh's real geometry is
still held to all of it.

### 4.2j Rotation goes through Euler, and that is checkable rather than tolerable

**FBX has no quaternion.** A NIF stores a rotation track as quaternion keys or as three
`XYZ Rotations` groups; FBX has only the second. A node's transform is the same story —
it rides on `Lcl Rotation`, Euler XYZ in degrees. So a rotation is decomposed on the way
out and rebuilt on the way back, and what returns is the same rotation to fewer digits:
`blacksmithforgemarker` sends `(0.500559, 0.501334, 0.499604, -0.498498)` and gets
`(0.500082, 0.499918, 0.500082, -0.499918)`; `boarriekling_varianta`'s node matrix sends
`-9.04376E-05` and gets `-4.37114E-08`.

It reads like precision noise and invites a tolerance. **Don't reach for one.** Put the
source's own rotation through the same two conversions and demand an exact match: a
rotation that came back as a *different* rotation still fails however close, and nobody has
to invent a threshold for how close is close enough. The same technique settles the
particle copy's half-float rounding (§4.2c) and the baked transform's byte-quantised
normals — it is the general answer whenever a value passes through a lossy representation
on a known path.

### 4.2k An unset transform is not an identity one

`NiQuatTransform` marks an unset component with **−FLT_MAX**, and unset is the *normal*
state: of the 17,647 interpolator transforms the game ships, 17,067 have an unset rotation,
15,750 an unset scale and 11,480 an unset translation. The keys supply the value; the
static transform beside them says nothing.

`NiTransformInterpolator` is written that way correctly — 17,642 of those blocks. The
remaining five are `NiLookAtInterpolator`, and they come back with an identity rotation
where the file had the sentinel. Two meshes in a 2,000-mesh sample report it.

**All five are the same shape**, and it is worth stating because it is §6A.2 again — a
thing falling between two routes. Every one is a `NiTransformController` on a
`NiNode` called `PreviewCam`, holding a `NiLookAtInterpolator`, in a file with sequences:

- `FbxNodeControllers.Write` skips it, because a sequence names it and the animation route
  owns those;
- the animation route declines it, because a look-at drives nothing a curve on an FBX
  property can express — which `FbxNodeControllers` says outright a few lines above
  `IsStructural`, noting that it "used to fall between the two routes, carried by neither".

The class survives, so something rebuilds the block; the transform inside it does not, and
no `Transform_Rotation` property is written into the scene at all. Whatever closed the gap
for the interpolator itself did not close it for what the interpolator holds.

Left as it is deliberately: five blocks in 22,047 meshes, against an interaction between
two carriers that would want understanding rather than guessing. Recorded because the shape
of the mistake is worth more than the fix — writing a plausible default where the format
has a way of saying "nothing here" is a different error from writing the wrong value, and
it does not look wrong in a viewer.

**A cylinder refit that was not close**, since fixed and worth remembering how it read:
`sewerentrancecollision01`'s `bhkCylinderShape` is 0.0143 long and 0.4572 across — a disc —
and came back 1.61 long. The cause was in `FitCapsule`, and §6A.2's shape: the degenerate
clamp for "a cloud shorter than it is wide" could never fire, because the two pulled-in
ends were taken through `Max` and `Min` which swap a crossed pair back into an uncrossed
one. For any cloud longer than it is wide the two forms agree exactly, so only flat shapes
ever showed it — and the code contradicted the comment sitting beside it.

### 4.2i An open gap, left open on purpose: collision strips data

`bhkNiTriStripsShape` holds its geometry in a `NiTriStripsData`, and a rebuilt one loses
four things the file had: `Material CRC`, `BS Data Flags`, `Has Vertex Colors` and the
`UV Sets` row. Two meshes in a 2,000-mesh sample report it.

Measured over all 22,047 meshes, there are **31 such blocks in the whole game**, and only
one of the four follows a rule:

| field | across the 31 |
| --- | --- |
| `Has Normals` | 1 on all 31 |
| `Material CRC` | 3741512247 ×18, 0 ×5, and two others |
| `BS Data Flags` | 1 ×24, 0 ×5, 4097 ×2 |
| `Has Vertex Colors` | 1 ×18, 0 ×13 |
| `UV Sets` | one row ×26, none ×5 |

**This is left reported rather than excused, and not half-fixed.** Excusing it would hide
real loss — these are values the file states and the rebuild drops. And setting
`Has Normals` on its own, the one derivable answer, would be worse than leaving it: the
flag gates a `Normals` array the collision path has nothing to put in, so the block would
claim an array it does not carry.

Carrying all four properly is the fix, and it is a carrier for 31 blocks in the game. That
is a judgement about effort, not about correctness, and it should be made deliberately
rather than by quietly adding a baseline line.

### 4.3 Editions

LE and SE are both file version 20.2.0.7 with user version 12. They differ only in the
Bethesda stream version: **83 is LE, 100 is SE**. That one number decides which
geometry block is legal (`BSTriShape` does not exist in LE) and changes
`NiParticleSystem`'s layout. `FbxToNifOptions.LegendaryEdition` selects it.

### 4.4 Resolve by name, and defer

Everything that crosses between blocks is resolved **by name**, not by index: skin
bones, animation targets, constraint entities, particle emitter and gravity objects,
collider objects, flipbook sources. A block index means nothing once exported.

Anything naming a node has to wait until the whole tree exists, because the target may
be a sibling the walk has not reached. `FbxToNif.Convert` runs these passes in order
after the tree is built:

1. collision (`BuildCollisionFrom`)
2. skins (`BuildPendingSkins`)
3. particle links (`ResolveParticleLinks`)
4. constraints (`WriteConstraints`)
5. animation (`WriteAnimations`)

If you add something that names a node, add a pass; do not try to resolve it inline.

### 4.5 Node naming

`Conversion/NameEncoding.cs` escapes characters FBX dislikes: space to `_s_`, `[` to
`_ob_`, `]` to `_cb_`, `:` to `_dd_`. Sanitize on the way out, unsanitize on the way
back.

Suffixes and markers that carry meaning:

| Marker | Means |
| --- | --- |
| `_support` | mesh holder, unwrapped on import |
| `_rb`, `_sp` | rigid body, simple shape phantom |
| `_con_`, `_attach_point` | constraint attachment point |
| `_sphere`, `_box`, `_capsule`, `_mesh`, ... | tessellated collision shape |

A node whose name has been through FBX may have been renamed in a DCC tool, so where a
NIF name is load-bearing it is carried as a property as well — see
`particle_modifier_name`.

---

## 5. Testing

### 5.1 The corpus

`Tests/Resources` holds three sets with different provenance, each with a README
recording its licence:

- root — four meshes this project's tooling produced
- `nifly/` — nifly's corpus, GPL-3.0
- `xpmsse/` — one creature skeleton, MIT, the only fixture with ragdoll constraints

`Tests/CorpusTests.cs` **finds** fixtures rather than listing them, so anything dropped
into `Tests/Resources` is automatically loaded and round-tripped, and
`Tests/RebuildTests.cs` rebuilds each one through the authoring API as well. Both
require the bytes back exactly: descriptors, conditions, array lengths and both stream
directions all have to agree.

These are the fast checks, and they run in the ordinary suite. The thorough ones are
§5.2, and they are where the bugs were.

**Adding a fixture**: drop it in a directory with a README naming its licence, and it
is picked up. Nothing extracted from the game may be committed — every vanilla
`skeleton.nif` is a Bethesda asset. When the corpus lacks something, either find an
openly licensed source or build the fixture in code.

### 5.2 The vanilla sweeps, which are where the bugs actually were

`Tests/BsaCorpusTests.cs` runs the same two checks over **every mesh Skyrim ships** —
22,047 of them, read in place out of `Skyrim - Meshes0.bsa` and `Meshes1.bsa` through
the Mutagen reader the project already depends on:

| | What it proves |
| --- | --- |
| `EveryVanillaMeshSavesBackByteForByte` | the reader and the serialiser agree |
| `EveryVanillaMeshRebuildsByteForByte` | the **authoring** path: `InsertBlock`, `SetArraySize`, conditions, header, string table |

Both pass, both are byte-exact, and both are skipped unless asked for:

```
SECMD_SKYRIM_DATA="/path/to/Skyrim Special Edition/Data" \
    dotnet test --filter "FullyQualifiedName~BsaCorpus"
```

They take about sixteen minutes together; `SECMD_BSA_SAMPLE=N` gives a quick version. A
path that is set but wrong fails rather than skipping, because somebody asked.

**Run them after any change to `Nif/`.** Every bug listed in §2.5 that is not marked
otherwise was found by these and by nothing else — twenty-four fixtures chosen for the
features they demonstrate cannot tell you what the hundredth-most-common block looks
like in the wild.

The second sweep is the more valuable of the pair: it drives the path `FbxToNif` uses.
Its helper, `RebuildTests.Rebuild`, copies a model block by block through the public API
and is worth reaching for whenever you need "the same file, built rather than read".

### 5.3 Synthetic fixtures

Most of what has been added recently has no corpus coverage and is built in the test:
colour controllers, flipbook controllers, mesh emitters, collider chains, ck-cmd-style
constraint scenes, >60-bone skins. `ParticleLinkTests` and `ColorAnimationTests` are
good models for how.

Build fixtures with values that cannot pass by symmetry. A colour whose three channels
differ, a rotation with all three axes involved, a frame whose axes are distinct.

### 5.3a Prefer a check that proves the gap to one that names it

`RoundTripBaseline` lists the fields a round trip is allowed to differ in. Every entry is
a hole: it is keyed by field *name*, so it excuses that field everywhere, including where
the difference has nothing to do with the reason recorded beside it.

`Vertex` and `Normal` were listed for the baked shape transform (§2 of the conversion
spec: an unskinned shape's transform goes into its vertices and the node's is reset). The
entry was true and the fields still moved for that reason — but any *other* geometry loss
in any shape in the game was excused by the same two lines, and the tangent frame's own
blanket entry hid the same displacement a second time.

`NifComparer.BakedTransformExplains` replaces all of it: it applies the transform the
exporter bakes — the shape's own when unskinned, identity when skinned, exactly what
`NifToFbx.BakedTransformOf` does — and requires the result to match **exactly**. On
`TestNifFile_OrderedNode_SE` and `TestNifFile_DeepGraph_SE` the error is 0 on every
vertex of every shape. Nothing else passes now, which is the point.

Two things it took to get right, both worth remembering:

- **Compare through the field's own encoding.** A normal and a tangent are `ByteVector3`,
  three signed bytes, not `Vector3`. The first version of the check guarded on
  `NifValueType.Vector3` and so did nothing at all for the two fields it existed for —
  and it did nothing *silently*, the tests simply still failed. Turn the expected vector
  back into a `NifValue` of the field's type before comparing, or rounding alone defeats
  it.
- **The bitangent is not a field.** It is three lanes — a float beside the position and
  two bytes beside the normal and tangent — so it has to be reassembled before the
  rotation and re-quantised after.

The general form: when a difference is explained by a transformation, apply the
transformation and demand equality. A named exclusion cannot tell the explained case from
the unexplained one.

### 5.4 What tests are for here

A test comment says **why a failure matters**, not what the code does. "An emitter that
lost its emitter object emits from the origin, and neither shows up as anything but the
effect being wrong" is the useful form. Several bugs in this codebase were invisible
except through their consequences, and the tests are where that is recorded.

---

## 6. Diagnosing a byte difference

Three techniques that worked, in the order to reach for them.

**An implausible array size means the block *before* it.** Reading is sequential, so a
block written or read short leaves the stream misaligned and the error surfaces
wherever the next plausible-looking length happens to be. *"failed to read block 12
(BSDismemberSkinInstance): array Bones has implausible size 20938752"* was a short skin
partition three blocks earlier; seventeen `bhkNiTriStripsShape` failures were all
`NiTriStripsData` immediately before them. Do not start where the exception points.

**Trees equal but bytes different means an encoding with two spellings.** Load the file,
save it, load your own output, and walk the two item trees in lockstep comparing values.
If nothing differs, no value was lost — something re-encoded. That is how the half-float
NaN was found: every value compared equal and one byte in the file did not.

**Check layout arithmetic against a synthetic block, not against a hex dump.** Insert
the block into an empty model, set the counts, call `UpdateHeader`, and read its entry
in the header's `Block Size` array. That gives the size the writer believes in, which
you can compare with the sum of the field widths nif.xml declares. It is faster than
locating the block in a file and immune to the arithmetic slips that make hand-decoding
unreliable.

---

## 6A. Five mistakes this format invites, all made repeatedly

Neither is subtle once named, and both have been made half a dozen times here, so it is
worth checking for them by reflex rather than rediscovering them.

### 6A.1 A list addressed by name, compared by position

A NIF is full of lists whose entries carry their own identity — a bone, a block, a named
target — and whose order is bookkeeping. Compared by index, a list holding the same
things in another order reads as *every entry being wrong*, which is both a false failure
and a very loud one: it buries the real differences underneath it.

Seven so far, each found the same way and fixed the same way, by aligning on what the
entry says it is:

| List | Identified by |
| --- | --- |
| `NiSkinInstance` bones | the bone node |
| `Extra Data List` | block class and name |
| `NiDefaultAVObjectPalette` `Objs` | the entry's own name |
| `NiSkinPartition` `Partitions` | the vertices in it |
| `NiSequence` `Controlled Blocks` | the target it drives |
| `NiSkinData` `Vertex Weights` | the vertex each weight names |

Before adding an alignment, **measure that it really is a permutation** — that the two
sides hold the same entries and only the order differs. Every one of these was checked
that way first, and the check is what distinguishes an ordering difference from data
genuinely going missing. The skin weights, for instance: 6 bones reordered, 11 identical,
none actually different.

Match whole, not entry by entry, so a list that really does hold different things still
fails instead of being paired up somehow.

### 6A.2 A fact learned on one side of the trip and not the other

The reader and the writer are two descriptions of the same format, and it is easy to
correct one and leave the other saying the opposite. The costs are asymmetric: the reader
being wrong shows up as a bad conversion, and the writer being wrong shows up as nothing
at all until a real file disagrees.

The worst case so far: `NifToFbx` established that a skin partition's triangles are in
the shape's numbering, wrote the evidence into a comment, and `NifSkinWriter` went on
putting them through the vertex map for as long again. Every rebuilt multi-partition mesh
drew the wrong vertices, and the spec's own §7.3 entry repeated the writer's belief back
as fact.

The same shape appears in smaller ways throughout: a `Min` fixed without its `Max`, a
`Num Bones` without its `Bones`, one shader field of twelve. **When a fact about the
format is established, go and find every place that states it** — the reader, the writer,
the comparer, the baseline, and the specs.

### 6A.3 A form inferred from the values it is written in

A NIF states what kind of thing something is and then states the numbers for it. It is
tempting to skip the first and read the kind off the second — there is no separate field
to carry, and the numbers are right there. It works until the numbers are all zero, which
is always allowed and usually means something.

The one that got through: a key group's type. A NIF key group is linear, quadratic, TBC,
XYZ or constant, and the conversion decided between quadratic and TBC by asking whether
any tension or any slope was non-zero. A TBC group with a tension of zero means a flat
handle, not an absent one, and it came back quadratic — a curve through the same points
with a different shape between them. Five key groups on `dlc01sebf_blastroof`, which the
comparison was excusing (see below).

The same shape, elsewhere: a transform that is unset versus one that is the identity
(§4.2k); a count beside a switched-off array (§4.2g). In each, the absent case and a
legitimate present case have the same bytes, and only the declared kind separates them.

**The rule: carry the kind, do not derive it.** Where the destination format has the same
distinction, carry it as that — FBX had `eTangentTCB` against `eTangentUser` the whole
time, and a tangent mode is set whether or not the floats under it are zero. Where it
does not, carry it as an extra property rather than reconstruct it.

**And check what the comparison says about it.** This was invisible for as long as it
existed because `RoundTripBaseline` excused every `Interpolation` difference, on the
strength of a reason — "0 is not a KeyType; an unset one becomes LINEAR_KEY" — that is
about one value and was keyed on the field. An excuse whose reason names a value must be
guarded to that value, or it excuses the whole field. Entries may now name a guard;
several of the remaining ones are directional in the same way and are not yet guarded:
`Mass`, the `Unused` padding, `Children` and `Num Children`, `Extra Targets`.

### 6A.4 A skin states its weights three times, and the partition is the one that counts

`NiSkinData` holds them per bone, unbounded; the partition holds them per vertex in four
slots; the vertex buffer holds the same four for the GPU. The last two agree exactly --
0 differences over 1,070,617 rows of a 3,000-mesh sample -- and `NiSkinData` disagrees
with both on about 0.25% of vertices.

**Read the partition.** It is what the renderer samples and what ck-cmd takes
(`FBXWrangler.cpp:1093`, using the bone list only for `skinTransform`), and
`NiSkinData` is rebuilt from it. The bone list is still read for each bone's skin
transform, which is stated there and nowhere else, and for whether it carries weights at
all -- a few dozen files keep theirs out of it deliberately and must come back that way.

Things that are true and were each learned the hard way:

- **`NiSkinData` is not bound by four influences.** It stores per bone, so a vertex may
  appear in any number of bone lists; about 2,000 vertices per 3,000 meshes are authored
  with five to eight. The four-slot limit is the partition's and the buffer's. "NIF only
  supports 4 influences" is true of what ships and false of what is authored.
- **A vertex over four is trimmed to the heaviest four and renormalised**, and that
  reproduces the shipped row for ~97% of them. The rest is a decision the exporter took
  and did not record.
- **The four slots of a row are a set, not a sequence.** Vanilla leaves gaps -- a zero
  between two influences -- and a rebuild that packs them reports every such row twice,
  once as a weight appearing and once as the same weight vanishing. This is §6A.1 one
  level below the rows, and it hid behind two wrong explanations before being found.
- **A bone with no weights is still part of the skin.** Dropping its FBX cluster drops
  the bone.
- **Bones are identified by their node, never by their name.** FBX cannot hold two
  objects of one name, so a second `Bone01` returns as `Bone01#1` while its NIF node is
  still `Bone01`. Matching the strings mapped nothing and wrote a whole vertex buffer of
  zeroes -- a mesh that loads and cannot move.

When measuring the two copies against each other, **separate absence from
contradiction**: a skin whose `NiSkinData` is empty is the documented fallback, not a
disagreement, and counting it as one turns 0.25% of vertices into "92% of meshes".

### 6A.5 A name is not an identity, and a NIF's names repeat

Node names are not unique in a NIF and nothing enforces that they should be. `tfxbloodshirt`
names three nodes `NPC L UpperarmTwist1 [LUt1]`; `signwindpeakinn01` hangs two collision
bodies off two nodes both called `c_Post`; `rootthornhookactivator` gives its root's name
to an `NiNode` beside it; `norsecrmsmdoorsm02` has two `Amulet01`.

Any map keyed by name therefore collapses, and whichever entry wins is an accident of
walk order. Four bugs came out of this in one day, none of them cosmetic:

| Bound by name | What went wrong |
| --- | --- |
| a skin's bones | the mesh bound to the wrong bone, a hundredth of a degree away |
| a constraint's entities | a sign's hinge joined to the wrong post |
| a sequence's extra targets | `Door Left` swapped for `DoorRight` |
| a vertex-buffer bone map | a whole buffer written as zeroes, on a mesh that could not move |

**Bind by the block, not by its name, wherever the block is in hand.** `_built` maps a NIF
block to the FBX object it became; that is the identity, and it is available at every
export site. On the way back, an FBX object's name *is* unique within the file, so the
name is safe there — the danger is only on the way out.

**Where the format itself addresses by name, this cannot be fixed and should not be
attempted.** A sequence's controlled block names its target as a string and the game
resolves it through a `NiDefaultAVObjectPalette`, which holds one entry per name. The
second node of a repeated name is unreachable by design. Two attempts to route around
that -- reading identity from the controlled block's own controller, and carrying the
target list as names -- broke 28 fixtures and 7 meshes respectively. The format's
ambiguity is the file's, not the converter's.

## 7. Working habits for this repo

- **Atomic commits.** One layer at a time, committed as it lands, not batched.
- **No `Co-Authored-By` trailers.**
- Commit messages explain *why*, in prose. Look at `git log` before writing one.
- The specs in `docs/` are the record of ck-cmd's behaviour. If you discover ck-cmd
  does something different from what a spec says, **correct the spec** — that has
  happened once already, over whether ck-cmd rebuilds NIF constraints at all.
- Run the full suite before committing. It takes about seven seconds.
- Run the vanilla sweeps (§5.2) before committing anything in `Nif/`. Sixteen minutes
  against 22,047 real files has repaid itself four times over, and every one of those
  bugs was invisible in the fast suite.
