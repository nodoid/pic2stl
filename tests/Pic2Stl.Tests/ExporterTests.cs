using System.IO.Compression;
using System.Xml.Linq;
using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Depth;
using Pic2Stl.Core.Export;
using Pic2Stl.Core.Geometry;
using Pic2Stl.Core.Meshing;

namespace Pic2Stl.Tests;

[TestFixture]
public class StlWriterTests
{
    private static Mesh SampleMesh()
    {
        var options = new SolidMeshOptions { WidthMm = 40, ReliefMm = 8, MinThicknessMm = 1.5f };
        return SolidMesher.Build(TestImages.Bump(12, 9), null, options);
    }

    [Test]
    public void Write_EmitsBinaryStlOfExpectedSize()
    {
        var mesh = SampleMesh();
        var bytes = StlWriter.ToBytes(mesh, "unit-test");

        var declared = BitConverter.ToUInt32(bytes, StlWriter.HeaderLength);
        Assert.Multiple(() =>
        {
            Assert.That(bytes.Length, Is.EqualTo(84 + mesh.Triangles.Count * 50));
            Assert.That(declared, Is.EqualTo((uint)mesh.Triangles.Count));
        });
    }

    [Test]
    public void Write_HeaderMentionsTheApp()
    {
        var bytes = StlWriter.ToBytes(SampleMesh(), "hello");
        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, StlWriter.HeaderLength);

        Assert.That(header, Does.Contain("Pic2Stl"));
    }

    [Test]
    public void Write_FirstFacetMatchesMeshWithUnitNormal()
    {
        var mesh = SampleMesh();
        var bytes = StlWriter.ToBytes(mesh);
        var offset = StlWriter.HeaderLength + 4;

        float Read(int index) => BitConverter.ToSingle(bytes, offset + index * 4);

        var normal = new Vector3(Read(0), Read(1), Read(2));
        var a = new Vector3(Read(3), Read(4), Read(5));
        var b = new Vector3(Read(6), Read(7), Read(8));
        var c = new Vector3(Read(9), Read(10), Read(11));

        var first = mesh.Triangles[0];
        Assert.Multiple(() =>
        {
            Assert.That(normal.Length, Is.EqualTo(1f).Within(1e-4));
            Assert.That(a, Is.EqualTo(mesh.VertexAt(first.A)));
            Assert.That(b, Is.EqualTo(mesh.VertexAt(first.B)));
            Assert.That(c, Is.EqualTo(mesh.VertexAt(first.C)));
        });
    }

    [Test]
    public void Write_RejectsEmptyMesh()
    {
        Assert.That(() => StlWriter.ToBytes(new Mesh()), Throws.InvalidOperationException);
    }
}

[TestFixture]
public class ThreeMfWriterTests
{
    private const string Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    private static (Mesh Mesh, Palette Palette) Sample(int colourCount = 4)
    {
        var image = colourCount > 4 ? TestImages.Gradient(48, 48) : TestImages.FourQuadrants();
        var quantised = PaletteBuilder.Build(image, colourCount);
        var depth = DepthMap.Constant(image.Width, image.Height, 0.6f);
        var mesh = SolidMesher.Build(depth, quantised.Map, new SolidMeshOptions { WidthMm = 60 });
        return (mesh, quantised.Palette);
    }

    [Test]
    public void Write_ContainsTheRequiredPackageParts()
    {
        var (mesh, palette) = Sample();
        using var archive = Open(mesh, palette);

        Assert.Multiple(() =>
        {
            Assert.That(archive.GetEntry("[Content_Types].xml"), Is.Not.Null);
            Assert.That(archive.GetEntry("_rels/.rels"), Is.Not.Null);
            Assert.That(archive.GetEntry("3D/3dmodel.model"), Is.Not.Null);
        });
    }

    [Test]
    public void Write_RelationshipsPointAtTheModelPart()
    {
        var (mesh, palette) = Sample();
        using var archive = Open(mesh, palette);

        var relationship = ReadXml(archive, "_rels/.rels").Root!.Elements().Single();
        Assert.Multiple(() =>
        {
            Assert.That(relationship.Attribute("Target")!.Value, Is.EqualTo("/3D/3dmodel.model"));
            Assert.That(relationship.Attribute("Type")!.Value, Does.Contain("3dmodel"));
        });
    }

    [Test]
    public void Write_DeclaresOneBaseMaterialPerPrintColour()
    {
        var (mesh, palette) = Sample(4);
        var model = ReadModel(mesh, palette);

        var materials = Resources(model).Element(XName.Get("basematerials", Core))!;
        var bases = materials.Elements(XName.Get("base", Core)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(bases, Has.Count.EqualTo(4));
            Assert.That(materials.Attribute("id")!.Value, Is.EqualTo("1"));
            Assert.That(bases.Select(b => b.Attribute("displaycolor")!.Value).Distinct().Count(), Is.EqualTo(4));
            Assert.That(bases.All(b => b.Attribute("displaycolor")!.Value.StartsWith('#')), Is.True);
            Assert.That(model.Attribute("unit")!.Value, Is.EqualTo("millimeter"));
        });
    }

    [Test]
    public void Write_NamesTheMaterialsAsTheUserDefinedThem()
    {
        var (mesh, palette) = Sample(4);
        string[] names = ["Skin", "Hair", "Shirt", "Shoes"];
        var bases = BaseMaterials(ReadModel(mesh, palette, names));

        Assert.Multiple(() =>
        {
            Assert.That(bases.Select(b => b.Attribute("name")!.Value), Is.EqualTo(names));
            Assert.That(
                bases.Select(b => b.Attribute("displaycolor")!.Value),
                Is.EqualTo(palette.Colours.Select(colour => colour.ToHex())),
                "The 3MF must carry the colour the user defined, not the one it was quantised from.");
        });
    }

    [Test]
    public void Write_FallsBackToNumberedNamesForUnnamedColours()
    {
        var (mesh, palette) = Sample(4);
        var bases = BaseMaterials(ReadModel(mesh, palette, ["", "  ", "Shoes", ""]));

        Assert.That(
            bases.Select(b => b.Attribute("name")!.Value),
            Is.EqualTo(new[] { "Colour 1", "Colour 2", "Shoes", "Colour 4" }));
    }

    [Test]
    public void Write_RejectsANameListThatDoesNotMatchThePalette()
    {
        var palette = new Palette(new[] { new Rgb(10, 20, 30) });
        var mesh = SolidMesher.Build(DepthMap.Constant(8, 8, 0.5f), null);

        Assert.That(
            () => ThreeMfWriter.ToBytes(mesh, palette.Colours, "test", ["one", "two"]),
            Throws.ArgumentException);
    }

    [Test]
    public void Write_SixteenColourPaletteIsSupported()
    {
        var (mesh, palette) = Sample(16);
        var model = ReadModel(mesh, palette);

        var bases = Resources(model).Element(XName.Get("basematerials", Core))!
            .Elements(XName.Get("base", Core)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(palette.Count, Is.EqualTo(16));
            Assert.That(bases, Has.Count.EqualTo(16));
        });
    }

    [Test]
    public void Write_PreservesEveryTriangleAcrossObjects()
    {
        var (mesh, palette) = Sample();
        var model = ReadModel(mesh, palette);

        var meshedObjects = Resources(model)
            .Elements(XName.Get("object", Core))
            .Where(o => o.Element(XName.Get("mesh", Core)) is not null)
            .ToList();

        var triangles = meshedObjects.Sum(o => o.Element(XName.Get("mesh", Core))!
            .Element(XName.Get("triangles", Core))!
            .Elements(XName.Get("triangle", Core)).Count());

        Assert.Multiple(() =>
        {
            Assert.That(meshedObjects, Has.Count.EqualTo(mesh.TrianglesPerMaterial().Count));
            Assert.That(triangles, Is.EqualTo(mesh.Triangles.Count));
        });
    }

    [Test]
    public void Write_TriangleIndicesStayInsideTheirVertexList()
    {
        var (mesh, palette) = Sample();
        var model = ReadModel(mesh, palette);

        var meshElements = Resources(model)
            .Elements(XName.Get("object", Core))
            .Select(o => o.Element(XName.Get("mesh", Core)))
            .OfType<XElement>()
            .ToList();

        Assert.That(meshElements, Is.Not.Empty);

        foreach (var meshElement in meshElements)
        {
            var vertexCount = meshElement.Element(XName.Get("vertices", Core))!
                .Elements(XName.Get("vertex", Core)).Count();

            foreach (var triangle in meshElement.Element(XName.Get("triangles", Core))!
                         .Elements(XName.Get("triangle", Core)))
                foreach (var attribute in new[] { "v1", "v2", "v3" })
                    Assert.That(int.Parse(triangle.Attribute(attribute)!.Value), Is.InRange(0, vertexCount - 1));
        }
    }

    [Test]
    public void Write_BuildItemReferencesAnObjectInThePackage()
    {
        var (mesh, palette) = Sample();
        var model = ReadModel(mesh, palette);

        var item = model.Element(XName.Get("build", Core))!.Elements(XName.Get("item", Core)).Single();
        var objectId = item.Attribute("objectid")!.Value;

        var ids = Resources(model).Elements(XName.Get("object", Core)).Select(o => o.Attribute("id")!.Value);

        Assert.That(ids, Does.Contain(objectId));
    }

    [Test]
    public void Write_MultiColourMeshIsAssembledFromComponents()
    {
        var (mesh, palette) = Sample();
        var model = ReadModel(mesh, palette);

        var assembly = Resources(model)
            .Elements(XName.Get("object", Core))
            .SingleOrDefault(o => o.Element(XName.Get("components", Core)) is not null);

        Assert.That(assembly, Is.Not.Null, "Multi-material output needs an assembly object.");
        Assert.That(assembly!.Element(XName.Get("components", Core))!.Elements(XName.Get("component", Core)).Count(),
            Is.EqualTo(mesh.TrianglesPerMaterial().Count));
    }

    [Test]
    public void Write_SingleColourMeshProducesOneObject()
    {
        var palette = new Palette(new[] { new Rgb(200, 30, 30) });
        var mesh = SolidMesher.Build(DepthMap.Constant(8, 8, 0.5f), null);
        var model = ReadModel(mesh, palette);

        var meshedObjects = Resources(model)
            .Elements(XName.Get("object", Core))
            .Where(o => o.Element(XName.Get("mesh", Core)) is not null)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(meshedObjects, Has.Count.EqualTo(1));
            Assert.That(Resources(model).Elements(XName.Get("object", Core))
                .Any(o => o.Element(XName.Get("components", Core)) is not null), Is.False);
            Assert.That(model.Element(XName.Get("build", Core))!.Elements(XName.Get("item", Core)).Single()
                .Attribute("objectid")!.Value, Is.EqualTo(meshedObjects[0].Attribute("id")!.Value));
        });
    }

    [Test]
    public void Write_GivesEveryColourPartItsOwnSpoolForTheSlicer()
    {
        var (mesh, palette) = Sample(4);
        string[] names = ["Skin", "Hair", "Shirt", "Shoes"];
        using var archive = Open(mesh, palette, names);

        var config = ReadXml(archive, "Metadata/model_settings.config").Root!;
        var parts = config.Elements(XName.Get("object")).Single().Elements(XName.Get("part")).ToList();

        static string Meta(XElement part, string key) =>
            part.Elements(XName.Get("metadata")).Single(m => m.Attribute("key")!.Value == key)
                .Attribute("value")!.Value;

        Assert.Multiple(() =>
        {
            // Slicers ignore a 3MF's own material list, so the spool assignment has to be spelled out here.
            Assert.That(parts, Has.Count.EqualTo(4));
            Assert.That(parts.Select(p => p.Attribute("id")!.Value), Is.EqualTo(new[] { "2", "3", "4", "5" }));
            Assert.That(parts.Select(p => Meta(p, "extruder")), Is.EqualTo(new[] { "1", "2", "3", "4" }));
            Assert.That(parts.Select(p => Meta(p, "name")), Is.EqualTo(names));
            Assert.That(config.Elements(XName.Get("plate")).Single()
                .Element(XName.Get("model_instance")), Is.Not.Null);
        });
    }

    [Test]
    public void Write_SingleColourNeedsNoPartList()
    {
        var palette = new Palette(new[] { new Rgb(200, 30, 30) });
        var mesh = SolidMesher.Build(DepthMap.Constant(8, 8, 0.5f), null);
        using var archive = Open(mesh, palette);

        var config = ReadXml(archive, "Metadata/model_settings.config").Root!;

        Assert.That(config.Elements(XName.Get("object")).Single().Elements(XName.Get("part")), Is.Empty);
    }

    [Test]
    public void Write_ShipsNoProjectSettingsSoTheSlicerKeepsItsOwn()
    {
        var (mesh, palette) = Sample(4);
        using var archive = Open(mesh, palette);

        Assert.That(
            archive.Entries.Select(entry => entry.FullName),
            Has.None.EqualTo("Metadata/project_settings.config"),
            "A partial project settings file overrides the slicer's print settings; it produced a G92 E0 " +
            "validation error in ElegooSlicer.");
    }

    [Test]
    public void Write_RejectsEmptyInput()
    {
        var palette = new Palette(new[] { new Rgb(1, 1, 1) });

        Assert.Multiple(() =>
        {
            Assert.That(() => ThreeMfWriter.ToBytes(new Mesh(), palette.Colours), Throws.InvalidOperationException);
            Assert.That(() => ThreeMfWriter.ToBytes(SolidMesher.Build(DepthMap.Constant(4, 4), null), Array.Empty<Rgb>()),
                Throws.ArgumentException);
        });
    }

    private static XElement Resources(XElement model) => model.Element(XName.Get("resources", Core))!;

    private static List<XElement> BaseMaterials(XElement model) =>
        Resources(model).Element(XName.Get("basematerials", Core))!
            .Elements(XName.Get("base", Core)).ToList();

    private static ZipArchive Open(Mesh mesh, Palette palette, IReadOnlyList<string>? names = null) =>
        new(new MemoryStream(ThreeMfWriter.ToBytes(mesh, palette.Colours, "test", names)), ZipArchiveMode.Read);

    private static XElement ReadModel(Mesh mesh, Palette palette, IReadOnlyList<string>? names = null)
    {
        using var archive = Open(mesh, palette, names);
        return ReadXml(archive, "3D/3dmodel.model").Root!;
    }

    private static XDocument ReadXml(ZipArchive archive, string entryName)
    {
        using var stream = archive.GetEntry(entryName)!.Open();
        return XDocument.Load(stream);
    }
}
