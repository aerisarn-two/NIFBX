using LeanMeshIO.Formats.Fbx;
using NIFSharp;

namespace NIFBX.Fbx
{
    /// <summary>
    /// Carries a skeleton's rest pose across a DCC tool, which cannot keep it.
    /// </summary>
    /// <remarks>
    /// A Skyrim bone is a transform. It has no length and no direction, so an
    /// importer that means to draw it has to invent one, and Blender's invents the
    /// best one there is: it aims each bone at the bone below it. That is what makes
    /// a rig usable -- a skeleton drawn along its own chain instead of ninety sticks
    /// all pointing the same way -- and it is also destructive, because Blender
    /// writes the aimed rest pose back out and its exporter has no per-bone inverse
    /// for it. A draugr came back with 83 of its 93 nodes rotated.
    ///
    /// Nothing is lost by the aiming itself. Re-aiming a bone spins its frame about
    /// its own joint and never moves a joint, so the file's own numbers are still
    /// the right answer and only have to survive the trip. They travel in
    /// <see cref="ReferenceProperty"/>.
    ///
    /// Two things decide the shape of this. The first is that per-bone properties do
    /// not survive: Blender's importer drops a bone model's custom properties on the
    /// floor -- measured, 0 of 92 -- and keeps only those on the node it spends on
    /// the armature object. So the whole pose travels as one table on that node, the
    /// way a creature's clips already travel as one <c>sk_clips</c> manifest.
    ///
    /// The second is that this converter must not be the one to decide when to use
    /// it. Rotating a node in place is a real edit -- a modder turning `WEAPON` to
    /// change how a sword sits -- and from here it is indistinguishable from the
    /// importer's aiming: same joint, different frame. Guessing costs somebody their
    /// edit. What can tell them apart is the DCC tool, which can compare the rig it
    /// is exporting against the rig it imported, and a bone that has not moved since
    /// import was moved by the importer. So the tool answers, in
    /// <see cref="AuthoredProperty"/>, and this reads the answer rather than forming
    /// an opinion. A scene that carries no answer -- anything that has not been
    /// through such a tool -- is taken at its word, exactly as before.
    /// </remarks>
    public static class FbxBoneRest
    {
        /// <summary>The rest pose as the NIF wrote it, for a DCC tool to refer to.</summary>
        /// <remarks>
        /// Written on the node an importer turns into the armature, never read back
        /// by this converter. It is what a tool needs to undo an importer's aiming.
        /// </remarks>
        public const string ReferenceProperty = "nif_bone_rest";

        /// <summary>The rest pose a DCC tool says is the true one, and is read back.</summary>
        /// <remarks>
        /// Written by the exporting tool, not by this converter, and holding only
        /// the bones whose rest pose the tool is sure of. Anything it leaves out is
        /// answered by the scene.
        /// </remarks>
        public const string AuthoredProperty = "sk_bone_rest";

        /// <summary>A pose as <c>name=matrix</c> entries, for a property.</summary>
        public static string Table(IEnumerable<(string Name, NifTransform Pose)> bones) =>
            string.Join(";", bones.Select(b => $"{b.Name}={FbxSkinIO.Matrix(b.Pose)}"));

        /// <summary>
        /// The rest transform every bone a DCC tool answered for, by node name.
        /// </summary>
        public static Dictionary<string, NifTransform> Authored(FbxScene scene)
        {
            var poses = new Dictionary<string, NifTransform>(StringComparer.Ordinal);

            foreach (FbxObject model in scene.OfClass("Model"))
            {
                if (model.Properties.GetString(AuthoredProperty) is not { Length: > 0 } table)
                    continue;

                foreach (string entry in table.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int split = entry.IndexOf('=');

                    if (split > 0)
                        poses[entry[..split]] = FbxSkinIO.ParseMatrix(entry[(split + 1)..]);
                }
            }

            return poses;
        }
    }
}
