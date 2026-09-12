# Particle systems: what the engine does with one

`nif-particle-spec.md` asks what a particle system *holds* and whether FBX can carry it.
This asks a different question: **what the engine does with the graph**, and therefore
which graphs are legal. It is the document to read before writing a particle system
rather than before reading one.

Four sources, in descending order of authority:

| | |
| --- | --- |
| **Gamebryo 2.6 source** | `CoreLibs/NiParticle/_Deprecated/` is the `NiPSys*` family itself — the exact classes Skyrim streams — and `NiPSConverter.cpp` states what each one *means* by converting it to the engine's newer particle system. |
| **`nif.xml`** | the field layout, and per-type `Order` defaults that agree with the engine's enum. |
| **The corpus** | 1,704 particle systems in 899 of the game's 22,047 meshes: what Bethesda actually ships, which is narrower than what is legal. |
| **NifSkope, GECK wiki** | conventions and one hard crash rule. |

Where they disagree, the source wins and the disagreement is recorded — §6 does exactly
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
  with itself — see §6 — so this never bites until something writes one that does not.)
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

## 4. Controllers: how one binds to a modifier

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

## 5. Hard rules

Things that are not style:

1. **A null entry in `Modifiers` crashes Oblivion.** NifSkope's sanitize spell removes
   them for exactly this reason (`spells/sanitize.cpp`: "remove empty modifier links
   (NiParticleSystem crashes Oblivion for those)").
2. **A modifier must have a name.** `AddModifier` asserts on it, because the name is how
   controllers find it.
3. **Modifier names must be unique within a system.** `GetModifierByName` returns the
   first match, so a repeat is unreachable.
4. **A controller's `Modifier Name` must resolve**, per `InterpTargetIsCorrectType`.
5. **`NiPSysUpdateCtlr` belongs last**, per §4.

---

## 6. What Bethesda actually ships

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

No controller in the game names a modifier that is not there — rule 4 of §5 holds across
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
of §5 that can be checked statically are satisfied by every shipped file.

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

## 7. What this means for a converter

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
