# Particle systems: what the engine does with one

`nif-particle-spec.md` asks what a particle system *holds* and whether FBX can carry it.
This asks a different question: **what the engine does with the graph**, and therefore
which graphs are legal. It is the document to read before writing a particle system
rather than before reading one, and §4 is worked recipes: five of the game's own effects
taken apart, with what each number does to the look of the thing.

Four sources, in descending order of authority:

| | |
| --- | --- |
| **Gamebryo 2.6 source** | `CoreLibs/NiParticle/_Deprecated/` is the `NiPSys*` family itself — the exact classes Skyrim streams — and `NiPSConverter.cpp` states what each one *means* by converting it to the engine's newer particle system. |
| **`nif.xml`** | the field layout, and per-type `Order` defaults that agree with the engine's enum. |
| **The corpus** | 1,704 particle systems in 899 of the game's 22,047 meshes: what Bethesda actually ships, which is narrower than what is legal. |
| **NifSkope, GECK wiki** | conventions and one hard crash rule. |

Where they disagree, the source wins and the disagreement is recorded — §7 does exactly
that to the GECK wiki's claim about controller chains.

---

## 1. The object model

A particle system is a **renderable node** that owns a **data block**, a **list of
modifiers**, and a **chain of controllers**.

```
NiParticleSystem            (a NiGeometry: it draws)
├── Data ─────────────► NiPSysData          the per-particle arrays
├── Modifiers[] ──────► NiPSysModifier…     the simulation, in order
└── Controller ───────► NiPSysEmitterCtlr   the controller chain
                        └── Next ─► NiPSysModifierActiveCtlr
                            └── Next ─► … ─► NiPSysUpdateCtlr
```

Three system classes ship in Skyrim:

| class | systems | what it is |
| --- | --- | --- |
| `NiParticleSystem` | 1,525 | camera-facing sprites |
| `BSStripParticleSystem` | 176 | a ribbon threaded through the particles |
| `NiMeshParticleSystem` | 3 | a mesh instanced per particle |

**The modifiers are the simulation.** The system itself holds no behaviour: it is a
list of steps, each one a block, run in an order the file states.

---

## 2. The slot model — `Order`

Every `NiPSysModifier` carries a `Name`, an `Order`, a back-pointer to its system, and
an `Active` flag. `Order` is the whole of the scheduling model, and the engine declares
the bands as an enum (`NiPSysModifier.h`):

```cpp
ORDER_KILLOLDPARTICLES = 0
ORDER_EMITTER          = 1000
ORDER_SPAWN            = 2000
ORDER_GENERAL          = 3000
ORDER_FORCE            = 4000
ORDER_COLLIDER         = 5000
ORDER_POSUPDATE        = 6000
ORDER_POSTPOSUPDATE    = 6500
ORDER_BOUNDUPDATE      = 7000
```

`nif.xml` carries the same enum with three values Bethesda added:

```
ORDER_BSLOD                =    1     before the emitter
ORDER_WORLDSHIFT_PARTSPAWN = 6600     between post-position and bound update
ORDER_SK_BSSTRIPUPDATE     = 8000     after everything
```

### The engine sorts on load, so the array order is decorative

`NiParticleSystem::LinkObject` reads the modifier links and calls `AddModifier` for each,
and `AddModifier` inserts into a list kept sorted by `Order`:

```cpp
if (pkCurModifier->GetOrder() > pkModifier->GetOrder())
{
    m_kModifierList.InsertBefore(kIter, pkModifier);
```

Two consequences worth stating plainly:

- **The order of the `Modifiers` array in the file does not decide execution order.**
  `Order` does. A file whose array disagrees with its `Order` values runs in `Order`
  sequence regardless, and is written back re-sorted. (Every vanilla file already agrees
  with itself — see §7 — so this never bites until something writes one that does not.)
- **Ties keep file order.** The test is strictly `>`, so a modifier is inserted after
  every equal-ordered modifier already present. Three `NiPSysDragModifier`s all at 4000
  run in the order the array lists them — which is why a file may hold several of one
  class at one `Order` and still be deterministic.

### Which class goes in which slot

`nif.xml` states a default per type, and the engine agrees. In the corpus **every class
uses exactly one `Order` value, without exception** — 22 classes, 1,704 systems, no
variation at all:

| `Order` | band | classes seen in Skyrim |
| --- | --- | --- |
| 0 | `KILLOLDPARTICLES` | `NiPSysAgeDeathModifier` |
| 1 | `BSLOD` | `BSPSysLODModifier` |
| 1000 | `EMITTER` | `NiPSysMeshEmitter`, `NiPSysBoxEmitter`, `NiPSysCylinderEmitter`, `NiPSysSphereEmitter`, `NiPSysSpawnModifier`, `BSPSysInheritVelocityModifier` |
| 3000 | `GENERAL` | `BSPSysSimpleColorModifier`, `BSPSysScaleModifier`, `BSPSysSubTexModifier`, `NiPSysRotationModifier` |
| 4000 | `FORCE` | `NiPSysGravityModifier`, `NiPSysDragModifier`, `NiPSysBombModifier`, `BSWindModifier` |
| 5000 | `COLLIDER` | `NiPSysColliderManager` |
| 6000 | `POS_UPDATE` | `NiPSysPositionModifier` |
| 6500 | `POSTPOS_UPDATE` | `BSPSysRecycleBoundModifier`, `BSPSysHavokUpdateModifier` |
| 7000 | `BOUND_UPDATE` | `NiPSysBoundUpdateModifier` |
| 8000 | `SK_BSSTRIPUPDATE` | `BSPSysStripUpdateModifier` |

**`NiPSysSpawnModifier` sits at `EMITTER`, not at `SPAWN`.** `nif.xml` says so, the
corpus agrees in all 1,704, and `ORDER_SPAWN` (2000) is unused by the game entirely. It
is a slot the format has and Skyrim never fills.

---

## 3. What each modifier means

From `NiPSConverter.cpp`, which is the engine stating the semantics by mapping each old
modifier onto its replacement. The new system has four **simulation steps** — general,
forces, colliders, final — and where a modifier lands says what kind of thing it is.

| modifier | becomes | meaning |
| --- | --- | --- |
| `NiPSysAgeDeathModifier` | *(nothing, plus a death spawner)* | kills particles past their lifespan. If `Spawn on Death` is set, its `Spawn Modifier` becomes the system's death spawner. |
| `NiPSys*Emitter` | `NiPSEmitter` on the system | births particles. Box, cylinder and sphere are volumes; mesh emits from a named object's surface. |
| `NiPSysSpawnModifier` | `NiPSSpawner` | births more particles from existing ones, with generation limits. |
| `NiPSysGrowFadeModifier` | general step's grow/shrink times | scales in at birth and out at death. |
| `NiPSysColorModifier` | general step's colour keys | a colour ramp over life. |
| `NiPSysBombModifier` | a force | an impulse from a point. |
| `NiPSysGravityModifier` | a force | constant acceleration, optionally toward an object. |
| `NiPSysDragModifier` | a force | velocity-proportional damping. |
| `NiPSys*FieldModifier` | a force | air, drag, gravity, radial, turbulence, vortex fields. |
| `NiPSysColliderManager` | colliders on the collider step | owns a list of planar and spherical colliders. |
| `NiPSysBoundUpdateModifier` | the system's bound updater | recomputes the bounding volume, every *n*th frame. |
| `NiPSysMeshUpdateModifier` | mesh particles | the meshes instanced per particle. |
| **`NiPSysPositionModifier`** | **nothing at all** | the converter includes its header and never reads one. In the old system it is the step that integrates velocity into position; in the new one that is implicit. It is present in 1,701 of 1,704 systems and carries no fields — a marker that the position step happens here. |

---

---

## 4. Recipes: building an effect that works

Everything below is read out of a shipped file. The units are the ones the format
uses: **speed in game units per second**, **life span in seconds**, **angles in
radians**, **radii in game units**. A `Variation` field is a symmetric ± range around
its base.

Three things govern the look of any of them, before any modifier is added:

| | |
| --- | --- |
| `Speed` ± `Speed Variation` | how hard particles are thrown |
| `Declination` ± `Declination Variation` | the half-angle of the cone off the emitter's axis — `0.1745` is 10°, `3.14159` is every direction |
| `Planar Angle Variation` | how far round the axis they may go; π means the full circle |

And `Life Span` ± `Life Span Variation` decides how far they get, because nothing stops
a particle but age.

### 4.1 A candle flame — the smallest complete effect

`meshes/mps/mpscandleflame01.nif`, two systems layered:

```
CandleFlame01                            396 particles, 4 sub-texture frames
  NiPSysBoxEmitter    speed 7.5 ±1.8   declination var 0.209 (12°)
                      planar var π      radius 1.25 ±0.16   life 0.333
  BSPSysSimpleColorModifier             fade in and out over life
  BSPSysScaleModifier                   grow over life

CandleGlow01                             320 particles, no sub-texture
  NiPSysBoxEmitter    speed 6 ±1.8                          radius 80
                      life 0.4
  BSPSysSimpleColorModifier
```

The grammar of it: **a narrow, slow, short-lived cone for the flame, and a second
system of one very large soft sprite for the glow.** The glow has no scale modifier and
no sub-texture — it is one big billboard whose only job is to brighten what is near it.
A radius of 80 against the flame's 1.25 is the whole difference.

Note there is no force of any kind. At 0.333 seconds and 7.5 units per second a
particle travels two and a half units; gravity would not be visible if it were there.

### 4.2 A spark fountain — a cone plus a sprite sheet

`meshes/effects/fxsparkfountain.nif`:

```
NiPSysBoxEmitter    speed 300 ±90   declination var 0.1745 (10°)
                    planar var π     radius 2         life 2
BSPSysSubTexModifier  end frame 15, of 42
NiPSysGravityModifier gravity axis (0,0,1)
```

Forty times the candle's speed through a *narrower* cone, and six times the life: that
is what makes a fountain rather than a flame. `Initial Radius 2` keeps each spark a
point.

`BSPSysSubTexModifier` is what makes a spark look like a spark — the sprite is a sheet
of 42 frames and each particle plays frames 0–15 of it over its life. Sub-texture
animation is how Skyrim gets detail out of a billboard, and every convincing effect in
the game uses it.

### 4.3 Isotropic drag is three modifiers, not one

`meshes/effects/fxsteamjet.nif`:

```
NiPSysDragModifier 'NiPSysDragModifier(X-Axis)'   percentage 0.07   drag axis (1,0,0)
NiPSysDragModifier 'NiPSysDragModifier(Y-Axis)'   percentage 0.07   drag axis (0,1,0)
NiPSysDragModifier 'NiPSysDragModifier(Z-Axis)'   percentage 0.07   drag axis (0,0,1)
```

**A `NiPSysDragModifier` damps along one axis only.** Drag in every direction is three
of them, one per axis, identical but for `Drag Axis`, all at `Order` 4000 — which is why
`NiPSysDragModifier` is the commonest modifier in the game at 2,245 instances across
1,704 systems, and why the names carry the axis. They are told apart by name alone, and
each has its own `NiPSysModifierActiveCtlr` so the animation can switch the three
together.

This is the single most copied idiom in Skyrim's effects. If a puff of something should
slow down as it travels, it is three drag modifiers at 0.03 to 0.07.

### 4.4 Gravity's strength is not in the gravity modifier

Of the 615 `NiPSysGravityModifier`s in the game's effect directories, **every one stores
`Gravity Strength = 0`**. 543 have no controller at all, and are inert: they occupy a
slot and do nothing. The other 72 are driven by a `NiPSysGravityStrengthCtlr`, and the
strength exists only in the animation.

So: to make something fall or rise, do not set the field. Add the modifier with a
strength of zero, give it a name, and animate it with a `NiPSysGravityStrengthCtlr`
naming that modifier. A positive strength along `(0,0,1)` lifts — which is how smoke
rises — and a negative one drops.

A converter that writes a static non-zero strength produces a file unlike any Bethesda
ships. One that drops the inert modifiers changes the modifier list an animation may
later name.

### 4.5 A campfire — three systems, one fire

`meshes/clutter/woodfires/campfire01burning.nif` is the pattern for anything that burns:

| system | emitter | life | what it contributes |
| --- | --- | --- | --- |
| `FlamesSmall03` | cylinder r 20, speed 45 ±40.5, radius 16 | 0.47 ±0.13 | the flame: fast, short, wide speed variation so tongues differ |
| `Firearticles` | cylinder r 32, speed 60 ±12, radius 20 | 1.0 ±0.2 | embers: 64-frame sheet, twice the life, tight speed |
| `smoke02` | cylinder r 16, speed 60 ±12, radius 10 | 2.0 ±0.4 | smoke: four times the flame's life, smallest radius at birth, grows |

Read down the life-span column and the recipe is plain: **each layer lives longer and
starts smaller than the one below it.** The flame is brief and broad, the embers persist,
the smoke outlasts everything and expands. All three use a *cylinder* emitter, because
a fire is a disc on the ground rather than a point.

Each carries its own `NiPSysRotationModifier` with `Rotation Speed 0.2618` (15°/s) and
`Random Rot Speed Sign = 1`, so every particle spins slowly in a random direction —
which is what stops a sheet of identical billboards reading as a repeating texture.

### 4.6 The skeleton to start from

Every working system in the game is this, with the middle filled in:

```
NiPSysAgeDeathModifier       order 0      required: nothing else removes particles
BSPSysLODModifier            order 1      required in practice: all 1,704 have one
<one emitter>                order 1000   box, cylinder, sphere or mesh
NiPSysSpawnModifier          order 1000   required in practice: all 1,704 have one
BSPSysSimpleColorModifier    order 3000   fade in and out; 1,666 of 1,704
  … rotation, scale, sub-texture, drag ×3, gravity …
NiPSysPositionModifier       order 6000   required: nothing else moves particles
NiPSysBoundUpdateModifier    order 7000   required: without it the bound never grows
```

plus a controller chain ending in `NiPSysUpdateCtlr`, and an emitter controller carrying
the birth rate. Leave out `NiPSysPositionModifier` and the particles are born and never
move; leave out `NiPSysAgeDeathModifier` and they never die.

---

## 5. Controllers: how one binds to a modifier

A `NiPSysModifierCtlr` targets the **particle system**, not the modifier, and names the
modifier it drives as a string:

```cpp
const char* NiPSysModifierCtlr::GetCtlrID()
{
    return (const char*) m_kModifierName;
}
```

`SetTarget` then resolves that name against the system's modifier list, and
`InterpTargetIsCorrectType` refuses a target that is not an `NiParticleSystem` or whose
modifier name does not resolve. So:

- **The binding is by name, and the name must resolve.** A controller naming a modifier
  the system does not hold is rejected by the engine rather than ignored.
- **`GetCtlrID()` is the modifier name.** This is what a `NiControllerSequence` writes
  into a controlled block's `Controller ID`, and it is how two controllers of one class
  on one system are told apart.

### `NiPSysEmitterCtlr` has two interpolators

Alone among them, it holds a second: `m_spEmitterActiveInterpolator`, `Visibility
Interpolator` in the file. Index 0 is the birth rate; index 1 switches the emitter on
and off. A reader that looks only at `Interpolator` sees half of what the block does.

### `NiPSysUpdateCtlr` must be last

`NiPSysUpdateCtlr::SetTarget` moves itself to the end of the target's controller list,
undoing the base class's prepend:

```cpp
// Ensure that this controller is the last one in m_pkTarget's
// controller list. NiTimeController::SetTarget ensures that this is
// the first controller in the list.
```

It is the step that runs the simulation, so everything that modifies the system must
have run first. A file that puts it elsewhere is silently repaired on load.

---

## 6. Hard rules

Things that are not style:

1. **A null entry in `Modifiers` crashes Oblivion.** NifSkope's sanitize spell removes
   them for exactly this reason (`spells/sanitize.cpp`: "remove empty modifier links
   (NiParticleSystem crashes Oblivion for those)").
2. **A modifier must have a name.** `AddModifier` asserts on it, because the name is how
   controllers find it.
3. **Modifier names must be unique within a system.** `GetModifierByName` returns the
   first match, so a repeat is unreachable.
4. **A controller's `Modifier Name` must resolve**, per `InterpTargetIsCorrectType`.
5. **`NiPSysUpdateCtlr` belongs last**, per §5.

---

## 7. What Bethesda actually ships

Legal and conventional are different, and the corpus says what the convention is.
1,704 systems, 899 files.

### The canonical chain

Every chain in the game is a prefix-and-suffix around a middle that varies:

```
NiPSysAgeDeathModifier          0     always, all 1,704
BSPSysLODModifier               1     always, all 1,704
<one emitter>                1000     box 553, mesh 636, cylinder 369, sphere 146
NiPSysSpawnModifier          1000     always, all 1,704
BSPSysSimpleColorModifier    3000     1,666
  … optional middle: rotation, scale, sub-texture, forces …
NiPSysPositionModifier       6000     1,701
NiPSysBoundUpdateModifier    7000     1,701
BSPSysStripUpdateModifier    8000     the 176 strip systems only
```

The commonest single arrangement, 69 systems:

```
AgeDeath > BSLOD > MeshEmitter > Spawn > SimpleColor > Scale
        > Bomb > Bomb > Position > BoundUpdate > StripUpdate
```

### Repeats are normal

`NiPSysDragModifier` occurs 2,245 times across 1,704 systems — commonly **three in a
row**, all at `Order` 4000. Since ties keep file order, the three are distinguishable
only by their names and their position in the array.

### `Active` is effectively always true

**One** modifier in the entire game ships inactive. A converter that drops the flag
would be right 1,703 times out of 1,704 and wrong once.

### Which controller drives which modifier

Observed bindings, and they are strictly typed in practice:

| controller | drives | count |
| --- | --- | --- |
| `NiPSysEmitterCtlr` | any emitter | 1,562 |
| `NiPSysModifierActiveCtlr` | `NiPSysDragModifier` 2,245, `NiPSysBombModifier` 490 | 2,735 |
| `NiPSysEmitterSpeedCtlr` | any emitter | 233 |
| `NiPSysGravityStrengthCtlr` | `NiPSysGravityModifier` — 230 of 230 | 230 |
| `BSPSysMultiTargetEmitterCtlr` | cylinder 64, box 47, sphere 31 — **never a mesh emitter** | 142 |
| `NiPSysEmitterInitialRadiusCtlr` | any emitter | 112 |
| `NiPSysEmitterLifeSpanCtlr` | any emitter | 76 |
| `NiPSysInitialRotSpeedCtlr` | `NiPSysRotationModifier` | 1 |

Two things stand out. **`NiPSysModifierActiveCtlr` only ever switches forces** — drag
and bomb, never an emitter, though nothing in the format forbids it. And
**`BSPSysMultiTargetEmitterCtlr` never targets a mesh emitter**: it is the multi-target
variant, and a mesh emitter already has its own object.

No controller in the game names a modifier that is not there — rule 4 of §6 holds across
the corpus.

### `NiPSysUpdateCtlr` is exactly one per system, and always last

1,704 of them for 1,704 systems, and in **every one** it is the final link of the chain.
The engine's rule is not merely respected, it is never tested: no shipped file needs the
repair `SetTarget` performs.

### The array is always sorted, though it need not be

All 1,704 write their `Modifiers` array in ascending `Order`. So the engine's re-sort on
load never fires on a vanilla file, and a converter that preserved array order and
ignored `Order` would look correct on every mesh in the game — right up until it met a
file it had itself written in some other sequence.

### The other invariants hold across the corpus

Of 1,704 systems: **no null modifier links**, **no duplicate modifier names within a
system**, and **no controller naming a modifier that is not there**. The four hard rules
of §6 that can be checked statically are satisfied by every shipped file.

Modifiers per band, which is the shape of a Skyrim particle system in one table:

| band | modifiers |
| --- | --- |
| `GENERAL` (3000) | 4,605 |
| `FORCE` (4000) | 4,101 |
| `EMITTER` (1000) | 3,595 |
| `KILLOLDPARTICLES` (0) | 1,704 |
| `BSLOD` (1) | 1,704 |
| `POS_UPDATE` (6000) | 1,701 |
| `BOUND_UPDATE` (7000) | 1,701 |
| `SK_BSSTRIPUPDATE` (8000) | 176 |
| `COLLIDER` (5000) | 76 |
| `POSTPOS_UPDATE` (6500) | 48 |

`SPAWN` (2000) and `WORLDSHIFT_PARTSPAWN` (6600) are empty: two of the twelve bands the
format defines are unused by the game.

### The chain does *not* start with an emitter controller

The GECK wiki states that "the controller should always be a `NiPSysEmitterCtlr` starting
a chain of particle controllers". It is not so, and not by a small margin — of 1,704
systems only **554** begin that way:

| first controller in the chain | systems |
| --- | --- |
| `NiPSysModifierActiveCtlr` | 884 |
| `NiPSysEmitterCtlr` | 554 |
| `NiPSysGravityStrengthCtlr` | 137 |
| `NiPSysEmitterLifeSpanCtlr` | 48 |
| `NiVisController` | 38 |
| `NiPSysEmitterInitialRadiusCtlr` | 24 |
| `NiPSysEmitterSpeedCtlr` | 19 |

Every system has a chain — none is absent — and 38 of them begin with a plain
`NiVisController`, which is not a particle controller at all. Nothing orders the chain
except the one rule that `NiPSysUpdateCtlr` ends it.

---

## 8. What this means for a converter

- **Carry `Order`, do not derive it.** It is a number in the file, the engine sorts on
  it, and the array order is not a substitute. That every class happens to use one value
  is a fact about Bethesda's exporter, not a rule — a hand-edited file may legitimately
  put a drag modifier at 4001 to sequence it after another.
- **Carry `Active`.** One file needs it.
- **Carry the modifier's `Name`**: it is the binding, not a label.
- **A controller's identity is its class plus its modifier name.** Two
  `NiPSysModifierActiveCtlr`s on one system differ only by the modifier they name, and
  keying them on class alone merges them (see `NifAnimAccess.ControllerIdOf`).
- **Read both of `NiPSysEmitterCtlr`'s interpolators.**
- **Put `NiPSysUpdateCtlr` last** when rebuilding a chain.
