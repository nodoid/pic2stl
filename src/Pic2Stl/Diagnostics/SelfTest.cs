#if DEBUG
using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Export;
using Pic2Stl.Core.Imaging;
using Pic2Stl.Core.Pipeline;

namespace Pic2Stl.Diagnostics;

/// <summary>
/// Runs the whole generation pipeline on the device without touching the UI, so a headless launch can prove
/// that the native AI runtime, the mesher and both exporters work on real hardware.
/// Enabled only when the app is launched with P2S_SELFTEST=1.
/// </summary>
internal static class SelfTest
{
    public static bool IsRequested => Environment.GetEnvironmentVariable("P2S_SELFTEST") == "1";

    public static async Task RunAsync(AiModelInstaller installer)
    {
        static void Log(string message) => Console.WriteLine($"[selftest] {message}");

        try
        {
            Log("start");
            var image = SyntheticPhoto();
            Log($"photo {image.Width}x{image.Height}");

            var options = new GenerationOptions { ColourCount = 4, ExportDetail = 96, PreviewDetail = 24 };

            var heuristic = new MeshGenerationPipeline(new HeuristicDepthEstimator());
            var offline = await heuristic.GenerateAsync(
                image,
                options,
                new Progress<GenerationProgress>(p => Log($"{p.Percent}% {p.Stage}")));
            Log($"heuristic tris={offline.TriangleCount} palette={offline.Palette.Count} volume={offline.Mesh.SignedVolume():0.0}");

            var directory = Path.Combine(FileSystem.CacheDirectory, "selftest");
            Directory.CreateDirectory(directory);
            var stlPath = Path.Combine(directory, "offline.stl");
            var threeMfPath = Path.Combine(directory, "offline.3mf");

            using (var stream = File.Create(stlPath))
                StlWriter.Write(stream, offline.Mesh, "selftest");

            using (var stream = File.Create(threeMfPath))
                ThreeMfWriter.Write(stream, offline.Mesh, offline.Palette.Colours, "selftest");

            Log($"stl={new FileInfo(stlPath).Length} 3mf={new FileInfo(threeMfPath).Length}");

            Log("ensuring ai model");
            var modelPath = await installer.EnsureModelAsync(
                new Progress<double>(p => Log($"download {p:P0}")));
            Log($"model bytes={new FileInfo(modelPath).Length}");

            using var estimator = new OnnxDepthEstimator(modelPath);
            var pipeline = new MeshGenerationPipeline(estimator);
            var ai = await pipeline.GenerateAsync(
                image,
                options,
                new Progress<GenerationProgress>(p => Log($"{p.Percent}% {p.Stage}")));

            var (min, max) = ai.Mesh.Bounds();
            Log($"ai tris={ai.TriangleCount} palette={ai.Palette.Count} " +
                $"volume={ai.Mesh.SignedVolume():0.0} size={max.X - min.X:0}x{max.Y - min.Y:0}x{max.Z - min.Z:0}mm " +
                $"time={ai.Elapsed.TotalSeconds:0.00}s");

            if (ai.Mesh.SignedVolume() <= 0)
                throw new InvalidOperationException("AI mesh has non-positive volume.");

            Log("SELFTEST COMPLETE");
        }
        catch (Exception ex)
        {
            Log($"FAILED {ex.GetType().Name}: {ex.Message}");
            Log(ex.StackTrace ?? string.Empty);
        }
    }

    /// <summary>A colourful synthetic photo with a central subject, so depth has something to find.</summary>
    private static ImageData SyntheticPhoto()
    {
        const int width = 400, height = 300;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var nx = x / (double)width - 0.5;
                var ny = y / (double)height - 0.5;

                var inCircle = nx * nx + ny * ny < 0.06;
                if (inCircle)
                {
                    pixels[i] = 230;
                    pixels[i + 1] = 90;
                    pixels[i + 2] = 60;
                }
                else
                {
                    var shade = Rgb.FromHsv(y * 0.6, 0.45, 0.85);
                    pixels[i] = shade.R;
                    pixels[i + 1] = shade.G;
                    pixels[i + 2] = shade.B;
                }

                pixels[i + 3] = 255;
            }
        }

        return new ImageData(width, height, pixels);
    }
}
#endif
