using System.Globalization;
using NIFBX.Conversion;

namespace NIFBX.Fbx
{
    /// <summary>
    /// A sequenced controller written on the node it drives, where a DCC tool will
    /// keep it.
    /// </summary>
    /// <remarks>
    /// A track inside a <c>NiControllerSequence</c> travels as a curve bound to a
    /// property of the node's model, and everything the curve cannot say -- which
    /// controller class it is, its flags, which interpolator, which data block it
    /// shared, the sequence's cycle type and text keys -- travels as properties on
    /// the animation stack. Both are the right places in FBX and both are gone after
    /// Blender: it writes stacks of its own, and it drops animation on a custom
    /// property it does not understand. An alduin, a dragon and its four blood
    /// meshes, a vampire lord's four files, a spriggan and a frostbite spider came
    /// back from Blender with every shader controller they have missing -- the
    /// manager, the sequences, the interpolators and the float data, 13 files of the
    /// 82 the sweep measured.
    ///
    /// This is the same answer <see cref="FbxVisibilityCodec"/> gives for a
    /// visibility curve, generalised: the node's own properties survive, so the
    /// whole interpolator is written there too, by the codec that already moves one
    /// as flat text (<see cref="FbxInterpolatorCodec"/>, which carries the data
    /// block with it). The curve is still written and still read first. This answers
    /// only when the curve is gone.
    ///
    /// Transform tracks are not mirrored. Blender keeps those -- they are a bone's
    /// own translation, rotation and scale -- and mirroring them would double the
    /// size of every animation in the file to say what already survived.
    /// </remarks>
    public static class FbxSequenceCodec
    {
        /// <summary>How many sequenced tracks a node carries.</summary>
        public const string CountProperty = "nif_seq_tracks";

        /// <summary>Prefix on one carried track's fields, before its index.</summary>
        public const string Prefix = "nsq_";

        /// <summary>That this node had a controller of its own, outside any sequence.</summary>
        /// <remarks>
        /// Those travel in the invented `Take 001` stack, and their keys survive a
        /// DCC tool -- but a tool bakes the whole rig into that stack, so what comes
        /// back says every bone has one. A dog states four node controllers and came
        /// back with 53, a chaurus 36 and came back with 42, each of the extras a
        /// controller attached to a bone that never moved.
        ///
        /// Blender's exporter has a setting for this and it is not enough: with
        /// `bake_anim_use_all_bones` off -- "force exporting at least one key of
        /// animation for all bones" -- a dog's stack still came back with 159 curve
        /// nodes for the 11 it went out with. So the nodes that had one say so, and
        /// on the way back only those are believed.
        ///
        /// A scene where nothing says so is a scene this did not write, and there
        /// every track is taken at face value, as it always was.
        /// </remarks>
        public const string StandaloneProperty = "nif_standalone_track";

        /// <summary>What one node says about one track it carries.</summary>
        public sealed record Carried(string Sequence, uint CycleType, string AccumRoot, AnimProperty Property);

        /// <summary>Writes a sequenced track onto the node it drives.</summary>
        public static void Write(FbxObject? model, AnimSequence sequence, AnimProperty property)
        {
            if (model is null || property.MirroredInterpolator is not { } fields)
                return;

            int at = Count(model);
            string prefix = $"{Prefix}{at}_";

            Set(model, prefix + "seq", sequence.Name);
            Set(model, prefix + "cycle", sequence.CycleType.ToString(CultureInfo.InvariantCulture));
            Set(model, prefix + "accum", sequence.AccumRootName);
            Set(model, prefix + "name", property.Name);
            Set(model, prefix + "ctlr", property.ControllerType);
            Set(model, prefix + "ctlrid", property.ControllerId);
            Set(model, prefix + "interpid", property.InterpolatorId);
            Set(model, prefix + "proptype", property.PropertyType);
            Set(model, prefix + "interp", property.InterpolatorType);

            if (property.DataId >= 0)
                Set(model, prefix + "datid", property.DataId.ToString(CultureInfo.InvariantCulture));

            if (property.ControllerFlags is { } flags)
                Set(model, prefix + "flags", flags.ToString(CultureInfo.InvariantCulture));

            if (property.ControllerPhase is { } phase)
                Set(model, prefix + "phase", phase.ToString("R", CultureInfo.InvariantCulture));

            foreach ((string name, string value) in fields)
                Set(model, $"{prefix}i_{name}", value);

            Set(model, CountProperty, (at + 1).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Everything one node carries, or an empty list.</summary>
        public static List<Carried> Read(FbxObject model)
        {
            var carried = new List<Carried>();
            int count = Count(model);

            for (int i = 0; i < count; i++)
            {
                string prefix = $"{Prefix}{i}_";
                string sequence = model.Properties.GetString(prefix + "seq");

                if (sequence.Length == 0)
                    continue;

                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                string inner = prefix + "i_";

                foreach (FbxProperty70 property in model.Properties.All)
                {
                    if (property.Name.StartsWith(inner, StringComparison.Ordinal))
                        fields[property.Name[inner.Length..]] = model.Properties.GetString(property.Name);
                }

                if (fields.Count == 0)
                    continue;

                carried.Add(new Carried(
                    sequence,
                    Number(model, prefix + "cycle"),
                    model.Properties.GetString(prefix + "accum"),
                    new AnimProperty
                    {
                        Name = model.Properties.GetString(prefix + "name"),
                        InterpolatorType = model.Properties.GetString(prefix + "interp"),
                        ControllerType = model.Properties.GetString(prefix + "ctlr"),
                        ControllerId = model.Properties.GetString(prefix + "ctlrid"),
                        InterpolatorId = model.Properties.GetString(prefix + "interpid"),
                        PropertyType = model.Properties.GetString(prefix + "proptype"),
                        DataId = (int)Number(model, prefix + "datid", -1),
                        ControllerFlags = Optional(model, prefix + "flags"),
                        ControllerPhase = Phase(model, prefix + "phase"),

                        // Mirrored, not carried: this is an ordinary track that hangs
                        // a controller on what it animates, and a carried one is told
                        // it has none. See `AnimProperty.WholeInterpolator`.
                        MirroredInterpolator = fields
                    }));
            }

            return carried;
        }

        private static void Set(FbxObject model, string name, string value)
        {
            if (value.Length > 0)
                model.Properties.SetUserString(name, value);
        }

        private static int Count(FbxObject model) => (int)Number(model, CountProperty);

        private static uint Number(FbxObject model, string name, int missing = 0) =>
            uint.TryParse(
                model.Properties.GetString(name), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint value)
                ? value
                : unchecked((uint)missing);

        private static uint? Optional(FbxObject model, string name) =>
            uint.TryParse(
                model.Properties.GetString(name), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint value)
                ? value
                : null;

        private static float? Phase(FbxObject model, string name) =>
            float.TryParse(
                model.Properties.GetString(name), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float value)
                ? value
                : null;
    }
}
