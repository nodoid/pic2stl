#if DEBUG
using Pic2Stl.Controls;
using Pic2Stl.Core.Imaging;
using Pic2Stl.Services;

namespace Pic2Stl;

/// <summary>
/// A scripted walk through the editor, so its look can be checked on real hardware by eye instead of guessed
/// at. Enabled only when the app is launched with P2S_SNAPSHOT=1: it loads a synthetic photo, drags boxes the
/// way a finger would, and writes a screenshot of every step into the app's Documents folder, where they can be
/// copied off the device.
/// </summary>
public partial class MainPage
{
    internal static bool SnapshotRequested => Environment.GetEnvironmentVariable("P2S_SNAPSHOT") == "1";

    internal async Task RunSnapshotAsync()
    {
        var log = new List<string>();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(documents);

        try
        {
            await Task.Delay(700);

            var subject = Diagnostics.SelfTest.SubjectPhoto().ResampleTo(800, 600);
            _photo = new LoadedPhoto(subject, PngEncoder.Encode(subject));
            _keep = null;
            _undo.Clear();
            ShowPhoto();
            ResetResult();
            await Task.Delay(400);
            await CaptureAsync(log, documents, "snap-1-photo");

            OpenEditor(PhotoEditMode.Crop);
            await Task.Delay(900);
            log.Add($"overlay {Overlay.Width:0}x{Overlay.Height:0} picture {Overlay.PictureBounds}");

            DragAcross(0.15f, 0.15f, 0.85f, 0.85f);
            await Task.Delay(400);
            log.Add($"crop box {Overlay.Selection}");
            await CaptureAsync(log, documents, "snap-2-crop");

            OnEditorCancel(null, EventArgs.Empty);
            await Task.Delay(250);

            OnSelectObjectClicked(null, EventArgs.Empty);
            await Task.Delay(900);
            DragAcross(0.15f, 0.15f, 0.85f, 0.85f);
            await Task.Delay(700);
            log.Add($"object box {Overlay.Selection} status='{EditorObjectStatus.Text}'");
            await CaptureAsync(log, documents, "snap-3-object");

            EditorToleranceSlider.Value = 0.06;
            await Task.Delay(700);
            log.Add($"strict status='{EditorObjectStatus.Text}'");
            await CaptureAsync(log, documents, "snap-4-strict");

            OnEditorApply(null, EventArgs.Empty);
            await Task.Delay(700);
            log.Add($"applied {_photo!.Image.Width}x{_photo.Image.Height} keep={_keep?.KeptCount} " +
                    $"undo='{UndoButton.Text}' shown={UndoButton.IsVisible}");
            await CaptureAsync(log, documents, "snap-5-applied");

            OnUndoClicked(null, EventArgs.Empty);
            await Task.Delay(500);
            log.Add($"undone {_photo!.Image.Width}x{_photo.Image.Height} keep={_keep?.KeptCount} " +
                    $"undoShown={UndoButton.IsVisible}");
            await CaptureAsync(log, documents, "snap-6-undone");
        }
        catch (Exception ex)
        {
            log.Add($"FAILED {ex.GetType().Name}: {ex.Message}");
            log.Add(ex.StackTrace ?? string.Empty);
        }

        var text = string.Join(Environment.NewLine, log);
        await File.WriteAllTextAsync(Path.Combine(documents, "snap.log"), text);
        Console.WriteLine($"[snapshot] {text}");
        System.Diagnostics.Debug.WriteLine(text);
    }

    /// <summary>Drags a box across the photo the way a finger would, through the overlay's own hit testing.</summary>
    private void DragAcross(float x0, float y0, float x1, float y1)
    {
        var picture = Overlay.PictureBounds;

        Overlay.BeginBox(new PointF(picture.Left + picture.Width * x0, picture.Top + picture.Height * y0));
        Overlay.GrowBox(new PointF(picture.Center.X, picture.Center.Y));
        Overlay.GrowBox(new PointF(picture.Left + picture.Width * x1, picture.Top + picture.Height * y1));
        Overlay.EndBox();
    }

    private static async Task CaptureAsync(List<string> log, string directory, string name)
    {
        var screen = await Microsoft.Maui.Media.Screenshot.Default.CaptureAsync();
        await using var stream = await screen.OpenReadAsync(Microsoft.Maui.Media.ScreenshotFormat.Png);

        var path = Path.Combine(directory, name + ".png");
        await using var file = File.Create(path);
        await stream.CopyToAsync(file);

        log.Add($"{name} {new FileInfo(path).Length} bytes");
    }
}
#endif
