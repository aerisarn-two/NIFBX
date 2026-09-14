using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// The texture slots past the second, and a DCC tool that will not hold them.
    /// </summary>
    /// <remarks>
    /// A <c>BSShaderTextureSet</c> has nine slots. FBX has a standard material
    /// property for the first two — <c>DiffuseColor</c> and <c>NormalMap</c> — and
    /// nothing at all for the glow map, the cubemap, its mask or the backlight, so
    /// the export names a property after the slot and connects the texture to that.
    ///
    /// Blender declines it: "material link 'slot5' ignored". The connection is gone
    /// from what it writes back, and the slot was rebuilt from connections alone, so
    /// the texture went with it. Of 24 effect meshes through Blender, all three that
    /// used a cubemap lost it and its mask while every diffuse and normal came back.
    ///
    /// The path is written beside the connection as a user property and does survive,
    /// so it stands in where the connection has gone. Second, not first: the
    /// connection is the thing a DCC tool lets a user rewire.
    /// </remarks>
    public class TextureSlotTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>The fixture, whose set fills slots 2, 4 and 7 as well.</summary>
        private const string Fixture = "TestNifFile_Animated_LE.nif";

        private static NifModel Source() => NifModel.Load(
            Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", Fixture), Db);

        private static FbxDocument Exported() => new NifToFbx(Source()).Convert();

        private static NifModel Rebuild(FbxDocument document) =>
            new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = "test", LegendaryEdition = true }).Convert(Db);

        /// <summary>
        /// Every <c>slotN</c> texture connection dropped, as Blender drops them.
        /// </summary>
        /// <remarks>
        /// Only those: the two standard properties are left alone, because Blender
        /// keeps those and a test that took them too would be measuring a file no
        /// tool produces.
        /// </remarks>
        private static FbxDocument WithoutSlotLinks(FbxDocument document)
        {
            FbxNode connections = document["Connections"]!;

            connections.Nodes.RemoveAll(
                c => c is not null
                    && c.Properties.Count >= 4
                    && c.Properties[0] as string == "OP"
                    && (c.Properties[3] as string)?.StartsWith("slot", StringComparison.Ordinal) == true);

            return document;
        }

        /// <summary>Every texture set in the model, as "slot:path" entries.</summary>
        private static List<string> Slots(NifModel model) =>
            [.. model.Blocks
                .Where(b => b.Name == "BSShaderTextureSet")
                .SelectMany(b => model.FindItem(b, "Textures")?.Children
                    .Select((c, i) => (Slot: i, Path: c.Value.ToString() ?? ""))
                    .Where(x => x.Path.Length > 0)
                    .Select(x => $"{x.Slot}:{x.Path}") ?? [])
                .OrderBy(x => x, StringComparer.Ordinal)];

        [Fact]
        public void TheFixtureUsesSlotsPastTheSecond()
        {
            // What makes the rest of this a test rather than a tautology.
            var high = Slots(Source()).Where(s => s[0] > '1').ToList();

            Assert.NotEmpty(high);
            Assert.Contains(high, s => s.Contains("cubemaps", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ASlotSurvivesItsConnectionBeingDropped()
        {
            List<string> whole = Slots(Rebuild(Exported()));
            List<string> stripped = Slots(Rebuild(WithoutSlotLinks(Exported())));

            // The paths themselves, slot by slot: a set rebuilt with the right number
            // of entries and the wrong ones in them renders the wrong thing.
            Assert.Equal(whole, stripped);
            Assert.Equal(Slots(Source()), whole);
        }

        [Fact]
        public void AConnectedTextureWinsOverTheCarriedPath()
        {
            FbxDocument document = Exported();
            var scene = new FbxScene(document);

            // What a user rewiring the slot in a DCC tool leaves: the texture on the
            // slot now names something else, while the property still records what
            // was exported. The texture is the one they changed, so it counts.
            const string edited = @"textures\cubemaps\EditedInADccTool_e.dds";

            FbxObject texture = scene.OfClass("Texture")
                .First(o => o.Name.EndsWith("slot5", StringComparison.Ordinal));

            texture.Node.Nodes.RemoveAll(n => n?.Name is "FileName" or "RelativeFilename");
            texture.Node.Nodes.Add(new FbxNode("FileName", edited));
            texture.Node.Nodes.Add(new FbxNode("RelativeFilename", edited));

            Assert.Contains($"4:{edited}", Slots(Rebuild(document)));
        }
    }
}
