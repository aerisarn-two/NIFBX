using System.Numerics;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Nif;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Whether a skin's own arithmetic closes on itself.
    /// </summary>
    /// <remarks>
    /// `SkinTransform` takes a vertex from the mesh into a bone and the bone's world
    /// transform takes it out again, so composing the two gives where the mesh
    /// stands -- and that is one answer for the whole skin, whichever bone is asked.
    /// A file that says otherwise is not being read the way it was written.
    ///
    /// This is here because it is one of the few checks a converter can make that
    /// does not compare its own output with itself. A NIF -> FBX -> NIF trip cannot
    /// see a fault the reader and the writer share, and neither can comparing an FBX
    /// against the NIF it came from when both sides were built on the same reading:
    /// applying a NIF's rotations transposed reinterprets the whole file
    /// consistently, so every such comparison still agrees while every model comes
    /// out mirrored. This does not agree. With the rotations transposed the bones of
    /// dlc1sabrecat disagree by 227 units, prisonerrags_0's by 92; read correctly all
    /// of them agree exactly.
    /// </remarks>
    public class SkinBindConsistencyTests
    {
        /// <summary>
        /// How far the per-bone answers may lie apart, in NIF units.
        /// </summary>
        /// <remarks>
        /// Small but not zero: a file states these matrices to its own precision, and
        /// nightingalebanneranim01's seven bones land within a twentieth of a unit of
        /// each other. The fault this exists to catch is three orders of magnitude
        /// larger.
        /// </remarks>
        private const double Tolerance = 0.5;

        [Theory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void EveryBoneAgreesOnWhereItsMeshStands(string name)
        {
            NifModel m = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name),
                NifXmlDatabase.LoadEmbedded());

            var world = new Dictionary<string, NifTransform>(StringComparer.Ordinal);

            void Walk(NifItem node, NifTransform above)
            {
                NifTransform here = m.GetTransform(node).ComposedWith(above);
                world[m.GetName(node)] = here;

                foreach (NifItem child in m.GetRefArray(node, "Children"))
                    Walk(child, here);
            }

            foreach (NifItem root in m.GetRefArray(m.Footer, "Roots"))
                Walk(root, NifTransform.Identity);

            foreach (NifItem shape in m.Blocks.Where(b => m.GetRef(b, "Skin") is not null))
            {
                if (NifSkinAccess.ReadSkin(m, shape) is not { } skin || skin.Bones.Count < 2)
                    continue;

                Vector3? first = null;
                string firstBone = string.Empty;

                foreach (SkinBone bone in skin.Bones)
                {
                    // A bone the file names but the tree does not reach says nothing
                    // about where the mesh is.
                    if (!world.TryGetValue(bone.Name, out NifTransform stands))
                        continue;

                    Matrix4x4 placed = bone.SkinTransform.ComposedWith(stands).ToMatrix();
                    var at = new Vector3(placed.M41, placed.M42, placed.M43);

                    if (first is null)
                    {
                        first = at;
                        firstBone = bone.Name;
                        continue;
                    }

                    Assert.True(
                        (first.Value - at).Length() < Tolerance,
                        $"{name}: '{m.GetName(shape)}' -- '{firstBone}' puts the mesh at "
                        + $"({first.Value.X:F2}, {first.Value.Y:F2}, {first.Value.Z:F2}) and "
                        + $"'{bone.Name}' at ({at.X:F2}, {at.Y:F2}, {at.Z:F2}). A skin has one "
                        + "bind pose, so the file is not being read the way it was written");
                }
            }
        }
    }
}
