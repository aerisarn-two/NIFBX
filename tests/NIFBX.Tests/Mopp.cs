using NIFSharp;
using NIFBX.Havok;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Whether MOPP code can be generated here, and which fixtures need it.
    /// </summary>
    /// <remarks>
    /// Rebuilding mesh collision means building a MOPP tree, and a MOPP tree can
    /// only be built by Havok's own code: mopper.exe, a 32-bit Windows binary, or
    /// NifMopp.dll. The package supplies mopper, but running it off Windows needs
    /// Wine, and a CI runner has none.
    ///
    /// So the tests that need it say so and are skipped when it is missing, the
    /// same way the corpus suites skip without an installed copy of the game.
    /// They are not skipped anywhere a developer would notice: mopper is beside
    /// the build output, and Wine is a package away.
    /// </remarks>
    internal static class Mopp
    {
        /// <summary>Whether any MOPP backend answered.</summary>
        public static bool Available { get; } = MoppGenerator.Resolve() is not null;

        public static string SkipReason =>
            "needs a MOPP backend: mopper.exe (with Wine, off Windows) or NifMopp.dll";

        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();
        private static readonly Dictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether rebuilding this fixture will have to generate MOPP code.
        /// </summary>
        /// <remarks>
        /// Asked of the file rather than kept as a list, so a fixture added later
        /// is handled without anyone remembering this exists. A file needs the
        /// backend if it carries a MOPP tree, or a compressed mesh shape — which is
        /// what a MOPP tree is built over.
        /// </remarks>
        public static bool Needs(string relative)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(relative, out bool known)) return known;

                bool needs;
                try
                {
                    NifModel model = NifModel.Load(
                        Path.Combine(AppContext.BaseDirectory, "Resources", relative), Db);

                    needs = model.Blocks.Any(b =>
                        b.Name is "bhkMoppBvTreeShape" or "bhkCompressedMeshShape");
                }
                catch
                {
                    // The corrupted fixture, and anything else that will not load:
                    // whatever fails, it will not be for want of a MOPP backend.
                    needs = false;
                }

                Cache[relative] = needs;
                return needs;
            }
        }

        /// <summary>Fixtures this run can actually rebuild.</summary>
        public static bool Runnable(string relative) => Available || !Needs(relative);
    }

    /// <summary>Skips a test that cannot run without a MOPP backend.</summary>
    internal sealed class MoppFactAttribute : FactAttribute
    {
        public MoppFactAttribute()
        {
            if (!Mopp.Available) Skip = Mopp.SkipReason;
        }
    }
}
