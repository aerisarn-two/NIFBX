using LeanMeshIO;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// A visibility track that outlives the curve carrying it.
    /// </summary>
    /// <remarks>
    /// A <c>NiVisController</c> travels as a curve on the node's standard
    /// <c>Visibility</c> property, and that is the right carrier: a tool that knows
    /// the property hides the object. Blender does not know it -- the curve is
    /// dropped on import, into nothing at all -- and the stacks Blender writes back
    /// are its own, so the stack that held the curve is gone too.
    ///
    /// Measured on a draugr's skeleton, whose <c>WEAPON</c> and <c>SHIELD</c> nodes
    /// each carry one: 187 blocks out, 181 back. The six are the two controllers,
    /// their interpolators and their data, and the weapon a draugr holds stops being
    /// hidden when the game says to hide it.
    ///
    /// So the track is written on the node as well, and read back from there when
    /// the curve is missing. What is asserted here is the losing case, because the
    /// winning one was already covered: strip every stack from the scene, which is
    /// what Blender leaves behind, and ask for the controller back.
    /// </remarks>
    public class VisibilityCarrierTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>How many keys the draugr's own tracks hold, at 1/30 apart.</summary>
        private const int Keys = 2601;

        [Fact]
        public void TheCurveIsStillTheCarrier()
        {
            FbxDocument document = ToFbx(WithVisibility());
            var scene = new FbxScene(document);

            Assert.Contains(
                scene.OfClass("AnimationCurveNode"),
                node => node.Name == AnimProperty.VisibilityName);
        }

        [Fact]
        public void AndTheNodeCarriesItToo()
        {
            var scene = new FbxScene(ToFbx(WithVisibility()));

            FbxObject node = Assert.Single(
                scene.OfClass("Model").Where(m => m.Name == "WEAPON"));

            Assert.NotEqual(string.Empty, node.Properties.GetString(FbxVisibilityCodec.Property));
        }

        [Fact]
        public void ATrackComesBackWhenTheCurveDoesNot()
        {
            NifModel source = WithVisibility();
            NifModel rebuilt = Rebuild(Stripped(ToFbx(source)));

            NifItem controller = Assert.Single(
                rebuilt.Blocks.Where(b => b.Name == AnimProperty.VisibilityController));

            NifItem interpolator = Assert.Single(
                rebuilt.Blocks.Where(b => b.Name == "NiBoolInterpolator"));

            NifItem data = Assert.Single(rebuilt.Blocks.Where(b => b.Name == "NiBoolData"));

            Assert.Same(interpolator, rebuilt.GetRef(controller, "Interpolator"));
            Assert.Same(data, rebuilt.GetRef(interpolator, "Data"));
        }

        /// <summary>
        /// And every key of it, at the times the file had rather than at regenerated
        /// ones.
        /// </summary>
        /// <remarks>
        /// The times look like a key every frame and are not exactly that: of a
        /// draugr's 2,601, only 1,862 are exactly <c>(float)(i / 30.0)</c>. A codec
        /// that regenerated them from a start and a step would come back close enough
        /// to pass a comparison and would still be handing back different numbers, so
        /// the awkward times are the ones written here.
        /// </remarks>
        [Fact]
        public void WithEveryKeyAtTheTimeItHad()
        {
            NifModel source = WithVisibility();
            float[] wanted = TimesOf(source);

            NifModel rebuilt = Rebuild(Stripped(ToFbx(source)));
            float[] got = TimesOf(rebuilt);

            Assert.Equal(Keys, got.Length);
            Assert.Equal(wanted, got);
        }

        [Fact]
        public void AndTheControllerKeepsItsFlags()
        {
            NifModel rebuilt = Rebuild(Stripped(ToFbx(WithVisibility())));

            NifItem controller = Assert.Single(
                rebuilt.Blocks.Where(b => b.Name == AnimProperty.VisibilityController));

            Assert.Equal(76u, rebuilt.GetUInt(controller, "Flags"));
        }

        /// <summary>A node with no track carries no property to be misread.</summary>
        [Fact]
        public void ANodeWithNothingToHideSaysNothing()
        {
            var scene = new FbxScene(ToFbx(WithVisibility()));

            foreach (FbxObject node in scene.OfClass("Model").Where(m => m.Name == "root"))
                Assert.Equal(string.Empty, node.Properties.GetString(FbxVisibilityCodec.Property));
        }

        // --- the pieces ---------------------------------------------------------

        /// <summary>A node whose visibility is animated, as a draugr's WEAPON is.</summary>
        private static NifModel WithVisibility()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem weapon = model.InsertBlock("NiNode");
            model.SetString(weapon, "Name", "WEAPON");

            model.SetArraySize(root, "Num Children", "Children", 1)!
                .Children[0].Value.SetLink(model.IndexOf(weapon));

            NifItem data = model.InsertBlock("NiBoolData");

            model.FindItem(data, @"Data\Num Keys")!.Value.SetCount(Keys);
            data.InvalidateConditionsRecursive();
            model.FindItem(data, @"Data\Interpolation")!.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(data, @"Data\Keys")!;
            model.UpdateArraySize(keys);

            for (int i = 0; i < Keys; i++)
            {
                // The awkward times on purpose -- see `WithEveryKeyAtTheTimeItHad`.
                model.FindItem(keys.Children[i], "Time")!.Value.SetFloat((float)(i / 30.0f));
                model.FindItem(keys.Children[i], "Value")!.Value.SetFloat(1f);
            }

            NifItem interpolator = model.InsertBlock("NiBoolInterpolator");
            model.SetRef(interpolator, "Data", data);

            NifItem controller = model.InsertBlock(AnimProperty.VisibilityController);
            model.SetRef(controller, "Interpolator", interpolator);
            model.FindItem(controller, "Flags")!.Value.SetCount(76);
            model.FindItem(controller, "Stop Time")!.Value.SetFloat(Keys / 30f);
            model.SetRef(controller, "Target", weapon);
            model.SetRef(weapon, "Controller", controller);

            return model;
        }

        private static float[] TimesOf(NifModel model)
        {
            NifItem data = Assert.Single(model.Blocks.Where(b => b.Name == "NiBoolData"));
            NifItem keys = model.FindItem(data, @"Data\Keys")!;

            return [.. keys.Children.Select(k => model.FindItem(k, "Time")!.Value.ToFloat())];
        }

        private static FbxDocument ToFbx(NifModel model) => new NifToFbx(model).Convert();

        /// <summary>What Blender leaves: the scene, with no animation in it at all.</summary>
        /// <remarks>
        /// Blunter than Blender, which reads the transform curves and writes its own
        /// stacks back. It is the visibility curve's fate that is being modelled, and
        /// that one Blender does not write back at all.
        /// </remarks>
        private static FbxDocument Stripped(FbxDocument document)
        {
            LeanMeshIO.Formats.Fbx.FbxNode objects =
                document.Nodes.First(n => n.Name == "Objects");

            objects.Nodes.RemoveAll(n => n.Name
                is "AnimationStack" or "AnimationLayer" or "AnimationCurveNode" or "AnimationCurve");

            return document;
        }

        private static NifModel Rebuild(FbxDocument document) =>
            new FbxToNif(new FbxScene(document)).Convert(Db);
    }
}
