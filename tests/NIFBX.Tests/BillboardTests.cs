using LeanMeshIO;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// A billboard node's rotation, which belongs to the engine rather than the file.
    /// </summary>
    /// <remarks>
    /// A <c>NiBillboardNode</c> turns to face the camera every frame. The campfire has
    /// two: the glow over the fire (mode 3, always face the camera) and the heat haze
    /// above it (mode 1, rotate about up). Left as fixed planes in a DCC tool they are
    /// only right from one direction, which is what they looked like.
    ///
    /// Showing one honestly in Blender means a Track To constraint, and Blender's FBX
    /// exporter writes the *evaluated* transform: a plane authored at no rotation at
    /// all came back at 111.8, 0, -143.1 after a round trip through one. So the
    /// authored rotation travels as a property and is preferred on the way back, and a
    /// DCC tool may aim these however it likes without the aiming becoming the file.
    /// </remarks>
    public class BillboardTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private const string Name = "GlowPlane";

        /// <summary>A rotation nothing would arrive at by accident.</summary>
        private static readonly NifMatrix33 Authored = new()
        {
            M11 = 0f, M12 = -1f, M13 = 0f,
            M21 = 1f, M22 = 0f, M23 = 0f,
            M31 = 0f, M32 = 0f, M33 = 1f
        };

        private static NifModel Build()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem billboard = model.InsertBlock("NiBillboardNode");
            model.SetString(billboard, "Name", Name);
            model.SetTransform(billboard, new NifTransform(new NifVector3(), Authored, 1f));

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(billboard));

            return model;
        }

        private static NifModel Rebuild(FbxDocument document) =>
            new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = "root", LegendaryEdition = true }).Convert(Db);

        private static NifMatrix33 RotationOf(NifModel model, string name)
        {
            NifItem node = model.Blocks.First(b => model.GetString(b, "Name") == name);

            return model.FindItem(node, "Rotation")!.Value.Get<NifMatrix33>();
        }

        [Fact]
        public void TheAuthoredRotationTravels()
        {
            var scene = new FbxScene(new NifToFbx(Build()).Convert());
            FbxObject node = scene.OfClass("Model").First(o => o.Name == Name);

            Assert.NotEqual(
                string.Empty,
                node.Properties.GetString(FbxNodeType.BillboardRotationProperty));
        }

        [Fact]
        public void ItWinsOverWhateverTheSceneRotatedItTo()
        {
            FbxDocument document = new NifToFbx(Build()).Convert();
            var scene = new FbxScene(document);

            // What a Track To constraint leaves once an exporter has written the
            // evaluated transform out.
            FbxObject node = scene.OfClass("Model").First(o => o.Name == Name);
            node.Properties.SetVector3("Lcl Rotation", 111.8, 0.0, -143.1);

            NifMatrix33 rotation = RotationOf(Rebuild(document), Name);

            Assert.Equal(0f, rotation.M11, 3);
            Assert.Equal(-1f, rotation.M12, 3);
            Assert.Equal(1f, rotation.M21, 3);
        }

        [Fact]
        public void AnOrdinaryNodeKeepsWhatTheSceneSays()
        {
            // The control: without the mark, a rotation a DCC tool left is the
            // rotation, which is what every other node in the file relies on.
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem plain = model.InsertBlock("NiNode");
            model.SetString(plain, "Name", Name);
            model.SetTransform(plain, new NifTransform(new NifVector3(), Authored, 1f));

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(plain));

            FbxDocument document = new NifToFbx(model).Convert();
            var scene = new FbxScene(document);

            FbxObject node = scene.OfClass("Model").First(o => o.Name == Name);
            node.Properties.SetVector3("Lcl Rotation", 111.8, 0.0, -143.1);

            NifMatrix33 rotation = RotationOf(Rebuild(document), Name);

            Assert.NotEqual(0f, rotation.M11, 3);
        }
    }
}
