using System.Diagnostics;
using LeanMeshIO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using NIFSharp;
using NIFBX.Conversion;
using NIFBX.Fbx;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// The round trip with Blender standing in the middle of it.
    /// </summary>
    /// <remarks>
    /// NIF to FBX to NIF asks whether this converter can read back what it wrote,
    /// and a fault the two directions share is invisible to it. Putting a reader
    /// that is not ours in the middle asks a harder question, and a different one
    /// from the FBX SDK probe: the SDK says whether the file is *valid*, Blender says
    /// whether it is *usable*, because a modder opens it there, changes something and
    /// exports it again.
    ///
    /// It measures a weaker property on purpose, and will for a long time. Everything
    /// a NIF says that FBX has no field for travels as a property on the model or on
    /// the animation stack — a controller's flags, an interpolator's class, a
    /// sequence's cycle type and text keys, which keys this export inserted — and
    /// Blender writes back none of the ones on the stack. So this is a census, the way
    /// the field sweep was when it started at 400 of 600, and the number is here to
    /// fall rather than to be passed.
    ///
    /// It has started to. A visibility track was the first thing moved off the stack
    /// and onto the node it drives (<see cref="Fbx.FbxVisibilityCodec"/>), because
    /// Blender drops the `Visibility` curve as well as the stack and a draugr was
    /// coming back with nothing to hide its weapon. Anything else on the stack can
    /// follow the same way; each one is its own measurement.
    ///
    /// Nothing here runs without Blender. It skips instead, which is what a machine
    /// with no copy installed should see.
    /// </remarks>
    public class BlenderRoundTripTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>Blender's executable, or null when there is none to run.</summary>
        /// <remarks>
        /// <c>SECMD_BLENDER</c> names it; otherwise the one on the path is taken.
        /// </remarks>
        internal static string? Executable
        {
            get
            {
                if (Environment.GetEnvironmentVariable("SECMD_BLENDER") is { Length: > 0 } named)
                    return File.Exists(named) ? named : null;

                foreach (string directory in
                         (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator))
                {
                    if (directory.Length == 0)
                        continue;

                    string candidate = Path.Combine(directory, "blender");

                    if (File.Exists(candidate))
                        return candidate;
                }

                return null;
            }
        }

        /// <summary>What <see cref="ThroughBlender"/> says when it ran out of patience.</summary>
        /// <remarks>
        /// Distinct from a refusal, and deliberately not a failure. Blender's importer
        /// slows down sharply with the number of objects in a file:
        /// `blacksmithforgemarker` is 880 of them, and Blender was still importing it
        /// twenty minutes later, where this converter wrote the same FBX in eleven
        /// seconds. That is a fact about Blender, not a defect in the file, so it is
        /// counted and reported rather than asserted on.
        /// </remarks>
        internal const string TooSlow = "Blender did not finish inside two minutes";

        /// <summary>The script Blender is handed, beside this file rather than packed.</summary>
        private static string Script =>
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Blender", "passthrough.py"));

        /// <summary>
        /// One model out through FBX, through Blender, and back.
        /// </summary>
        /// <returns>The rebuilt model, or null when Blender did not produce one.</returns>
        private static NifModel? ThroughBlender(NifModel source, string name, out string said)
        {
            NifItem? root = source.FindItem(source.Footer, "Roots") is { Children.Count: > 0 } roots
                ? source.GetBlock(roots.Children[0])
                : null;

            said = string.Empty;

            if (root is null)
                return null;

            string folder = Path.Combine(Path.GetTempPath(), "nifbx-blender", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                string ours = Path.Combine(folder, "ours.fbx");
                string theirs = Path.Combine(folder, "theirs.fbx");

                new NifToFbx(source).Convert().Save(ours);

                var start = new ProcessStartInfo(Executable!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                // Not `--factory-startup`: the importer with an Armature panel is the
                // add-on's, and factory startup is precisely what does not load it.
                foreach (string argument in new[]
                         { "-b", "--python", Script, "--", ours, theirs })
                {
                    start.ArgumentList.Add(argument);
                }

                using Process blender = Process.Start(start)!;

                // Long enough for the heaviest mesh in the game and short enough that a
                // sweep of thousands cannot hang on one of them.
                // Drained as it arrives rather than with `ReadToEnd`, which blocks until
                // the stream closes -- that is, until Blender exits. Read that way the
                // timeout below is never reached in time and is dead code: one mesh the
                // size of `blacksmithforgemarker` held a sweep for half an hour, which
                // is precisely what the timeout exists to stop.
                var chatter = new System.Text.StringBuilder();

                void Keep(object _, DataReceivedEventArgs e)
                {
                    if (e.Data is not null)
                        lock (chatter) chatter.AppendLine(e.Data);
                }

                blender.OutputDataReceived += Keep;
                blender.ErrorDataReceived += Keep;
                blender.BeginOutputReadLine();
                blender.BeginErrorReadLine();

                if (!blender.WaitForExit(120_000))
                {
                    blender.Kill(entireProcessTree: true);
                    said = TooSlow;
                    return null;
                }

                // Again without a limit, which is what flushes the two readers.
                blender.WaitForExit();

                string output;

                lock (chatter)
                    output = chatter.ToString();

                if (!File.Exists(theirs))
                {
                    // Blender reports a failing script on standard output, among the
                    // rest of its startup chatter, so the last lines are what to keep.
                    said = string.Join(
                        " / ",
                        output
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0)
                            .TakeLast(4));

                    return null;
                }

                return new FbxToNif(
                    new FbxScene(FbxDocument.Load(theirs)),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    }).Convert(Db);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            }
        }

        /// <summary>What a model loses on the way through, as the sweep measures it.</summary>
        /// <remarks>
        /// Saved and reloaded first, exactly as the corpus sweeps do: writing is where
        /// the format's own narrowing happens, and comparing before it measures numbers
        /// no NIF can hold.
        /// </remarks>
        private static string? Differences(NifModel source, NifModel rebuilt)
        {
            using var written = new MemoryStream();
            rebuilt.Save(written);
            written.Position = 0;
            rebuilt = NifModel.Load(written, Db);

            string? blocks = BsaCorpusTests.CompareBlocks(source, rebuilt);
            List<NifDifference> fields = RoundTripBaseline.Unexplained(source, rebuilt);

            if (blocks is null && fields.Count == 0)
                return null;

            string census = string.Join(
                ", ",
                fields.GroupBy(d => d.Field)
                    .OrderByDescending(g => g.Count())
                    .Take(6)
                    .Select(g => $"{g.Key} x{g.Count()}"));

            census = $"{fields.Count} field(s): {census}";

            return (blocks, fields.Count) switch
            {
                (null, _) => census,
                (_, 0) => blocks,
                _ => $"{blocks}; {census}"
            };
        }

        /// <summary>
        /// Every committed fixture, through Blender, field by field.
        /// </summary>
        /// <remarks>
        /// Reported rather than asserted, because what Blender drops is not a defect in
        /// this converter and a failing assertion would say only what is written above.
        /// What is asserted is the part that would be a defect: that Blender read the
        /// file at all, and that what came back is a NIF with the root it went in with.
        /// A file Blender refuses is this converter's problem, whatever the reason.
        /// </remarks>
        [BlenderTheory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void EveryFixtureSurvivesBlender(string name)
        {
            NifModel source = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

            NifModel? rebuilt = ThroughBlender(source, name, out string said);

            Assert.True(rebuilt is not null, $"{name}: Blender would not read the FBX written for it. {said}");

            string? differences = Differences(source, rebuilt!);

            Console.WriteLine(differences is null ? $"{name}: matches" : $"{name}: {differences}");
        }

        /// <summary>
        /// A rig's rest pose, through Blender and back.
        /// </summary>
        /// <remarks>
        /// A Skyrim bone is a transform with no length and no direction, so an
        /// importer that draws one has to invent a direction, and Blender's invents
        /// the only useful one: it aims each bone at the bone below it. That is what
        /// makes a rig posable rather than a heap of sticks, and Blender then writes
        /// the aimed pose back out with no per-bone inverse for it. Measured here
        /// without the add-on: 44 of `skeleton_cow`'s 48 nodes come back turned, and
        /// 82 of a draugr's 93, the worst by 180 degrees -- while not one of them, in
        /// either file, has moved.
        ///
        /// That every joint stays put is the whole reason this is recoverable. The
        /// NIF's own pose rides out in <see cref="FbxBoneRest.ReferenceProperty"/>,
        /// and SKDcc's `skyrim_export.rest` hands back the bones nobody has moved
        /// since import -- which it can tell, and this converter cannot, because it
        /// wrote down what the importer did before anyone touched it.
        ///
        /// <c>SKEX_ADDON</c> names the directory holding that add-on. Without it this
        /// reports the damage and stops, which is what a checkout with no copy of
        /// SKDcc beside it should see.
        /// </remarks>
        [BlenderFact]
        public void ARigKeepsItsRestPoseThroughBlender()
        {
            const string Fixture = "xpmsse/skeleton_cow.nif";

            NifModel source = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", Fixture), Db);

            NifModel? rebuilt = ThroughBlender(source, Fixture, out string said);

            Assert.True(rebuilt is not null, $"Blender would not read the rig. {said}");

            Dictionary<string, NifTransform> before = WorldByName(source);
            Dictionary<string, NifTransform> after = WorldByName(rebuilt!);

            var turned = new List<string>();
            int moved = 0;

            foreach ((string name, NifTransform was) in before)
            {
                if (!after.TryGetValue(name, out NifTransform now))
                    continue;

                if (Apart(was.Translation, now.Translation) > 0.01)
                    moved++;

                if (Between(was.Rotation, now.Rotation) > 0.5)
                    turned.Add(name);
            }

            Console.WriteLine(
                $"{Fixture}: {before.Count} nodes, {moved} moved, {turned.Count} turned");

            if (Environment.GetEnvironmentVariable("SKEX_ADDON") is not { Length: > 0 })
                return;

            Assert.Empty(turned);
            Assert.Equal(0, moved);
        }

        /// <summary>Where every named node stands, which is order-independent.</summary>
        /// <remarks>
        /// Not <c>NifComparer</c>: it pairs children by index, so a converter that
        /// reorders nodes reports every transform as different when none has moved.
        /// </remarks>
        private static Dictionary<string, NifTransform> WorldByName(NifModel model)
        {
            var found = new Dictionary<string, NifTransform>(StringComparer.Ordinal);

            void Walk(NifItem node, NifTransform above)
            {
                NifTransform here = model.GetTransform(node).ComposedWith(above);

                if (model.GetName(node) is { Length: > 0 } name)
                    found[name] = here;

                foreach (NifItem child in model.GetRefArray(node, "Children"))
                    Walk(child, here);
            }

            if (model.FindItem(model.Footer, "Roots") is { Children.Count: > 0 } roots)
                Walk(model.GetBlock(roots.Children[0]), NifTransform.Identity);

            return found;
        }

        private static double Apart(NifVector3 a, NifVector3 b)
        {
            double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;

            return Math.Sqrt((x * x) + (y * y) + (z * z));
        }

        /// <summary>The angle between two rotations, in degrees.</summary>
        private static double Between(NifMatrix33 a, NifMatrix33 b)
        {
            NifQuat p = new NifTransform(new NifVector3(), a, 1f).ToQuaternion();
            NifQuat q = new NifTransform(new NifVector3(), b, 1f).ToQuaternion();

            double dot = Math.Abs((p.X * q.X) + (p.Y * q.Y) + (p.Z * q.Z) + (p.W * q.W));

            return Math.Acos(Math.Clamp(dot, -1d, 1d)) * 360d / Math.PI;
        }

        /// <summary>
        /// A batch of the game's own meshes, through Blender.
        /// </summary>
        /// <remarks>
        /// Sampled and batched, because Blender costs about four seconds a mesh where
        /// the in-process sweeps cost a tenth of one: the whole corpus would be a day.
        /// <c>SECMD_BLENDER_SAMPLE</c> sets how many are taken from each archive, and
        /// the sample is chosen by a stable hash of the path so the same meshes are
        /// checked from run to run and a fall in the number means something.
        ///
        /// The share that comes back unchanged is a ratchet, as
        /// <see cref="BsaCorpusTests"/>'s are. It is low and expected to stay low until
        /// the stack properties have somewhere to live that Blender carries.
        /// </remarks>
        [BlenderFact]
        public void ABatchOfTheGameSurvivesBlender()
        {
            if (BsaCorpusTests.DataFolder() is not { } data)
                return;

            // Ten from each archive. A fixture goes through Blender in three seconds
            // and one of the game's meshes takes nearer a minute -- they are an order
            // of magnitude heavier -- so the default is what fits in a coffee break
            // and `SECMD_BLENDER_SAMPLE` is how to ask for more.
            int sample = int.TryParse(
                Environment.GetEnvironmentVariable("SECMD_BLENDER_SAMPLE"),
                out int configured) ? configured : 10;

            var checked_ = new List<(string Path, string? Differences)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            foreach (string archive in new[] { "Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa" })
            {
                string full = Path.Combine(data, archive);

                if (!File.Exists(full))
                    continue;

                var files = Archive.CreateReader(GameRelease.SkyrimSE, full).Files
                    .Where(f => f.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => BsaCorpusTests.StableHash(f.Path))
                    .Take(sample)
                    .ToList();

                Console.WriteLine($"{archive}: {files.Count} meshes");

                foreach (var file in files)
                {
                    NifModel source;

                    try
                    {
                        using var input = new MemoryStream(file.GetBytes());
                        source = NifModel.Load(input, Db);
                    }
                    catch (NifFormatException)
                    {
                        continue;
                    }

                    var each = System.Diagnostics.Stopwatch.StartNew();
                    NifModel? rebuilt = ThroughBlender(source, file.Path, out string said);

                    string? differences = rebuilt is null
                        ? (said == TooSlow ? TooSlow : $"Blender would not read it. {said}")
                        : Differences(source, rebuilt);

                    checked_.Add((file.Path, differences));

                    // One line each, as it happens. A sweep that says nothing until it
                    // ends is a sweep nobody can tell has hung.
                    Console.WriteLine(
                        $"  {each.Elapsed:mm\\:ss} {file.Path}: {differences ?? "matches"}");
                }
            }

            if (checked_.Count == 0)
                return;

            var refused = checked_
                .Where(c => c.Differences?.StartsWith("Blender would not read it", StringComparison.Ordinal) == true)
                .ToList();

            var slow = checked_.Where(c => c.Differences == TooSlow).ToList();
            int unchanged = checked_.Count(c => c.Differences is null);

            Console.WriteLine(
                $"--- {checked_.Count} through Blender in {clock.Elapsed:hh\\:mm\\:ss}, "
                + $"{unchanged} unchanged, {refused.Count} refused, {slow.Count} too slow for Blender");

            // A mesh Blender will not open is this converter's fault whatever else is
            // true of it, so that one is a gate rather than a share. One Blender merely
            // takes too long over is not: see `TooSlow`. And what a Blender round trip
            // loses is counted above and not asserted on either -- see the remarks on
            // this class for why that number is large and whose it is.
            Assert.True(
                refused.Count == 0,
                $"Blender refused {refused.Count}: "
                + string.Join(", ", refused.Take(5).Select(r => $"{r.Path} -- {r.Differences}")));
        }
    }

    /// <summary>Skips a test that has no Blender to run.</summary>
    internal sealed class BlenderTheoryAttribute : TheoryAttribute
    {
        public BlenderTheoryAttribute()
        {
            if (BlenderRoundTripTests.Executable is null)
                Skip = "no Blender found; set SECMD_BLENDER or put one on the path";
        }
    }

    /// <inheritdoc cref="BlenderTheoryAttribute"/>
    internal sealed class BlenderFactAttribute : FactAttribute
    {
        public BlenderFactAttribute()
        {
            if (BlenderRoundTripTests.Executable is null)
                Skip = "no Blender found; set SECMD_BLENDER or put one on the path";
        }
    }
}
