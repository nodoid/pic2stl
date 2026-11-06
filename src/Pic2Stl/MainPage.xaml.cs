using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Export;
using Pic2Stl.Core.Pipeline;
using Pic2Stl.Services;

namespace Pic2Stl;

public partial class MainPage : ContentPage
{
    private readonly PhotoDecoder _photoDecoder;
    private readonly AiModelInstaller _modelInstaller;

    private LoadedPhoto? _photo;
    private GenerationResult? _result;
    private string? _threeMfPath;
    private string? _stlPath;
    private bool _busy;

    public MainPage(PhotoDecoder photoDecoder, AiModelInstaller modelInstaller)
    {
        InitializeComponent();

        _photoDecoder = photoDecoder;
        _modelInstaller = modelInstaller;

#if DEBUG
        if (Diagnostics.SelfTest.IsRequested)
            _ = Diagnostics.SelfTest.RunAsync(modelInstaller, photoDecoder);
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
            await DisplayAlertAsync("Camera unavailable", ex.Message, "OK");
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
            await DisplayAlertAsync("Photos unavailable", ex.Message, "OK");
        }
    }

    private async Task LoadPhotoAsync(FileResult file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            _photo = await _photoDecoder.LoadAsync(stream);

            PhotoPreview.Source = ImageSource.FromStream(() => new MemoryStream(_photo.PreviewPng));
            GenerateButton.IsEnabled = true;
            SetProgress(0, "Photo ready");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not read that photo", ex.Message, "OK");
        }
    }

    private void OnWidthChanged(object? sender, ValueChangedEventArgs e)
    {
        WidthLabel.Text = $"{e.NewValue:0} mm";
    }

    private async void OnGenerateClicked(object? sender, EventArgs e)
    {
        if (_photo is null || _busy)
            return;

        _busy = true;
        GenerateButton.IsEnabled = false;
        CaptureButton.IsEnabled = false;
        PickButton.IsEnabled = false;
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
                ColourCount = ColourSwitch.IsToggled ? 16 : 4,
                WidthMm = (float)WidthSlider.Value,
                ExportDetail = 220,
                PreviewDetail = 40,
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

        var palette = result.Palette.Colours;
        var mesh = result.Mesh;

        await Task.Run(() =>
        {
            using (var stream = File.Create(threeMf))
                ThreeMfWriter.Write(stream, mesh, palette, "Pic2Stl model");

            using (var stream = File.Create(stl))
                StlWriter.Write(stream, mesh, "Pic2Stl model");
        });

        _threeMfPath = threeMf;
        _stlPath = stl;
    }

    private void ShowResult(GenerationResult result)
    {
        Viewer.SetMesh(result.PreviewMesh, result.Palette.Colours);
        ViewerHint.IsVisible = false;

        var (min, max) = result.Mesh.Bounds();
        StatsLabel.Text =
            $"{result.TriangleCount:N0} triangles · {result.Palette.Count} colours · " +
            $"{max.X - min.X:0}×{max.Y - min.Y:0}×{max.Z - min.Z:0} mm · " +
            $"{result.EngineName} · {result.Elapsed.TotalSeconds:0.0}s";
        StatsLabel.IsVisible = true;

        Share3mfButton.IsEnabled = true;
        ShareStlButton.IsEnabled = true;

        SetProgress(100, "Model ready");
    }

    private async void OnShareThreeMf(object? sender, EventArgs e) => await ShareAsync(_threeMfPath, "Share 3MF");

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
