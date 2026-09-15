using LeanMeshIO;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using NIFBX.Nif;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Collision surviving a full NIF to FBX to NIF trip, which is the only way to
    /// tell that the tessellating and the fitting agree with each other.
    /// </summary>
    public class CollisionImportTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        private static NifModel RoundTrip(string nif, out List<string> warnings)
        {
            NifModel source = NifModel.Load(PathTo(nif), Db);
            FbxDocument document = new NifToFbx(source).Convert();

            var converter = new FbxToNif(new FbxScene(document), new FbxToNifOptions
            {
                RootName = Path.GetFileNameWithoutExtension(nif),
                LegendaryEdition = true
            });

            NifModel rebuilt = converter.Convert(Db);
            warnings = converter.Warnings;

            using var stream = new MemoryStream();
            rebuilt.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        public static TheoryData<string, string> CollisionFiles() => new()
        {
            { "generate_rb_box.nif", "bhkBoxShape" },
            { "generate_rb_sphere.nif", "bhkSphereShape" },
            { "generate_rb.nif", "bhkConvexVerticesShape" }
        };

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void RebuildsTheCollisionObject(string nif, string unusedShape)
        {
            NifModel model = RoundTrip(nif, out _);

            Assert.Contains(model.Blocks, b => b.Name == "bhkCollisionObject");
            Assert.Contains(model.Blocks, b => model.BlockInherits(b, "bhkRigidBody"));
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void ABodyAuthoredWithoutSettingsGetsBethesdaCommonestOnes(string nif, string unusedShape)
        {
            // A body modelled in a DCC tool carries no nif_rb_* properties at all, so
            // the scalars have to fall back to something. That something is Bethesda's
            // own commonest value, not nif.xml's default, because the two disagree:
            // vanilla damping sits on a 1/1024 grid (0.099609375, not 0.1) and a
            // static's penetration depth is 0.1 where nif.xml says 0.15.
            NifModel source = NifModel.Load(PathTo(nif), Db);
            FbxDocument document = new NifToFbx(source).Convert();
            var scene = new FbxScene(document);

            // Strip every carried setting, leaving the layer -- which is what decides
            // static from moving, and which a DCC body does carry by convention.
            foreach (FbxObject node in scene.Objects.Where(o => o.Class == "Model"))
            {
                foreach (FbxRigidBodyInfo.Scalar scalar in FbxRigidBodyInfo.Scalars)
                    node.Properties.Remove(scalar.Property);
            }

            var converter = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = Path.GetFileNameWithoutExtension(nif),
                LegendaryEdition = true
            });

            NifModel rebuilt = converter.Convert(Db);

            List<NifItem> bodies =
                [.. rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "bhkRigidBody"))];

            Assert.NotEmpty(bodies);

            foreach (NifItem body in bodies)
            {
                bool isStatic = FbxRigidBodyInfo.IsStatic(FbxCollisionMaterial.LayerOf(rebuilt, body));

                foreach (FbxRigidBodyInfo.Scalar scalar in FbxRigidBodyInfo.Scalars)
                {
                    NifItem? item = rebuilt.FindItem(body, $@"Rigid Body Info\{scalar.Field}");

                    Assert.NotNull(item);
                    Assert.Equal(scalar.Default(isStatic), item!.Value.ToFloat());
                }
            }
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void RebuildsTheSameShapeKind(string nif, string expectedShape)
        {
            NifModel model = RoundTrip(nif, out _);

            // The suffix written on export is what picks the primitive on import, so
            // a box must come back a box rather than a hull of its corners.
            Assert.Contains(model.Blocks, b => b.Name == expectedShape);
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void ConvertsWithoutWarnings(string nif, string unusedShape)
        {
            RoundTrip(nif, out List<string> warnings);

            Assert.Empty(warnings);
        }

        /// <summary>
        /// A body's centre of mass and inertia tensor come back as the file wrote them.
        /// </summary>
        /// <remarks>
        /// Both are authored and neither was carried. The centre never reached the FBX
        /// at all, so every body came back centred on its own origin; the tensor was
        /// recomputed from the mass and the shape, which is the right answer for a body
        /// somebody authored in a DCC tool and the wrong one for a body that arrived
        /// with a tensor of its own. Bethesda's differ from the computed ones -- a
        /// draugr's neck holds 0.485 where the computation gives 0.101 -- and between
        /// them they were 59 of the 72 fields its skeleton came back disagreeing about.
        ///
        /// The values are put on the fixture here rather than found in one. The three
        /// committed bodies centre themselves on their own origin and hold a tensor the
        /// computation reproduces, so none of them can tell a carried value from a
        /// recomputed one -- and the file that does hold real ones,
        /// `TestNifFile_DeepGraph_SE`, holds them on statics, whose mass properties are
        /// dropped on purpose (a static carrying a mass is treated as movable, which is
        /// how scenery falls through the world).
        /// </remarks>
        [Fact]
        public void ABodyKeepsTheCentreAndTensorItWasAuthoredWith()
        {
            NifModel source = NifModel.Load(PathTo("generate_rb.nif"), Db);
            NifItem body = Assert.Single(Bodies(source));

            // On a layer that simulates. Mass properties are dropped for a static, on
            // purpose -- a static carrying a mass is treated as movable, which is how
            // scenery falls through the world -- and every committed fixture is one.
            Layer(source, body, "SKYL_BIPED");

            // Numbers no formula would arrive at, so a recomputed answer cannot pass.
            var centre = new NifVector4(0.125f, -0.375f, 0.5f, 0f);
            source.FindItem(body, @"Rigid Body Info\Center")!.Value.Set(centre);

            for (int i = 0; i < FbxRigidBodyInfo.InertiaFields.Count; i++)
            {
                source.FindItem(
                    body, $@"Rigid Body Info\Inertia Tensor\{FbxRigidBodyInfo.InertiaFields[i]}")!
                    .Value.SetFloat(1f + i);
            }

            NifModel rebuilt = RoundTrip(source);
            NifItem after = Assert.Single(Bodies(rebuilt));

            NifVector4 back = rebuilt.FindItem(after, @"Rigid Body Info\Center")!.Value.Get<NifVector4>();

            Assert.Equal(centre.X, back.X, 5);
            Assert.Equal(centre.Y, back.Y, 5);
            Assert.Equal(centre.Z, back.Z, 5);

            for (int i = 0; i < FbxRigidBodyInfo.InertiaFields.Count; i++)
            {
                Assert.Equal(
                    1f + i,
                    rebuilt.FindItem(
                        after, $@"Rigid Body Info\Inertia Tensor\{FbxRigidBodyInfo.InertiaFields[i]}")!
                        .Value.ToFloat(),
                    4);
            }
        }

        /// <summary>Puts a body on a named collision layer.</summary>
        private static void Layer(NifModel model, NifItem body, string layer)
        {
            Assert.True(
                model.Database.TryGetEnumOptionValue("SkyrimLayer", layer, out uint value),
                $"nif.xml has no SkyrimLayer called {layer}");

            // By type rather than by name: twelve things under a body are called
            // `Layer`, and the one that decides is the one typed as the enum -- which
            // is how `FbxCollisionMaterial.LayerOf` finds it.
            NifItem field = Flatten(body).First(i => i.Type == FbxCollisionMaterial.LayerEnum);

            field.Value.SetCount(value);
        }

        /// <summary>Every item under one, itself included.</summary>
        private static IEnumerable<NifItem> Flatten(NifItem item)
        {
            yield return item;

            foreach (NifItem child in item.Children)
            {
                // Not through a reference: that is another block, not part of this one.
                if (child.Value.IsLink)
                    continue;

                foreach (NifItem inner in Flatten(child))
                    yield return inner;
            }
        }

        /// <summary>The bodies of a model, in block order.</summary>
        private static IEnumerable<NifItem> Bodies(NifModel model) =>
            model.Blocks.Where(b => b.Name is "bhkRigidBody" or "bhkRigidBodyT");

        /// <summary>A model out to FBX and back, without going through a file first.</summary>
        private static NifModel RoundTrip(NifModel source)
        {
            FbxDocument document = new NifToFbx(source).Convert();

            NifModel rebuilt = new FbxToNif(new FbxScene(document), new FbxToNifOptions
            {
                LegendaryEdition = true
            }).Convert(Db);

            using var stream = new MemoryStream();
            rebuilt.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        [Fact]
        public void CollisionAttachesToTheNodeItCameFrom()
        {
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            NifItem? owner = model.Blocks.FirstOrDefault(b =>
                model.BlockInherits(b, "NiAVObject") && model.GetRef(b, "Collision Object") is not null);

            Assert.NotNull(owner);

            NifItem collision = model.GetRef(owner!, "Collision Object")!;

            // ...and points back at it.
            Assert.Equal(model.IndexOf(owner!), model.FindItem(collision, "Target")!.Value.ToLink());
        }

        [Fact]
        public void BoxKeepsItsDimensions()
        {
            NifModel source = NifModel.Load(PathTo("generate_rb_box.nif"), Db);
            NifVector3 before = source.FindItem(
                source.Blocks.First(b => b.Name == "bhkBoxShape"), "Dimensions")!.Value.Get<NifVector3>();

            NifModel model = RoundTrip("generate_rb_box.nif", out _);
            NifVector3 after = model.FindItem(
                model.Blocks.First(b => b.Name == "bhkBoxShape"), "Dimensions")!.Value.Get<NifVector3>();

            // Tessellated to metres-scaled geometry and fitted back, so a couple of
            // decimal places is the honest tolerance.
            Assert.Equal(before.X, after.X, 3);
            Assert.Equal(before.Y, after.Y, 3);
            Assert.Equal(before.Z, after.Z, 3);
        }

        [Fact]
        public void SphereKeepsItsRadius()
        {
            NifModel source = NifModel.Load(PathTo("generate_rb_sphere.nif"), Db);
            float before = source.FindItem(
                source.Blocks.First(b => b.Name == "bhkSphereShape"), "Radius")!.Value.ToFloat();

            NifModel model = RoundTrip("generate_rb_sphere.nif", out _);
            float after = model.FindItem(
                model.Blocks.First(b => b.Name == "bhkSphereShape"), "Radius")!.Value.ToFloat();

            // A tessellated sphere's vertices sit exactly on the radius, so the
            // fitted sphere should land close.
            Assert.Equal(before, after, 2);
        }

        [Fact]
        public void ConvexKeepsItsHullAndGainsPlanes()
        {
            NifModel model = RoundTrip("generate_rb.nif", out _);

            NifItem shape = model.Blocks.First(b => b.Name == "bhkConvexVerticesShape");

            uint vertices = model.GetUInt(shape, "Num Vertices");
            uint normals = model.GetUInt(shape, "Num Normals");

            Assert.True(vertices >= 4, $"a hull needs at least four vertices, got {vertices}");

            // Havok needs the face planes too; it does not derive them.
            Assert.True(normals > 0, "convex shapes must carry their face planes");
        }

        [Fact]
        public void StaticBodiesGetZeroMass()
        {
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            NifItem body = model.Blocks.First(b => model.BlockInherits(b, "bhkRigidBody"));

            // A static with a mass is treated as movable, which is how scenery ends
            // up falling through the world.
            Assert.Equal(0f, model.FindItem(body, @"Rigid Body Info\Mass")!.Value.ToFloat(), 5);
        }

        [Fact]
        public void StaticBodiesGetAZeroedInertiaTensor()
        {
            // ck-cmd zeroes the whole matrix alongside the mass, and its conversion
            // writes zero into the fourth column whatever the tensor held, so all
            // twelve components go. A static that keeps a tensor is a body Havok can
            // still be asked to spin.
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            NifItem body = model.Blocks.First(b => model.BlockInherits(b, "bhkRigidBody"));

            for (int row = 1; row <= 3; row++)
            {
                for (int column = 1; column <= 4; column++)
                {
                    string field = $@"Rigid Body Info\Inertia Tensor\m{row}{column}";

                    Assert.Equal(0f, model.FindItem(body, field)!.Value.ToFloat(), 6);
                }
            }
        }

        [Fact]
        public void BodyTransformReturnsToHavokMetres()
        {
            NifModel source = NifModel.Load(PathTo("generate_rb_box.nif"), Db);
            NifItem sourceBody = source.Blocks.First(b => source.BlockInherits(b, "bhkRigidBody"));
            NifVector4 before = source.FindItem(sourceBody, @"Rigid Body Info\Translation")!.Value.Get<NifVector4>();

            NifModel model = RoundTrip("generate_rb_box.nif", out _);
            NifItem body = model.Blocks.First(b => model.BlockInherits(b, "bhkRigidBody"));
            NifVector4 after = model.FindItem(body, @"Rigid Body Info\Translation")!.Value.Get<NifVector4>();

            // Scaled out to units and back to metres; the two factors are not exact
            // reciprocals, so this is close rather than equal.
            Assert.Equal(before.X, after.X, 2);
            Assert.Equal(before.Y, after.Y, 2);
            Assert.Equal(before.Z, after.Z, 2);
        }

        [Fact]
        public void RenderGeometrySurvivesAlongsideCollision()
        {
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            // The collision must not have displaced the visible mesh.
            Assert.Contains(model.Blocks, b => b.Name == "NiTriShape");
            Assert.Contains(model.Blocks, b => b.Name == "bhkBoxShape");
        }
    }
}
