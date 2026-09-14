using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Scaffolding a DCC tool built to show a file with, coming back as model.
    /// </summary>
    /// <remarks>
    /// A tool reading one of these files usually has to build things the NIF has
    /// never heard of. SKDcc's particle add-on makes an emitter volume to emit
    /// from, a force field for each gravity modifier and a quad for the particles
    /// to be, because Blender has no emitter, no gravity term and no sprite.
    ///
    /// Exported and converted back, all of it became model: the campfire came home
    /// at 131 blocks against 108, with eleven <c>NiNode</c>s and nine
    /// <c>BSTriShape</c>s that were never in the file. Clearing the add-on's work
    /// before exporting avoids it and depends on remembering to.
    ///
    /// So the scaffolding marks itself and this walk passes over it, along with
    /// anything hanging under it — Blender bakes every particle out as a real
    /// object parented to the emitter, so skipping the node alone would leave fifty
    /// copies of the quad behind.
    /// </remarks>
    public class GeneratedNodeTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Source() => NifModel.Load(
            Path.Combine(
                AppContext.BaseDirectory, "Resources", "nifly", "TestNifFile_Animated_LE.nif"),
            Db);

        private static NifModel Rebuild(FbxDocument document) =>
            new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = "test", LegendaryEdition = true }).Convert(Db);

        /// <summary>Whether the rebuilt model has a block of that name.</summary>
        private static bool Has(NifModel model, string name) =>
            model.Blocks.Any(b => model.GetString(b, "Name") == name);

        /// <summary>One of the file's own nodes, marked as if an add-on made it.</summary>
        /// <remarks>
        /// Marking something the file really has, rather than inventing a node to
        /// mark: it takes the same path through the walk as everything else, so the
        /// test says what the mark does and nothing about how the fixture was built.
        /// </remarks>
        private static FbxDocument Marking(string name, out FbxDocument plain)
        {
            plain = new NifToFbx(Source()).Convert();

            FbxDocument marked = new NifToFbx(Source()).Convert();
            FbxObject node = new FbxScene(marked).OfClass("Model").First(o => o.Name == name);

            node.Properties.Set(
                FbxNodeType.GeneratedProperty, "int", "", FbxProperties.UserFlags, 1);

            return marked;
        }

        [Fact]
        public void AnUnmarkedNodeIsConverted()
        {
            // The control, and what makes the test below mean anything.
            Assert.True(Has(Rebuild(new NifToFbx(Source()).Convert()), "Glow"));
        }

        [Fact]
        public void AMarkedNodeIsPassedOver()
        {
            FbxDocument marked = Marking("Glow", out FbxDocument plain);

            NifModel before = Rebuild(plain);
            NifModel after = Rebuild(marked);

            Assert.True(Has(before, "Glow"));
            Assert.False(Has(after, "Glow"), "a marked node should not be converted");

            // And it took its subtree with it, which is the half that matters:
            // Blender bakes every particle out as a real object parented to the
            // emitter, so skipping the node alone would leave the copies behind.
            Assert.True(after.Blocks.Count < before.Blocks.Count);
        }

        [Fact]
        public void TheMarkIsReadWhateverTypeItCarries()
        {
            // A DCC tool writes whatever its own language calls true, and Blender
            // writes an integer. Asking for it as a string found nothing on every
            // node, which is how the first version of this passed its own tests and
            // changed nothing about a real file.
            var node = new FbxScene(new FbxDocument()).AddObject("Model", "x", "Null");

            Assert.False(FbxNodeType.IsGenerated(node));

            node.Properties.Set(
                FbxNodeType.GeneratedProperty, "int", "", FbxProperties.UserFlags, 1);

            Assert.True(FbxNodeType.IsGenerated(node));
        }
    }
}
