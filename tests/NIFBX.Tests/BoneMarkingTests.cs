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
    ///
    /// And a chain has to hang from a marked node it does not own, because Blender
    /// spends the topmost bone on the armature object itself. A file whose bones are
    /// flat siblings -- most of the game's characters -- otherwise arrives as one
    /// empty armature per bone and nothing deforming the mesh.
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

            // Both kinds are bones: "Root" tops a chain and "LimbNode" hangs below one,
            // which the SDK reads back as FbxSkeleton::eRoot and eLimbNode.
            var limbs = new HashSet<string>(
                scene.Objects
                    .Where(o => o.Class == "Model" && o.SubClass is "LimbNode" or "Root")
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

            // The chain has to be whole: a node with a bone above it and a bone below
            // it lies on the path between them, so it is a bone too. Without that a
            // bone's parent can be an empty and the armature comes apart there.
            var parent = new Dictionary<NifItem, NifItem>();

            void Chain(NifItem node)
            {
                foreach (NifItem child in m.GetRefArray(node, "Children"))
                {
                    parent[child] = node;
                    Chain(child);
                }
            }

            var roots = m.GetRefArray(m.Footer, "Roots").ToList();

            foreach (NifItem root in roots)
                Chain(root);

            bool Below(NifItem node) =>
                m.GetRefArray(node, "Children").Any(c => limbs.Contains(m.GetName(c)) || Below(c));

            foreach (NifItem node in parent.Keys)
            {
                if (limbs.Contains(m.GetName(node)) || !Below(node))
                    continue;

                bool above = false;

                for (NifItem? at = parent.GetValueOrDefault(node);
                     at is not null;
                     at = parent.GetValueOrDefault(at))
                {
                    if (limbs.Contains(m.GetName(at))) { above = true; break; }
                }

                Assert.False(
                    above,
                    $"{name}: '{m.GetName(node)}' has a bone above it and a bone below it and "
                    + "is not one, so the chain between them is broken");
            }

            // ...and every chain hangs from a node that is marked too, because Blender
            // takes the topmost one to build the armature object out of and only what
            // is under it becomes bones. Leave a chain topped by a real bone and that
            // bone is the one the armature does not have.
            foreach (NifItem node in parent.Keys)
            {
                if (!limbs.Contains(m.GetName(node))) continue;
                if (parent.GetValueOrDefault(node) is not { } above) continue;

                Assert.True(
                    limbs.Contains(m.GetName(above)),
                    $"{name}: '{m.GetName(node)}' tops a chain, so Blender will spend it "
                    + $"on the armature; '{m.GetName(above)}' above it has to be marked "
                    + "instead");
            }

            // A file with no geometry is a skeleton, and all of it is bones.
            bool anyGeometry = m.Blocks.Any(
                b => m.BlockInherits(b, "BSTriShape") || m.BlockInherits(b, "NiTriBasedGeom"));

            if (anyGeometry)
                return;

            // A root on its own is neither a skeleton nor a mesh, and says nothing
            // either way -- TestNifFile_RootNonZero is one.
            foreach (NifItem node in parent.Keys)
            {
                if (!m.BlockInherits(node, "NiNode")) continue;

                Assert.True(
                    limbs.Contains(m.GetName(node)),
                    $"{name}: this file has no geometry, so it is a skeleton, and "
                    + $"'{m.GetName(node)}' is not marked a bone");
            }
        }
    }
}
