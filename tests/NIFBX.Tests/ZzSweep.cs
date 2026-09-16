using System.IO;
using NIFSharp;
using Xunit;
using Xunit.Abstractions;
namespace NIFBX.Tests
{
    public class ZzSweep(ITestOutputHelper output)
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();
        [Fact]
        public void Compare()
        {
            string? list = Environment.GetEnvironmentVariable("SWEEP_PAIRS");
            if (list is null || !File.Exists(list)) return;
            int clean = 0, total = 0;
            foreach (string line in File.ReadAllLines(list))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 3) continue;
                total++;
                string verdict;
                try { verdict = Verdict(parts[0], parts[1]); }
                catch (Exception e) { verdict = "UNREADABLE " + e.Message; }
                if (verdict.StartsWith("lossless", StringComparison.Ordinal)) clean++;
                output.WriteLine($"{parts[2],-22} {Path.GetFileName(parts[0]),-30} {verdict}");
            }
            output.WriteLine($"TOTAL {clean} of {total} lossless");
        }
        private static string Verdict(string a, string b)
        {
            NifModel before = NifModel.Load(a, Db), after = NifModel.Load(b, Db);
            var was = World(before); var now = World(after);
            int moved = 0, turned = 0, missing = 0; double wm = 0, wt = 0; string wn = "";
            foreach ((string name, NifTransform t) in was)
            {
                if (!now.TryGetValue(name, out NifTransform u)) { missing++; continue; }
                double d = Math.Sqrt(Sq(t.Translation.X-u.Translation.X)+Sq(t.Translation.Y-u.Translation.Y)+Sq(t.Translation.Z-u.Translation.Z));
                if (d > 0.01) { moved++; if (d > wm) { wm = d; wn = name; } }
                double ang = Between(t.Rotation, u.Rotation);
                if (ang > 0.5) { turned++; wt = Math.Max(wt, ang); }
            }
            string blocks = BsaCorpusTests.CompareBlocks(before, after) ?? "";
            int fields = RoundTripBaseline.Unexplained(before, after).Count;
            bool ok = missing == 0 && moved == 0 && turned == 0 && blocks.Length == 0;
            return ok
                ? $"lossless ({was.Count} nodes, {before.Blocks.Count} blocks, {fields} fields)"
                : $"LOSSY {moved} moved (worst {wm:F2} {wn}), {turned} turned (worst {wt:F1}), {fields} fields"
                  + (blocks.Length > 0 ? $"; {blocks}" : "");
        }
        private static double Sq(double x) => x * x;
        private static double Between(NifMatrix33 p, NifMatrix33 q)
        {
            NifQuat x = new NifTransform(new NifVector3(), p, 1f).ToQuaternion();
            NifQuat y = new NifTransform(new NifVector3(), q, 1f).ToQuaternion();
            return Math.Acos(Math.Clamp(Math.Abs(x.X*y.X+x.Y*y.Y+x.Z*y.Z+x.W*y.W), -1d, 1d)) * 360d / Math.PI;
        }
        private static Dictionary<string, NifTransform> World(NifModel model)
        {
            var r = new Dictionary<string, NifTransform>(StringComparer.Ordinal);
            void Walk(NifItem n, NifTransform above)
            {
                NifTransform here = model.GetTransform(n).ComposedWith(above);
                if (model.GetName(n) is { Length: > 0 } name) r[name] = here;
                foreach (NifItem c in model.GetRefArray(n, "Children")) Walk(c, here);
            }
            if (model.FindItem(model.Footer, "Roots") is { Children.Count: > 0 } roots)
                Walk(model.GetBlock(roots.Children[0]), NifTransform.Identity);
            return r;
        }
    }
}
