using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using NIFBX.Fbx;
using Xunit;
using Xunit.Abstractions;
namespace NIFBX.Tests
{
    public class ZzIn(ITestOutputHelper output)
    {
        [Fact]
        public void Show()
        {
            string? path = Environment.GetEnvironmentVariable("IN_FBX");
            if (path is null) return;
            using FileStream f = File.OpenRead(path);
            var scene = new FbxScene(FbxDocument.Load(f));
            int carriers = scene.OfClass("Model").Count(
                m => m.Properties.GetString(FbxNodeControllers.CountProperty).Length > 0);
            var stacks = scene.OfClass("AnimationStack").ToList();
            output.WriteLine($"  models carrying npc_ controllers: {carriers}");
            output.WriteLine($"  animation stacks: {stacks.Count}");
            foreach (FbxObject s in stacks.Where(x => x.Name.Contains("Take", StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"    TAKE '{s.Name}' userprops={s.Properties.All.Count(p => p.IsUserDefined)}");
            output.WriteLine($"  curve nodes: {scene.OfClass("AnimationCurveNode").Count()}");
        }
    }
}
