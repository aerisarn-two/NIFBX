using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFBX.Fbx;
using Xunit;
using Xunit.Abstractions;
namespace NIFBX.Tests
{
    public class ZzCons(ITestOutputHelper output)
    {
        [Fact]
        public void Show()
        {
            string? path = Environment.GetEnvironmentVariable("CONS_FBX");
            if (path is null) return;
            using FileStream f = File.OpenRead(path);
            var scene = new FbxScene(FbxDocument.Load(f));
            string want = Environment.GetEnvironmentVariable("CONS_LIKE") ?? "Femur";
            foreach (FbxObject o in scene.OfClass("Model"))
            {
                if (!o.Name.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;
                var keys = o.Properties.All
                    .Where(p => p.Name.StartsWith("constraint_", StringComparison.Ordinal))
                    .Select(p => $"{p.Name}={o.Properties.GetString(p.Name)}")
                    .ToList();
                if (keys.Count > 0)
                    output.WriteLine($"  {o.Name}\n      {string.Join("\n      ", keys.Take(8))}");
            }
        }
    }
}
