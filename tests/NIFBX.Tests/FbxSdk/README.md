# Asking a reader that is not ours

A NIF → FBX → NIF comparison cannot see a fault the reader and the writer share.
Several have hidden exactly there, behind a green suite and a 22,047-mesh sweep:

| | |
| --- | --- |
| a missing top-level `CreationTime` record | every FBX this converter had written was refused by the Autodesk SDK |
| `NifValue` narrowing on write rather than on set | the sweep compared numbers no NIF can store |
| quadratic key tangents read from the wrong end | a waterwheel eased in and out instead of turning at its authored speed |
| rotations applied `v*R` where the file means `R*v` | every model came out mirrored |

Each was symmetric, so the trip closed over it. `fbxprobe` exists so there is
something to ask that made none of those assumptions.

```sh
./build.sh                       # needs an unpacked SDK; see below
./fbxprobe where model.fbx       # every mesh's control points in world space
./fbxprobe curve model.fbx Node  # a node's rotation curves, keys and sampled values
```

`FbxSdkGeometryTests` uses `where` and skips when the probe has not been built.

## Building

Point `FBXSDK` at an unpacked Autodesk FBX SDK; the default is
`~/Dev/fbx202039_fbxsdk_linux`. It links **statically** on purpose: the shared
object asks for versioned libxml2 symbols (`LIBXML2_2.4.30`) that a current
system libxml2 does not carry, and the static archive references them
unversioned.

Nothing here is packaged. The test project is `IsPackable=false` and only
`src/NIFBX` is packed, so the sources and the binary stay where they are.

## What it cannot do

It cannot catch a fault that reinterprets the whole file consistently. Applying
every rotation transposed is one: the FBX and the NIF still agree with each
other, because both sides moved together, and comparing them proves nothing. Only
a check the file has to satisfy on its own terms sees that —
`SkinBindConsistencyTests` is one, asking whether every bone of a skin agrees on
where its mesh stands, and there are two more worth writing:

- a collision proxy has to coincide with the mesh it stands for
- an animation's first key has to equal the pose its node is saved in
