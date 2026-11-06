using System.Net;
using Pic2Stl.Core.Depth;

namespace Pic2Stl.Tests;

[TestFixture]
public class AiModelInstallerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUri = request.RequestUri;
            return Task.FromResult(responder(request));
        }
    }

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pic2stl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static HttpResponseMessage ModelResponse(long length = AiModelInstaller.MinimumPlausibleSize + 1024)
    {
        var content = new ByteArrayContent(new byte[length]);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Test]
    public async Task EnsureModelAsync_DownloadsOnceAndReportsProgress()
    {
        var handler = new StubHandler(_ => ModelResponse());
        using var http = new HttpClient(handler);
        var installer = new AiModelInstaller(http, _directory);
        var reports = new ProgressCollector<double>();

        var path = await installer.EnsureModelAsync(reports);
        var values = await reports.WaitForAsync(r => r.Count > 0 && r[^1] >= 1.0);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(installer.IsInstalled, Is.True);
            Assert.That(handler.Requests, Is.EqualTo(1));
            Assert.That(handler.LastUri, Is.EqualTo(AiModelInstaller.DefaultModelUri));
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(AiModelInstaller.MinimumPlausibleSize));
            Assert.That(File.Exists(path + ".download"), Is.False, "Temporary download file should be renamed.");
            Assert.That(values, Is.Not.Empty);
            Assert.That(values[^1], Is.EqualTo(1.0));
        });
    }

    [Test]
    public async Task EnsureModelAsync_SecondCallUsesTheCache()
    {
        var handler = new StubHandler(_ => ModelResponse());
        using var http = new HttpClient(handler);
        var installer = new AiModelInstaller(http, _directory);

        await installer.EnsureModelAsync();
        await installer.EnsureModelAsync();

        Assert.That(handler.Requests, Is.EqualTo(1));
    }

    [Test]
    public void EnsureModelAsync_RejectsTruncatedDownload()
    {
        var handler = new StubHandler(_ => ModelResponse(4096));
        using var http = new HttpClient(handler);
        var installer = new AiModelInstaller(http, _directory);

        Assert.That(async () => await installer.EnsureModelAsync(), Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(installer.IsInstalled, Is.False);
            Assert.That(Directory.GetFiles(_directory), Is.Empty, "A failed download must not leave files behind.");
        });
    }

    [Test]
    public void EnsureModelAsync_SurfacesHttpFailures()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler);
        var installer = new AiModelInstaller(http, _directory);

        Assert.That(async () => await installer.EnsureModelAsync(), Throws.TypeOf<HttpRequestException>());
        Assert.That(installer.IsInstalled, Is.False);
    }

    [Test]
    public void ModelPath_IsInsideTheGivenDirectory()
    {
        using var http = new HttpClient(new StubHandler(_ => ModelResponse()));
        var installer = new AiModelInstaller(http, _directory);

        Assert.That(Path.GetDirectoryName(installer.ModelPath), Is.EqualTo(_directory));
        Assert.That(Path.GetExtension(installer.ModelPath), Is.EqualTo(".onnx"));
    }

    [Test]
    public void Constructor_ValidatesArguments()
    {
        using var http = new HttpClient(new StubHandler(_ => ModelResponse()));

        Assert.Multiple(() =>
        {
            Assert.That(() => new AiModelInstaller(null!, _directory), Throws.ArgumentNullException);
            Assert.That(() => new AiModelInstaller(http, ""), Throws.ArgumentException);
        });
    }
}
