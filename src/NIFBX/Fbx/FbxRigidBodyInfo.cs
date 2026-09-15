using NIFSharp;
using System.Globalization;
using NIFBX.Nif;

namespace NIFBX.Fbx
{
    /// <summary>
    /// Carries a rigid body's mass and collision layer through FBX.
    /// </summary>
    /// <remarks>
    /// These two are carried and the inertia tensor is not, because they are different
    /// kinds of fact. The mass is authored — ck-cmd's own example files give a box and
    /// a sphere of different sizes the same mass, which no density can produce — while
    /// the tensor follows from the mass and the shape, and is computed on import by
    /// <see cref="Conversion.HavokInertia"/>.
    ///
    /// The layer travels because it decides everything else: ck-cmd picks a body's
    /// motion system, quality and solver deactivation from it, and a static body is
    /// given a zero mass whatever it was carrying. A static with a mass is treated as
    /// movable, which is how a piece of scenery ends up falling through the world.
    /// </remarks>
    public static class FbxRigidBodyInfo
    {
        /// <summary>The property the mass travels in.</summary>
        public const string MassProperty = "nif_rb_mass";

        /// <summary>The property the collision layer travels in.</summary>
        public const string LayerProperty = "nif_rb_layer";

        /// <summary>The body's centre of mass, as the file authored it.</summary>
        /// <remarks>
        /// Carried because it is authored and was not being carried: it never reached
        /// the FBX, so every body came back with a centre of mass of zero. Across a
        /// draugr's 19 bodies that is 17 of them moved -- a neck whose mass sits at
        /// (-0.0015, -0.0025, 0.2188) simulating as though it sat at its origin.
        /// </remarks>
        public const string CenterProperty = "nif_rb_center";

        /// <summary>The body's inertia tensor, as the file authored it.</summary>
        /// <remarks>
        /// The tensor is a consequence of the mass and the shape and can be computed,
        /// and <see cref="Conversion.FbxToNif"/> does compute one for a body that
        /// arrives without it. That is the right answer for a body somebody authored
        /// in a DCC tool and the wrong one for a body that came out of a file which
        /// already had a tensor: Bethesda's own differ from the computed ones -- a
        /// draugr's neck holds 0.485 where the computation gives 0.101 -- and handing
        /// back a different number than the one that was handed over is not a round
        /// trip, whatever else it is.
        /// </remarks>
        public const string InertiaProperty = "nif_rb_inertia";

        /// <summary>The property the contact callback delay travels in.</summary>
        /// <remarks>
        /// How long Havok waits before running a body's contact callbacks. nif.xml
        /// defaults it to 0xffff, which is what 2,401 of the 2,406 vanilla bodies
        /// sampled hold -- and five hold zero, which is a body that reports contacts at
        /// once rather than never. A ushort, so it does not belong with the float
        /// scalars above.
        /// </remarks>
        public const string ContactDelayProperty = "nif_rb_contact_delay";

        /// <summary>The property the rest of the collision filter travels in.</summary>
        /// <remarks>
        /// The layer is one field of a `HavokFilter`; this is the byte beside it. See
        /// <see cref="FbxCollisionMaterial.FilterFlagsOf"/> for what is in it and why
        /// none of it can be worked out instead.
        /// </remarks>
        public const string FilterFlagsProperty = "nif_rb_filter_flags";

        /// <summary>
        /// One simulation scalar: what it is called, and what a body that arrives
        /// without it should get.
        /// </summary>
        /// <param name="Field">The nif.xml field name, under <c>Rigid Body Info</c>.</param>
        /// <param name="Static">The value for a body on a layer Havok never moves.</param>
        /// <param name="Moving">The value for a body that simulates.</param>
        public readonly record struct Scalar(string Field, float Static, float Moving)
        {
            /// <summary>The property this scalar travels in.</summary>
            public string Property => "nif_rb_" + Field.Replace(' ', '_').ToLowerInvariant();

            /// <summary>The default for a body of the given kind.</summary>
            public float Default(bool isStatic) => isStatic ? Static : Moving;
        }

        /// <summary>
        /// The simulation scalars carried verbatim, with the value to fall back on.
        /// </summary>
        /// <remarks>
        /// These are authored, not derived. Across the 14,408 rigid bodies Skyrim
        /// ships: penetration depth takes 2,185 distinct values, friction 16, angular
        /// damping 9, restitution 8, linear damping 5. ck-cmd reads every one of them
        /// straight back off the body when it builds a ragdoll
        /// (`Skeleton.cpp:1003-1028`), which is what they are for.
        ///
        /// Damping is on the list even though that same ragdoll path overrides it --
        /// angular to 0.049805, linear to 0, with `GetAngularDamping()` commented out
        /// beside it. That is a choice about ragdolls, not a statement that the field
        /// is meaningless, and the 51 bodies shipping a zero linear damping against
        /// 14,158 shipping 0.0996 say it is read.
        ///
        /// The fallbacks are Bethesda's own commonest values rather than nif.xml's
        /// defaults, so that a body authored in a DCC tool -- which carries none of
        /// these -- comes out looking like a body Bethesda exported. They agree for
        /// friction, restitution and the two ceilings, and differ for three:
        ///
        /// - Damping. Vanilla holds 0.099609375 and 0.0498046875 where nif.xml says
        ///   0.1 and 0.05. Those are 102/1024 and 51/1024: the exporter quantises
        ///   damping onto a 1/1024 grid, and 99.3% and 99.2% of static bodies sit
        ///   exactly there. nif.xml documents the round number nobody actually writes.
        /// - Penetration depth, which is the one that splits by kind: statics take 0.1
        ///   (62.7%) and movers 0.15 (38.4%, and nif.xml's default). With 2,185
        ///   distinct values it is plainly computed per body, so this is a starting
        ///   point rather than an answer -- but it is the right starting point for
        ///   each kind.
        ///
        /// Three siblings are deliberately absent: `Time Factor`, `Gravity Factor` and
        /// `Rolling Friction Multiplier` hold one value each across all 14,408 bodies
        /// (1, 1 and 0), so nif.xml's default is already the authored value and
        /// carrying them would be ceremony.
        /// </remarks>
        public static readonly Scalar[] Scalars =
        [
            new("Linear Damping", 0.099609375f, 0.099609375f),
            new("Angular Damping", 0.0498046875f, 0.0498046875f),
            new("Friction", 0.5f, 0.5f),
            new("Restitution", 0.4f, 0.4f),
            new("Penetration Depth", 0.1f, 0.15f),
            new("Max Linear Velocity", 104.4f, 104.4f),
            new("Max Angular Velocity", 31.57f, 31.57f)
        ];

        /// <summary>The layer assumed for a body that arrives without one.</summary>
        public const string DefaultLayer = "SKYL_STATIC";

        /// <summary>How Havok simulates a body: the three enums that travel together.</summary>
        public readonly record struct MotionProfile(
            string MotionSystem, string QualityType, string SolverDeactivation);

        /// <summary>The properties the motion profile travels in, in profile order.</summary>
        public static readonly (string Field, string Property)[] MotionFields =
        [
            ("Motion System", "nif_rb_motion_system"),
            ("Quality Type", "nif_rb_quality_type"),
            ("Solver Deactivation", "nif_rb_solver_deactivation")
        ];

        /// <summary>
        /// The profile a body of this layer gets when it carries none of its own.
        /// </summary>
        /// <remarks>
        /// ck-cmd's three-way split by layer (`FBXWrangler.cpp:4912-4943`), with one
        /// value corrected against the corpus. Across the 14,408 bodies Skyrim ships,
        /// each kind has a clear mode:
        ///
        /// - static: `BOX_STABILIZED` / `INVALID` / `OFF`, 87.8% of 10,508 — ck-cmd's
        ///   answer exactly.
        /// - animstatic and biped: `BOX_INERTIA` / `FIXED` / `LOW`, 97.0% of 2,158 —
        ///   ck-cmd's answer exactly.
        /// - clutter: `SPHERE_STABILIZED` / `MOVING` / `LOW`, 85.8% of 1,742. ck-cmd
        ///   writes `MO_SYS_DYNAMIC` for the motion system here and no vanilla clutter
        ///   body holds it. The two are not really in conflict: nif.xml describes
        ///   `DYNAMIC` as a request Havok resolves at construction into a sphere or box
        ///   inertia, so ck-cmd writes the request and Bethesda writes the answer. A
        ///   file records what a thing *is*, so this follows the corpus.
        ///
        /// None of these is better than 88% within its kind, which is why the profile
        /// is carried rather than derived whenever there is one to carry.
        /// </remarks>
        public static MotionProfile DefaultProfile(string layer) => layer switch
        {
            "SKYL_ANIMSTATIC" or "SKYL_BIPED" =>
                new("MO_SYS_BOX_INERTIA", "MO_QUAL_FIXED", "SOLVER_DEACTIVATION_LOW"),

            "SKYL_CLUTTER" =>
                new("MO_SYS_SPHERE_STABILIZED", "MO_QUAL_MOVING", "SOLVER_DEACTIVATION_LOW"),

            _ => new("MO_SYS_BOX_STABILIZED", "MO_QUAL_INVALID", "SOLVER_DEACTIVATION_OFF")
        };

        /// <summary>The profile carried on a node, falling back per layer.</summary>
        public static MotionProfile ProfileOf(FbxObject bodyNode, string layer)
        {
            MotionProfile fallback = DefaultProfile(layer);

            string Carried(int index, string otherwise)
            {
                string name = bodyNode.Properties.GetString(MotionFields[index].Property);

                return name.Length > 0 ? name : otherwise;
            }

            return new MotionProfile(
                Carried(0, fallback.MotionSystem),
                Carried(1, fallback.QualityType),
                Carried(2, fallback.SolverDeactivation));
        }

        /// <summary>The nine fields of an inertia tensor, in the order nif.xml lists them.</summary>
        private static readonly string[] Rows =
            ["m11", "m12", "m13", "m21", "m22", "m23", "m31", "m32", "m33"];

        /// <summary>A float written so it reads back as itself.</summary>
        private static string Number(float value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>The centre of mass a node carries, or null where it carries none.</summary>
        public static NifVector4? CenterOf(FbxObject bodyNode)
        {
            ArgumentNullException.ThrowIfNull(bodyNode);

            float[] parts = Floats(bodyNode.Properties.GetString(CenterProperty), 4);

            return parts.Length == 4 ? new NifVector4(parts[0], parts[1], parts[2], parts[3]) : null;
        }

        /// <summary>The inertia tensor a node carries, in `Rows` order, or null.</summary>
        public static IReadOnlyList<float>? InertiaOf(FbxObject bodyNode)
        {
            ArgumentNullException.ThrowIfNull(bodyNode);

            float[] parts = Floats(bodyNode.Properties.GetString(InertiaProperty), 9);

            return parts.Length == 9 ? parts : null;
        }

        /// <summary>The field names an inertia tensor is written under.</summary>
        public static IReadOnlyList<string> InertiaFields => Rows;

        private static float[] Floats(string text, int wanted)
        {
            if (text.Length == 0)
                return [];

            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != wanted)
                return [];

            var values = new float[wanted];

            for (int i = 0; i < wanted; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return [];
            }

            return values;
        }

        /// <summary>Records a body's mass and layer on the node standing for it.</summary>
        public static void Write(FbxObject bodyNode, NifModel model, NifItem body)
        {
            if (model.FindItem(body, @"Rigid Body Info\Mass") is { } mass)
            {
                bodyNode.Properties.SetUserString(
                    MassProperty, mass.Value.ToFloat().ToString("R", CultureInfo.InvariantCulture));
            }

            bodyNode.Properties.SetUserString(LayerProperty, FbxCollisionMaterial.LayerOf(model, body));

            if (model.FindItem(body, @"Rigid Body Info\Center") is { } centre)
            {
                NifVector4 c = centre.Value.Get<NifVector4>();

                bodyNode.Properties.SetUserString(
                    CenterProperty, string.Join(' ', Number(c.X), Number(c.Y), Number(c.Z), Number(c.W)));
            }

            if (model.FindItem(body, @"Rigid Body Info\Inertia Tensor") is { } inertia)
            {
                bodyNode.Properties.SetUserString(InertiaProperty, string.Join(' ',
                    Rows.Select(field => Number(
                        model.FindItem(inertia, field)?.Value.ToFloat() ?? 0f))));
            }

            // Only when there is something in it: 2,965 of 3,071 vanilla filters are
            // zero, so a scene gains a property per body that says something rather
            // than one per body.
            if (FbxCollisionMaterial.FilterFlagsOf(model, body) is var filter and not 0u)
            {
                bodyNode.Properties.SetUserString(
                    FilterFlagsProperty, filter.ToString(CultureInfo.InvariantCulture));
            }

            foreach (Scalar scalar in Scalars)
            {
                if (model.FindItem(body, $@"Rigid Body Info\{scalar.Field}") is not { } item)
                    continue;

                bodyNode.Properties.SetUserString(
                    scalar.Property, item.Value.ToFloat().ToString("R", CultureInfo.InvariantCulture));
            }

            // Only when it is not the value the schema already gives, so a scene gains
            // a property for the five bodies that say something and not for the 2,401
            // that do not.
            if (model.FindItem(body, @"Rigid Body Info\Process Contact Callback Delay") is { } delay
                && delay.Value.ToUInt() != DefaultContactDelay)
            {
                bodyNode.Properties.SetUserString(
                    ContactDelayProperty, delay.Value.ToUInt().ToString(CultureInfo.InvariantCulture));
            }

            // The body's own flags, which sit outside `Rigid Body Info`. Written only
            // when there is something in them, as the filter flags are.
            if (model.FindItem(body, "Body Flags") is { } bodyFlags
                && bodyFlags.Value.ToUInt() is var flags and not 0u)
            {
                bodyNode.Properties.SetUserString(
                    BodyFlagsProperty, flags.ToString(CultureInfo.InvariantCulture));
            }

            // How Havok simulates it. Carried by enum name, since the numbers mean
            // different things in the three enums involved.
            foreach ((string field, string property) in MotionFields)
            {
                if (model.FindItem(body, $@"Rigid Body Info\{field}") is not { } item)
                    continue;

                if (model.Database.TryGetEnumOptionName(item.Type, item.Value.ToUInt(), out string option))
                    bodyNode.Properties.SetUserString(property, option);
            }
        }

        /// <summary>
        /// One carried scalar, or the fallback when the node carried none.
        /// </summary>
        public static float ScalarOf(FbxObject bodyNode, Scalar scalar, bool isStatic) =>
            float.TryParse(
                bodyNode.Properties.GetString(scalar.Property),
                NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : scalar.Default(isStatic);

        /// <summary>The mass carried with a node, or null when none was.</summary>
        public static float? MassOf(FbxObject bodyNode)
        {
            string text = bodyNode.Properties.GetString(MassProperty);

            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float mass)
                ? mass
                : null;
        }

        /// <summary>What nif.xml gives a body that says nothing.</summary>
        public const uint DefaultContactDelay = 0xffff;

        /// <summary>The property a body's own flags travel in.</summary>
        /// <remarks>
        /// `Body Flags` sits on `bhkRigidBody` rather than inside `Rigid Body Info`,
        /// and says whether the body reports its collisions to the game. Nearly every
        /// vanilla body holds 0; `dwarvenoil` holds 1, and wrote 0 without this.
        /// </remarks>
        public const string BodyFlagsProperty = "nif_rb_body_flags";

        /// <summary>A body's own flags as carried, or none.</summary>
        public static uint BodyFlagsOf(FbxObject bodyNode) =>
            uint.TryParse(
                bodyNode.Properties.GetString(BodyFlagsProperty),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags)
                ? flags
                : 0u;

        /// <summary>The contact callback delay carried with a node, or the default.</summary>
        public static uint ContactDelayOf(FbxObject bodyNode) =>
            uint.TryParse(
                bodyNode.Properties.GetString(ContactDelayProperty),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out uint delay)
                ? delay
                : DefaultContactDelay;

        /// <summary>The rest of the collision filter carried with a node.</summary>
        public static uint FilterFlagsOf(FbxObject bodyNode) =>
            uint.TryParse(
                bodyNode.Properties.GetString(FilterFlagsProperty),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags)
                ? flags
                : 0u;

        /// <summary>The collision layer carried with a node, or the static default.</summary>
        public static string LayerOf(FbxObject bodyNode)
        {
            string layer = bodyNode.Properties.GetString(LayerProperty);

            return layer.Length > 0 ? layer : DefaultLayer;
        }

        /// <summary>
        /// Whether a layer is one Havok never moves.
        /// </summary>
        /// <remarks>
        /// ck-cmd's division: animated and biped bodies get box inertia, clutter gets
        /// full dynamics, and everything else is static and loses its mass.
        /// </remarks>
        public static bool IsStatic(string layer) =>
            layer is not ("SKYL_ANIMSTATIC" or "SKYL_BIPED" or "SKYL_CLUTTER");
    }
}
