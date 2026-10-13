using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using OnvifSim.Analytics;
using OnvifSim.Onvif.Services;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;

namespace OnvifSim.Onvif;

// One document of a camera's metadata stream (tt:MetadataStream, ONVIF Streaming spec 5.1.2.1): the
// people its analytics sees as objects of a scene frame, and where its PTZ head is. Boxes are in the
// normalized frame coordinates, so no Transformation is needed; the class is given both ways the schema
// allows, ClassCandidate for older clients and Type, which ONVIF recommends now.
public static class MetadataDocument
{
    private static readonly XmlSerializer PtzStatusSerializer =
        new(typeof(PTZStatus), new XmlRootAttribute("PTZStatus") { Namespace = OnvifNamespaces.Schema });

    // Only tt, which the document has declared already: the serializer would declare every prefix it is given.
    private static readonly XmlSerializerNamespaces SchemaPrefix = new([new XmlQualifiedName("tt", OnvifNamespaces.Schema)]);

    private static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(false), Indent = false };

    public static byte[] Write(AnalyticsFrame frame)
    {
        var camera = frame.Camera;
        var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, Settings))
        {
            const string tt = OnvifNamespaces.Schema;
            writer.WriteStartDocument();
            writer.WriteStartElement("tt", "MetadataStream", tt);
            if (camera.Metadata.Analytics)
            {
                writer.WriteStartElement("VideoAnalytics", tt);
                writer.WriteStartElement("Frame", tt);
                writer.WriteAttributeString("UtcTime", frame.UtcTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                foreach (var obj in frame.Objects) WriteObject(writer, obj);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            if (camera.Metadata.PtzStatus && frame.Ptz is { } head && camera.Ptz is { } limits)
            {
                writer.WriteStartElement("PTZ", tt);
                PtzStatusSerializer.Serialize(writer, CameraPtz.Status(limits, head, frame.UtcTime.UtcDateTime), SchemaPrefix);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        return buffer.ToArray();
    }

    private static void WriteObject(XmlWriter writer, TrackedObject obj)
    {
        const string tt = OnvifNamespaces.Schema;
        var box = obj.Box;
        writer.WriteStartElement("Object", tt);
        writer.WriteAttributeString("ObjectId", obj.ObjectId.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("Appearance", tt);

        writer.WriteStartElement("Shape", tt);
        writer.WriteStartElement("BoundingBox", tt);
        writer.WriteAttributeString("left", Number(box.Left));
        writer.WriteAttributeString("top", Number(box.Top));
        writer.WriteAttributeString("right", Number(box.Right));
        writer.WriteAttributeString("bottom", Number(box.Bottom));
        writer.WriteEndElement();
        writer.WriteStartElement("CenterOfGravity", tt);
        writer.WriteAttributeString("x", Number(box.CenterX));
        writer.WriteAttributeString("y", Number(box.CenterY));
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("Class", tt);
        writer.WriteStartElement("ClassCandidate", tt);
        writer.WriteElementString("Type", tt, "Human");
        writer.WriteElementString("Likelihood", tt, "1");
        writer.WriteEndElement();
        writer.WriteStartElement("Type", tt);
        writer.WriteAttributeString("Likelihood", "1");
        writer.WriteString("Human");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    // Three decimals: the frame is 2 units wide, so a thousandth is about a pixel at the widest frame (2040).
    private static string Number(float value) => MathF.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
