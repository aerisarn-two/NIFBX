using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// The controllers that configure a particle system, rather than play on it.
    /// </summary>
    /// <remarks>
    /// A <c>NiPSysEmitterCtlr</c> holds the birth rate and the emission window. Both
    /// are what the effect *is*, in the same way its modifier stack is, and the rest
    /// of the system already travels as settings on its node. But the controller
    /// holds interpolators, so it used to go the animation route instead — into a
    /// stack this port invents to hold controllers no sequence names — and any tool
    /// that drops that stack dropped the emission with it. Blender drops it.
    ///
    /// The interpolators are hardly animation. Of the game's 1,704 emitter
    /// controllers, 1,600 hold a birth rate with no data block at all and 1,055 an
    /// emitter-active track with none either; of the 649 that do carry keys, every
    /// one's first and last key time is the controller's own start and stop, the
    /// commonest shape being on at the start and off at the stop. Only a dozen blink.
    ///
    /// So a controller on a particle system that no sequence names travels with the
    /// system. Nothing is lost on the ones that do animate, because the carrier takes
    /// the whole interpolator and its data block, keys included.
    /// </remarks>
    public class ParticleControllerTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private const string SystemName = "Flames";

        private const string EmitterName = "NiPSysCylinderEmitter:0";

        private const float Rate = 15f;

        private const float Stop = 3.33333f;

        /// <summary>
        /// A particle system with an emitter controller and nothing playing it.
        /// </summary>
        /// <remarks>
        /// Built rather than loaded: the committed fixture's emitter controller holds
        /// blend interpolators, which is the mark of one a sequence drives, and that
        /// is the other case (see <see cref="ASequencedControllerIsLeftToItsSequence"/>).
        /// A file with a standalone one is a vanilla effect mesh, and those are not
        /// committed here.
        /// </remarks>
        private static NifModel Build()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem system = model.InsertBlock("NiParticleSystem");
            model.SetString(system, "Name", SystemName);
            model.SetRef(system, "Data", model.InsertBlock("NiPSysData"));

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(system));

            NifItem emitter = model.InsertBlock("NiPSysCylinderEmitter");
            model.SetString(emitter, "Name", EmitterName);
            model.SetRef(emitter, "Target", system);

            if (model.SetArraySize(system, "Num Modifiers", "Modifiers", 1) is { } stack)
                stack.Children[0].Value.SetLink(model.IndexOf(emitter));

            NifItem controller = model.InsertBlock("NiPSysEmitterCtlr");
            model.SetString(controller, "Modifier Name", EmitterName);
            model.FindItem(controller, "Stop Time")!.Value.SetFloat(Stop);
            model.SetRef(controller, "Target", system);
            model.SetRef(system, "Controller", controller);

            // The commonest shape in the game: a rate that never changes, stored in an
            // interpolator because that is the slot the controller has.
            NifItem rate = model.InsertBlock("NiFloatInterpolator");
            model.FindItem(rate, "Value")!.Value.SetFloat(Rate);
            model.SetRef(controller, "Interpolator", rate);

            model.SetRef(controller, "Visibility Interpolator", BuildWindow(model));

            return model;
        }

        /// <summary>On at the start, off at the stop: the emission window.</summary>
        private static NifItem BuildWindow(NifModel model)
        {
            NifItem data = model.InsertBlock("NiBoolData");

            model.FindItem(data, @"Data\Num Keys")!.Value.SetCount(2);
            data.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(data, @"Data\Keys")!;
            model.UpdateArraySize(keys);

            model.FindItem(keys.Children[0], "Time")!.Value.SetFloat(0f);
            model.FindItem(keys.Children[0], "Value")!.Value.SetCount(1);
            model.FindItem(keys.Children[1], "Time")!.Value.SetFloat(Stop);
            model.FindItem(keys.Children[1], "Value")!.Value.SetCount(0);

            NifItem interpolator = model.InsertBlock("NiBoolInterpolator");
            model.SetRef(interpolator, "Data", data);
            return interpolator;
        }

        private static FbxDocument Exported(NifModel model) => new NifToFbx(model).Convert();

        private static NifModel Rebuild(FbxDocument document) =>
            new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = "root", LegendaryEdition = true }).Convert(Db);

        /// <summary>
        /// Every animation record taken out, as Blender leaves a file whose only
        /// animation was on custom properties.
        /// </summary>
        private static FbxDocument WithoutAnimation(FbxDocument document)
        {
            FbxNode objects = document["Objects"]!;
            FbxNode connections = document["Connections"]!;

            bool IsAnimation(FbxNode? n) => n?.Name is
                "AnimationStack" or "AnimationLayer" or "AnimationCurveNode" or "AnimationCurve";

            var dropped = objects.Nodes
                .Where(IsAnimation)
                .Select(n => Convert.ToInt64(n!.Properties[0]))
                .ToHashSet();

            objects.Nodes.RemoveAll(IsAnimation);

            connections.Nodes.RemoveAll(
                c => c is not null
                    && c.Properties.Count >= 3
                    && (dropped.Contains(Convert.ToInt64(c.Properties[1]))
                        || dropped.Contains(Convert.ToInt64(c.Properties[2]))));

            return document;
        }

        /// <summary>The emitter controller of the rebuilt system, as text to compare.</summary>
        private static string Emission(NifModel model)
        {
            NifItem? controller = model.Blocks.FirstOrDefault(b => b.Name == "NiPSysEmitterCtlr");

            if (controller is null)
                return "no emitter controller";

            NifItem? rate = model.GetRef(controller, "Interpolator");
            NifItem? window = model.GetRef(controller, "Visibility Interpolator");
            NifItem? data = window is null ? null : model.GetRef(window, "Data");

            var keys = data is null
                ? []
                : model.FindItem(data, @"Data\Keys")?.Children ?? [];

            return $"modifier={model.GetString(controller, "Modifier Name")} "
                + $"stop={model.FindItem(controller, "Stop Time")?.Value} "
                + $"rate={(rate is null ? "-" : model.FindItem(rate, "Value")?.Value)} "
                + $"window=[{string.Join(", ", keys.Select(
                    k => $"{model.FindItem(k, "Time")?.Value}={model.FindItem(k, "Value")?.Value}"))}]";
        }

        [Fact]
        public void AStandaloneEmitterControllerIsNotAnimation()
        {
            FbxDocument document = Exported(Build());
            var scene = new FbxScene(document);

            // It travels as settings on the node, beside the system and its modifiers.
            FbxObject node = scene.OfClass("Model").First(o => o.Name == SystemName);

            Assert.Equal("1", node.Properties.GetString(FbxNodeControllers.CountProperty));
            Assert.Equal(
                "NiPSysEmitterCtlr",
                node.Properties.GetString($"{FbxNodeControllers.Prefix}0_type"));

            // And not into the stack this port invents for controllers no sequence
            // names, which is the thing a DCC tool is free to throw away.
            Assert.Empty(scene.OfClass("AnimationStack"));
            Assert.Empty(scene.OfClass("AnimationCurve"));
        }

        [Fact]
        public void TheEmissionSurvivesADocumentWithNoAnimation()
        {
            string whole = Emission(Rebuild(Exported(Build())));
            string stripped = Emission(Rebuild(WithoutAnimation(Exported(Build()))));

            // The rate and the window themselves, not that a controller came back:
            // one rebuilt with rate zero and no keys passes a census and emits nothing.
            Assert.Equal(
                $"modifier={EmitterName} stop={Stop} rate={Rate} window=[0=1, {Stop}=0]",
                whole);

            Assert.Equal(whole, stripped);
        }

        [Fact]
        public void ASequencedControllerIsLeftToItsSequence()
        {
            // The committed fixture's emitter controller is named by a sequence, and
            // its interpolators are the blend slots that mark it as such. That one is
            // the animation layer's, and carrying it here as well would rebuild it
            // twice -- so the structural carrier must not claim it.
            NifModel model = NifModel.Load(
                Path.Combine(
                    AppContext.BaseDirectory, "Resources", "nifly", "TestNifFile_Animated_LE.nif"),
                Db);

            var scene = new FbxScene(Exported(model));
            FbxObject node = scene.OfClass("Model").First(o => o.Name == "PCloud06");

            // Only the update switch, which holds no interpolator and always travelled
            // this way. Two would mean the emitter controller had been claimed as well.
            Assert.Equal("1", node.Properties.GetString(FbxNodeControllers.CountProperty));
            Assert.Equal(
                "NiPSysUpdateCtlr",
                node.Properties.GetString($"{FbxNodeControllers.Prefix}0_type"));
        }
    }
}
