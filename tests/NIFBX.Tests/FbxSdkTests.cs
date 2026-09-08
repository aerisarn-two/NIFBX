using System.Diagnostics;
using NIFSharp;
using NIFBX.Conversion;
using Xunit;

namespace NIFBX.Tests
{
    /// <summary>
    /// Whether the Autodesk FBX SDK will open what this converter writes.
    /// </summary>
    /// <remarks>
    /// Every other test here reads an FBX with this project's own reader, and a
    /// mirror agrees with itself: the suite was fully green while every file the
    /// converter produced was refused outright by the SDK, with "Invalid FBX File",
    /// for want of one record. Nothing that only reads its own output can find that.
    ///
    /// So the oracle is somebody else's parser. ck-cmd links the SDK and its
    /// `importfbx` loads a scene through it, which is enough to ask the only
    /// question that matters here — does it open? — without building a validator or
    /// shipping a native dependency. It is a 32-bit Windows binary, so off Windows
    /// it runs under Wine, the same way mopper does.
    ///
    /// Point <c>SECMD_CKCMD</c> at the folder holding <c>ck-cmd.exe</c>. Without it
    /// these skip, like the corpus suites without an installed copy of the game.
    /// </remarks>
    public class FbxSdkTests
    {
        [CkCmdTheory]
        [MemberData(nameof(RoundTripTests.EveryFixture), MemberType = typeof(RoundTripTests))]
        public void TheSdkOpensWhatWeWrite(string name)
        {
            NifModel model = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name),
                NifXmlDatabase.LoadEmbedded());

            string fbx = Path.Combine(
                Path.GetTempPath(), $"sdkcheck-{Guid.NewGuid():N}.fbx");

            try
            {
                new NifToFbx(model).Convert().Save(fbx);

                string output = Load(fbx);

                // The SDK's own words. ck-cmd prints them and carries on, so the
                // exit code says nothing and the output is what has to be read.
                Assert.DoesNotContain("Invalid FBX File", output);
            }
            finally
            {
                if (File.Exists(fbx)) File.Delete(fbx);
            }
        }

        /// <summary>The folder holding ck-cmd.exe, or null when it was not named.</summary>
        internal static string? CkCmd
        {
            get
            {
                string? folder = Environment.GetEnvironmentVariable("SECMD_CKCMD");

                return folder is not null && File.Exists(Path.Combine(folder, "ck-cmd.exe"))
                    ? folder
                    : null;
            }
        }

        /// <summary>Asks ck-cmd to load the file, and returns everything it said.</summary>
        private static string Load(string fbx)
        {
            bool windows = OperatingSystem.IsWindows();

            // ck-cmd resolves its own DLLs from the working directory, so it is run
            // from where it lives and given an absolute path. Z: is Wine's root.
            var start = new ProcessStartInfo
            {
                FileName = windows ? "ck-cmd.exe" : "wine",
                WorkingDirectory = CkCmd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!windows)
            {
                start.ArgumentList.Add("ck-cmd.exe");
                start.Environment["WINEDEBUG"] = "-all";
            }

            start.ArgumentList.Add("importfbx");
            start.ArgumentList.Add(windows ? fbx : "Z:" + Path.GetFullPath(fbx));
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(".");

            using Process process = Process.Start(start)!;

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            process.WaitForExit(milliseconds: 300_000);
            return output;
        }
    }

    /// <summary>Skips a test that has no ck-cmd to ask.</summary>
    internal sealed class CkCmdTheoryAttribute : TheoryAttribute
    {
        public CkCmdTheoryAttribute()
        {
            if (FbxSdkTests.CkCmd is null)
                Skip = "set SECMD_CKCMD to the folder holding ck-cmd.exe, which links the FBX SDK";
        }
    }
}
