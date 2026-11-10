#if DEBUG
using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Export;
using Pic2Stl.Core.Geometry;
using Pic2Stl.Core.Imaging;
using Pic2Stl.Core.Pipeline;
using Pic2Stl.Services;
#if IOS
using CoreGraphics;
using UIKit;
#endif

namespace Pic2Stl.Diagnostics;

/// <summary>
/// Runs the whole generation pipeline on the device without touching the UI, so a headless launch can prove
/// that the native AI runtime, the mesher and both exporters work on real hardware.
/// Enabled only when the app is launched with P2S_SELFTEST=1.
/// </summary>
internal static class SelfTest
{
    /// <summary>A 9x5 RGBA PNG stored with Adam7 interlacing, made by an independent encoder.</summary>
    private const string InterlacedPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAkAAAAFCAYAAAHgVPb9AAAAhUlEQVR42mPgEpHTcLJpK2BU0wjyYRJzUMsxComa4JfRt4" +
        "VZQs7CSkM5wJFZwlolUURBzoRFQEvJi49LSggZMwhKahvKK7s66+rGh1qbV6YziOvrB6taeqYaOyeXO/rWdjLIuhqXavv7t" +
        "ltGps90T25czcgrr2DJzsrLiQ8zCpuqRBNSBABYVR1QfiFHmgAAAABJRU5ErkJggg==";

    public static bool IsRequested => Environment.GetEnvironmentVariable("P2S_SELFTEST") == "1";

    public static async Task RunAsync(AiModelInstaller installer, PhotoDecoder photoDecoder)
    {
        static void Log(string message) => Console.WriteLine($"[selftest] {message}");

        static void CheckPixel(ImageData image, int x, int y, int red, int green, int blue, int alpha)
        {
            var colour = image.GetColor(x, y);
            var actualAlpha = image.GetChannel(x, y, 3);

            if (colour != new Rgb((byte)red, (byte)green, (byte)blue) || actualAlpha != alpha)
                throw new InvalidOperationException(
                    $"Interlaced pixel ({x},{y}) decoded as {colour} alpha {actualAlpha}, wanted ({red},{green},{blue}) alpha {alpha}.");
        }

        try
        {
            Log("start");

#if IOS
            // Decoding touches UIKit, so it must work when called from a worker thread, as photo loading does.
            var jpeg = await MainThread.InvokeOnMainThreadAsync(CreateTestJpeg);
            var decoded = await Task.Run(() => photoDecoder.LoadAsync(new MemoryStream(jpeg)));
            // IHDR sits at offset 16; its bit depth and colour type describe the encoded PNG.
            Log($"photo decode {decoded.Image.Width}x{decoded.Image.Height} png={decoded.PreviewPng.Length} " +
                $"depth={decoded.PreviewPng[24]} type={decoded.PreviewPng[25]}");
            if (decoded.Image.Width != 480)
                throw new InvalidOperationException($"Decoded photo came back {decoded.Image.Width}px wide.");
            if (decoded.PreviewPng[24] != 8)
                throw new InvalidOperationException($"Photo was encoded at {decoded.PreviewPng[24]} bits per channel.");

            // Row 0 has to stay the top of the picture: the mesher puts it at the top of the model, so a decode
            // that flips or mirrors the pixels prints the photo the wrong way round. Three marked corners catch
            // both a vertical flip and a horizontal mirror.
            var marked = new ImageData(4, 3, MarkedCorners());
            var loadedMarked = await Task.Run(() => photoDecoder.LoadAsync(new MemoryStream(PngEncoder.Encode(marked))));
            var markedImage = loadedMarked.Image;
            var topLeft = markedImage.GetColor(0, 0);
            var topRight = markedImage.GetColor(markedImage.Width - 1, 0);
            var bottomLeft = markedImage.GetColor(0, markedImage.Height - 1);

            if (!IsRed(topLeft) || !IsGreen(topRight) || !IsBlue(bottomLeft))
                throw new InvalidOperationException(
                    $"Photo corners came out {Corners(markedImage)}; the camera path flipped or mirrored the photo.");

            Log($"photo corners {Corners(markedImage)}");

            // Dominant-channel checks rather than exact values, because a wide-colour device converts to sRGB.
            static bool IsRed(Rgb colour) => colour.R > 200 && colour.G < 60 && colour.B < 60;
            static bool IsGreen(Rgb colour) => colour.G > 200 && colour.R < 60 && colour.B < 60;
            static bool IsBlue(Rgb colour) => colour.B > 200 && colour.R < 60 && colour.G < 60;
#endif

            // Interlaced PNGs arrive as seven sub-images; check the pixels land where they belong.
            var interlaced = PngDecoder.Decode(Convert.FromBase64String(InterlacedPng));
            Log($"interlaced {interlaced.Width}x{interlaced.Height}");

            if (interlaced.Width != 9 || interlaced.Height != 5)
                throw new InvalidOperationException($"Interlaced PNG decoded as {interlaced.Width}x{interlaced.Height}.");

            CheckPixel(interlaced, 0, 0, 10, 20, 30, 40);
            CheckPixel(interlaced, 1, 0, 17, 25, 43, 49);
            CheckPixel(interlaced, 4, 0, 38, 40, 82, 76);
            CheckPixel(interlaced, 3, 2, 37, 57, 73, 101);
            CheckPixel(interlaced, 8, 4, 78, 104, 142, 180);

            // Crop, object selection and the preview encoder, exactly as the editor uses them.
            var subject = SubjectPhoto();
            var cropped = subject.Crop(new SelectionRect(10, 10, 120, 90));
            var encoded = PngEncoder.Encode(cropped);
            var roundTripped = PngDecoder.Decode(encoded);
            var selection = ObjectSelector.Select(subject, new SelectionRect(5, 5, 190, 140));
            Log($"crop {cropped.Width}x{cropped.Height} png={encoded.Length} " +
                $"object={selection.Bounds} kept={selection.Mask.KeptCount}");

            // The edge brush cuts a disc out of the found object, exactly as a drag on its outline does.
            var trimmed = ObjectSelector.Refine(
                selection, subject.Width, subject.Height, [new SelectionStroke(0.5f, 0.5f, 0.02f, false)]);
            Log($"refine kept={selection.Mask.KeptCount}->{trimmed.Mask.KeptCount}");

            if (trimmed.Mask.KeptCount >= selection.Mask.KeptCount)
                throw new InvalidOperationException("Refining the object did not cut anything away.");

            if (roundTripped.Width != cropped.Width || roundTripped.Height != cropped.Height)
                throw new InvalidOperationException(
                    $"Cropped preview came back {roundTripped.Width}x{roundTripped.Height}.");

            if (selection.Mask.IsEverything)
                throw new InvalidOperationException("Object selection kept the whole box instead of the subject.");

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

            var objectPhoto = subject.Crop(selection.Bounds);
            // Two filament colours the user defined, so the run also proves the photo is mapped onto them.
            Rgb[] filaments = [new(220, 60, 40), new(40, 70, 130)];
            var maskedOptions = new GenerationOptions
            {
                ColourCount = 4,
                Palette = filaments,
                ExportDetail = 64,
                PreviewDetail = 20,
                Keep = selection.Mask,
            };
            var masked = await heuristic.GenerateAsync(objectPhoto, maskedOptions);
            var (maskedMin, maskedMax) = masked.Mesh.Bounds();
            Log($"masked tris={masked.TriangleCount} palette={masked.Palette.Count} " +
                $"volume={masked.Mesh.SignedVolume():0.0} size={maskedMax.X - maskedMin.X:0}x{maskedMax.Y - maskedMin.Y:0}mm");

            if (masked.Palette.Count != filaments.Length)
                throw new InvalidOperationException("The masked model did not use the print colours it was given.");

            if (masked.Mesh.SignedVolume() <= 0)
                throw new InvalidOperationException("Masked mesh has non-positive volume.");

            // The object is trimmed out of the photo's plate, so its rim walls must still close the surface.
            if (!IsWatertight(masked.Mesh))
                throw new InvalidOperationException("Trimmed mesh has open edges.");

            Log("masked watertight");

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

    /// <summary>Every edge must be used once in each direction, which is what makes a rim wall close the solid.</summary>
    private static bool IsWatertight(Mesh mesh)
    {
        var edges = new HashSet<(int, int)>();

        foreach (var triangle in mesh.Triangles)
            foreach (var edge in new[] { (triangle.A, triangle.B), (triangle.B, triangle.C), (triangle.C, triangle.A) })
                if (!edges.Add(edge))
                    return false;

        return edges.All(edge => edges.Contains((edge.Item2, edge.Item1)));
    }

    /// <summary>White with a red top-left, green top-right and blue bottom-left, so a flip or mirror shows up.</summary>
    private static byte[] MarkedCorners()
    {
        const int width = 4, height = 3;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = 255;
            pixels[i * 4 + 1] = 255;
            pixels[i * 4 + 2] = 255;
            pixels[i * 4 + 3] = 255;
        }

        void Paint(int x, int y, byte r, byte g, byte b)
        {
            var i = (y * width + x) * 4;
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
        }

        Paint(0, 0, 255, 0, 0);
        Paint(width - 1, 0, 0, 255, 0);
        Paint(0, height - 1, 0, 0, 255);
        return pixels;
    }

    private static string Corners(ImageData image) =>
        $"tl={Hex(image.GetColor(0, 0))} tr={Hex(image.GetColor(image.Width - 1, 0))} " +
        $"bl={Hex(image.GetColor(0, image.Height - 1))}";

    private static string Hex(Rgb colour) => $"({colour.R},{colour.G},{colour.B})";

    /// <summary>A red subject on a blue field, so the object selector has a clear edge to follow.</summary>
    internal static ImageData SubjectPhoto()
    {
        const int width = 200, height = 150;
        var pixels = new byte[width * height * 4];
        var centreX = width / 2.0;
        var centreY = height / 2.0;
        var radius = 45.0;

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var subject = (x - centreX) * (x - centreX) + (y - centreY) * (y - centreY) <= radius * radius;

                pixels[i] = subject ? (byte)220 : (byte)35;
                pixels[i + 1] = subject ? (byte)60 : (byte)70;
                pixels[i + 2] = subject ? (byte)40 : (byte)130;
                pixels[i + 3] = 255;
            }

        return new ImageData(width, height, pixels);
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

#if IOS
    /// <summary>A JPEG, made the way iOS encodes photos, to feed the real photo decoder.</summary>
    private static byte[] CreateTestJpeg()
    {
        using var format = new UIGraphicsImageRendererFormat { Opaque = true, Scale = 1 };
        using var renderer = new UIGraphicsImageRenderer(new CGSize(480, 360), format);
        using var image = renderer.CreateImage(context =>
        {
            var cg = context.CGContext;
            cg.SetFillColor(UIColor.FromRGB(40, 90, 160).CGColor);
            cg.FillRect(new CGRect(0, 0, 480, 360));
            cg.SetFillColor(UIColor.FromRGB(240, 200, 60).CGColor);
            cg.FillRect(new CGRect(120, 80, 200, 200));
        });
        using var jpeg = image.AsJPEG(0.9f);
        return jpeg.ToArray();
    }
#endif
}
#endif
