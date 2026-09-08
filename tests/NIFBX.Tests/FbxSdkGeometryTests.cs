using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Nif;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Whether the Autodesk SDK puts a mesh where the NIF puts it.
    /// </summary>
    /// <remarks>
    /// Everything else here reads back what this project wrote, and a NIF -> FBX ->
    /// NIF comparison cannot see a fault the reader and the writer share. Several
    /// have hidden exactly there: rotations applied transposed, so every model came
    /// out mirrored while the trip closed perfectly; a missing `CreationTime` record
    /// that made every file this converter had ever written unreadable to the SDK;
    /// quadratic tangents taken from the wrong end of their segment. All were
    /// symmetric, and all of them survived a green suite and a 22,047-mesh sweep.
    ///
    /// So this asks a reader that made none of those assumptions. It exports a
    /// fixture, has the SDK evaluate each mesh's control points in world space --
    /// through `EvaluateGlobalTransform` and the geometric offset, the way any
    /// importer would -- and requires the answer to be where the NIF's own arithmetic
    /// puts the same vertices.
    ///
    /// `FbxSdk/build.sh` builds the probe; without it these skip, like the corpus
    /// suites without an installed copy of the game.
    /// </remarks>
    public class FbxSdkGeometryTests
    {
        /// <summary>How far apart the two may land, in NIF units.</summary>
        /// <remarks>
        /// The SDK works in doubles and decomposes the Euler angles this writes, so
        /// the last digits differ. The faults this exists to catch move a mesh by
        /// hundreds of units.
        /// </remarks>
        private const double Tolerance = 0.05;

        [SdkTheory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void TheSdkPutsAMeshWhereTheNifPutsIt(string name)
        {
            NifModel m = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name),
                NifXmlDatabase.LoadEmbedded());

            string fbx = Path.Combine(Path.GetTempPath(), $"sdkgeometry-{Guid.NewGuid():N}.fbx");

            try
            {
                new NifToFbx(m).Convert().Save(fbx);

                Dictionary<string, (Vector3 Min, Vector3 Max)> theirs = Probe(fbx);
                Dictionary<string, (Vector3 Min, Vector3 Max)> ours = FromNif(m);

                foreach ((string mesh, (Vector3 min, Vector3 max)) in theirs)
                {
                    // The holder carries the mesh; the geometry under it has the name
                    // the NIF used.
                    string shape = mesh.EndsWith("_support", StringComparison.Ordinal)
                        ? mesh[..^"_support".Length]
                        : mesh;

                    if (!ours.TryGetValue(NameEncoding.Unsanitize(shape), out var mine))
                        continue;

                    Assert.True(
                        (min - mine.Min).Length() < Tolerance && (max - mine.Max).Length() < Tolerance,
                        $"{name}: the SDK puts '{shape}' at "
                        + $"[{min.X:F1},{min.Y:F1},{min.Z:F1}]..[{max.X:F1},{max.Y:F1},{max.Z:F1}] "
                        + $"and the NIF at "
                        + $"[{mine.Min.X:F1},{mine.Min.Y:F1},{mine.Min.Z:F1}]"
                        + $"..[{mine.Max.X:F1},{mine.Max.Y:F1},{mine.Max.Z:F1}]");
                }
            }
            finally
            {
                if (File.Exists(fbx)) File.Delete(fbx);
            }
        }

        /// <summary>Every shape's vertices in world space, the NIF's own way.</summary>
        private static Dictionary<string, (Vector3, Vector3)> FromNif(NifModel m)
        {
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

            var result = new Dictionary<string, (Vector3, Vector3)>(StringComparer.Ordinal);

            foreach (NifItem shape in m.Blocks.Where(
                b => m.BlockInherits(b, "BSTriShape") || m.BlockInherits(b, "NiTriBasedGeom")))
            {
                NifItem? data = m.FindItem(shape, "Vertex Data");

                if (data is null || data.Children.Count == 0)
                {
                    NifItem? instance = m.GetRef(shape, "Skin");
                    NifItem? part = instance is null ? null : m.GetRef(instance, "Skin Partition");
                    data = part is null ? null : m.FindItem(part, "Vertex Data");
                }

                var points = new List<NifVector3>();

                if (data is { Children.Count: > 0 })
                {
                    foreach (NifItem row in data.Children)
                        if (m.FindItem(row, "Vertex") is { } p)
                            points.Add(p.Value.Get<NifVector3>());
                }
                else
                {
                    points.AddRange(m.GetVertices(m.GetRef(shape, "Data") ?? shape));
                }

                if (points.Count == 0) continue;

                NifTransform place = world.GetValueOrDefault(m.GetName(shape), NifTransform.Identity);

                Vector3 min = new(float.MaxValue), max = new(float.MinValue);

                foreach (NifVector3 p in points)
                {
                    NifVector3 at = place.Apply(p);
                    var v = new Vector3(at.X, at.Y, at.Z);

                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }

                result[m.GetName(shape)] = (min, max);
            }

            return result;
        }

        /// <summary>Every mesh's world box, as the SDK evaluates it.</summary>
        private static Dictionary<string, (Vector3, Vector3)> Probe(string fbx)
        {
            var start = new ProcessStartInfo(SdkProbe!, ["where", fbx])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(milliseconds: 120_000);

            var result = new Dictionary<string, (Vector3, Vector3)>(StringComparer.Ordinal);

            foreach (string line in output.Split('\n'))
            {
                if (line.StartsWith("rejected", StringComparison.Ordinal))
                    Assert.Fail($"the SDK refused the file: {line}");

                if (!line.StartsWith("mesh ", StringComparison.Ordinal)) continue;

                var fields = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (string pair in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
                {
                    int at = pair.IndexOf('=');
                    if (at > 0) fields[pair[..at]] = pair[(at + 1)..];
                }

                if (!fields.TryGetValue("name", out string? name)) continue;

                result[name] = (Vector(fields.GetValueOrDefault("min")),
                                Vector(fields.GetValueOrDefault("max")));
            }

            return result;
        }

        private static Vector3 Vector(string? text)
        {
            string[] parts = (text ?? "").Split(',');

            if (parts.Length != 3) return Vector3.Zero;

            return new Vector3(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        /// <summary>The probe, when it has been built.</summary>
        internal static string? SdkProbe
        {
            get
            {
                string? named = Environment.GetEnvironmentVariable("SECMD_FBXPROBE");

                if (named is not null && File.Exists(named)) return named;

                // Beside its source, which is where build.sh puts it.
                string beside = Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "FbxSdk", "fbxprobe");

                return File.Exists(beside) ? Path.GetFullPath(beside) : null;
            }
        }
    }

    /// <summary>Skips a test that has no FBX SDK probe to ask.</summary>
    internal sealed class SdkTheoryAttribute : TheoryAttribute
    {
        public SdkTheoryAttribute()
        {
            if (FbxSdkGeometryTests.SdkProbe is null)
                Skip = "run tests/NIFBX.Tests/FbxSdk/build.sh, or set SECMD_FBXPROBE";
        }
    }
}
