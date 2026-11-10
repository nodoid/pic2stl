using System.Globalization;
using Microsoft.Maui.Storage;
using Pic2Stl.Controls;
using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Export;
using Pic2Stl.Core.Geometry;
using Pic2Stl.Core.Imaging;
using Pic2Stl.Core.Pipeline;
using Pic2Stl.Services;

namespace Pic2Stl;

public partial class MainPage : ContentPage
{
    private readonly PhotoDecoder _photoDecoder;
    private readonly AiModelInstaller _modelInstaller;

    /// <summary>How many edits back the undo button can reach; photos are capped at 1024px so each step is small.</summary>
    private const int UndoLimit = 6;

    private readonly List<PhotoStep> _undo = [];

    /// <summary>What to tell the user when nothing is selected yet; the same line is reused after a redraw.</summary>
    private const string ObjectHint = "Tap the object you want — its edges become the outline. Drag the outline to add or trim edges.";

    private const string ObjectIdleStatus = "Tap the object you want to keep. Its edges become the outline.";

    private LoadedPhoto? _photo;
    private SelectionMask? _keep;
    private PhotoEditMode _editMode;
    private bool _sliderDragging;

    /// <summary>Nudges the user made to the found outline, as strokes in photo fractions.</summary>
    private readonly List<SelectionStroke> _strokes = [];
    private ObjectSelection? _previewBase;
    private ImageData? _previewSource;
    private SelectionRect _previewBox;

    /// <summary>Where the user tapped to pick an object, in photo fractions; null when the box was drawn.</summary>
    private PointF? _tapPoint;
    private GenerationResult? _result;
    private string? _threeMfPath;
    private string? _stlPath;
    private bool _busy;

    /// <summary>The colour picker currently covering the page, if any.</summary>
    private ColourPickerView? _modal;

    /// <summary>The print colours as the user defined them, and the spool names that go with them into the 3MF.</summary>
    private readonly List<Rgb> _printColours = [];
    private readonly List<string> _printNames = [];
    private int _colourCount = 4;

    /// <summary>Where the four user-defined swatches are kept between app launches.</summary>
    private const string UserColoursPreference = "user-defined-colours";

    /// <summary>The four user-defined swatches in the colour picker, kept between colour edits and app launches.</summary>
    private readonly Rgb[] _userColours =
    [
        new(0xE4, 0x6F, 0xA5),
        new(0x3F, 0xA8, 0x45),
        new(0x2D, 0x6B, 0xD8),
        new(0xF0, 0x7C, 0x22),
    ];

    public MainPage(PhotoDecoder photoDecoder, AiModelInstaller modelInstaller)
    {
        InitializeComponent();

        _photoDecoder = photoDecoder;
        _modelInstaller = modelInstaller;

        LoadUserColours();

        Overlay.SelectionChanged += OnSelectionChanged;
        Overlay.StrokeAdded += OnStrokeAdded;
        Overlay.RefineEnded += OnRefineEnded;
        Overlay.Tapped += OnOverlayTapped;

#if DEBUG
        if (Diagnostics.SelfTest.IsRequested)
            _ = Diagnostics.SelfTest.RunAsync(modelInstaller, photoDecoder);

        if (SnapshotRequested)
        {
            var started = false;
            Loaded += async (_, _) =>
            {
                if (started)
                    return;

                started = true;
                await RunSnapshotAsync();
            };
        }
#endif
    }

    private async void OnCaptureClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported)
            {
                await DisplayAlertAsync("No camera", "This device cannot take photos from the app.", "OK");
                return;
            }

            var file = await MediaPicker.Default.CapturePhotoAsync();
            if (file is not null)
                await LoadPhotoAsync(file);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(
                "Camera unavailable",
                $"The camera could not be opened. Check that Pic2Stl is allowed to use it in Settings, then try again.\n\n({ex.Message})",
                "OK");
        }
    }

    private async void OnPickClicked(object? sender, EventArgs e)
    {
        try
        {
            var files = await MediaPicker.Default.PickPhotosAsync();
            var file = files?.FirstOrDefault();
            if (file is not null)
                await LoadPhotoAsync(file);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(
                "Photos unavailable",
                $"The photo library could not be opened. Check that Pic2Stl is allowed to use photos in Settings, then try again.\n\n({ex.Message})",
                "OK");
        }
    }

    private async Task LoadPhotoAsync(FileResult file)
    {
        GenerateButton.IsEnabled = false;
        SetProgress(0, "Reading photo");

        try
        {
            await using var stream = await file.OpenReadAsync();
            _photo = await _photoDecoder.LoadAsync(stream);
            _keep = null;
            _undo.Clear();

            ShowPhoto();
            ResetResult();
        }
        catch (OperationCanceledException)
        {
            // The picker was dismissed; leave whatever photo was already loaded alone.
            SetProgress(0, _photo is null ? "Ready when you are" : "Photo ready");
        }
        catch (PhotoLoadException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            SetProgress(0, _photo is null ? "No photo loaded" : "Photo ready");
            await DisplayAlertAsync("Could not read that photo", ex.Message, "OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            SetProgress(0, _photo is null ? "No photo loaded" : "Photo ready");
            await DisplayAlertAsync(
                "Could not read that photo",
                $"Something went wrong while reading that photo. Try another one.\n\n({ex.Message})",
                "OK");
        }
        finally
        {
            GenerateButton.IsEnabled = _photo is not null;
        }
    }

    private void ShowPhoto()
    {
        if (_photo is null)
            return;

        PhotoPreview.Source = ImageSource.FromStream(() => new MemoryStream(_photo.PreviewPng));
        EditBar.IsVisible = true;
        ColoursButton.IsEnabled = true;
        UndoButton.IsVisible = _undo.Count > 0;
        UndoButton.Text = _undo.Count > 0 ? $"Undo {_undo[^1].Label}" : "Undo";
        GenerateButton.IsEnabled = true;
        SetProgress(0, _keep is null ? "Photo ready" : "Object selected");
    }

    /// <summary>Drops the model whenever the photo it came from changes, so a stale export cannot be shared.</summary>
    private void ResetResult()
    {
        _result = null;
        _threeMfPath = null;
        _stlPath = null;
        SeedPrintColours();

        Viewer.Clear();
        ViewerHint.IsVisible = true;
        StatsLabel.IsVisible = false;
        Share3mfButton.IsEnabled = false;
        ShareStlButton.IsEnabled = false;
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        // A new box invalidates edge nudges that were made for the old one.
        ClearObjectEdits();

        EditorApplyButton.IsEnabled = Overlay.HasSelection;
        UpdateOutlinePreview();
    }

    private void OnCropClicked(object? sender, EventArgs e) => OpenEditor(PhotoEditMode.Crop);

    private void OnSelectObjectClicked(object? sender, EventArgs e) => OpenEditor(PhotoEditMode.Object);

    private void OpenEditor(PhotoEditMode mode)
    {
        if (_photo is null || _busy)
            return;

        _editMode = mode;
        var cropping = mode == PhotoEditMode.Crop;

        EditorTitle.Text = cropping ? "Crop the photo" : "Select an object";
        EditorHint.Text = cropping
            ? "Drag a box around the part you want. The 3D model is built from that area only."
            : ObjectHint;
        EditorApplyButton.Text = cropping ? "Crop" : "Use object";
        EditorApplyButton.IsEnabled = false;
        EditorObjectOptions.IsVisible = !cropping;
        EditorObjectStatus.Text = cropping ? string.Empty : ObjectIdleStatus;

        EditorImage.Source = ImageSource.FromStream(() => new MemoryStream(_photo.PreviewPng));
        Overlay.Mode = mode;
        Overlay.Start(_photo.Image.Width, _photo.Image.Height);
        Overlay.SetOutline(null, default, null);
        EditorPanel.IsVisible = true;
    }

    private void OnEditorRedraw(object? sender, EventArgs e)
    {
        if (_photo is null)
            return;

        ClearObjectEdits();
        Overlay.Start(_photo.Image.Width, _photo.Image.Height);
        EditorApplyButton.IsEnabled = false;
        if (_editMode == PhotoEditMode.Object)
            EditorObjectStatus.Text = ObjectIdleStatus;
    }

    private void OnEditorCancel(object? sender, EventArgs e)
    {
        EditorPanel.IsVisible = false;
        ClearObjectEdits();
        Overlay.SetOutline(null, default, null);
    }

    /// <summary>Forgets the found object, the tap that picked it and any brush strokes made to it.</summary>
    private void ClearObjectEdits()
    {
        _strokes.Clear();
        _previewBase = null;
        _previewSource = null;
        _tapPoint = null;
    }

    /// <summary>
    /// A single tap picks the object under the finger: the match is grown from that pixel, so its bounds and
    /// outline are worked out for the user instead of asking them to draw a box around it.
    /// </summary>
    private void OnOverlayTapped(object? sender, PointF point)
    {
        if (_photo is null || _editMode != PhotoEditMode.Object)
            return;

        ClearObjectEdits();
        _tapPoint = point;
        UpdateOutlinePreview();
        EditorApplyButton.IsEnabled = Overlay.HasSelection;
    }

    /// <summary>
    /// Shows the object the colour match would keep, computed on a downscaled copy so the strictness slider and
    /// the edge brush stay responsive. The base match is cached, because brushing repeats it many times a
    /// second; Apply repeats the same match, plus the strokes, at full resolution.
    /// </summary>
    private void UpdateOutlinePreview()
    {
        if (_photo is null || _editMode != PhotoEditMode.Object || (_tapPoint is null && !Overlay.HasSelection))
        {
            _previewBase = null;
            _previewSource = null;
            Overlay.SetOutline(null, default, null);
            if (!string.IsNullOrEmpty(EditorObjectStatus.Text))
                EditorObjectStatus.Text = ObjectIdleStatus;
            return;
        }

        var image = _photo.Image;
        var scale = Math.Min(1.0, 320.0 / Math.Max(image.Width, image.Height));
        var preview = scale < 1
            ? image.ResampleTo(
                Math.Max(2, (int)Math.Round(image.Width * scale)),
                Math.Max(2, (int)Math.Round(image.Height * scale)))
            : image;

        var tolerance = (float)EditorToleranceSlider.Value;
        ObjectSelection found;

        if (_tapPoint is { } tap)
        {
            found = ObjectSelector.SelectAt(
                preview,
                (int)Math.Clamp(tap.X * preview.Width, 0f, preview.Width - 1),
                (int)Math.Clamp(tap.Y * preview.Height, 0f, preview.Height - 1),
                tolerance);
            _previewBox = found.Bounds;
        }
        else
        {
            var box = Overlay.Selection!.Value;
            var rect = SelectionRect.FromFractions(box.X, box.Y, box.Right, box.Bottom, preview.Width, preview.Height);
            if (rect.Width < 2 || rect.Height < 2)
            {
                _previewBase = null;
                _previewSource = null;
                Overlay.SetOutline(null, default, null);
                return;
            }

            _previewBox = rect;
            found = ObjectSelector.Select(preview, rect, tolerance);
        }

        _previewSource = preview;
        _previewBase = found;

        var selection = ObjectSelector.Refine(found, preview.Width, preview.Height, _strokes);
        ShowSelection(selection, withTint: true);

        // The tap already worked out where the object is, so the overlay is simply told the box it covers. That
        // gives Apply something to act on without pretending the user dragged a box around it.
        if (_tapPoint is not null)
            Overlay.SetBox(Fractions(selection.Bounds, preview));
    }

    private static RectF Fractions(SelectionRect bounds, ImageData image) => new(
        (float)bounds.X / image.Width,
        (float)bounds.Y / image.Height,
        (float)bounds.Width / image.Width,
        (float)bounds.Height / image.Height);

    /// <summary>Draws the selection over the photo; the tint is left out while the edge is being dragged.</summary>
    private void ShowSelection(ObjectSelection selection, bool withTint)
    {
        var preview = _previewSource;
        if (preview is null)
            return;

        var bounds = new RectF(
            (float)selection.Bounds.X / preview.Width,
            (float)selection.Bounds.Y / preview.Height,
            (float)selection.Bounds.Width / preview.Width,
            (float)selection.Bounds.Height / preview.Height);

        // The tint is a preview only, so it is drawn at the same reduced size it was matched at, and re-encoding
        // it on every brush stroke would make the drag stutter.
        byte[]? tint = withTint
            ? PngEncoder.Encode(selection.Mask.Tint(new Rgb(0x4A, 0xDE, 0xA9), 80))
            : null;

        Overlay.SetOutline(tint, bounds, Edges(selection, preview), selection.Mask);

        // Measured against the box the user drew, or the area their tap covered, so every step of the slider
        // gives a bigger number.
        var kept = selection.Mask.KeptCount / (float)(_previewBox.Width * _previewBox.Height);
        var area = _tapPoint is null ? "the box" : "the tapped area";
        EditorObjectStatus.Text = selection.Mask.IsEverything
            ? "No separate object found here — tap directly on the object, or drag its outline to cut background away."
            : $"Keeps {kept:P0} of {area} · drag the outline to add or trim edges.";
    }

    /// <summary>Adds one brush stroke to the object and redraws the outline it produces.</summary>
    private void OnStrokeAdded(object? sender, SelectionStroke stroke)
    {
        if (_previewBase is null || _previewSource is null)
            return;

        _strokes.Add(stroke);
        ShowSelection(
            ObjectSelector.Refine(_previewBase, _previewSource.Width, _previewSource.Height, _strokes),
            withTint: false);
    }

    /// <summary>The drag is over, so the full preview (tint included) can be drawn again.</summary>
    private void OnRefineEnded(object? sender, EventArgs e)
    {
        if (_previewBase is null || _previewSource is null)
            return;

        ShowSelection(
            ObjectSelector.Refine(_previewBase, _previewSource.Width, _previewSource.Height, _strokes),
            withTint: true);
    }

    /// <summary>The found object's edge as line segments in photo fractions, for the overlay to draw.</summary>
    private static float[] Edges(ObjectSelection selection, ImageData preview)
    {
        var outline = selection.Mask.Outline();
        var edges = new float[outline.Count * 4];
        var bounds = selection.Bounds;

        for (var i = 0; i < outline.Count; i++)
        {
            var (x0, y0, x1, y1) = outline[i];
            edges[i * 4] = (bounds.X + x0) / preview.Width;
            edges[i * 4 + 1] = (bounds.Y + y0) / preview.Height;
            edges[i * 4 + 2] = (bounds.X + x1) / preview.Width;
            edges[i * 4 + 3] = (bounds.Y + y1) / preview.Height;
        }

        return edges;
    }

    private void OnToleranceDragStarted(object? sender, EventArgs e) => _sliderDragging = true;

    private void OnToleranceDragCompleted(object? sender, EventArgs e)
    {
        _sliderDragging = false;
        UpdateOutlinePreview();
    }

    private void OnToleranceChanged(object? sender, ValueChangedEventArgs e)
    {
        EditorToleranceLabel.Text = $"{e.NewValue:P0}";

        // Dragging the thumb would otherwise re-match on every intermediate value.
        if (!_sliderDragging)
            UpdateOutlinePreview();
    }

    private void OnEditorApply(object? sender, EventArgs e)
    {
        if (_photo is null || Overlay.Selection is not { } box)
            return;

        var image = _photo.Image;
        var rect = SelectionRect.FromFractions(box.X, box.Y, box.Right, box.Bottom, image.Width, image.Height);

        if (rect.Width < 2 || rect.Height < 2)
        {
            EditorHint.Text = _tapPoint is null
                ? "That box is too small — drag a bigger one."
                : "That area is too small — tap a bigger object.";
            return;
        }

        ImageData edited;
        SelectionMask? keep;
        if (_editMode == PhotoEditMode.Crop)
        {
            edited = image.Crop(rect);
            keep = null;
        }
        else
        {
            // A tap is repeated at full resolution from the same pixel, so Apply matches what the preview showed.
            ObjectSelection selection;
            if (_tapPoint is { } tap)
                selection = ObjectSelector.SelectAt(
                    image,
                    (int)Math.Clamp(tap.X * image.Width, 0f, image.Width - 1),
                    (int)Math.Clamp(tap.Y * image.Height, 0f, image.Height - 1),
                    (float)EditorToleranceSlider.Value);
            else
                selection = ObjectSelector.Select(image, rect, (float)EditorToleranceSlider.Value);

            selection = ObjectSelector.Refine(selection, image.Width, image.Height, _strokes);
            edited = image.Crop(selection.Bounds);

            // A mask that keeps every pixel is the same as having no mask at all.
            keep = selection.Mask.IsEverything ? null : selection.Mask;
        }

        PushUndo(_editMode == PhotoEditMode.Crop ? "crop" : "object");

        _photo = new LoadedPhoto(edited, PngEncoder.Encode(edited));
        _keep = keep;
        EditorPanel.IsVisible = false;
        ClearObjectEdits();
        Overlay.SetOutline(null, default, null);

        ShowPhoto();
        ResetResult();
    }

    /// <summary>Remembers the photo as it is now, so the next edit can be taken back one step.</summary>
    private void PushUndo(string label)
    {
        if (_photo is null)
            return;

        _undo.Add(new PhotoStep(_photo, _keep, label));
        if (_undo.Count > UndoLimit)
            _undo.RemoveAt(0);
    }

    private void OnUndoClicked(object? sender, EventArgs e)
    {
        if (_undo.Count == 0 || _busy)
            return;

        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _photo = step.Photo;
        _keep = step.Keep;

        ShowPhoto();
        ResetResult();
    }

    private void OnWidthChanged(object? sender, ValueChangedEventArgs e)
    {
        WidthLabel.Text = $"{e.NewValue:0} mm";
    }

    private void OnFewerColours(object? sender, EventArgs e) => SetColourCount(_colourCount - 1);

    private void OnMoreColours(object? sender, EventArgs e) => SetColourCount(_colourCount + 1);

    private void SetColourCount(int count)
    {
        count = Math.Clamp(count, 1, 16);
        if (count == _colourCount)
            return;

        _colourCount = count;
        ColourCountLabel.Text = count.ToString(CultureInfo.InvariantCulture);
        ColourCountDown.IsEnabled = count > 1;
        ColourCountUp.IsEnabled = count < 16;

        // The palette and the material map both depend on the count, so the current model no longer matches.
        ResetResult();
    }

    /// <summary>
    /// Fills the print colours from the photo itself, as a starting point the user can rename or replace before
    /// generating. Names already given to earlier colours are kept, including across regenerations.
    /// </summary>
    private void SeedPrintColours()
    {
        if (_photo is null)
        {
            _printColours.Clear();
            _printNames.Clear();
            ColourChips.Children.Clear();
            PaletteRows.Children.Clear();
            PrintColoursRow.IsVisible = false;
            PaletteLayer.IsVisible = false;
            return;
        }

        var image = _photo.Image;
        var scale = Math.Min(1.0, 200.0 / Math.Max(image.Width, image.Height));
        var sample = scale < 1
            ? image.ResampleTo(
                Math.Max(2, (int)Math.Round(image.Width * scale)),
                Math.Max(2, (int)Math.Round(image.Height * scale)))
            : image;

        var chosen = PaletteBuilder.Build(sample, _colourCount).Palette.Colours;
        var names = new List<string>(_printNames);

        _printColours.Clear();
        _printColours.AddRange(chosen);
        _printNames.Clear();

        for (var i = 0; i < _printColours.Count; i++)
            _printNames.Add(i < names.Count ? names[i] : DefaultColourName(i));

        BuildColourChips();
        PrintColoursRow.IsVisible = true;
    }

    private static string DefaultColourName(int index) => $"Colour {index + 1}";

    /// <summary>One tappable swatch per print colour; its label is the name that goes into the 3MF.</summary>
    private void BuildColourChips()
    {
        ColourChips.Children.Clear();

        for (var i = 0; i < _printColours.Count; i++)
        {
            var index = i;

            var swatch = new Border
            {
                WidthRequest = 34,
                HeightRequest = 34,
                Stroke = Color.FromArgb("#242C42"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                BackgroundColor = ToColour(_printColours[i]),
            };

            var name = new Label
            {
                Text = _printNames[i],
                FontSize = 9,
                TextColor = Color.FromArgb("#8C97B5"),
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                WidthRequest = 52,
            };

            var cell = new VerticalStackLayout { Spacing = 3, Children = { swatch, name } };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => OpenColourEditor(index);
            cell.GestureRecognizers.Add(tap);
            ColourChips.Children.Add(cell);
        }
    }

    private void OnColoursClicked(object? sender, EventArgs e)
    {
        if (_printColours.Count == 0)
            return;

        BuildPaletteRows();
        PaletteLayer.IsVisible = true;
    }

    private void OnPaletteDone(object? sender, EventArgs e) => PaletteLayer.IsVisible = false;

    /// <summary>One row per print colour: the swatch and its name, with the whole row opening the picker.</summary>
    private void BuildPaletteRows()
    {
        PaletteRows.Children.Clear();

        for (var i = 0; i < _printColours.Count; i++)
        {
            var index = i;

            var swatch = new Border
            {
                WidthRequest = 30,
                HeightRequest = 30,
                BackgroundColor = ToColour(_printColours[i]),
                Stroke = Color.FromArgb("#242C42"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                VerticalOptions = LayoutOptions.Center,
            };

            var name = new Label
            {
                Text = _printNames[i],
                FontSize = 14,
                TextColor = Color.FromArgb("#DCE4FF"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };

            var code = new Label
            {
                Text = Hex(_printColours[i]),
                FontSize = 11,
                TextColor = Color.FromArgb("#8C97B5"),
                VerticalOptions = LayoutOptions.Center,
            };

            var grid = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
            };

            grid.Add(swatch);
            grid.Add(name, 1);
            grid.Add(code, 2);

            var row = new Border
            {
                BackgroundColor = Color.FromArgb("#0E1220"),
                Stroke = Color.FromArgb("#242C42"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(10, 8),
                Content = grid,
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => OpenColourEditor(index);
            row.GestureRecognizers.Add(tap);
            PaletteRows.Children.Add(row);
        }
    }

    /// <summary>Opens the preset and custom-shade picker as a modal card, then keeps what the user applies.</summary>
    private async void OpenColourEditor(int index)
    {
        if (index < 0 || index >= _printColours.Count || _modal is not null)
            return;

        var picker = new ColourPickerView(index + 1, _printColours[index], _printNames[index], _userColours);

        _modal = picker;
        ModalLayer.Children.Add(picker);
        ModalLayer.IsVisible = true;
        await picker.AnimateInAsync();

        var choice = await picker.Result;

        _modal = null;
        await picker.AnimateOutAsync();
        ModalLayer.Children.Clear();
        ModalLayer.IsVisible = false;

        if (choice is null)
            return;

        _printColours[index] = choice.Colour;
        _printNames[index] = choice.Name;

        for (var i = 0; i < _userColours.Length && i < choice.UserColours.Count; i++)
            _userColours[i] = choice.UserColours[i];

        SaveUserColours();
        BuildColourChips();
        if (PaletteLayer.IsVisible)
            BuildPaletteRows();

        if (_result is not null)
            Viewer.UpdatePalette(_printColours);
    }

    /// <summary>The system back button closes the picker or the palette rather than leaving the app.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_modal is not null)
        {
            _modal.Cancel();
            return true;
        }

        if (PaletteLayer.IsVisible)
        {
            PaletteLayer.IsVisible = false;
            return true;
        }

        return base.OnBackButtonPressed();
    }

    /// <summary>Restores the four swatches the user defined last time; unreadable entries keep their default.</summary>
    private void LoadUserColours()
    {
        var parts = Preferences.Default
            .Get(UserColoursPreference, string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < _userColours.Length; i++)
            if (i < parts.Length && TryParseHex(parts[i], out var colour))
                _userColours[i] = colour;
    }

    private void SaveUserColours() =>
        Preferences.Default.Set(UserColoursPreference, string.Join(",", _userColours.Select(Hex)));

    private static string Hex(Rgb colour) => $"{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private static bool TryParseHex(string text, out Rgb colour)
    {
        colour = default;

        if (text.Length != 6 ||
            !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;

        colour = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private static Color ToColour(Rgb colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    private async void OnGenerateClicked(object? sender, EventArgs e)
    {
        if (_photo is null || _busy)
            return;

        _busy = true;
        GenerateButton.IsEnabled = false;
        CaptureButton.IsEnabled = false;
        PickButton.IsEnabled = false;
        CropButton.IsEnabled = false;
        SelectObjectButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        Share3mfButton.IsEnabled = false;
        ShareStlButton.IsEnabled = false;

        try
        {
            SetProgress(0, "Preparing");

            var estimator = await CreateEstimatorAsync();

            ProgressBar.Progress = 0.30;

            var pipeline = new MeshGenerationPipeline(estimator);
            var options = new GenerationOptions
            {
                ColourCount = _colourCount,
                Palette = _printColours.Count > 0 ? _printColours.ToArray() : null,
                WidthMm = (float)WidthSlider.Value,
                ExportDetail = 220,
                PreviewDetail = 40,
                Keep = _keep,
            };

            _result = await pipeline.GenerateAsync(_photo.Image, options, new PipelineProgress(this));

            await SaveExportsAsync(_result);
            ShowResult(_result);
        }
        catch (Exception ex)
        {
            SetProgress(0, "Generation failed");
            await DisplayAlertAsync("Could not generate the model", ex.Message, "OK");
        }
        finally
        {
            _busy = false;
            GenerateButton.IsEnabled = _photo is not null;
            CaptureButton.IsEnabled = true;
            PickButton.IsEnabled = true;
            CropButton.IsEnabled = true;
            SelectObjectButton.IsEnabled = true;
            UndoButton.IsEnabled = true;
            Share3mfButton.IsEnabled = _threeMfPath is not null;
            ShareStlButton.IsEnabled = _stlPath is not null;
        }
    }

    /// <summary>
    /// Uses the free on-device model, downloading it on first run. If that is impossible (offline first run,
    /// load failure) the pipeline falls back to the offline relief estimate so the app still produces a model.
    /// </summary>
    private async Task<IDepthEstimator> CreateEstimatorAsync()
    {
        var modelDirectory = Path.Combine(FileSystem.AppDataDirectory, "models");

        try
        {
            string modelPath;
            if (_modelInstaller.IsInstalled)
            {
                modelPath = _modelInstaller.ModelPath;
            }
            else
            {
                SetProgress(0, "Downloading free AI model (one time)");
                modelPath = await _modelInstaller.EnsureModelAsync(new DownloadProgress(this));
            }

            SetProgress(30, "Loading AI model");
            return await Task.Run(() => new OnnxDepthEstimator(modelPath));
        }
        catch (Exception)
        {
            SetProgress(30, "AI model unavailable, using offline estimate");
            return new HeuristicDepthEstimator();
        }
    }

    private async Task SaveExportsAsync(GenerationResult result)
    {
        var directory = Path.Combine(FileSystem.AppDataDirectory, "exports");
        Directory.CreateDirectory(directory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var threeMf = Path.Combine(directory, $"pic2stl-{stamp}.3mf");
        var stl = Path.Combine(directory, $"pic2stl-{stamp}.stl");

        var palette = _printColours.Count > 0 ? _printColours : result.Palette.Colours;
        var names = _printNames.Count == palette.Count ? _printNames : null;

        await Task.Run(() =>
        {
            // The 3MF is sliced colour by colour, so it wants the mesh whose colours each close as a solid.
            using (var stream = File.Create(threeMf))
                ThreeMfWriter.Write(stream, result.PrintMesh, palette, "Pic2Stl model", names);

            using (var stream = File.Create(stl))
                StlWriter.Write(stream, result.Mesh, "Pic2Stl model");
        });

        _threeMfPath = threeMf;
        _stlPath = stl;
    }

    private void ShowResult(GenerationResult result)
    {
        var palette = _printColours.Count > 0 ? _printColours : result.Palette.Colours;
        Viewer.SetMesh(result.PreviewMesh, palette);
        ViewerHint.IsVisible = false;

        var (min, max) = result.Mesh.Bounds();
        StatsLabel.Text =
            $"{result.TriangleCount:N0} triangles · {palette.Count} colours · " +
            $"{max.X - min.X:0}×{max.Y - min.Y:0}×{max.Z - min.Z:0} mm · " +
            $"{result.EngineName} · {result.Elapsed.TotalSeconds:0.0}s";
        StatsLabel.IsVisible = true;

        Share3mfButton.IsEnabled = true;
        ShareStlButton.IsEnabled = true;

        SetProgress(100, "Model ready");
    }

    private async void OnShareThreeMf(object? sender, EventArgs e)
    {
        // Written at share time so the file carries the colours as the user last defined them.
        if (_result is not null && _threeMfPath is { } path)
        {
            var mesh = _result.PrintMesh;
            var palette = _printColours.Count > 0 ? _printColours : _result.Palette.Colours;
            var names = _printNames.Count == palette.Count ? _printNames : null;

            await Task.Run(() =>
            {
                using var stream = File.Create(path);
                ThreeMfWriter.Write(stream, mesh, palette, "Pic2Stl model", names);
            });
        }

        await ShareAsync(_threeMfPath, "Share 3MF");
    }

    private async void OnShareStl(object? sender, EventArgs e) => await ShareAsync(_stlPath, "Share STL");

    private async Task ShareAsync(string? path, string title)
    {
        if (path is null || !File.Exists(path))
            return;

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(path),
        });
    }

    private void SetProgress(double percent, string stage)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var clamped = Math.Clamp(percent, 0, 100);
            ProgressBar.Progress = clamped / 100.0;
            ProgressBar.Text = stage;
            ProgressBar.IsAnimating = clamped is > 0 and < 100;
        });
    }

    /// <summary>A photo state worth returning to: the photo itself plus the object mask that went with it.</summary>
    private sealed record PhotoStep(LoadedPhoto Photo, SelectionMask? Keep, string Label);

    /// <summary>Maps the pipeline's 0-100 onto the 30-100 window left after model loading.</summary>
    private sealed class PipelineProgress(MainPage page) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value) =>
            page.SetProgress(30 + value.Percent * 0.7, value.Stage);
    }

    /// <summary>Maps the one-time model download onto the first 30 percent of the bar.</summary>
    private sealed class DownloadProgress(MainPage page) : IProgress<double>
    {
        public void Report(double value) =>
            page.SetProgress(Math.Clamp(value, 0, 1) * 30, "Downloading free AI model (one time)");
    }
}
