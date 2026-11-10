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

            log.Add($"user colours at start={string.Join(",", _userColours.Select(Hex))} " +
                    $"pref='{Preferences.Default.Get(UserColoursPreference, string.Empty)}'");

            OpenColourEditor(0);
            await Task.Delay(800);
            log.Add($"picker visible={ModalLayer.IsVisible} children={ModalLayer.Children.Count}");
            await CaptureAsync(log, documents, "snap-7-picker");

            _modal?.Cancel();
            await Task.Delay(600);
            log.Add($"picker closed visible={ModalLayer.IsVisible} children={ModalLayer.Children.Count}");

            OpenColourEditor(0);
            await Task.Delay(700);
            _modal?.PickShadeForSnapshot(0, 76);
            await Task.Delay(600);
            log.Add($"shade chosen user[0]={Hex(_userColours[0])}");
            await CaptureAsync(log, documents, "snap-8-shades");

            var back = _modal?.GetVisualTreeDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Text == "‹ Colours");
            ((IButtonController?)back)?.SendClicked();
            await Task.Delay(500);
            await CaptureAsync(log, documents, "snap-9-shade-chosen");

            var apply = _modal?.GetVisualTreeDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Text == "Apply");
            log.Add($"apply button found={apply is not null}");
            ((IButtonController?)apply)?.SendClicked();
            await Task.Delay(600);
            log.Add($"after apply visible={ModalLayer.IsVisible} chips={ColourChips.Children.Count} " +
                    $"colour={_printColours[0]} name='{_printNames[0]}' " +
                    $"saved='{Preferences.Default.Get(UserColoursPreference, string.Empty)}'");
            await CaptureAsync(log, documents, "snap-10-applied");

            OpenColourEditor(0);
            await Task.Delay(700);
            _modal?.DragForSnapshot(40);
            await Task.Delay(500);
            log.Add($"small drag stays open={ModalLayer.IsVisible}");
            _modal?.DragForSnapshot(160);
            await Task.Delay(700);
            log.Add($"long drag closes visible={ModalLayer.IsVisible} children={ModalLayer.Children.Count}");

            // The main page scrolls, so the controls stay reachable when they do not all fit.
            log.Add($"scroll height={MainScroll.ContentSize.Height:0} viewport={MainScroll.Height:0}");
            await MainScroll.ScrollToAsync(0, 1000, false);
            await Task.Delay(400);
            log.Add($"scrolled to y={MainScroll.ScrollY:0}");
            await CaptureAsync(log, documents, "snap-11-scrolled");
            await MainScroll.ScrollToAsync(0, 0, false);
            await Task.Delay(300);

            // A single tap should pick the object and work its bounds out, with no box drawn.
            OnSelectObjectClicked(null, EventArgs.Empty);
            await Task.Delay(900);
            TapAt(0.5f, 0.5f);
            await Task.Delay(700);
            log.Add($"tap box {Overlay.Selection} apply={EditorApplyButton.IsEnabled} " +
                    $"status='{EditorObjectStatus.Text}'");
            await CaptureAsync(log, documents, "snap-12-tap");

            OnEditorApply(null, EventArgs.Empty);
            await Task.Delay(700);
            log.Add($"tap applied {_photo!.Image.Width}x{_photo.Image.Height} keep={_keep?.KeptCount} " +
                    $"editorVisible={EditorPanel.IsVisible}");
            await CaptureAsync(log, documents, "snap-13-tap-applied");

            // The picker is reachable from the Colours button without scrolling to the chip strip.
            ((IButtonController?)ColoursButton)?.SendClicked();
            await Task.Delay(700);
            log.Add($"palette visible={PaletteLayer.IsVisible} rows={PaletteRows.Children.Count}");
            await CaptureAsync(log, documents, "snap-14-palette");

            OpenColourEditor(0);
            await Task.Delay(800);
            log.Add($"picker from palette visible={ModalLayer.IsVisible} paletteBehind={PaletteLayer.IsVisible}");
            await CaptureAsync(log, documents, "snap-15-picker-from-palette");

            _modal?.Cancel();
            await Task.Delay(600);
            OnPaletteDone(null, EventArgs.Empty);
            await Task.Delay(300);
            log.Add($"palette closed visible={PaletteLayer.IsVisible}");
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

    /// <summary>Taps the photo the way a finger would: press and release without moving, so no box is drawn.</summary>
    private void TapAt(float x, float y)
    {
        var picture = Overlay.PictureBounds;
        var point = new PointF(picture.Left + picture.Width * x, picture.Top + picture.Height * y);

        Overlay.BeginBox(point);
        Overlay.EndBox();
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
