namespace Pic2Stl.Core.Depth;

/// <summary>
/// Downloads the free, Apache-2.0 licensed Depth Anything V2 Small model from Hugging Face once and
/// caches it locally. No account, key or payment is required.
/// </summary>
public sealed class AiModelInstaller
{
    /// <summary>Quantised weights, ~26 MB, ungated download.</summary>
    public static readonly Uri DefaultModelUri =
        new("https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model_quantized.onnx");

    /// <summary>A truncated download is always below this size; the real file is ~26 MB.</summary>
    public const long MinimumPlausibleSize = 20L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Uri _modelUri;

    public AiModelInstaller(HttpClient httpClient, string modelDirectory, Uri? modelUri = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        _httpClient = httpClient;
        _modelUri = modelUri ?? DefaultModelUri;
        ModelPath = Path.Combine(modelDirectory, "depth-anything-v2-small-int8.onnx");
    }

    public string ModelPath { get; }

    public bool IsInstalled => File.Exists(ModelPath) && new FileInfo(ModelPath).Length >= MinimumPlausibleSize;

    /// <summary>Ensures the model exists, reporting download progress in the 0..1 range.</summary>
    public async Task<string> EnsureModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            progress?.Report(1.0);
            return ModelPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var temporaryPath = ModelPath + ".download";

        try
        {
            using var response = await _httpClient
                .GetAsync(_modelUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalLength = response.Content.Headers.ContentLength ?? -1;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (totalLength > 0)
                        progress?.Report((double)copied / totalLength);
                }
            }

            var length = new FileInfo(temporaryPath).Length;
            if (length < MinimumPlausibleSize)
                throw new InvalidOperationException($"Downloaded model is only {length:N0} bytes, which looks truncated.");

            File.Move(temporaryPath, ModelPath, overwrite: true);
            progress?.Report(1.0);
            return ModelPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }
}
