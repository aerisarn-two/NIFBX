using LeanMeshIO;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Whether the file says which of its nodes are bones.
    /// </summary>
    /// <remarks>
    /// A NIF's bones are ordinary nodes and nothing marks them out. FBX says it twice
    /// -- in a model's subclass and in a `NodeAttribute` carrying `TypeFlags:
    /// Skeleton` -- and an importer that finds neither builds no armature. Blender
    /// gives you a pile of empties, which is not something anyone can animate.
    ///
    /// Two cases are easy to miss, and both were. A skeleton file has no skin to name
    /// its bones: `skeleton_cow` is forty-eight nodes with no geometry at all, and
    /// came out as ninety-five empties. And a bone carrying no weight of its own but
    /// with a weighted child is still part of the chain -- left unmarked it breaks the
    /// armature in two.
    /// </remarks>
    public class BoneMarkingTests
    {
        [Theory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void EveryBoneSaysItIsOne(string name)
        {
            NifModel m = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name),
                NifXmlDatabase.LoadEmbedded());

            var scene = new FbxScene(new NifToFbx(m).Convert());

            var limbs = new HashSet<string>(
                scene.Objects
                    .Where(o => o.Class == "Model" && o.SubClass == "LimbNode")
                    .Select(o => NameEncoding.Unsanitize(o.Name)),
                StringComparer.Ordinal);

            // Every bone a skin names.
            foreach (NifItem shape in m.Blocks.Where(b => m.GetRef(b, "Skin") is not null))
            {
                NifItem instance = m.GetRef(shape, "Skin")!;

                foreach (NifItem bone in m.GetRefArray(instance, "Bones"))
                {
                    string named = m.GetName(bone);

                    Assert.True(
                        limbs.Contains(named),
                        $"{name}: '{named}' is weighted by '{m.GetName(shape)}' and is not marked "
                        + "a bone, so an importer will build it as an empty");
                }
            }

            // A file with no geometry is a skeleton, and all of it is bones.
            bool anyGeometry = m.Blocks.Any(
                b => m.BlockInherits(b, "BSTriShape") || m.BlockInherits(b, "NiTriBasedGeom"));

            if (anyGeometry)
                return;

            // A root on its own is neither a skeleton nor a mesh, and says nothing
            // either way -- TestNifFile_RootNonZero is one.
            void Walk(NifItem node)
            {
                foreach (NifItem child in m.GetRefArray(node, "Children"))
                {
                    if (m.BlockInherits(child, "NiNode"))
                    {
                        Assert.True(
                            limbs.Contains(m.GetName(child)),
                            $"{name}: this file has no geometry, so it is a skeleton, and "
                            + $"'{m.GetName(child)}' is not marked a bone");
                    }

                    Walk(child);
                }
            }

            foreach (NifItem root in m.GetRefArray(m.Footer, "Roots"))
                Walk(root);
        }
    }
}
