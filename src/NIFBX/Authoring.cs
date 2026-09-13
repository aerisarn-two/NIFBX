using NIFSharp;

namespace NIFBX
{
    /// <summary>
    /// Who to record as having written a converted file.
    /// </summary>
    /// <remarks>
    /// A file that came out of a converter should say so. The NIF header has three
    /// fields for it and every vanilla mesh leaves all three empty, which is worth
    /// knowing in itself: a file with something in them did not ship with the game,
    /// and a year later that is the difference between "why does this behave oddly"
    /// and "because it went through this tool at that version".
    ///
    /// <b>Nothing is stamped unless a signature is set.</b> The library does not
    /// name itself by default, and deliberately: a converter that writes into the
    /// header on every save makes every vanilla file differ from itself the moment
    /// it is loaded and written back, which is the one property the round-trip work
    /// is built on. The application sets this once at startup; a test suite does
    /// not, and sees the files it always saw.
    /// </remarks>
    public static class Authoring
    {
        /// <summary>The tool and version to record, or null to record nothing.</summary>
        /// <remarks>
        /// Set it to something a person can act on -- a name and a version, e.g.
        /// <c>se-cmd 0.1.9.0</c>. It travels into the NIF header and the FBX's
        /// Creator, both of which a modder can read without a tool of their own.
        /// </remarks>
        public static string? Signature { get; set; }

        /// <summary>What wrote the FBX: the library, and the application when it says.</summary>
        public static string Creator => CreatorFor(Signature);

        /// <summary>The same, for a signature given rather than set.</summary>
        /// <remarks>
        /// <see cref="Signature"/> is one setting shared by everything in the
        /// process, which is right for an application and wrong for a test: xUnit
        /// runs test classes in parallel, so a test that sets it decides what every
        /// other class writes while it runs. Anything that wants a particular
        /// signature asks for it here instead.
        /// </remarks>
        public static string CreatorFor(string? signature) =>
            signature is { Length: > 0 } named ? $"{Library}, {named}" : Library;

        /// <summary>This library and its version.</summary>
        public static string Library { get; } = $"NIFBX {VersionOf(typeof(Authoring))}";

        /// <summary>
        /// Records the signature in a converted model's header.
        /// </summary>
        /// <remarks>
        /// <c>Author</c> is the tool, because that is the field NifSkope shows and a
        /// modder looks at first. <c>Process Script</c> is the library underneath
        /// it, so a report naming a problem names the thing that has to be fixed
        /// rather than the thing it was run from.
        ///
        /// Only for a model this library built out of a scene. A model that was
        /// loaded is somebody else's file and keeps whatever it came with.
        /// </remarks>
        public static void Stamp(NifModel model) => Stamp(model, Signature);

        /// <summary>The same, for a signature given rather than set.</summary>
        public static void Stamp(NifModel model, string? signature)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (signature is not { Length: > 0 } named)
                return;

            Set(model, "Author", named);
            Set(model, "Process Script", Library);
        }

        /// <summary>Writes one of the header's export strings, where the version has it.</summary>
        private static void Set(NifModel model, string field, string value)
        {
            NifItem? header = model.Header.Children
                .FirstOrDefault(c => c.Def.Name == "BS Header");

            if (header?.Children.FirstOrDefault(c => c.Def.Name == field) is not { } item)
                return;

            model.SetString(item, value);
        }

        private static string VersionOf(Type type) =>
            type.Assembly.GetName().Version is { } version
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : "unversioned";
    }
}
