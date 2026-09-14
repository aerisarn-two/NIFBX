using System.Globalization;
using LeanMeshIO.Formats.Fbx;

namespace NIFBX.Fbx
{
    /// <summary>
    /// Mirrors the material of a node that has no geometry onto the node itself.
    /// </summary>
    /// <remarks>
    /// A particle system and an empty shape both keep their shader and alpha
    /// property on a <c>Model</c> with a <c>Material</c> connected and no
    /// <c>Geometry</c> — there are no triangles for either to dress, but the shader
    /// is still what the effect looks like.
    ///
    /// Blender does not survive that shape. Its importer turns a geometry-less Model
    /// into an Empty, an Empty carries no material, and its exporter writes none
    /// back: a campfire through Blender came home missing three
    /// <c>BSEffectShaderProperty</c> and three <c>NiAlphaProperty</c> blocks, one
    /// pair per particle system, while all seven real meshes kept theirs.
    ///
    /// What Blender does carry is a node's user-defined properties — that is how the
    /// whole particle system already travels — so the material rides there too. The
    /// <c>Material</c> object is still written exactly as before and is still what
    /// the import prefers: it is the standard FBX construct, every DCC tool shows
    /// it, and a user who edits the shading in one expects that edit to win. This is
    /// the fallback for when the tool in the middle has thrown it away.
    ///
    /// Note this is additive on both sides. Nothing that reads these files today
    /// sees a difference, and the mirror is ignored whenever the real material
    /// survives.
    /// </remarks>
    public static class FbxNodeMaterial
    {
        /// <summary>Prefix on a mirrored property, before the name it had.</summary>
        /// <remarks>
        /// A prefix rather than the bare name because the node has properties of its
        /// own with the same spelling: a shader's controller chain and a particle
        /// system's are both <c>nac_order</c>, and one would have overwritten the
        /// other.
        /// </remarks>
        public const string Prefix = "nodemat_";

        /// <summary>Separates the parts of one mirrored property.</summary>
        /// <remarks>
        /// The separator <see cref="FbxNodeControllers.ChainOrderProperty"/> already
        /// uses, chosen for the same reason: a unit separator cannot occur in a NIF
        /// string, a texture path or a number, and it survives both FBX encodings.
        /// </remarks>
        private const char Separator = '\u001f';

        /// <summary>Copies every property of a material onto the node holding it.</summary>
        public static void Write(FbxObject node, FbxObject material)
        {
            foreach (FbxProperty70 property in material.Properties.All)
            {
                IReadOnlyList<object?> values = property.Values;

                var parts = new List<string>(4 + values.Count)
                {
                    property.Type,
                    property.SubType,
                    property.Flags,
                    values.Count.ToString(CultureInfo.InvariantCulture)
                };

                foreach (object? value in values)
                    parts.Add(Text(value));

                // Always a user string, whatever the property was: only user-defined
                // properties reach a DCC tool as custom properties, so a colour
                // mirrored as a colour would be dropped by the very step this exists
                // to survive.
                node.Properties.SetUserString(
                    Prefix + property.Name, string.Join(Separator, parts));
            }
        }

        /// <summary>Whether a node carries a mirrored material.</summary>
        public static bool WasWritten(FbxObject node) =>
            node.Properties.All.Any(p => p.Name.StartsWith(Prefix, StringComparison.Ordinal));

        /// <summary>
        /// Rebuilds the material from the mirror, or null when the node has none.
        /// </summary>
        /// <remarks>
        /// The rebuilt object is detached: it stands in for the material so far as
        /// being asked for its properties, which is all the import ever asks a
        /// geometry-less node's material for — the effect shader, the alpha settings
        /// and the controller chain order are each read off <c>Properties</c> and
        /// nothing else. Its textures are not rebuilt because nothing reads them: an
        /// effect shader names its own textures in its own fields, and the
        /// <c>Texture</c> objects exist for the DCC tool's sake.
        /// </remarks>
        public static FbxObject? Read(FbxObject node)
        {
            if (!WasWritten(node))
                return null;

            var material = new FbxObject(new FbxNode("Material"));

            foreach (FbxProperty70 property in node.Properties.All)
            {
                if (!property.Name.StartsWith(Prefix, StringComparison.Ordinal))
                    continue;

                if (property.Values.Count == 0 || property.Values[0] is not string text)
                    continue;

                string[] parts = text.Split(Separator);

                if (parts.Length < 4 || !int.TryParse(
                        parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                {
                    continue;
                }

                string type = parts[0];

                // A single value is rejoined rather than taken from one part, because
                // it is the one kind that can hold the separator itself: a chain
                // order is a list of names written into one string with it.
                object[] values = count == 1
                    ? [Value(type, string.Join(Separator, parts.Skip(4)))]
                    : [.. parts.Skip(4).Take(count).Select(part => Value(type, part))];

                material.Properties.Set(
                    property.Name[Prefix.Length..], type, parts[1], parts[2], values);
            }

            return material;
        }

        /// <summary>One value as it goes into the mirror.</summary>
        private static string Text(object? value) => value switch
        {
            null => string.Empty,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        /// <summary>
        /// One value as it comes back out, in the type the property declared.
        /// </summary>
        /// <remarks>
        /// The type matters because the readers ask typed questions of it —
        /// <c>GetVector3</c> wants three numbers, <c>GetInt</c> an integer — and a
        /// property restored as text would answer some of them wrongly.
        /// </remarks>
        private static object Value(string type, string text) => type switch
        {
            "int" or "enum" or "bool" =>
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                    ? i
                    : 0,

            "KString" or "DateTime" or "Blob" or "" => text,

            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d
                : text
        };
    }
}
