using System.Globalization;
using System.Text;
using NIFBX.Conversion;

namespace NIFBX.Fbx
{
    /// <summary>
    /// A visibility track written on the node, where a DCC tool will keep it.
    /// </summary>
    /// <remarks>
    /// A <c>NiVisController</c> travels as a curve on the node's standard
    /// <c>Visibility</c> property, which is the right place for it: a tool that
    /// understands the property actually hides the object rather than showing a
    /// number nobody reads.
    ///
    /// Blender is not such a tool. It drops the curve on import -- not into
    /// <c>hide_viewport</c>, not into an action, nowhere -- and the stacks it writes
    /// on the way out are new ones of its own, so everything this layer records on a
    /// stack goes with them. A draugr's skeleton carries two of these, on its
    /// <c>WEAPON</c> and <c>SHIELD</c> nodes, and came back from Blender without
    /// them: six blocks gone, and the weapon a draugr holds no longer hidden when
    /// the game says to hide it.
    ///
    /// A node's own properties do survive, which is why the track is written there
    /// as well. The curve is still written and still read first; this answers only
    /// when the curve is gone.
    ///
    /// <b>The times are stored rather than regenerated.</b> They look like a key
    /// every frame -- 2,601 of them, 1/30 apart, over 86.67 seconds -- and they are
    /// not: only 1,862 of the draugr's 2,601 are exactly <c>(float)(i / 30.0)</c>,
    /// and 1,724 are exactly <c>start + i * step</c>. The rest sit a few millionths
    /// off, because that is what the numbers in the file are. Writing them out costs
    /// about 25 KB a track and is the only way to hand back what was handed over.
    ///
    /// The values are run-length coded, because a visibility track is a handful of
    /// runs however many keys it has: both of the draugr's are 2,601 keys of the
    /// same value, which is <c>2601x1</c>.
    /// </remarks>
    public static class FbxVisibilityCodec
    {
        /// <summary>The property the track is written under.</summary>
        public const string Property = "nif_vis_track";

        private const char SectionSeparator = ';';
        private const char RunSeparator = 'x';

        /// <summary>Writes a visibility track onto the node it drives.</summary>
        /// <returns>Whether anything was written.</returns>
        public static bool Write(FbxObject? node, AnimProperty property, string sequence)
        {
            if (node is null
                || property.Name != AnimProperty.VisibilityName
                || property.Curves.Length == 0
                || !property.Curves[0].HasKeys)
            {
                return false;
            }

            AnimCurve curve = property.Curves[0];
            var times = new StringBuilder();

            foreach (AnimKey key in curve.Keys)
            {
                if (times.Length > 0)
                    times.Append(' ');

                times.Append(key.Time.ToString("R", CultureInfo.InvariantCulture));
            }

            var header = string.Join(
                SectionSeparator,
                sequence,
                property.InterpolatorType,
                ((int)curve.Keys[0].Interpolation).ToString(CultureInfo.InvariantCulture),
                property.ControllerFlags?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                property.ControllerPhase?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                property.DataId >= 0 ? property.DataId.ToString(CultureInfo.InvariantCulture) : string.Empty);

            node.Properties.SetUserString(
                Property,
                string.Join(SectionSeparator, header, times.ToString(), Runs(curve)));

            return true;
        }

        /// <summary>The sequence a node's carried track belongs to.</summary>
        public static string SequenceOf(FbxObject node)
        {
            ArgumentNullException.ThrowIfNull(node);

            string stored = node.Properties.GetString(Property);
            int at = stored.IndexOf(SectionSeparator);

            return at > 0 ? stored[..at] : string.Empty;
        }

        /// <summary>The track a node carries, or null where it carries none.</summary>
        public static AnimProperty? Read(FbxObject node)
        {
            ArgumentNullException.ThrowIfNull(node);

            string stored = node.Properties.GetString(Property);

            if (stored.Length == 0)
                return null;

            string[] parts = stored.Split(SectionSeparator);

            // Six header fields, then the times and the runs.
            if (parts.Length < 8)
                return null;

            var property = new AnimProperty(1)
            {
                Name = AnimProperty.VisibilityName,
                ControllerType = AnimProperty.VisibilityController,
                InterpolatorType = parts[1],
                IsBoolean = true,
            };

            if (uint.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags))
                property.ControllerFlags = flags;

            if (float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float phase))
                property.ControllerPhase = phase;

            if (int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dataId))
                property.DataId = dataId;

            var interpolation = (AnimInterpolation)(
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) ? raw : 0);

            string[] times = parts[6].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float[] values = Values(parts[7]);

            // A track whose halves disagree is not one this can rebuild, and half a
            // visibility track is worse than none: the node would come back visible
            // for part of a sequence it was never in.
            if (times.Length == 0 || times.Length != values.Length)
                return null;

            for (int i = 0; i < times.Length; i++)
            {
                if (!float.TryParse(times[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float time))
                    return null;

                property.Curves[0].Keys.Add(new AnimKey(time, values[i], interpolation));
            }

            return property;
        }

        /// <summary>The values as runs: <c>2601x1</c>, or <c>90x1 30x0 90x1</c>.</summary>
        private static string Runs(AnimCurve curve)
        {
            var runs = new StringBuilder();
            int start = 0;

            for (int i = 1; i <= curve.Keys.Count; i++)
            {
                if (i < curve.Keys.Count && curve.Keys[i].Value == curve.Keys[start].Value)
                    continue;

                if (runs.Length > 0)
                    runs.Append(' ');

                runs.Append((i - start).ToString(CultureInfo.InvariantCulture))
                    .Append(RunSeparator)
                    .Append(curve.Keys[start].Value.ToString("R", CultureInfo.InvariantCulture));

                start = i;
            }

            return runs.ToString();
        }

        /// <summary>The runs expanded back into one value per key.</summary>
        private static float[] Values(string runs)
        {
            var values = new List<float>();

            foreach (string run in runs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int at = run.IndexOf(RunSeparator);

                if (at <= 0
                    || !int.TryParse(run[..at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                    || !float.TryParse(run[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || count <= 0)
                {
                    return [];
                }

                for (int i = 0; i < count; i++)
                    values.Add(value);
            }

            return [.. values];
        }
    }
}
