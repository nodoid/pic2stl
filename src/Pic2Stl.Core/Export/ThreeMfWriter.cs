using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Pic2Stl.Core.Colours;
using Pic2Stl.Core.Geometry;

namespace Pic2Stl.Core.Export;

/// <summary>
/// Writes a 3MF package (OPC zip with a core 3D model part). Each print colour becomes a basematerial,
/// and triangles are split into one object per material so a multi-material slicer can assign spools.
/// </summary>
public static class ThreeMfWriter
{
    private const string ModelNamespace = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private const string ContentTypesXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml" />
        </Types>
        """;
    private const string RelationshipsXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel" />
        </Relationships>
        """;

    public static void Write(
        Stream stream,
        Mesh mesh,
        IReadOnlyList<Rgb> palette,
        string title = "Pic2Stl model",
        IReadOnlyList<string>? colourNames = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(palette);

        if (mesh.Triangles.Count == 0)
            throw new InvalidOperationException("Cannot write a 3MF with no triangles.");

        if (palette.Count == 0)
            throw new ArgumentException("A 3MF needs at least one material.", nameof(palette));

        if (colourNames is not null && colourNames.Count != palette.Count)
            throw new ArgumentException("Give every print colour a name, or none.", nameof(colourNames));

        var usedMaterials = mesh.TrianglesPerMaterial().Keys.OrderBy(i => i).ToArray();
        if (usedMaterials.Any(m => m >= palette.Count))
            throw new InvalidOperationException("A triangle references a material that is not in the palette.");

        // Materials id 1, one mesh object per colour from id 2 upwards, then the assembly object.
        const int materialsId = 1;
        var objectIds = new Dictionary<byte, int>();
        var nextId = 2;
        foreach (var material in usedMaterials)
            objectIds[material] = nextId++;

        var assemblyId = nextId;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(archive, "_rels/.rels", RelationshipsXml);

        var modelEntry = archive.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal);
        // The entry stream must be closed so the deflate stream flushes into the archive.
        using (var entryStream = modelEntry.Open())
            WriteModel(entryStream, mesh, palette, title, colourNames, usedMaterials, materialsId, objectIds,
                assemblyId);

        var settingsEntry = archive.CreateEntry("Metadata/model_settings.config", CompressionLevel.Optimal);
        using (var entryStream = settingsEntry.Open())
            WriteModelSettings(entryStream, title, usedMaterials, objectIds, assemblyId, colourNames);
    }

    public static byte[] ToBytes(
        Mesh mesh,
        IReadOnlyList<Rgb> palette,
        string title = "Pic2Stl model",
        IReadOnlyList<string>? colourNames = null)
    {
        using var buffer = new MemoryStream();
        Write(buffer, mesh, palette, title, colourNames);
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void WriteModel(
        Stream stream,
        Mesh mesh,
        IReadOnlyList<Rgb> palette,
        string title,
        IReadOnlyList<string>? colourNames,
        byte[] usedMaterials,
        int materialsId,
        IReadOnlyDictionary<byte, int> objectIds,
        int assemblyId)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = " ",
            NewLineChars = "\n",
        };

        using var xml = XmlWriter.Create(stream, settings);

        xml.WriteStartDocument();
        xml.WriteStartElement("model", ModelNamespace);
        xml.WriteAttributeString("unit", "millimeter");
        xml.WriteAttributeString("xml", "lang", null, "en-GB");

        WriteMetadata(xml, "Title", title);
        WriteMetadata(xml, "Designer", "Pic2Stl");
        WriteMetadata(xml, "Description", "AI depth-derived solid generated on device with Depth Anything V2.");
        WriteMetadata(xml, "Application", "Pic2Stl");

        xml.WriteStartElement("resources", ModelNamespace);

        xml.WriteStartElement("basematerials", ModelNamespace);
        xml.WriteAttributeString("id", materialsId.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < palette.Count; i++)
        {
            xml.WriteStartElement("base", ModelNamespace);
            xml.WriteAttributeString("name", ColourName(colourNames, i));
            xml.WriteAttributeString("displaycolor", palette[i].ToHex());
            xml.WriteEndElement();
        }

        xml.WriteEndElement();

        foreach (var material in usedMaterials)
        {
            var triangles = mesh.Triangles.Where(t => t.Material == material).ToArray();
            WriteMeshObject(xml, objectIds[material], materialsId, material, mesh, triangles);
        }

        if (usedMaterials.Length > 1)
        {
            xml.WriteStartElement("object", ModelNamespace);
            xml.WriteAttributeString("id", assemblyId.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("type", "model");
            xml.WriteStartElement("components", ModelNamespace);
            foreach (var material in usedMaterials)
            {
                xml.WriteStartElement("component", ModelNamespace);
                xml.WriteAttributeString("objectid", objectIds[material].ToString(CultureInfo.InvariantCulture));
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // resources

        xml.WriteStartElement("build", ModelNamespace);
        xml.WriteStartElement("item", ModelNamespace);
        xml.WriteAttributeString("objectid", (usedMaterials.Length > 1 ? assemblyId : objectIds[usedMaterials[0]])
            .ToString(CultureInfo.InvariantCulture));
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteEndElement(); // model
        xml.WriteEndDocument();
    }

    /// <summary>
    /// The colour and spool assignments the slicing apps actually read. They ignore a 3MF's own material list, so
    /// without this part every colour arrives as one default-coloured part. No print settings are written: the
    /// slicer keeps the profile the user already chose.
    /// </summary>
    private static void WriteModelSettings(
        Stream stream,
        string title,
        byte[] usedMaterials,
        IReadOnlyDictionary<byte, int> objectIds,
        int assemblyId,
        IReadOnlyList<string>? colourNames)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = " ",
            NewLineChars = "\n",
        };

        using var xml = XmlWriter.Create(stream, settings);

        var builtObjectId = usedMaterials.Length > 1 ? assemblyId : objectIds[usedMaterials[0]];
        var builtObject = builtObjectId.ToString(CultureInfo.InvariantCulture);

        xml.WriteStartDocument();
        xml.WriteStartElement("config");

        xml.WriteStartElement("object");
        xml.WriteAttributeString("id", builtObject);
        WriteSetting(xml, "name", title);
        WriteSetting(xml, "extruder", "1");

        if (usedMaterials.Length > 1)
        {
            for (var i = 0; i < usedMaterials.Length; i++)
            {
                var spool = (i + 1).ToString(CultureInfo.InvariantCulture);

                // Part ids run from 2: the slicer reads id 1 as the object itself, so a part numbered from 1
                // lands on the next colour and leaves the last one unassigned.
                xml.WriteStartElement("part");
                xml.WriteAttributeString("id", (i + 2).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("subtype", "normal_part");
                WriteSetting(xml, "name", ColourName(colourNames, usedMaterials[i]));
                WriteSetting(xml, "extruder", spool);
                xml.WriteEndElement();
            }
        }

        xml.WriteEndElement(); // object

        xml.WriteStartElement("plate");
        WriteSetting(xml, "plater_id", "1");
        WriteSetting(xml, "plater_name", "");
        WriteSetting(xml, "locked", "false");
        xml.WriteStartElement("model_instance");
        WriteSetting(xml, "object_id", builtObject);
        WriteSetting(xml, "instance_id", "0");
        WriteSetting(xml, "identify_id", "1");
        xml.WriteEndElement();
        xml.WriteEndElement(); // plate

        xml.WriteEndElement(); // config
        xml.WriteEndDocument();
    }

    private static void WriteMeshObject(
        XmlWriter xml,
        int objectId,
        int materialsId,
        byte material,
        Mesh mesh,
        IReadOnlyList<Triangle> triangles)
    {
        // Compact the vertex list to only those used by this material's triangles.
        var localIndices = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        var remapped = new List<Triangle>(triangles.Count);

        foreach (var triangle in triangles)
        {
            var a = LocalIndex(triangle.A);
            var b = LocalIndex(triangle.B);
            var c = LocalIndex(triangle.C);
            remapped.Add(new Triangle(a, b, c, material));
        }

        int LocalIndex(int global)
        {
            if (localIndices.TryGetValue(global, out var existing))
                return existing;

            var index = vertices.Count;
            vertices.Add(mesh.VertexAt(global));
            localIndices[global] = index;
            return index;
        }

        xml.WriteStartElement("object", ModelNamespace);
        xml.WriteAttributeString("id", objectId.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("type", "model");
        xml.WriteAttributeString("pid", materialsId.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("pindex", material.ToString(CultureInfo.InvariantCulture));

        xml.WriteStartElement("mesh", ModelNamespace);

        xml.WriteStartElement("vertices", ModelNamespace);
        foreach (var v in vertices)
        {
            xml.WriteStartElement("vertex", ModelNamespace);
            xml.WriteAttributeString("x", Format(v.X));
            xml.WriteAttributeString("y", Format(v.Y));
            xml.WriteAttributeString("z", Format(v.Z));
            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // vertices

        xml.WriteStartElement("triangles", ModelNamespace);
        foreach (var t in remapped)
        {
            xml.WriteStartElement("triangle", ModelNamespace);
            xml.WriteAttributeString("v1", t.A.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("v2", t.B.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("v3", t.C.ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // triangles
        xml.WriteEndElement(); // mesh
        xml.WriteEndElement(); // object
    }

    /// <summary>What the slicer will call this spool: the user's name, or a neutral default.</summary>
    private static string ColourName(IReadOnlyList<string>? names, int index) =>
        names is { } given && !string.IsNullOrWhiteSpace(given[index]) ? given[index].Trim() : $"Colour {index + 1}";

    private static void WriteMetadata(XmlWriter xml, string name, string value)
    {
        xml.WriteStartElement("metadata", ModelNamespace);
        xml.WriteAttributeString("name", name);
        xml.WriteString(value);
        xml.WriteEndElement();
    }

    /// <summary>A slicer setting: the config files spell metadata as key/value attributes, not as text.</summary>
    private static void WriteSetting(XmlWriter xml, string key, string value)
    {
        xml.WriteStartElement("metadata");
        xml.WriteAttributeString("key", key);
        xml.WriteAttributeString("value", value);
        xml.WriteEndElement();
    }

    private static string Format(float value) =>
        MathF.Abs(value) < 0.0000005f ? "0" : value.ToString("0.#####", CultureInfo.InvariantCulture);
}
