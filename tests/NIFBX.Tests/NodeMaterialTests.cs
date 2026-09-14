using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// The material of a node that has no geometry, and surviving a tool that
    /// throws it away.
    /// </summary>
    /// <remarks>
    /// A particle system and an empty shape both hang their shader and alpha
    /// property off a <c>Model</c> with no <c>Geometry</c> under it. Blender reads
    /// such a node as an Empty, and an Empty carries no material, so everything the
    /// effect looks like was lost on the way through: a campfire came back without
    /// three <c>BSEffectShaderProperty</c> and three <c>NiAlphaProperty</c> blocks,
    /// one pair per system, while all seven of its real meshes kept theirs.
    ///
    /// So the material is mirrored onto the node as user properties, which Blender
    /// does carry. What is measured here is that mirror: that it is written where it
    /// is needed and nowhere else, that it rebuilds the same shader the material
    /// would have, and that it never wins against a material that is actually there.
    ///
    /// Blender is not needed to ask the question. Dropping every <c>Material</c> from
    /// the document is what Blender's pass amounts to, and doing it here makes the
    /// test exact and quick rather than dependent on a copy being installed —
    /// <see cref="BlenderRoundTripTests"/> is where the real thing runs.
    /// </remarks>
    public class NodeMaterialTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>The particle system in the fixture, and the mesh beside it.</summary>
        private const string System = "PCloud06";

        private const string Mesh = "Low02:1";

        private static NifModel Source() => NifModel.Load(
            Path.Combine(
                AppContext.BaseDirectory, "Resources", "nifly", "TestNifFile_Animated_LE.nif"),
            Db);

        /// <summary>The fixture as FBX, freshly built so a test may edit it.</summary>
        private static FbxDocument Exported() => new NifToFbx(Source()).Convert();

        private static NifModel Rebuild(FbxDocument document) =>
            new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = "test", LegendaryEdition = true }).Convert(Db);

        /// <summary>
        /// Every <c>Material</c> taken out of the document, as a DCC tool leaves it.
        /// </summary>
        /// <remarks>
        /// The connections go too. A connection naming an object that is no longer
        /// there is not what Blender writes, and leaving one would be testing against
        /// a file no tool produces.
        /// </remarks>
        private static FbxDocument WithoutMaterials(FbxDocument document)
        {
            FbxNode objects = document["Objects"]!;
            FbxNode connections = document["Connections"]!;

            var dropped = objects.Nodes
                .Where(n => n?.Name == "Material")
                .Select(n => Convert.ToInt64(n!.Properties[0]))
                .ToHashSet();

            objects.Nodes.RemoveAll(n => n?.Name == "Material");

            connections.Nodes.RemoveAll(
                c => c is not null
                    && c.Properties.Count >= 3
                    && (dropped.Contains(Convert.ToInt64(c.Properties[1]))
                        || dropped.Contains(Convert.ToInt64(c.Properties[2]))));

            return document;
        }

        /// <summary>The named block's shader and alpha property, as text to compare.</summary>
        /// <remarks>
        /// The fields rather than the block count: a shader that comes back with the
        /// right class and the wrong texture passes a census and renders nothing.
        /// </remarks>
        private static string Shading(NifModel model, string name)
        {
            NifItem block = model.Blocks.First(b => model.GetString(b, "Name") == name);
            NifItem? shader = model.GetRef(block, "Shader Property");
            NifItem? alpha = model.GetRef(block, "Alpha Property");

            string fields = shader is null
                ? "no shader"
                : string.Join(
                    " ",
                    new[]
                    {
                        "Source Texture", "Greyscale Texture", "Base Color", "Base Color Scale",
                        "Falloff Start Opacity", "Falloff Stop Opacity", "Soft Falloff Depth",
                        "Texture Clamp Mode", "Lighting Influence", "Shader Flags 1", "Shader Flags 2"
                    }
                        .Select(f => $"{f}={model.FindItem(shader, f)?.Value}"));

            return $"{shader?.Name ?? "-"} {fields} "
                + $"alpha={(alpha is null ? "-" : model.GetUInt(alpha, "Flags").ToString())}"
                + $"/{(alpha is null ? "-" : model.GetUInt(alpha, "Threshold").ToString())}";
        }

        private static FbxObject NodeNamed(FbxScene scene, string name) =>
            scene.OfClass("Model").First(o => o.Name == name);

        [Fact]
        public void AGeometrylessNodeCarriesItsMaterial()
        {
            var scene = new FbxScene(Exported());
            FbxObject node = NodeNamed(scene, System);

            Assert.True(
                FbxNodeMaterial.WasWritten(node),
                "a particle system's material has nowhere else to survive");

            // The mirror is the material, not a summary of it: every property the
            // material carries is on the node under the prefix.
            FbxObject material = scene.ChildrenOf(node.Id).First(o => o.Class == "Material");

            foreach (FbxProperty70 property in material.Properties.All)
                Assert.True(
                    node.Properties.Contains(FbxNodeMaterial.Prefix + property.Name),
                    $"{property.Name} was not mirrored");
        }

        [Fact]
        public void AMeshCarriesNoMirror()
        {
            var scene = new FbxScene(Exported());

            var meshes = scene.OfClass("Model")
                .Where(o => scene.ChildrenOf(o.Id).Any(c => c.Class == "Geometry"))
                .ToList();

            Assert.NotEmpty(meshes);

            // A mesh keeps its material through any tool, because there is geometry
            // for it to be the material of. Mirroring it would be bytes for nothing on
            // every shape in the game.
            foreach (FbxObject mesh in meshes)
                Assert.False(
                    FbxNodeMaterial.WasWritten(mesh),
                    $"{mesh.Name} has geometry and does not need a mirror");
        }

        [Fact]
        public void TheMirrorRebuildsTheSameShadingTheMaterialWould()
        {
            string whole = Shading(Rebuild(Exported()), System);
            string stripped = Shading(Rebuild(WithoutMaterials(Exported())), System);

            // Not "a shader came back" — the same shader, field for field, as the one
            // the material builds when it is there.
            Assert.Equal(whole, stripped);

            // And that this is a real comparison rather than two empties matching.
            Assert.Contains("BSEffectShaderProperty", whole, StringComparison.Ordinal);
            Assert.Contains("IceShards01", whole, StringComparison.Ordinal);
        }

        [Fact]
        public void AMeshStillLosesItsMaterialWithoutOne()
        {
            // The counterpart of the test above, and what makes it mean something: the
            // mesh has no mirror, so stripping the materials does take its shader. If
            // this ever passes by accident the test above is measuring nothing.
            string stripped = Shading(Rebuild(WithoutMaterials(Exported())), Mesh);

            Assert.Equal("- no shader alpha=-/-", stripped);
        }

        [Fact]
        public void TheMaterialWinsOverTheMirror()
        {
            FbxDocument document = Exported();
            var scene = new FbxScene(document);
            FbxObject node = NodeNamed(scene, System);
            FbxObject material = scene.ChildrenOf(node.Id).First(o => o.Class == "Material");

            // What a user does in a DCC tool: change the shading and export. The mirror
            // is left as it was, which is what a tool that does not know about it would
            // leave — so the two now disagree, and the material has to be the one that
            // counts.
            const string edited = @"textures\effects\EditedInABlender.dds";

            material.Properties.SetUserString("es_source_texture", edited);

            NifModel rebuilt = Rebuild(document);
            NifItem system = rebuilt.Blocks.First(b => rebuilt.GetString(b, "Name") == System);
            NifItem shader = rebuilt.GetRef(system, "Shader Property")!;

            Assert.Equal(edited, rebuilt.GetString(shader, "Source Texture"));
        }
    }
}
