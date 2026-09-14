using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// A particle system's constants, put where a tool can see them.
    /// </summary>
    /// <remarks>
    /// A controller a sequence drives holds its value in an interpolator with no data,
    /// and that value travels on the animation stack — which is whose it is, since the
    /// next sequence can hold a different one.
    ///
    /// It is also invisible to every DCC tool. Blender turns a stack into an Action and
    /// keeps none of its user properties: the lumbermill waterwheel's birth rate of 90
    /// a second reaches the FBX as
    /// <c>const_PArray07|NiPSysEmitterCtlr|NiPSysMeshEmitter:0|BirthRate</c> and is
    /// nowhere in the imported scene — not on an object, an action, a collection or the
    /// scene. An add-on building that effect had no rate and fell back to one particle
    /// a frame, 24 a second against the 90 the file asks for.
    ///
    /// Driven through <see cref="FbxAnimWriter.AddSequence"/> with data made here
    /// rather than through a game asset, so the suite tests this on any machine.
    /// </remarks>
    public class ParticleConstantTests
    {
        private const string Rate = "BirthRate";

        /// <summary>A stack holding one constant, on a node of the given block type.</summary>
        private static FbxScene Written(string blockType, float value = 90f, uint? flags = 104)
        {
            var scene = new FbxScene(new FbxDocument());
            FbxObject node = scene.AddObject("Model", "PArray07", "Null");

            node.Properties.SetUserString(FbxNodeType.Property, blockType);

            var track = new AnimTrack { NodeName = node.Name };

            track.Properties.Add(new AnimProperty
            {
                Name = $"NiPSysEmitterCtlr|NiPSysMeshEmitter:0|{Rate}",
                ControllerType = "NiPSysEmitterCtlr",
                ControllerId = "NiPSysMeshEmitter:0",
                InterpolatorId = Rate,
                Constant = value,
                ControllerFlags = flags,
            });

            var sequence = new AnimSequence { Name = "Idle", Start = 0f, Stop = 16.666668f };
            sequence.Tracks.Add(track);

            FbxAnimWriter.AddSequence(scene, sequence, _ => node);

            return scene;
        }

        private static FbxObject NodeOf(FbxScene scene) =>
            scene.OfClass("Model").First(o => o.Name == "PArray07");

        private static string Key(string suffix = "") =>
            $"{FbxNodeControllers.AnimatedFieldPrefix}NiPSysEmitterCtlr_NiPSysMeshEmitter:0_{Rate}{suffix}";

        [Fact]
        public void TheRateReachesTheNode()
        {
            FbxObject node = NodeOf(Written("NiParticleSystem"));

            Assert.Equal("90", node.Properties.GetString(Key()));
        }

        [Fact]
        public void ItTravelsUnderThePrefixASequencedControllerAlreadyUses()
        {
            // So a tool reading a sequenced controller's own fields is already reading
            // this: `nac_NiPSysEmitterCtlr_NiPSysMeshEmitter:0_Modifier Name` sits
            // beside it, written by the same convention.
            Assert.StartsWith(FbxNodeControllers.AnimatedFieldPrefix, Key(), StringComparison.Ordinal);
        }

        [Fact]
        public void TheControllerFlagsComeWithIt()
        {
            // Bits 1-2 are the cycle type, which says whether the emitter runs its span
            // once or goes back and runs it again — a campfire that burns against one
            // that goes out.
            FbxObject node = NodeOf(Written("NiParticleSystem"));

            Assert.Equal("104", node.Properties.GetString(Key(FbxAnimWriter.FlagsSuffix)));
        }

        [Fact]
        public void AStripParticleSystemCountsToo()
        {
            FbxObject node = NodeOf(Written("BSStripParticleSystem"));

            Assert.Equal("90", node.Properties.GetString(Key()));
        }

        [Fact]
        public void NothingElseGetsThem()
        {
            // A mirror that landed on every animated node would be a different feature
            // and a much larger file. An animated waterwheel is not a particle system,
            // and none of this is its business.
            FbxObject node = NodeOf(Written("NiNode"));

            Assert.False(node.Properties.Contains(Key()));
        }

        [Fact]
        public void TheStackStillHasIt()
        {
            // The mirror is an addition, not a move: the stack stays the authority on
            // the way back, so nothing here can change what the file rebuilds to.
            FbxScene scene = Written("NiParticleSystem");

            FbxObject stack = scene.OfClass("AnimationStack").First();

            Assert.True(stack.Properties.Contains(
                $"{FbxAnimWriter.ConstantPrefix}PArray07|NiPSysEmitterCtlr|NiPSysMeshEmitter:0|{Rate}"));
        }
    }
}
