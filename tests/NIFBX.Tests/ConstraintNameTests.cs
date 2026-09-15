using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// A constraint recognised by what it says, not only by what it is called.
    /// </summary>
    /// <remarks>
    /// An attachment point's name carries both body names — on a draugr,
    /// <c>NPC L Forearm_rb_con_NPC L Hand_rb_attach_point</c>, 85 characters. Blender
    /// caps an object name at 63 and replaces the tail with a hash, so that comes back
    /// as <c>..._rb_con_NPC4f2a91c6</c>: the separator survives but the far body's name
    /// does not, and a longer pair of names would lose the separator too.
    ///
    /// The properties say the same thing and have no length limit, so they are enough
    /// to recognise one by.
    /// </remarks>
    public class ConstraintNameTests
    {
        private static FbxObject Node(FbxScene scene, string name) =>
            scene.AddObject("Model", name, "Null");

        [Fact]
        public void TheNameIsStillEnough()
        {
            // ck-cmd names them and states nothing, and its scenes have to keep working.
            var scene = new FbxScene(new FbxDocument());

            Assert.True(FbxConstraintReader.IsAttachmentPoint(
                Node(scene, "Pelvis_rb_con_Neck_rb_attach_point")));
        }

        [Fact]
        public void SoIsSayingSoWhenTheNameHasBeenCut()
        {
            var scene = new FbxScene(new FbxDocument());
            FbxObject node = Node(scene, "Pelvis_rb_c");

            Assert.False(FbxConstraintReader.IsAttachmentPoint(node));

            node.Properties.SetUserString(FbxConstraintWriter.BodyAProperty, "Neck_rb");

            Assert.True(FbxConstraintReader.IsAttachmentPoint(node));
        }

        [Fact]
        public void AndTheTypeAloneWillDo()
        {
            var scene = new FbxScene(new FbxDocument());
            FbxObject node = Node(scene, "whatever_a_tool_called_it");

            node.Properties.SetUserString(FbxConstraintWriter.TypeProperty, "Ragdoll");

            Assert.True(FbxConstraintReader.IsAttachmentPoint(node));
        }

        [Fact]
        public void TheFarFrameIsStillNotAJoint()
        {
            // It is a child of an attachment point and inherits its name and its
            // properties; it is half of a joint, not another one.
            var scene = new FbxScene(new FbxDocument());
            FbxObject node = Node(scene, "Pelvis_rb_con_Neck_rb_attach_point_frame_a");

            node.Properties.SetUserString(FbxConstraintWriter.TypeProperty, "Ragdoll");
            node.Properties.SetUserString(FbxConstraintWriter.FrameProperty, "A");

            Assert.False(FbxConstraintReader.IsAttachmentPoint(node));
        }

        [Fact]
        public void APlainNodeIsStillAPlainNode()
        {
            var scene = new FbxScene(new FbxDocument());

            Assert.False(FbxConstraintReader.IsAttachmentPoint(Node(scene, "NPC Spine2")));
        }
    }
}
