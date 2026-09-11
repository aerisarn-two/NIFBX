using LeanMeshIO;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using NIFBX.Nif;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// The parts of a joint that HKFBX and this library have to say the same way.
    /// </summary>
    /// <remarks>
    /// The same ragdoll is in two files. A creature's <c>skeleton.nif</c> holds it
    /// hung off the rig bones in Havok's own units, and its <c>skeleton.hkx</c>
    /// holds it over a ragdoll skeleton of its own in game units. One FBX has to be
    /// readable by whichever library is going to write the other, so the things
    /// both can express are expressed identically: the two bodies, both frames of
    /// the joint, and the six limits ck-cmd named first.
    ///
    /// What stays private to this library is the <c>hkc_</c> dump of every
    /// descriptor field, which is what makes a NIF round trip byte-exact and is
    /// keyed by names only nif.xml knows.
    /// </remarks>
    public class SharedConstraintConventionTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load() =>
            NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "xpmsse", "skeleton_cow.nif"), Db);

        private static FbxDocument? _exported;

        private static FbxDocument Export() => _exported ??= new NifToFbx(Load()).Convert();

        private static FbxObject Joint() =>
            new FbxScene(Export()).OfClass("Model").First(
                node => node.Properties.GetString(FbxConstraintWriter.TypeProperty) == "Ragdoll"
                        && node.Properties.GetString(FbxConstraintWriter.FrameProperty) == "B");

        [Fact]
        public void AJointNamesBothOfItsBodies()
        {
            FbxObject joint = Joint();

            Assert.NotEqual(string.Empty, joint.Properties.GetString(FbxConstraintWriter.BodyAProperty));
            Assert.NotEqual(string.Empty, joint.Properties.GetString(FbxConstraintWriter.BodyBProperty));
        }

        /// <summary>
        /// The limits ck-cmd's importrig and HKFBX both read, beside the private
        /// dump rather than instead of it.
        /// </summary>
        [Fact]
        public void AJointCarriesTheSharedLimitsAndTheFullDescriptor()
        {
            FbxObject joint = Joint();

            foreach (string limit in new[]
                     {
                         "coneMaxAngle", "planeMinAngle", "planeMaxAngle",
                         "twistMinAngle", "twistMaxAngle", "maxFriction",
                     })
            {
                Assert.True(joint.Properties.Contains(limit), $"no {limit}");
            }

            Assert.True(joint.Properties.Contains(FbxConstraintWriter.FieldPrefix + NifFieldCodec.Key(string.Empty, "Cone Max Angle")));

            // And they agree, because they come from the same field.
            Assert.Equal(
                float.Parse(joint.Properties.GetString(FbxConstraintWriter.FieldPrefix + NifFieldCodec.Key(string.Empty, "Cone Max Angle")),
                    System.Globalization.CultureInfo.InvariantCulture),
                (float)joint.Properties.GetDouble("coneMaxAngle"),
                4);
        }

        /// <summary>
        /// A joint has two frames and a node's placement can only be one of them.
        /// ck-cmd computes the far one and writes only the near one, so a joint
        /// exported through it comes back with half of itself.
        /// </summary>
        [Fact]
        public void TheFarFrameRidesOnAChildNode()
        {
            var scene = new FbxScene(Export());
            FbxObject joint = Joint();

            FbxObject far = Assert.Single(
                scene.ChildrenOf(joint.Id),
                child => child.Properties.GetString(FbxConstraintWriter.FrameProperty) == "A");

            Assert.EndsWith(FbxConstraintWriter.FarFrameSuffix, far.Name, StringComparison.Ordinal);
        }

        /// <summary>
        /// And it is not mistaken for a joint of its own: it is a child of one and
        /// inherits its name, separator and all.
        /// </summary>
        [Fact]
        public void TheFarFrameIsNotReadAsAnotherJoint()
        {
            var scene = new FbxScene(Export());

            int joints = scene.ReadConstraints().Count;
            int ragdollsAndHinges = 11 + 12;

            Assert.Equal(ragdollsAndHinges, joints);
        }

        [Fact]
        public void TheFarFrameComesBackOnTheWayIn()
        {
            ConstraintImport constraint = new FbxScene(Export()).ReadConstraints()
                .First(c => c.Type == "Ragdoll");

            Assert.NotNull(constraint.FrameA);
        }

        /// <summary>
        /// Names do not survive: Blender caps an object name at 63 characters and
        /// rewrites the overflow as a hash, which is why the bodies are named in
        /// properties. A joint whose name has been mangled still resolves.
        /// </summary>
        [Fact]
        public void ARenamedJointStillKnowsItsBodies()
        {
            FbxDocument document = new NifToFbx(Load()).Convert();
            var scene = new FbxScene(document);

            FbxObject joint = scene.OfClass("Model").First(
                node => node.Properties.GetString(FbxConstraintWriter.TypeProperty) == "Ragdoll"
                        && node.Properties.GetString(FbxConstraintWriter.FrameProperty) == "B");

            string owner = joint.Properties.GetString(FbxConstraintWriter.BodyAProperty);
            string other = joint.Properties.GetString(FbxConstraintWriter.BodyBProperty);

            // As Blender leaves a long one: everything past its limit replaced by a
            // hash of itself. The cow's names are short enough to survive intact, so
            // the cut is made here where Blender would have made it on a longer
            // name — through the second bone name, taking the suffix with it.
            string qualified = joint.QualifiedName;
            int at = qualified.IndexOf(FbxConstraintWriter.NameSeparator, StringComparison.Ordinal);

            joint.QualifiedName =
                qualified[..(at + FbxConstraintWriter.NameSeparator.Length + 3)] + "4f2a91c6";

            Assert.DoesNotContain(FbxConstraintWriter.NameSuffix, joint.Name, StringComparison.Ordinal);

            ConstraintImport read = Assert.Single(
                scene.ReadConstraints(), c => c.OwnerName == owner);

            Assert.Equal(other, read.OtherName);
        }
    }
}
