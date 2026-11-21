using System.Globalization;
using System.Xml;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.IO;

/// <summary>Експорт на TIN повърхнини в LandXML 1.2 (за обмен с Civil 3D, Plateia, и др.).</summary>
public static class LandXmlWriter
{
    private const string Ns = "http://www.landxml.org/schema/LandXML-1.2";

    public static void Write(string path, IEnumerable<Surface> surfaces)
    {
        var settings = new XmlWriterSettings { Indent = true };
        using var w = XmlWriter.Create(path, settings);
        var ci = CultureInfo.InvariantCulture;
        var now = DateTime.Now;

        w.WriteStartDocument();
        w.WriteStartElement("LandXML", Ns);
        w.WriteAttributeString("version", "1.2");
        w.WriteAttributeString("date", now.ToString("yyyy-MM-dd", ci));
        w.WriteAttributeString("time", now.ToString("HH:mm:ss", ci));

        w.WriteStartElement("Units", Ns);
        w.WriteStartElement("Metric", Ns);
        w.WriteAttributeString("areaUnit", "squareMeter");
        w.WriteAttributeString("linearUnit", "meter");
        w.WriteAttributeString("volumeUnit", "cubicMeter");
        w.WriteAttributeString("temperatureUnit", "celsius");
        w.WriteAttributeString("pressureUnit", "milliBars");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("Surfaces", Ns);
        foreach (var s in surfaces)
        {
            var tris = s.Triangles;
            w.WriteStartElement("Surface", Ns);
            w.WriteAttributeString("name", s.Name);

            if (s.Breaklines.Count > 0 || s.Boundaries.Count > 0)
            {
                w.WriteStartElement("SourceData", Ns);
                if (s.Boundaries.Count > 0)
                {
                    w.WriteStartElement("Boundaries", Ns);
                    foreach (var b in s.Boundaries)
                    {
                        w.WriteStartElement("Boundary", Ns);
                        w.WriteAttributeString("name", b.Name);
                        w.WriteAttributeString("bndType", b.Type switch
                        {
                            BoundaryType.Hide => "void",
                            BoundaryType.Show => "island",
                            _ => "outer",
                        });
                        w.WriteAttributeString("edgeTrim", "true");
                        w.WriteStartElement("PntList2D", Ns);
                        var ring = b.Vertices.Append(b.Vertices[0]);
                        w.WriteString(string.Join(" ", ring.Select(p => string.Create(ci, $"{p.Y:F4} {p.X:F4}"))));
                        w.WriteEndElement();
                        w.WriteEndElement();
                    }
                    w.WriteEndElement(); // Boundaries
                }
                if (s.Breaklines.Count > 0)
                {
                    w.WriteStartElement("Breaklines", Ns);
                    foreach (var b in s.Breaklines)
                    {
                        w.WriteStartElement("Breakline", Ns);
                        w.WriteAttributeString("name", b.Name);
                        w.WriteAttributeString("brkType", "standard");
                        w.WriteStartElement("PntList3D", Ns);
                        w.WriteString(string.Join(" ", b.Vertices.Select(p => string.Create(ci, $"{p.Y:F4} {p.X:F4} {p.Z:F4}"))));
                        w.WriteEndElement();
                        w.WriteEndElement();
                    }
                    w.WriteEndElement(); // Breaklines
                }
                w.WriteEndElement(); // SourceData
            }

            w.WriteStartElement("Definition", Ns);
            w.WriteAttributeString("surfType", "TIN");

            // Използват се само точките, участващи в триъгълници
            var used = new SortedSet<int>(tris);
            var id = new Dictionary<int, int>();
            w.WriteStartElement("Pnts", Ns);
            int n = 1;
            foreach (var i in used)
            {
                id[i] = n;
                var p = s.Vertices[i];
                w.WriteStartElement("P", Ns);
                w.WriteAttributeString("id", n.ToString(ci));
                // LandXML: северна координата, източна, кота
                w.WriteString(string.Create(ci, $"{p.Y:F4} {p.X:F4} {p.Z:F4}"));
                w.WriteEndElement();
                n++;
            }
            w.WriteEndElement();

            w.WriteStartElement("Faces", Ns);
            for (int k = 0; k < tris.Length; k += 3)
            {
                w.WriteStartElement("F", Ns);
                w.WriteString(string.Create(ci, $"{id[tris[k]]} {id[tris[k + 1]]} {id[tris[k + 2]]}"));
                w.WriteEndElement();
            }
            w.WriteEndElement();

            w.WriteEndElement(); // Definition
            w.WriteEndElement(); // Surface
        }
        w.WriteEndElement(); // Surfaces
        w.WriteEndElement(); // LandXML
        w.WriteEndDocument();
    }
}
