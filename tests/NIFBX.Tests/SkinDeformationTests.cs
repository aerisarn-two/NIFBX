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
    /// Whether a skinned vertex lands where the NIF's own skinning puts it.
    /// </summary>
    /// <remarks>
    /// A skin at rest leaves the mesh where the mesh already is. That is what this
    /// checks: summing a vertex over its clusters, with the bones where the file puts
    /// them, has to give the vertex back at the shape's own placement.
    ///
    /// It is deliberately not "reproduce NiSkinData's arithmetic". That matrix is the
    /// inverse bind and the nodes are not at the bind pose, so `SkinTransform *
    /// boneWorld` moves the mesh by however far the saved pose is from the authored one
    /// -- 77 units on nightingalebanneranim01, far more on dlc1sabrecat, which is what
    /// garbled the creature. NifSkope draws the mesh undeformed and so should a viewer
    /// opening the FBX.
    ///
    /// NiSkinData's matrices are still carried verbatim on each cluster, so the round
    /// trip returns them exactly; what the clusters compute with is derived against the
    /// bones as they actually stand.
    /// </remarks>
    public class SkinDeformationTests
    {
        /// <summary>How far apart the two sums may land, in NIF units.</summary>
        private const double Tolerance = 0.01;

        [Theory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void ASkinnedVertexLandsWhereTheNifPutsIt(string name)
        {
            NifModel m = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name),
                NifXmlDatabase.LoadEmbedded());

            if (!m.Blocks.Any(b => m.GetRef(b, "Skin") is not null))
                return;

            var scene = new FbxScene(new NifToFbx(m).Convert());

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

            foreach (NifItem shape in m.Blocks.Where(b => m.GetRef(b, "Skin") is not null))
            {
                if (NifSkinAccess.ReadSkin(m, shape) is not { } skin) continue;

                string want = NameEncoding.Sanitize(NifAnimAccess.TrackName(m, shape));

                if (scene.Objects.FirstOrDefault(o => o.Class == "Geometry" && o.Name == want)
                    is not { } geometry)
                {
                    continue;
                }

                var vertices = VerticesOf(m, shape);

                if (vertices.Count == 0) continue;

                var clusters = ClustersOf(scene, geometry);

                foreach (SkinBone bone in skin.Bones)
                {
                    if (!clusters.TryGetValue(bone.Name, out var c)) continue;

                    // The bone stands where the file puts it, whatever the bind was.
                    Assert.True(
                        Close(c.Link, world.GetValueOrDefault(bone.Name, Matrix4x4.Identity)),
                        $"{name}: '{want}' -- the cluster for '{bone.Name}' does not hold "
                        + "the bone's own placement");
                }

                Matrix4x4 where = world.GetValueOrDefault(m.GetName(shape), Matrix4x4.Identity);
                double worst = 0;

                for (int v = 0; v < vertices.Count; v++)
                {
                    Vector3 deformed = Vector3.Zero;
                    float total = 0;

                    foreach (SkinBone bone in skin.Bones)
                    {
                        if (!clusters.TryGetValue(bone.Name, out var c)) continue;
                        if (!c.Weights.TryGetValue(v, out float fw) || fw == 0) continue;

                        deformed += fw * Vector3.Transform(vertices[v], c.Transform * c.Link);
                        total += fw;
                    }

                    // A vertex the clusters do not fully weight is not fully placed by
                    // them either, so there is nothing to compare.
                    if (total < 0.999f) continue;

                    worst = Math.Max(
                        worst, (Vector3.Transform(vertices[v], where) - deformed).Length());
                }

                Assert.True(
                    worst < Tolerance,
                    $"{name}: '{want}' is {worst:F3} units out at rest -- its own skin, with its "
                    + "bones where the file puts them, does not leave the mesh where it is");
            }
        }

        private static List<Vector3> VerticesOf(NifModel m, NifItem shape)
        {
            NifItem? vd = m.FindItem(shape, "Vertex Data");

            if (vd is null || vd.Children.Count == 0)
            {
                NifItem? instance = m.GetRef(shape, "Skin");
                NifItem? part = instance is null ? null : m.GetRef(instance, "Skin Partition");
                vd = part is null ? null : m.FindItem(part, "Vertex Data");
            }

            var result = new List<Vector3>();

            if (vd is not null)
            {
                foreach (NifItem row in vd.Children)
                    if (m.FindItem(row, "Vertex") is { } p)
                    {
                        NifVector3 q = p.Value.Get<NifVector3>();
                        result.Add(new Vector3(q.X, q.Y, q.Z));
                    }
            }
            else
            {
                foreach (NifVector3 q in m.GetVertices(m.GetRef(shape, "Data") ?? shape))
                    result.Add(new Vector3(q.X, q.Y, q.Z));
            }

            return result;
        }

        private static Dictionary<string, (Matrix4x4 Transform, Matrix4x4 Link, Dictionary<int, float> Weights)>
            ClustersOf(FbxScene scene, FbxObject geometry)
        {
            var found =
                new Dictionary<string, (Matrix4x4, Matrix4x4, Dictionary<int, float>)>(StringComparer.Ordinal);

            foreach (FbxObject cluster in scene.ChildrenOf(geometry.Id)
                         .Where(o => o.Class == "Deformer" && o.SubClass == "Skin")
                         .SelectMany(sk => scene.ChildrenOf(sk.Id))
                         .Where(o => o.Class == "Deformer" && o.SubClass == "Cluster"))
            {
                if (scene.ChildrenOf(cluster.Id).FirstOrDefault(o => o.Class == "Model") is not { } bone)
                    continue;

                var weights = new Dictionary<int, float>();

                if (cluster.Child("Indexes")?.Properties.FirstOrDefault() is int[] ix
                    && cluster.Child("Weights")?.Properties.FirstOrDefault() is double[] wt)
                {
                    for (int i = 0; i < ix.Length && i < wt.Length; i++)
                        weights[ix[i]] = (float)wt[i];
                }

                string named = NameEncoding.Unsanitize(bone.Name);

                if (found.TryGetValue(named, out var already))
                {
                    foreach ((int vertex, float value) in weights)
                        already.Item3[vertex] = value;
                }
                else
                {
                    found[named] = (Read(cluster.Child("Transform")), Read(cluster.Child("TransformLink")), weights);
                }
            }

            return found;
        }

        private static Matrix4x4 Invert(Matrix4x4 m) =>
            Matrix4x4.Invert(m, out Matrix4x4 inverted) ? inverted : Matrix4x4.Identity;

        private static bool Close(Matrix4x4 a, Matrix4x4 b)
        {
            float[] x = [a.M11, a.M12, a.M13, a.M21, a.M22, a.M23, a.M31, a.M32, a.M33, a.M41, a.M42, a.M43];
            float[] y = [b.M11, b.M12, b.M13, b.M21, b.M22, b.M23, b.M31, b.M32, b.M33, b.M41, b.M42, b.M43];

            for (int i = 0; i < x.Length; i++)
                if (Math.Abs(x[i] - y[i]) > Tolerance) return false;

            return true;
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
