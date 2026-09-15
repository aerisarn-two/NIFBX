using LeanMeshIO;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// A partitioned skin crosses into FBX as one deformer, not one per partition.
    /// </summary>
    /// <remarks>
    /// It used to be one per partition. That is what ck-cmd does -- it counts a mesh's
    /// skin deformers to get the partition count -- and FBX does not read them that
    /// way: a geometry with three skin deformers is deformed three times. Blender does
    /// exactly that, so a draugr's body, whose skin has three partitions, arrived with
    /// three armature modifiers and was visibly destroyed. Posed, it stood 1.4 units
    /// wide with its shoulders below the armour it wears; every weight was present and
    /// every one was applied three times.
    ///
    /// A partition is a view over one shared set of weights -- which vertices a slice
    /// draws, and with which bones -- and the file says so itself: a vertex on the seam
    /// between two body parts is in both partitions' lists. A view is not a second
    /// deformation, so it does not get a second deformer. It rides beside the weights
    /// instead, and comes back from there.
    /// </remarks>
    public class SkinPartitionCarrierTests
    {
        /// <summary>Three partitions over six vertices, sharing a seam and a bone.</summary>
        private static SkinData Sliced()
        {
            var skin = new SkinData { SkeletonRoot = "root" };

            foreach (string name in new[] { "Bone0", "Bone1" })
            {
                var bone = new SkinBone { Name = name };

                for (ushort v = 0; v < 6; v++)
                    bone.Weights.Add((v, 0.5f));

                skin.Bones.Add(bone);
            }

            AddPart(skin, [0, 1, 2], [0], 0);
            AddPart(skin, [2, 3, 4], [0, 1], 0);   // 2 is the seam, in both
            AddPart(skin, [4, 5], [1], 3);         // and a level of its own
            return skin;
        }

        private static void AddPart(SkinData skin, ushort[] vertices, int[] bones, uint lod)
        {
            var part = new SkinPartitionInfo { LodLevel = lod };

            part.Vertices.AddRange(vertices);
            part.Bones.AddRange(bones);
            skin.Partitions.Add(part);
        }

        /// <summary>A scene holding one geometry and the bones the skin names.</summary>
        private static (FbxScene Scene, FbxObject Geometry) Staged()
        {
            var scene = new FbxScene(new FbxDocument());

            FbxObject model = scene.AddObject("Model", "mesh", "Mesh");
            FbxObject geometry = scene.AddObject("Geometry", "mesh", "Mesh");

            scene.ConnectToRoot(model);
            scene.Connect(geometry, model);

            var bones = new Dictionary<string, FbxObject>(StringComparer.Ordinal);

            foreach (string name in new[] { "Bone0", "Bone1" })
            {
                FbxObject bone = scene.AddObject("Model", name, "LimbNode");
                scene.ConnectToRoot(bone);
                bones[name] = bone;
            }

            Assert.Empty(FbxSkinIO.AddSkin(scene, geometry, Sliced(), bones, NifTransform.Identity));

            return (scene, geometry);
        }

        [Fact]
        public void ThreePartitionsAreOneDeformer()
        {
            (FbxScene scene, FbxObject geometry) = Staged();

            Assert.Single(
                scene.ChildrenOf(geometry.Id),
                o => o.Class == "Deformer" && o.SubClass == "Skin");
        }

        [Fact]
        public void AndEveryWeightIsWrittenOnce()
        {
            (FbxScene scene, FbxObject geometry) = Staged();

            SkinData back = Assert.IsType<SkinData>(FbxSkinIO.ReadSkin(scene, geometry));

            Assert.Equal(2, back.Bones.Count);
            Assert.All(back.Bones, b => Assert.Equal(6, b.Weights.Count));
        }

        [Fact]
        public void AndTheSlicesComeBackAsTheyWere()
        {
            (FbxScene scene, FbxObject geometry) = Staged();

            SkinData back = Assert.IsType<SkinData>(FbxSkinIO.ReadSkin(scene, geometry));
            SkinData was = Sliced();

            Assert.Equal(was.Partitions.Count, back.Partitions.Count);

            for (int i = 0; i < was.Partitions.Count; i++)
            {
                Assert.Equal(was.Partitions[i].Vertices, back.Partitions[i].Vertices);
                Assert.Equal(was.Partitions[i].Bones, back.Partitions[i].Bones);
                Assert.Equal(was.Partitions[i].LodLevel, back.Partitions[i].LodLevel);
            }
        }

        /// <summary>
        /// The views and the body slots ride where a DCC tool keeps them.
        /// </summary>
        /// <remarks>
        /// Not a style point. A deformer's properties are the deformer's own business
        /// and Blender writes a deformer of its own, so anything recorded there is
        /// gone: a draugr's body came back with one body part where its file has three,
        /// and had done all along. The dismemberment is what lets armour hide the body
        /// under it, so losing it is not cosmetic.
        ///
        /// A node's properties survive. What is asserted is where each thing is, since
        /// that is the whole of the difference, and the round trip through Blender is
        /// what proved it rather than anything in the format's own terms.
        /// </remarks>
        [Fact]
        public void TheSlicesRideOnTheNodeWhereADccToolKeepsThem()
        {
            (FbxScene scene, FbxObject geometry) = Staged();

            FbxObject node = Assert.Single(
                scene.ParentsOf(geometry.Id), o => o.Class == "Model");

            Assert.Equal("3", node.Properties.GetString(FbxSkinIO.PartitionCountProperty));
            Assert.NotEqual(string.Empty, node.Properties.GetString($"{FbxSkinIO.PartitionViewPrefix}0_vertices"));
        }

        /// <summary>The seam vertex is in two slices and weighted once.</summary>
        [Fact]
        public void ASeamVertexIsInBothSlicesAndWeightedOnce()
        {
            (FbxScene scene, FbxObject geometry) = Staged();

            SkinData back = Assert.IsType<SkinData>(FbxSkinIO.ReadSkin(scene, geometry));

            Assert.Contains(back.Partitions[0].Vertices, v => v == 2);
            Assert.Contains(back.Partitions[1].Vertices, v => v == 2);

            foreach (SkinBone bone in back.Bones)
                Assert.Single(bone.Weights, w => w.Vertex == 2);
        }
    }
}
