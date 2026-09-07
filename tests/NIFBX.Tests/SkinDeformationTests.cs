using System.Numerics;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using NIFBX.Nif;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Where a skinned vertex ends up, computed from the NIF and from the FBX.
    /// </summary>
    /// <remarks>
    /// The two are different sums over the same weights. The NIF takes a vertex into
    /// each bone with NiSkinData's own matrix and out again with the bone's world
    /// transform. The FBX takes it into the world at bind time with the cluster's
    /// `Transform`, back into the bone with the inverse of its `TransformLink`, and
    /// out with the bone's world transform. They have to land in the same place.
    ///
    /// Nothing else here asks that question. The round trip reads back what this
    /// converter wrote and agrees with itself however the bind pose is spelled, and
    /// two faults hid behind exactly that: `TransformLink` held the matrix that undoes
    /// the bind rather than the bind, and the mesh's node carried a transform the
    /// cluster had already applied. Both left the animation playing over geometry in
    /// the wrong place -- the second by 290 units on nightingalebanneranim01 -- and
    /// neither changed a single field of the rebuilt NIF.
    ///
    /// Checked at rest and across the animation, with each side sampling its own
    /// curves, so a mismatch between what the skin says and what the take does shows
    /// up as well.
    /// </remarks>
    public class SkinDeformationTests
    {
        /// <summary>
        /// How far apart the two sums may land, in NIF units.
        /// </summary>
        /// <remarks>
        /// A hundredth of a unit. The vertices are halves and the transforms decompose
        /// through Euler angles, so the two sums drift in the fourth decimal; the
        /// faults this exists to catch were 78, 120 and 290 units.
        /// </remarks>
        private const double Tolerance = 0.01;

        [Theory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void ASkinnedVertexLandsWhereTheNifPutsIt(string name)
        {
            Check(name, nudge: false);
        }

        /// <summary>
        /// The same, with the skinned shapes moved off the origin first.
        /// </summary>
        /// <remarks>
        /// A shape's own transform takes no part in how a NIF deforms it -- the skin
        /// places the mesh -- so moving the node must change nothing at all. It used to
        /// change everything: the transform was written onto the node above the mesh in
        /// the FBX, where a reader applies it on top of a deformation that is already
        /// in the world, and the mesh moved twice.
        ///
        /// Every committed fixture happens to have its skinned shapes at the origin, so
        /// nothing here could tell. Rather than commit a Bethesda mesh to catch it --
        /// `nightingalebanneranim01`, whose banner sits 290 units off -- the case is
        /// made by moving one.
        /// </remarks>
        [Theory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void MovingASkinnedShapesNodeMovesNothing(string name)
        {
            Check(name, nudge: true);
        }

        private static void Check(string name, bool nudge)
        {
            NifModel m = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name),
                NifXmlDatabase.LoadEmbedded());

            if (!m.Blocks.Any(b => m.GetRef(b, "Skin") is not null))
                return;

            if (nudge)
            {
                foreach (NifItem skinned in m.Blocks.Where(b => m.GetRef(b, "Skin") is not null))
                    m.FindItem(skinned, "Translation")?.Value.Set(new NifVector3(17f, -29f, 43f));
            }

            var document = new NifToFbx(m).Convert();

            // --- the NIF side -------------------------------------------------
            var world = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);

            void Walk(NifItem node, Matrix4x4 above)
            {
                Matrix4x4 here = m.GetTransform(node).ToMatrix() * above;
                world[m.GetName(node)] = here;

                foreach (NifItem child in m.GetRefArray(node, "Children"))
                    Walk(child, here);
            }

            foreach (NifItem root in m.GetRefArray(m.Footer, "Roots"))
                Walk(root, Matrix4x4.Identity);

            var scene0 = new FbxScene(document);

            foreach (NifItem shape in m.Blocks.Where(b => m.GetRef(b, "Skin") is not null))
            {
            SkinData skin = NifSkinAccess.ReadSkin(m, shape)!;

            // A skinned SSE shape keeps its vertices in the skin partition.
            // (per shape)
            var vertices = new List<NifVector3>();

            NifItem? vertexData = m.FindItem(shape, "Vertex Data");

            if (vertexData is null || vertexData.Children.Count == 0)
            {
                NifItem? instance = m.GetRef(shape, "Skin");
                NifItem? partition = instance is null ? null : m.GetRef(instance, "Skin Partition");
                vertexData = partition is null ? null : m.FindItem(partition, "Vertex Data");
            }

            if (vertexData is not null)
            {
                foreach (NifItem row in vertexData.Children)
                    if (m.FindItem(row, "Vertex") is { } p)
                        vertices.Add(p.Value.Get<NifVector3>());
            }
            else
            {
                vertices.AddRange(m.GetVertices(m.GetRef(shape, "Data") ?? shape));
            }


            // --- the FBX side, matched to this shape by name --------------------
            FbxScene scene = scene0;

            string want = NameEncoding.Sanitize(NifAnimAccess.TrackName(m, shape));

            if (scene.Objects.FirstOrDefault(o => o.Class == "Geometry" && o.Name == want)
                is not { } geometry)
            {
                continue;
            }

            double[] raw = (double[])geometry.Child("Vertices")!.Properties[0];

            FbxObject? meshModel = scene.ParentsOf(geometry.Id).FirstOrDefault(o => o.Class == "Model");
            Matrix4x4 meshNode = meshModel is null
                ? Matrix4x4.Identity
                : FbxGlobalTransform.Of(scene, meshModel).ToMatrix();


            // Every cluster, by bone name.
            var clusters = new Dictionary<string, (Matrix4x4 T, Matrix4x4 Link, Dictionary<int, float> W)>(
                StringComparer.Ordinal);

            // Only this geometry's clusters. Two shapes in one file share bone names,
            // and a scene-wide sweep pairs a shape with the other one's bind pose.
            var mine = scene.ChildrenOf(geometry.Id)
                .Where(o => o.Class == "Deformer" && o.SubClass == "Skin")
                .SelectMany(sk => scene.ChildrenOf(sk.Id))
                .Where(o => o.Class == "Deformer" && o.SubClass == "Cluster");

            foreach (FbxObject cluster in mine)
            {
                if (scene.ChildrenOf(cluster.Id).FirstOrDefault(o => o.Class == "Model") is not { } bone)
                    continue;

                var w = new Dictionary<int, float>();

                if (cluster.Child("Indexes")?.Properties.FirstOrDefault() is int[] ix
                    && cluster.Child("Weights")?.Properties.FirstOrDefault() is double[] wt)
                {
                    for (int i = 0; i < ix.Length && i < wt.Length; i++)
                        w[ix[i]] = (float)wt[i];
                }

                string boneName = NameEncoding.Unsanitize(bone.Name);

                if (clusters.TryGetValue(boneName, out var already))
                {
                    foreach ((int vertex, float value) in w)
                        already.W[vertex] = value;
                }
                else
                {
                    clusters[boneName] =
                        (Read(cluster.Child("Transform")), Read(cluster.Child("TransformLink")), w);
                }
            }

            // --- bone world transforms at a time, each side from its own curves ---
            //
            // The rest comparison above feeds both sums the same bone transforms, so it
            // says the bind pose agrees and nothing about the animation. Here each side
            // samples the curves it actually carries: the NIF its interpolators, the
            // FBX its AnimCurves.
            var nifTracks = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);
            var fbxTracks = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);
            float stop = 0f;

            foreach (AnimSequence sequence in NifAnimAccess.ReadAnimations(m))
            {
                stop = MathF.Max(stop, sequence.Stop);

                foreach (AnimTrack track in sequence.Tracks)
                    nifTracks.TryAdd(track.NodeName, track);
            }

            foreach (AnimSequence sequence in scene.ReadAnimations())
                foreach (AnimTrack track in sequence.Tracks)
                    fbxTracks.TryAdd(NameEncoding.Unsanitize(track.NodeName), track);

            var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
            var restOf = new Dictionary<string, NifTransform>(StringComparer.Ordinal);

            void Chain(NifItem node, string? above)
            {
                string here = m.GetName(node);
                parentOf[here] = above;
                restOf[here] = m.GetTransform(node);

                foreach (NifItem child in m.GetRefArray(node, "Children"))
                    Chain(child, here);
            }

            foreach (NifItem root in m.GetRefArray(m.Footer, "Roots"))
                Chain(root, null);

            Matrix4x4 WorldAt(string bone, IReadOnlyDictionary<string, AnimTrack> tracks, float time)
            {
                Matrix4x4 acc = Matrix4x4.Identity;

                for (string? at = bone; at is not null && parentOf.ContainsKey(at); at = parentOf[at])
                {
                    acc *= LocalAt(tracks.GetValueOrDefault(at), restOf.GetValueOrDefault(at, NifTransform.Identity), time);
                }

                return acc;
            }

            // --- the same vertices, both ways ---------------------------------
            double worst = 0, worstNode = 0;

            for (int v = 0; v < vertices.Count; v++)
            {
                Vector3 fromNif = Vector3.Zero, fromFbx = Vector3.Zero;
                float totalNif = 0, totalFbx = 0;

                var point = new Vector3(vertices[v].X, vertices[v].Y, vertices[v].Z);

                foreach (SkinBone bone in skin.Bones)
                {
                    float weight = 0;

                    foreach ((ushort vertex, float value) in bone.Weights)
                        if (vertex == v) { weight = value; break; }

                    if (weight == 0) continue;

                    Matrix4x4 boneWorld = world.GetValueOrDefault(bone.Name, Matrix4x4.Identity);

                    fromNif += weight * Vector3.Transform(
                        point, bone.SkinTransform.ToMatrix() * boneWorld);
                    totalNif += weight;

                    if (!clusters.TryGetValue(bone.Name, out var c)) continue;
                    if (!c.W.TryGetValue(v, out float fw) || fw == 0) continue;

                    Matrix4x4.Invert(c.Link, out Matrix4x4 linkInv);

                    fromFbx += fw * Vector3.Transform(point, c.T * linkInv * boneWorld);
                    totalFbx += fw;
                }

                if (totalNif == 0 || totalFbx == 0) continue;

                worst = Math.Max(worst, (fromNif - fromFbx).Length());
                worstNode = Math.Max(worstNode,
                    (fromNif - Vector3.Transform(fromFbx, meshNode)).Length());

            }

            // A skinned mesh's node has to sit at the origin. The cluster matrices put
            // the deformed vertex in the world already, so a node with a transform of
            // its own places the mesh a second time -- which is only visible here when
            // a fixture's skinned shape has one, and none does. Asserted directly so
            // the invariant is checked rather than merely happening to hold.
            Assert.True(
                meshNode == Matrix4x4.Identity,
                $"{name}: the node above skinned geometry '{want}' is not at the origin, "
                + "so its skin places it twice");

            Assert.True(
                worst < Tolerance,
                $"{name}: '{want}' is {worst:F3} units from where the NIF puts it at rest");

            // And with the mesh node's own transform applied on top, since a reader is
            // entitled to place the deformed result under the node it hangs from. The
            // node has to be at the origin for both readings to agree.
            Assert.True(
                worstNode < Tolerance,
                $"{name}: '{want}' is {worstNode:F3} units out once its node is applied; "
                + "the mesh is being placed twice");

            foreach (float time in new[] { 0f, stop * 0.25f, stop * 0.5f, stop * 0.75f, stop })
            {
                double gap = 0;

                var atTime = new Dictionary<string, (Matrix4x4 Nif, Matrix4x4 Fbx)>(StringComparer.Ordinal);

                foreach (SkinBone bone in skin.Bones)
                {
                    atTime[bone.Name] =
                        (WorldAt(bone.Name, nifTracks, time), WorldAt(bone.Name, fbxTracks, time));
                }

                for (int v = 0; v < vertices.Count; v++)
                {
                    Vector3 a = Vector3.Zero, b = Vector3.Zero;
                    bool any = false;

                    var point = new Vector3(vertices[v].X, vertices[v].Y, vertices[v].Z);

                    foreach (SkinBone bone in skin.Bones)
                    {
                        float weight = 0;

                        foreach ((ushort vertex, float value) in bone.Weights)
                            if (vertex == v) { weight = value; break; }

                        if (weight == 0 || !clusters.TryGetValue(bone.Name, out var c)) continue;
                        if (!c.W.TryGetValue(v, out float fw) || fw == 0) continue;

                        Matrix4x4.Invert(c.Link, out Matrix4x4 linkInv);
                        (Matrix4x4 nifWorld, Matrix4x4 fbxWorld) = atTime[bone.Name];

                        a += weight * Vector3.Transform(point, bone.SkinTransform.ToMatrix() * nifWorld);
                        b += fw * Vector3.Transform(point, c.T * linkInv * fbxWorld);
                        any = true;
                    }

                    if (any) gap = Math.Max(gap, (a - b).Length());
                }

                Assert.True(
                    gap < Tolerance,
                    $"{name}: '{want}' is {gap:F3} units out at t={time:F3}");
            }
            }
        }

        /// <summary>A curve's value at a time, linear between keys.</summary>
        private static float Sample(AnimCurve curve, float time, float fallback)
        {
            if (curve.Keys.Count == 0) return fallback;
            if (time <= curve.Keys[0].Time) return curve.Keys[0].Value;

            for (int i = 1; i < curve.Keys.Count; i++)
            {
                if (time > curve.Keys[i].Time) continue;

                AnimKey a = curve.Keys[i - 1], b = curve.Keys[i];

                if (time >= b.Time) return b.Value;
                if (a.Interpolation == AnimInterpolation.Constant) return a.Value;

                float span = b.Time - a.Time;
                return span <= 0f ? b.Value : a.Value + (b.Value - a.Value) * ((time - a.Time) / span);
            }

            return curve.Keys[^1].Value;
        }

        /// <summary>A track's local transform at a time, over the node's rest pose.</summary>
        private static Matrix4x4 LocalAt(AnimTrack? track, NifTransform rest, float time)
        {
            if (track is null) return rest.ToMatrix();

            NifVector3 t = rest.Translation;
            NifVector3 r = rest.ToEulerDegrees();
            float s = rest.Scale;

            var moved = new NifTransform(
                new NifVector3(
                    Sample(track.Translation[0], time, t.X),
                    Sample(track.Translation[1], time, t.Y),
                    Sample(track.Translation[2], time, t.Z)),
                NifTransform.RotationFromEulerDegrees(
                    Sample(track.Rotation[0], time, r.X),
                    Sample(track.Rotation[1], time, r.Y),
                    Sample(track.Rotation[2], time, r.Z)),
                Sample(track.Scale[0], time, s));

            return moved.ToMatrix();
        }

        private static Matrix4x4 Read(FbxNode? node)
        {
            if (node?.Properties.FirstOrDefault() is not double[] { Length: 16 } m)
                return Matrix4x4.Identity;

            return new Matrix4x4(
                (float)m[0], (float)m[1], (float)m[2], (float)m[3],
                (float)m[4], (float)m[5], (float)m[6], (float)m[7],
                (float)m[8], (float)m[9], (float)m[10], (float)m[11],
                (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
        }

    }
}
