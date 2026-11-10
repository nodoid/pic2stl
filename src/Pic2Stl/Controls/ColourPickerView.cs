using Pic2Stl.Core.Colours;

namespace Pic2Stl.Controls;

/// <summary>What the colour picker returns when the user applies a colour.</summary>
public sealed record ColourChoice(Rgb Colour, string Name, IReadOnlyList<Rgb> UserColours);

/// <summary>
/// The print-colour picker, shown as a dimmed overlay with a centred card: sixteen presets in a 4x4 grid, plus
/// four user-defined swatches. Tapping a user swatch swaps the card for the 256-shade selector, and whatever is
/// chosen shows straight away on the card before the user applies it.
/// </summary>
public sealed class ColourPickerView : ContentView
{
    private const int UserColourCount = 4;

    private readonly TaskCompletionSource<ColourChoice?> _result = new();
    private readonly int _number;
    private readonly Rgb[] _userColours = new Rgb[UserColourCount];
    private readonly Border[] _presetSwatches = new Border[ColourPalettes.Presets.Count];
    private readonly Border[] _userSwatches = new Border[UserColourCount];
    private readonly Border[] _shadeSwatches = new Border[ColourPalettes.ShadeRows * ColourPalettes.ShadeColumns];
    private readonly Border _preview;
    private readonly Border _shadePreview;
    private readonly Label _shadeHex;
    private readonly Entry _nameEntry;
    private readonly Grid _cardBody = new();
    private readonly Grid _layer;
    private readonly Border _card;
    private View? _presetsBody;
    private View? _shadeBody;
    private int _shadeSlot;
    private int _shadeSelection = -1;
    private Rgb _chosen;

    public ColourPickerView(int number, Rgb colour, string name, IReadOnlyList<Rgb> userColours)
    {
        _number = number;
        _chosen = colour;

        for (var i = 0; i < UserColourCount; i++)
            _userColours[i] = i < userColours.Count ? userColours[i] : colour;

        _preview = new Border
        {
            WidthRequest = 54,
            HeightRequest = 54,
            BackgroundColor = ToColour(colour),
            Stroke = Color.FromArgb("#EEF2FF"),
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            VerticalOptions = LayoutOptions.Center,
        };

        _shadePreview = new Border
        {
            WidthRequest = 30,
            HeightRequest = 30,
            Stroke = Color.FromArgb("#EEF2FF"),
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            VerticalOptions = LayoutOptions.Center,
        };

        _shadeHex = new Label
        {
            FontSize = 12,
            TextColor = Color.FromArgb("#DCE4FF"),
            VerticalOptions = LayoutOptions.Center,
        };

        _nameEntry = new Entry
        {
            Text = name,
            MaxLength = 40,
            BackgroundColor = Color.FromArgb("#0E1220"),
            TextColor = Color.FromArgb("#EEF2FF"),
            Placeholder = "e.g. Skin",
            PlaceholderColor = Color.FromArgb("#5A6480"),
        };

        // A grab handle at the top of the card is the swipe-down target, so dragging never fights the shade grid's scroll.
        var handle = new Border
        {
            WidthRequest = 40,
            HeightRequest = 4,
            BackgroundColor = Color.FromArgb("#3A4560"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 2 },
            HorizontalOptions = LayoutOptions.Center,
        };

        var handleArea = new Grid { HeightRequest = 20 };
        handleArea.Children.Add(handle);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnCardPan;
        handleArea.GestureRecognizers.Add(pan);

        var cardContent = new VerticalStackLayout { Spacing = 2 };
        cardContent.Add(handleArea);
        cardContent.Add(_cardBody);

        _card = new Border
        {
            BackgroundColor = Color.FromArgb("#161C2C"),
            Stroke = Color.FromArgb("#242C42"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Padding = 18,
            VerticalOptions = LayoutOptions.Center,
            Content = cardContent,
        };

        _layer = new Grid
        {
            BackgroundColor = Color.FromArgb("#E60B0F1A"),
            Padding = new Thickness(18),
            Children = { _card },
        };

        // Taps outside the card cancel, the way a modal should; the card itself swallows its own taps.
        var dismiss = new TapGestureRecognizer();
        dismiss.Tapped += (_, _) => Cancel();
        _layer.GestureRecognizers.Add(dismiss);

        Content = _layer;

        ShowPresets();
        Select(colour);
    }

    /// <summary>Completes with the chosen colour, or null if the picker was cancelled.</summary>
    public Task<ColourChoice?> Result => _result.Task;

    /// <summary>Closes the picker without choosing anything, so the awaiting caller is always released.</summary>
    public void Cancel() => _result.TrySetResult(null);

    /// <summary>Fades the backdrop in while the card rises into place; the scale makes it read as a card on top.</summary>
    public async Task AnimateInAsync()
    {
        _layer.Opacity = 0;
        _card.Opacity = 0;
        _card.Scale = 0.94;

        await Task.WhenAll(
            _layer.FadeToAsync(1, 160, Easing.CubicOut),
            _card.FadeToAsync(1, 180, Easing.CubicOut),
            _card.ScaleToAsync(1, 220, Easing.CubicOut));
    }

    /// <summary>Fades the picker away from wherever a swipe left the card.</summary>
    public async Task AnimateOutAsync()
    {
        await Task.WhenAll(
            _card.FadeToAsync(0, 120, Easing.CubicIn),
            _card.ScaleToAsync(0.95, 130, Easing.CubicIn),
            _layer.FadeToAsync(0, 140, Easing.CubicIn));
    }

#if DEBUG
    internal void PickShadeForSnapshot(int slot, int index)
    {
        ShowShades(slot);
        ApplyShade(index);
    }

    internal void DragForSnapshot(double totalY)
    {
        OnCardPan(this, new PanUpdatedEventArgs(GestureStatus.Running, 0, 0, totalY));
        OnCardPan(this, new PanUpdatedEventArgs(GestureStatus.Completed, 0, 0, totalY));
    }
#endif

    /// <summary>Drags the card down with the finger, and dismisses it if it was dragged far enough.</summary>
    private void OnCardPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Running:
                var drag = Math.Max(0, e.TotalY);
                _card.TranslationY = drag;
                _card.Opacity = Math.Max(0.35, 1 - (drag / 500));
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_card.TranslationY > 90)
                {
                    Cancel();
                    break;
                }

                _ = _card.TranslateToAsync(0, 0, 180, Easing.CubicOut);
                _ = _card.FadeToAsync(1, 180, Easing.CubicOut);
                break;
        }
    }

    private void ShowPresets()
    {
        _presetsBody ??= BuildPresetsBody();

        _cardBody.Children.Clear();
        _cardBody.Add(_presetsBody);
    }

    private Task ShowPresetsAsync()
    {
        ShowPresets();
        return Task.CompletedTask;
    }

    private void ShowShades(int slot)
    {
        _shadeSlot = slot;
        _shadeBody ??= BuildShadeBody();

        _cardBody.Children.Clear();
        _cardBody.Add(_shadeBody);

        ShowShadeChoice(_userColours[slot]);
    }

    private View BuildPresetsBody()
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(54)));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        header.Add(_preview, 0, 0);

        var heading = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        heading.Add(Label($"Print colour {_number}", 22, "#EEF2FF", FontAttributes.Bold));
        heading.Add(Label("Tap a preset, or one of your own colours below.", 11, "#8C97B5"));
        header.Add(heading, 1, 0);

        var presets = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var column = 0; column < 4; column++)
            presets.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < 4; row++)
            presets.RowDefinitions.Add(new RowDefinition(new GridLength(46)));

        for (var i = 0; i < _presetSwatches.Length; i++)
        {
            var colourAt = ColourPalettes.Presets[i];
            var swatch = CreateSwatch(colourAt, () =>
            {
                Select(colourAt);
                return Task.CompletedTask;
            });

            _presetSwatches[i] = swatch;
            Grid.SetColumn(swatch, i % 4);
            Grid.SetRow(swatch, i / 4);
            presets.Children.Add(swatch);
        }

        var userRow = new HorizontalStackLayout { Spacing = 10 };
        for (var i = 0; i < UserColourCount; i++)
        {
            var slot = i;
            var swatch = CreateSwatch(_userColours[i], () => PickUserColourAsync(slot));
            _userSwatches[i] = swatch;
            userRow.Add(swatch);
        }

        var nameField = new VerticalStackLayout { Spacing = 3 };
        nameField.Add(Label("Filament name", 12, "#8C97B5"));
        nameField.Add(_nameEntry);

        var buttons = new Grid { ColumnSpacing = 10 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        buttons.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        buttons.Add(Button("Cancel", "#1B2234", "#8C97B5", false, CloseAsync), 0, 0);
        buttons.Add(Button("Apply", "#6C8CFF", "#0B0F1A", true, ApplyAsync), 1, 0);

        var stack = new VerticalStackLayout { Spacing = 12 };
        stack.Add(header);
        stack.Add(nameField);
        stack.Add(Label("Presets", 14, "#DCE4FF"));
        stack.Add(presets);
        stack.Add(Label("User defined", 14, "#DCE4FF"));
        stack.Add(userRow);
        stack.Add(Label("Tap a user colour to choose from 256 shades.", 11, "#5A6480"));
        stack.Add(buttons);

        return new ScrollView { Content = stack, MaximumHeightRequest = 640 };
    }

    private View BuildShadeBody()
    {
        var grid = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        for (var column = 0; column < ColourPalettes.ShadeColumns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < ColourPalettes.ShadeRows; row++)
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(34)));

        for (var row = 0; row < ColourPalettes.ShadeRows; row++)
        {
            for (var column = 0; column < ColourPalettes.ShadeColumns; column++)
            {
                var index = row * ColourPalettes.ShadeColumns + column;
                var swatch = new Border
                {
                    BackgroundColor = ToColour(ColourPalettes.Shades[index]),
                    Stroke = Color.FromArgb("#242C42"),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => ApplyShade(index);
                swatch.GestureRecognizers.Add(tap);

                _shadeSwatches[index] = swatch;
                Grid.SetColumn(swatch, column);
                Grid.SetRow(swatch, row);
                grid.Children.Add(swatch);
            }
        }

        var back = new Button
        {
            Text = "‹ Colours",
            BackgroundColor = Color.FromArgb("#1B2234"),
            TextColor = Color.FromArgb("#DCE4FF"),
            CornerRadius = 10,
            FontSize = 13,
            HeightRequest = 36,
            Padding = new Thickness(12, 0),
            HorizontalOptions = LayoutOptions.Start,
        };
        back.Clicked += (_, _) => ShowPresets();

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var heading = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        heading.Add(Label("Pick a colour", 20, "#EEF2FF", FontAttributes.Bold));
        heading.Add(Label("Left to right changes the hue, top to bottom goes from dark to pale.", 11, "#8C97B5"));

        header.Add(back, 0, 0);
        header.Add(heading, 1, 0);

        var stack = new VerticalStackLayout { Spacing = 12 };
        stack.Add(header);
        stack.Add(new ScrollView { Content = grid, HeightRequest = 380 });

        // The live preview shows the shade under the finger, so the choice is visible before leaving the grid.
        var live = new Grid { ColumnSpacing = 10 };
        live.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(30)));
        live.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        live.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        live.Add(_shadePreview, 0, 0);
        live.Add(_shadeHex, 1, 0);
        live.Add(Button("Use colour", "#6C8CFF", "#0B0F1A", true, ShowPresetsAsync), 2, 0);

        stack.Add(live);
        stack.Add(Label("Tap a shade to pick it; the outline marks your choice. Swipe the card down to cancel.", 11, "#5A6480"));

        return stack;
    }

    /// <summary>Marks a shade as the slot's colour now, keeps the grid open so the outline and preview can be seen.</summary>
    private void ApplyShade(int index)
    {
        var colour = ColourPalettes.Shades[index];
        _userColours[_shadeSlot] = colour;
        _userSwatches[_shadeSlot].BackgroundColor = ToColour(colour);

        ShowShadeChoice(colour);
        Select(colour);
    }

    private void ShowShadeChoice(Rgb colour)
    {
        _shadePreview.BackgroundColor = ToColour(colour);
        _shadeHex.Text = $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

        _shadeSelection = -1;
        for (var i = 0; i < ColourPalettes.Shades.Count; i++)
        {
            if (ColourPalettes.Shades[i] != colour)
                continue;

            _shadeSelection = i;
            break;
        }

        for (var i = 0; i < _shadeSwatches.Length; i++)
            Highlight(_shadeSwatches[i], i == _shadeSelection);
    }

    private void Select(Rgb colour)
    {
        _chosen = colour;
        _preview.BackgroundColor = ToColour(colour);

        // If the colour is one of the user's own, mark that swatch rather than a preset it happens to equal.
        var userIndex = Array.IndexOf(_userColours, colour);

        for (var i = 0; i < _presetSwatches.Length; i++)
            Highlight(_presetSwatches[i], userIndex < 0 && ColourPalettes.Presets[i] == colour);

        for (var i = 0; i < _userSwatches.Length; i++)
            Highlight(_userSwatches[i], i == userIndex);
    }

    private Task PickUserColourAsync(int slot)
    {
        ShowShades(slot);
        return Task.CompletedTask;
    }

    private Task ApplyAsync()
    {
        var name = _nameEntry.Text?.Trim();
        _result.TrySetResult(new ColourChoice(
            _chosen,
            string.IsNullOrEmpty(name) ? $"Colour {_number}" : name,
            _userColours.ToArray()));

        return Task.CompletedTask;
    }

    private Task CloseAsync()
    {
        Cancel();
        return Task.CompletedTask;
    }

    private static Border CreateSwatch(Rgb colour, Func<Task> tapped)
    {
        var swatch = new Border
        {
            BackgroundColor = ToColour(colour),
            Stroke = Color.FromArgb("#242C42"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 9 },
            MinimumWidthRequest = 46,
            HeightRequest = 46,
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await tapped();
        swatch.GestureRecognizers.Add(tap);

        return swatch;
    }

    private static void Highlight(Border swatch, bool selected)
    {
        swatch.Stroke = Color.FromArgb(selected ? "#EEF2FF" : "#242C42");
        swatch.StrokeThickness = selected ? 3 : 1;
    }

    private static Label Label(string text, double size, string colour, FontAttributes attributes = FontAttributes.None) =>
        new()
        {
            Text = text,
            FontSize = size,
            TextColor = Color.FromArgb(colour),
            FontAttributes = attributes,
        };

    private static Button Button(string text, string background, string foreground, bool bold, Func<Task> tapped)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = Color.FromArgb(background),
            TextColor = Color.FromArgb(foreground),
            FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            CornerRadius = 12,
            HeightRequest = 44,
            FontSize = 14,
        };

        button.Clicked += async (_, _) => await tapped();
        return button;
    }

    private static Color ToColour(Rgb colour) => Color.FromRgb(colour.R, colour.G, colour.B);
}
