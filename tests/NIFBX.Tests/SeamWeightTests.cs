using System.Diagnostics;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using NIFBX.Nif;
using LeanMeshIO;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// A skinned mesh whose control points are shared across its seams, as Blender's are,
    /// comes back with every vertex weighted.
    /// </summary>
    public class SeamWeightTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>
        /// Every vertex a welded control point splits into keeps the point's bones.
        /// </summary>
        /// <remarks>
        /// The weights used to reach one copy of a split point and none of the others. A
        /// cat made in Blender came back with 364 of its 1,555 vertices bound to nothing,
        /// and in the game those collapse to the origin and tear the skin open along every
        /// seam -- with every triangle present and the skin's bind pose exactly right, so
        /// no check on either could see it.
        /// </remarks>
        [BlenderFact]
        public void EveryVertexOfAWeldedSeamIsWeighted()
        {
            NifModel source = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", "TestNifFile_Skinned_SE.nif"), Db);

            string folder = Path.Combine(Path.GetTempPath(), "nifbx-seams", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                string ours = Path.Combine(folder, "ours.fbx");
                string welded = Path.Combine(folder, "welded.fbx");
                new NifToFbx(source).Convert().Save(ours);

                var start = new ProcessStartInfo(BlenderRoundTripTests.Executable!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Blender", "weld.py"));
                foreach (string argument in new[] { "-b", "--python", script, "--", ours, welded })
                    start.ArgumentList.Add(argument);

                using Process blender = Process.Start(start)!;
                string output = blender.StandardOutput.ReadToEnd();
                blender.WaitForExit(120_000);
                Assert.True(File.Exists(welded), output);

                int weldedPoints = output.Split('\n').Where(l => l.StartsWith("WELDED ")).Select(l => int.Parse(l[7..].Trim())).FirstOrDefault();
                Assert.True(weldedPoints > 0, "the fixture has no seam to weld, so the test proves nothing");

                NifModel back = new FbxToNif(new FbxScene(FbxDocument.Load(welded)), new FbxToNifOptions()).Convert(Db);
                NifItem shape = back.Blocks.First(b => back.GetSkinInstance(b) is not null);
                SkinData skin = back.ReadSkin(shape)!;

                var weighted = skin.ByVertex().ToDictionary(v => v.Key, v => v.Value.Sum(i => i.Weight));
                NifItem instance = back.GetSkinInstance(shape)!;
                NifItem? partition = back.GetRef(instance, "Skin Partition");
                int vertices = partition is not null && back.FindItem(partition, "Vertex Data") is { Children.Count: > 0 } data
                    ? data.Children.Count
                    : (int)(back.FindItem(shape, "Num Vertices")?.Value.ToUInt() ?? 0);
                Assert.True(vertices > 0);
                var bare = Enumerable.Range(0, vertices).Where(v => weighted.GetValueOrDefault((ushort)v) < 0.99f).ToList();

                Assert.True(bare.Count == 0,
                    $"{bare.Count} of {vertices} vertices carry no weight, e.g. {string.Join(", ", bare.Take(5))}");
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            }
        }
    }
}
