# Carrying Havok constraints through FBX into Maya, Max and Blender

**Status**: validated against the Blender 5.0.1 source, the FBX SDK 2020.3.9 headers,
ck-cmd's source and Autodesk's own documentation. Sources at the end.

The question this answers: can FBX's own constraint objects express a Havok ragdoll, so
that the link between two bodies is an object reference rather than a name — and can
per-application scripts then turn what arrives into constraints an animator can
actually use?

The short answer is that **FBX's constraint objects cannot carry a ragdoll at all**, in
any application, and that the second half of the question is the one worth pursuing:
properties are the wire format, and a script in each application builds the native
constraint on arrival. That is also what Autodesk's own pipeline does.

---

## 1. What FBX actually offers

`FbxConstraint::EType`, the complete list (FBX SDK 2020.3.9):

| Value | Meaning |
| --- | --- |
| `eUnknown` | invalid |
| `ePosition` | point constraint |
| `eRotation` | orient constraint |
| `eScale` | scale constraint |
| `eParent` | parent constraint |
| `eSingleChainIK` | single-chain IK |
| `eAim` | aim constraint |
| `eCharacter` | character constraint |
| `eCustom` | user-defined |

The base class carries `Weight` (0–100), `Active` and `Lock`. That is the whole surface.

**There is no physics constraint, no rigid body, no joint limit, no ragdoll.** Every one
of these is an *animation* constraint: a rule for driving one transform from another.
None of them has an angular limit, a friction torque, a cone, a twist range, or a second
frame. A `bhkRagdollConstraint` has no representation here even in principle — not a
lossy one, none.

`eCustom` is not an escape hatch. It is a hook for an application to attach its own
constraint implementation; the FBX SDK documentation is explicit that a reader which
does not know about your extension ignores it. It is a name for something the file does
not describe.

### 1.1 FBX has no rigid body either

The same holds for the bodies. There is no mass, no inertia tensor, no collision shape,
no simulation flag anywhere in the FBX object model. A `bhkRigidBody` is carried today
as a `Null` model with properties, and there is no alternative.

---

## 2. What each application does with FBX constraints

### 2.1 Blender — nothing, in both directions

`io_scene_fbx` as shipped with Blender 5.0.1 contains **zero occurrences of the string
"constraint"** across all eleven of its Python modules. The importer's object dispatch
handles exactly seven classes:

```
Geometry, Material, Video, Texture, NodeAttribute, Model, Pose
```

An `FbxConstraint` of any type is read past and discarded. The exporter never writes
one. There is likewise no `rigid_body` handling in either direction, so Blender's own
rigid body constraints — which are otherwise a good match, see §5.1 — do not survive
export either.

### 2.2 Maya — six types, none of them useful here

Maya's FBX plug-in has explicit Constraints options on both import and export, and
supports **Point, Aim, Orient, Scale, Parent and IK Handle**. That is the intersection
of `FbxConstraint::EType` with Maya's own constraint nodes.

This is real support, and it is irrelevant to a ragdoll: all six are animation
constraints. Maya's documentation recommends exporting them only when the destination
is MotionBuilder, and says to bake first otherwise.

### 2.3 3ds Max — nothing, bake instead

Max does not consume FBX constraints. Autodesk's guidance is the reverse direction:
Max's Bake Animation option exists precisely because "all unsupported constraints and
controllers, including the 3ds Max Biped, are resampled into animation curves" for
applications that cannot read them. Maya's own FBX documentation names 3ds Max as the
example of "a package that does not support these constraints".

### 2.4 ck-cmd tried this and abandoned it

Worth recording because the evidence is in the file. `FBXWrangler.cpp` contains three
`FbxConstraintParent` constructions — at lines 2096, 2179 and 2228 — and **every one of
them is inside a comment block**:

```cpp
/*Quaternion rotation = matA.GetRotation().AsQuaternion();
...
FbxConstraintParent * fbx_constraint = FbxConstraintParent::Create(constraint_node, ...);
fbx_constraint->SetConstrainedObject(child);
fbx_constraint->AddConstraintSource(constraint_node);
...
fbx_constraint->AffectRotationZ = false;*/
```

What ships is the node and its string properties. Somebody wrote the FBX-constraint
route, found it did not carry what was needed, and commented it out rather than delete
it.

---

## 3. What does survive: properties

Custom properties are the only mechanism that crosses all three applications.

| | Import | Export | Types |
| --- | --- | --- | --- |
| Blender | yes, as ID properties | yes | string, int, double, float, bool, vector2/3/4, enum |
| Maya | yes, as Extra Attributes | yes | REAL↔float, BOOL, INTEGER, STRING, VECTOR, ENUM |
| 3ds Max | yes, as custom attributes | yes | spinner (angle/float/integer/percent/world units), slider, checkbox, colour picker, array, string |

Three caveats that matter for the schema in §4:

- **Maya applies extra attributes to transform nodes and materials only.** Shape-node
  attributes cannot be exported, because `FbxGeometry` has no user properties. Our
  constraint nodes are transforms, so this is satisfied by construction — but it means
  nothing may be hung on a mesh.
- **Max has two separate mechanisms.** Individual typed custom attributes are the good
  one. The *User Defined Properties* text box is a different thing, stored as one opaque
  string under `UDP3DSMAX`. Do not use the latter. Blender's importer has a special case
  that splits a `UDP3DSMAX` blob on newlines and `=` or `:` into named properties, which
  is a useful bridge in the Max→Blender direction but is not a schema to design against.
- **Max string custom attributes are reported unreliable in practice** by more than one
  studio, while int and float go through. This is the strongest practical argument for
  the typed numeric properties added in `hkx-constraint-spec.md` §4.3 rather than
  ck-cmd's strings.

### 3.1 Numeric properties remain readable by ck-cmd

ck-cmd writes the six limits as `FbxStringDT` and reads them back with
`std::atof(get_property<FbxString>(...).Buffer())` (`HKXWrangler.cpp:2878–2897`). We
write them as `Number`, i.e. FBX `eDouble`.

These interoperate. `get_property` ends in `FbxProperty::Get<FbxString>()`, and the SDK
defines the conversion — `fbxpropertytypes.h:948`:

```cpp
inline bool FbxTypeCopy(FbxString& pDst, const FbxDouble& pSrc){ pDst=FbxString(pSrc); return true; }
```

so a double property read as a string yields its decimal text and `atof` recovers it.
The typed form is therefore strictly better: ck-cmd still reads it, and Blender, Maya
and Max get a number instead of a string they would have to parse.

Blender's importer asserts the raw type matches the declared one (`Number` must carry a
`FLOAT64`), so the declared type and the payload must agree exactly. `float` is a
different declared type with a `FLOAT32` payload; mixing them aborts the import.

---

## 4. Precedent: Autodesk does not put physics in FBX either

Worth knowing before designing anything, because it is the same problem solved by the
vendor who owns the format.

3ds Max Interactive (formerly Stingray) imports ragdolls as **two files with the same
stem**: the character's FBX-derived unit, and a *separate* `.xml` physics file exported
in APEX/PhysX format carrying the rigid bodies and constraints. The engine pairs them by
name on import.

So Autodesk's own answer to "how do I get a ragdoll from Max into my engine" is a
sidecar file, not FBX. Our properties are the same decision taken differently: an
in-band sidecar rather than an out-of-band one, which avoids the pairing-by-filename
fragility at the cost of having to survive each application's property handling.

---

## 5. The design

**Properties are the truth. Native constraints are a view.**

```
   NIF / HKX  ──►  FBX node + properties  ──►  [DCC script]  ──►  native constraint
                            ▲                                            │
                            └──────────────  [DCC script]  ◄─────────────┘
```

The FBX file carries what `hkx-constraint-spec.md` §4 already defines: an attachment
point node placed at frame B, a `_frame_a` child at frame A, `constraint_type`,
`constraint_body_a`/`_b`, the full `hkc_` descriptor dump, and the shared limits under
ck-cmd's names. No FBX constraint object is written, in any profile.

A per-application script then does the translation on arrival, and undoes it on the way
out. This is the only design in which the link is a real object reference *inside* the
application — a Blender pointer, a Maya connection, a Max node reference — because none
of those can be serialised into FBX by any route.

It also retires the name-length problem for good. Once a script has built the native
constraint, the link is a reference; renaming is safe, and on export the script
regenerates both the node name and `constraint_body_a`/`_b` from the references. The
63-character truncation measured in `hkx-constraint-spec.md` §4.6 costs nothing.

### 5.1 What each application has to build into

**Blender** — `bpy.types.RigidBodyConstraint`, verified against 5.0.1:

```
TYPES:    FIXED, POINT, HINGE, SLIDER, PISTON, GENERIC, GENERIC_SPRING, MOTOR
POINTERS: object1, object2
ANGULAR:  use_limit_ang_x/y/z, limit_ang_{x,y,z}_{lower,upper}
```

`object1`/`object2` are genuine pointer properties, and the angular limits are
**asymmetric** (`lower` and `upper` independently), which matches Havok exactly.

**Maya** — the Bullet plug-in's rigid body constraint: Point, Hinge, Slider,
**Cone-Twist**, Six Degrees-of-Freedom, Spring Hinge, Spring 6-DOF. Cone-Twist is
Bullet's ragdoll joint and is the natural target; 6-DOF is the fallback where each of
the six axes can be locked, free or limited.

**3ds Max** — MassFX constraints, which are PhysX D6 joints presented as presets:

| Preset | Definition |
| --- | --- |
| Rigid | translation, swing and twist all locked |
| Slide | rigid, plus limited Y translation |
| Hinge | rigid, with Swing 1 limited to 100° |
| Twist | rigid, with Twist unlimited |
| Universal | rigid, with Swing 1 and Swing 2 limited to 45° |
| Ball & Socket | rigid, Swing 1 and 2 limited to 80°, Twist unlimited |

Every preset is the same D6 joint with different limits, and each of Swing 1, Swing 2
and Twist can be set Locked, Limited or Free. Twist is rotation about the constraint's
local X.

### 5.2 The mapping

Havok's `hkpRagdollConstraintData` atoms, as ck-cmd reads them
(`HKXWrangler.cpp:2878–2884`): `m_coneLimit.m_maxAngle`, `m_planesLimit.m_minAngle` and
`.m_maxAngle`, `m_twistLimit.m_minAngle` and `.m_maxAngle`, and
`m_angFriction.m_maxFrictionTorque`.

| NIF block | Blender | Maya (Bullet) | Max (MassFX) |
| --- | --- | --- | --- |
| `bhkRagdollConstraint` | `GENERIC` | Cone-Twist | Ball & Socket, limits overridden |
| `bhkLimitedHingeConstraint` | `HINGE` + limits | Hinge + limits | Hinge, limits overridden |
| `bhkHingeConstraint` | `HINGE` | Hinge | Twist about the hinge axis |
| `bhkBallAndSocketConstraint` | `POINT` | Point | Ball & Socket, all free |
| `bhkStiffSpringConstraint` | `GENERIC_SPRING` | Spring 6-DOF | Slide with a spring |
| `bhkPrismaticConstraint` | `SLIDER` | Slider | Slide |

and, per axis:

| Havok | Blender | Maya | Max |
| --- | --- | --- | --- |
| `coneMaxAngle` | `limit_ang_y_lower/upper` = ∓cone | `swingSpan1` | Swing 1 limit |
| `planeMinAngle` / `planeMaxAngle` | `limit_ang_z_lower/upper` directly | `swingSpan2` (see §5.3) | Swing 2 limit (see §5.3) |
| `twistMinAngle` / `twistMaxAngle` | `limit_ang_x_lower/upper` directly | `twistSpan` (see §5.3) | Twist Limit (asymmetric supported) |
| `maxFriction` | no equivalent — keep in properties | no equivalent | no equivalent |

Angles are radians in the NIF and in Blender. Maya and Max script interfaces are in
degrees; converting is the script's job, and it is the single commonest way to get this
wrong.

### 5.3 Asymmetric limits are where fidelity is lost

Havok's plane and twist limits are **asymmetric**: an independent minimum and maximum.
So are Blender's. Bullet's Cone-Twist spans and PhysX's swing limits are **symmetric**:
one half-angle about the joint frame's axis.

So a Havok joint whose plane limit runs from −12° to +48° cannot be expressed directly
in Maya's Cone-Twist or Max's Swing. The faithful approximation is to rotate the
constraint frame by the midpoint and use the half-range as the span:

```
offset = (max + min) / 2          # rotate the joint frame by this
span   = (max - min) / 2          # the symmetric limit
```

This reproduces the range exactly. What it changes is the frame, so a script that does
this **must not write the rotated frame back** on export — the original frame is in the
`hkc_` properties and stays authoritative.

Max's Twist accepts an independent low and high limit and so needs no such treatment;
only the two Swings do. Blender needs none at all.

`maxFriction` — Havok's angular friction torque — has no counterpart in any of the
three. It survives only in the properties, which is fine, because nothing in a DCC would
do anything with it.

---

## 6. Normative rules for a translation script

These are the rules that keep a round trip honest. They apply equally to all three
applications.

**R1. Never discard a property you did not understand.** Every `hkc_*` property, and
every property the schema does not name, is copied to the native object verbatim and
written back unchanged. The `hkc_` dump is what makes a NIF round trip byte-exact; a
script that rebuilds a constraint from the six shared limits alone has destroyed a
stiff spring, a ball and socket, and every chain.

**R2. The properties win unless the user edited the constraint.** On export, write back
the stored properties as they came in. Only regenerate a value from the native
constraint when that constraint has actually been changed. Record enough state on
import — a hash of the values written, stored as a property — to tell the two apart.
Without this, every round trip degrades the file by the width of §5.3's approximation
even when nobody touched anything.

**R3. Names are regenerated from references, never trusted.** On export the node name
and `constraint_body_a`/`_b` are rebuilt from the native constraint's object references.
On import they are read in the order `constraint_body_a`/`_b` first, node name second,
parent third.

**R4. Both frames survive.** The `_frame_a` child node is not a bone, not a body, and
not a joint. It carries `constraint_frame = "A"` and must be recreated on export at the
frame the native constraint implies — or, under R2, written back unchanged.

**R5. Units are converted at the boundary and nowhere else.** Radians in the file,
whatever the application uses internally, and the `bhkScaleFactor` of 69.99125 is
applied only where `hkx-constraint-spec.md` §4.4 says it is.

**R6. A body is a body in both worlds.** A `_rb` node becomes a native rigid body with
its mass, inertia and collision shape from `nif_rb_*`, or the constraint has nothing to
attach to. Where the application requires a simulation world for constraints to exist —
Blender's `RigidBodyWorld`, Max's MassFX scene, Maya's Bullet solver — the script
creates it.

### 6.1 Two different jobs, and they want different constraints

Worth separating in any implementation, because they are not the same feature:

- **Ragdoll tuning** wants rigid body constraints and a simulation — the tables above.
- **Hand animation** wants the limits enforced while posing, which means *bone*
  constraints on the rig: Blender `LIMIT_ROTATION`, Maya's rotate limits on the joint,
  Max's Rotation controller limits. Same cone/twist numbers, different target, no
  simulation, and no rigid bodies needed.

An animator posing a creature is served by the second and obstructed by the first. A
script should offer them as separate operations rather than guessing.

---

## 7. Non-goals

- **No FBX constraint objects are written, ever.** §1 and §2 establish they cannot
  carry a ragdoll and that two of the three applications discard them. Writing one would
  produce, in Maya only, a genuine parent constraint that fights the rig.
- **No sidecar file.** §4's precedent is noted rather than followed: pairing by filename
  is fragile in a mod pipeline where files are renamed and repacked.
- **No attempt to make Blender's FBX exporter emit rigid bodies.** That is a patch to
  Blender, not to us, and R1–R3 make it unnecessary.

---

## Sources

- [FbxConstraint Class Reference](https://help.autodesk.com/cloudhelp/2015/ENU/FBX-Developer-Help/cpp_ref/class_fbx_constraint.html) — the `EType` list and base properties
- [Maya FBX Export options](https://help.autodesk.com/view/MAYAUL/2023/ENU/?guid=GUID-FE8DBEAA-C2DD-43B3-9933-4BA4CDDEAA89) and [FBX Import options](https://knowledge.autodesk.com/support/maya/learn-explore/caas/CloudHelp/cloudhelp/2023/ENU/Maya-DataExchange/files/GUID-0CD41066-4C27-48AE-9776-366DB11B4FDF-htm.html) — the six supported constraint types, and the advice to bake for 3ds Max
- [Maya FBX custom properties](https://download.autodesk.com/us/fbx/2013/Maya_FBX_Plug-in_Help/files/GUID-78403C67-CF2D-4270-8AB0-24994E12B771.htm) — extra attributes, type conversions, transform-and-material-only limitation
- [3ds Max FBX Custom Properties/Attributes](https://help.autodesk.com/cloudhelp/2018/ENU/3DSMax-Data-Exchange/files/GUID-21B34883-49AC-46BF-80A6-38D26AFC6C19.htm) — supported custom attribute types, and `UDP3DSMAX`
- [3ds Max FBX Animation / Bake animation](https://help.autodesk.com/cloudhelp/2021/ENU/3DSMax-Data-Exchange/files/GUID-31EA2A4F-A126-498B-96E8-31811522738A.htm) — unsupported constraints and controllers are resampled
- [Stingray: Create and import a ragdoll](https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/stingray_help/creating_gameplay/physics/create_import_ragdoll.html) — the APX sidecar precedent
- [Maya Bullet Constraint Types](https://help.autodesk.com/cloudhelp/2018/ENU/Maya-SimulationEffects/files/GUID-CDB3638D-23AF-49EF-8EF6-53081EE4D39D.htm) — Point, Hinge, Slider, Cone-Twist, 6-DOF, Spring variants
- [3ds Max MassFX Constraint Helper](https://knowledge.autodesk.com/support/3ds-max/learn-explore/caas/CloudHelp/cloudhelp/2019/ENU/3DSMax-Simulation-Effects/files/GUID-A089EB2B-45A1-4A6B-8B06-221A75267881-htm.html) and [MassFX Toolbar](https://help.autodesk.com/cloudhelp/2023/ENU/3DSMax-Simulation-Effects/files/GUID-DEDC3C01-9F80-42BB-BECB-F0868FBBADB4.htm) — the preset definitions and swing/twist limits
- [3ds Max custom attributes and FBX, in practice](https://www.tech-artists.org/t/fbx-3ds-max-custom-attr-string-data-not-exporting-to-fbx/6054) — which types studios find survive
- Read directly rather than cited: `io_scene_fbx` as shipped with Blender 5.0.1;
  `fbxsdk/core/fbxpropertytypes.h` from FBX SDK 2020.3.9; ck-cmd's `FBXWrangler.cpp` and
  `HKXWrangler.cpp`; `bpy.types.RigidBodyConstraint` queried from Blender 5.0.1.
