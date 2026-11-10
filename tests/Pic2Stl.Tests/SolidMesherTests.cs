using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Geometry;
using Pic2Stl.Core.Imaging;
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
    public void Build_ClosesEveryColourSoASlicerCanGiveItItsOwnSpool()
    {
        const int width = 20, height = 20;
        var indices = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                indices[y * width + x] = (byte)(x < width / 2 ? 0 : 1);

        var mesh = SolidMesher.Build(
            DepthMap.Constant(width, height),
            new MaterialMap(width, height, indices),
            Options.SealingColours());

        // Splitting a solid by material leaves each colour open along the seam unless the seam is walled.
        foreach (var material in new byte[] { 0, 1 })
        {
            var colour = SubMesh(mesh, material);
            Assert.That(colour.Triangles, Is.Not.Empty);
            TestImages.AssertWatertight(colour);
            TestImages.AssertOutwardFacing(colour);
        }
    }

    [Test]
    public void Build_LeavesASingleColourSolidUnchanged()
    {
        var depth = DepthMap.Constant(12, 10);
        var plain = SolidMesher.Build(depth, null, Options);
        var tagged = SolidMesher.Build(depth, new MaterialMap(12, 10, new byte[120]), Options);

        Assert.That(tagged.Triangles, Has.Count.EqualTo(plain.Triangles.Count));
    }

    [Test]
    public void Build_KeepsTheSingleSolidWatertightUnlessSealingColoursIsAskedFor()
    {
        var depth = DepthMap.Constant(12, 10);
        var indices = new byte[120];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = (byte)(i % 2);

        var mesh = SolidMesher.Build(depth, new MaterialMap(12, 10, indices), Options);

        TestImages.AssertWatertight(mesh);
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
    public void Build_WithOutlinePrintsTheObjectInsteadOfThePlate()
    {
        const int width = 30, height = 20;
        var depth = DepthMap.Constant(width, height, 0.5f);

        // A horizontal band: 30x4 pixels of subject in a 30x20 photo. The trimmed model is therefore 80x10.7 mm,
        // where the untrimmed plate would have been 80x53.3 mm.
        var keep = new bool[width * height];
        for (var y = 8; y < 12; y++)
            for (var x = 0; x < width; x++)
                keep[y * width + x] = true;

        var mesh = SolidMesher.Build(depth, null, Options, new SelectionMask(width, height, keep));
        var (min, max) = mesh.Bounds();

        Assert.Multiple(() =>
        {
            Assert.That(max.X - min.X, Is.EqualTo(Options.WidthMm).Within(1e-3));
            Assert.That(max.Y - min.Y, Is.EqualTo(Options.WidthMm * 4 / 30).Within(1e-3));
        });

        TestImages.AssertWatertight(mesh);
        TestImages.AssertOutwardFacing(mesh);
    }

    [Test]
    public void Build_WithOutlineClosesTheObjectWithATaperedEdge()
    {
        const int width = 40, height = 30;
        var depth = DepthMap.Constant(width, height, 0.5f);
        var keep = new bool[width * height];

        for (var y = 5; y < 25; y++)
            for (var x = 5; x < 35; x++)
                keep[y * width + x] = true;

        var full = SolidMesher.Build(depth, null, Options);
        var trimmed = SolidMesher.Build(depth, null, Options, new SelectionMask(width, height, keep));

        var fullFront = full.Vertices.Where(v => v.Z > 0).Min(v => v.Z);
        var trimmedFront = trimmed.Vertices.Where(v => v.Z > 0).Min(v => v.Z);

        Assert.Multiple(() =>
        {
            // The untrimmed plate keeps the full relief right up to its rectangle; the object fades to the base
            // at its own outline, so the height the photo cannot show is not printed as a vertical wall.
            Assert.That(fullFront, Is.EqualTo(Options.MinThicknessMm + 0.5f * Options.ReliefMm).Within(1e-4));
            Assert.That(trimmedFront, Is.EqualTo(Options.MinThicknessMm).Within(1e-4));
            Assert.That(trimmed.SignedVolume(), Is.LessThan(full.SignedVolume()));
        });

        TestImages.AssertWatertight(trimmed);
        TestImages.AssertOutwardFacing(trimmed);
    }

    [Test]
    public void Build_WithIrregularOutlineStaysWatertight()
    {
        const int width = 36, height = 36;
        var keep = new bool[width * height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                keep[y * width + x] = (x - 18) * (x - 18) + (y - 18) * (y - 18) <= 12 * 12;

        // A hole inside the object must become a wall too, not an open surface.
        for (var y = 15; y < 21; y++)
            for (var x = 15; x < 21; x++)
                keep[y * width + x] = false;

        var mesh = SolidMesher.Build(DepthMap.Constant(width, height, 0.7f), null, Options,
            new SelectionMask(width, height, keep));

        TestImages.AssertWatertight(mesh);
        TestImages.AssertOutwardFacing(mesh);
    }

    [Test]
    public void Build_RejectsMismatchedOutlineSize()
    {
        var depth = DepthMap.Constant(8, 8);

        Assert.That(
            () => SolidMesher.Build(depth, null, Options, SelectionMask.All(4, 4)),
            Throws.ArgumentException);
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

    /// <summary>The triangles of one material, rebuilt as a mesh of its own.</summary>
    private static Mesh SubMesh(Mesh mesh, byte material)
    {
        var sub = new Mesh();
        var remap = new Dictionary<int, int>();

        foreach (var t in mesh.Triangles.Where(t => t.Material == material))
            sub.AddTriangle(Index(t.A), Index(t.B), Index(t.C), material);

        return sub;

        int Index(int vertex)
        {
            if (remap.TryGetValue(vertex, out var existing))
                return existing;

            var index = sub.AddVertex(mesh.VertexAt(vertex));
            remap[vertex] = index;
            return index;
        }
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
