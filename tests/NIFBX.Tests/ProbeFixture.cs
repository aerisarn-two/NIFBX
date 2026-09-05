using NIFSharp;
using NIFBX.Nif;
using Xunit;

namespace NIFBX.Tests;

/// <summary>
/// A scratch probe: prints what a fixture's skin partitions actually hold.
/// </summary>
/// <remarks>
/// Not an assertion of anything. Set SECMD_PROBE_FIXTURE to a path under
/// Resources and run this one test to see the bone list and the spread of
/// authored influences per vertex; unset, it does nothing.
///
/// Recovered by decompiling se-cmd.Tests.dll after the original was deleted
/// during the extraction, so the shape is right but the names and any comments
/// are not the ones that were written.
/// </remarks>
public class ProbeFixture
{
    [Fact]
    public void Probe()
    {
        string? fixture = Environment.GetEnvironmentVariable("SECMD_PROBE_FIXTURE");
        if (fixture is null) return;

        NifXmlDatabase db = NifXmlDatabase.LoadEmbedded();

        using FileStream stream = File.OpenRead(Path.Combine("Resources", fixture));
        NifModel model = NifModel.Load(stream, db);

        foreach (NifItem block in model.Blocks)
        {
            if (NifAccess.GetRef(model, block, "Skin Partition") is null) continue;

            NifItem? data = NifAccess.GetRef(model, block, "Data");
            if (data is null) continue;

            List<string> bones = NifAccess.GetRefArray(model, block, "Bones")
                .Select(b => NifAccess.GetName(model, b))
                .ToList();

            var repeated = bones.GroupBy(b => b).Where(g => g.Count() > 1).ToList();

            Console.WriteLine($"### {block.Name}: {bones.Count} bones, {repeated.Count} named more than once");

            NifItem? boneList = model.FindItem(data, "Bone List");
            if (boneList is null) continue;

            var influences = new Dictionary<uint, int>();

            foreach (NifItem bone in boneList.Children)
            {
                NifItem? weights = model.FindItem(bone, "Vertex Weights");
                if (weights is null) continue;

                foreach (NifItem entry in weights.Children)
                {
                    uint index = model.FindItem(entry, "Index")?.Value.ToUInt() ?? 0u;
                    float weight = model.FindItem(entry, "Weight")?.Value.ToFloat() ?? 0f;

                    if (weight != 0f)
                        influences[index] = influences.GetValueOrDefault(index) + 1;
                }
            }

            var spread = influences.Values.GroupBy(c => c).OrderBy(g => g.Key);

            Console.WriteLine("    authored influences per vertex: "
                + string.Join(", ", spread.Select(g => $"{g.Key}->{g.Count()}")));
        }
    }
}
