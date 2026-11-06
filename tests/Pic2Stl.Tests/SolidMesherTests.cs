using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Meshing;

namespace Pic2Stl.Tests;

[TestFixture]
public class SolidMesherTests
{
    private static readonly SolidMeshOptions Options = new()
    {
        WidthMm = 80f,
        ReliefMm = 16f,
        BackRatio = 0.45f,
        MinThicknessMm = 2f,
    };

    [Test]
    public void Build_ProducesWatertightOutwardFacingSolid()
    {
        var mesh = SolidMesher.Build(TestImages.Bump(), materials: null, Options);

        TestImages.AssertWatertight(mesh);
        TestImages.AssertOutwardFacing(mesh);
    }

    [Test]
    public void Build_VertexAndTriangleCountsMatchGrid()
    {
        const int width = 24, height = 18;
        var mesh = SolidMesher.Build(DepthMap.Constant(width, height), null, Options);

        var cells = (width - 1) * (height - 1);
        var perimeterEdges = 2 * (width - 1) + 2 * (height - 1);

        Assert.Multiple(() =>
        {
            Assert.That(mesh.Vertices, Has.Count.EqualTo(width * height * 2));
            Assert.That(mesh.Triangles, Has.Count.EqualTo(cells * 4 + perimeterEdges * 2));
        });
    }

    [Test]
    public void Build_FlatDepthGivesSlabOfExpectedThickness()
    {
        var mesh = SolidMesher.Build(DepthMap.Constant(10, 10, 0f), null, Options);
        var (min, max) = mesh.Bounds();

        // Front sits at MinThickness, back at -0.6 * MinThickness.
        Assert.Multiple(() =>
        {
            Assert.That(max.Z, Is.EqualTo(Options.MinThicknessMm).Within(1e-4));
            Assert.That(min.Z, Is.EqualTo(-0.6 * Options.MinThicknessMm).Within(1e-4));
            Assert.That(max.X - min.X, Is.EqualTo(Options.WidthMm).Within(1e-3));
            Assert.That(max.Y - min.Y, Is.EqualTo(Options.WidthMm).Within(1e-3));
        });
    }

    [Test]
    public void Build_ModelIsSolidWithPositiveVolume()
    {
        const int size = 20;
        var mesh = SolidMesher.Build(DepthMap.Constant(size, size, 0f), null, Options);

        var expected = Options.WidthMm * Options.WidthMm * Options.MinThicknessMm * 1.6;
        Assert.That(mesh.SignedVolume(), Is.EqualTo(expected).Within(expected * 0.01));
    }

    [Test]
    public void Build_ReliefIncreasesVolumeAndHeight()
    {
        var flat = SolidMesher.Build(DepthMap.Constant(16, 16, 0f), null, Options);
        var bump = SolidMesher.Build(TestImages.Bump(16, 16), null, Options);

        Assert.That(bump.Bounds().Max.Z, Is.GreaterThan(flat.Bounds().Max.Z));
        Assert.That(bump.SignedVolume(), Is.GreaterThan(flat.SignedVolume()));
    }

    [Test]
    public void Build_AssignsMaterialsFromTheMapInsidePaletteRange()
    {
        var image = TestImages.FourQuadrants();
        var quantised = PaletteBuilder.Build(image, 4);
        var depth = DepthMap.Constant(image.Width, image.Height, 0.5f);

        var mesh = SolidMesher.Build(depth, quantised.Map, Options);
        var used = mesh.TrianglesPerMaterial().Keys;

        Assert.That(used, Is.Not.Empty);
        Assert.That(used.All(m => m < quantised.Palette.Count), "Material index outside the palette.");
    }

    [Test]
    public void Build_RejectsMismatchedMaterialSize()
    {
        var depth = DepthMap.Constant(8, 8);
        var map = new MaterialMap(4, 4, new byte[16]);

        Assert.That(() => SolidMesher.Build(depth, map, Options), Throws.ArgumentException);
    }

    [Test]
    public void Build_RejectsGridSmallerThanTwoByTwo()
    {
        Assert.That(() => SolidMesher.Build(DepthMap.Constant(1, 4), null, Options), Throws.ArgumentException);
    }

    [Test]
    public void Perimeter_WalksEveryBoundaryPointOnce()
    {
        var points = SolidMesher.Perimeter(5, 4);

        Assert.Multiple(() =>
        {
            Assert.That(points, Has.Count.EqualTo(2 * (5 - 1) + 2 * (4 - 1)));
            Assert.That(points.Distinct().Count(), Is.EqualTo(points.Count));
            Assert.That(points, Does.Contain((0, 0)));
            Assert.That(points, Does.Contain((4, 3)));
        });
    }
}
