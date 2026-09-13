using NIFBX.Conversion;
using NIFSharp;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// What a converted file says about where it came from.
    /// </summary>
    /// <remarks>
    /// Nothing here touches <see cref="Authoring.Signature"/>. It is one setting
    /// shared by the whole process and xUnit runs test classes in parallel, so a
    /// test that set it would decide what the corpus sweeps wrote while they ran --
    /// and those compare every field of every block against the file they came
    /// from. The signature is passed instead.
    /// </remarks>
    public sealed class AuthoringTests
    {
        [Fact]
        public void NothingIsStampedUntilSomethingClaimsIt()
        {
            NifModel model = NifModel.CreateNew(NifXmlDatabase.LoadEmbedded(), 0x14020007, 12, 100);
            Authoring.Stamp(model, null);

            Assert.Equal(string.Empty, Script(model));
        }

        /// <summary>
        /// The default matters as much as the stamp. Every vanilla mesh leaves the
        /// header's export strings empty, and a converter that filled them in on
        /// every save would make each of those files differ from itself the moment
        /// it was loaded and written back.
        /// </summary>
        [Fact]
        public void TheApplicationIsRecordedWhenItSaysWhoItIs()
        {
            NifModel model = NifModel.CreateNew(NifXmlDatabase.LoadEmbedded(), 0x14020007, 12, 100);
            model.SetString(model.Header.Children.First(c => c.Def.Name == "BS Header")
                .Children.First(c => c.Def.Name == "Author"), "someone who made this");

            Authoring.Stamp(model, "se-cmd 9.9.9.9");

            // The tool and the library beneath it, in the field that means "what
            // processed this" and that no vanilla mesh uses.
            Assert.StartsWith("se-cmd 9.9.9.9, NIFBX ", Script(model));

            // And the maker is left alone. 1,046 of 1,106 vanilla meshes name an
            // artist there, and a converter is not one.
            Assert.Equal("someone who made this", Author(model));
        }

        [Fact]
        public void TheFbxCreatorNamesTheLibraryAndTheApplication()
        {
            Assert.Equal(Authoring.Library, Authoring.CreatorFor(null));
            Assert.Equal($"{Authoring.Library}, se-cmd 9.9.9.9", Authoring.CreatorFor("se-cmd 9.9.9.9"));
        }

        private static string Author(NifModel model) => Field(model, "Author");

        private static string Script(NifModel model) => Field(model, "Process Script");

        private static string Field(NifModel model, string name)
        {
            NifItem header = model.Header.Children.First(c => c.Def.Name == "BS Header");

            return model.GetString(header, name);
        }
    }
}
