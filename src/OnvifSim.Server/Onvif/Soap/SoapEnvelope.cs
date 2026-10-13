using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OnvifSim.Onvif.Soap;

// Reads a SOAP request and writes the response or fault in the same SOAP version. ONVIF uses SOAP 1.2;
// 1.1 is answered too because a few older clients still send it.
public sealed class SoapEnvelope
{
    public const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    public const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 4 * 1024 * 1024,
        IgnoreComments = true,
    };

    private SoapEnvelope(string envelopeNamespace, XElement? security, XElement body)
    {
        EnvelopeNamespace = envelopeNamespace;
        Security = security;
        Body = body;
    }

    public string EnvelopeNamespace { get; }
    // The wsse:Security header, if any.
    public XElement? Security { get; }
    // The first element inside soap:Body: the operation's request element.
    public XElement Body { get; }

    // Throws OnvifFault (WellFormed) for anything that is not a SOAP request with a body element.
    public static SoapEnvelope Parse(Stream stream)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, ReaderSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw OnvifFault.WellFormed($"The request is not well-formed XML: {ex.Message}");
        }

        var envelope = document.Root!;
        string ns = envelope.Name.NamespaceName;
        if (envelope.Name.LocalName != "Envelope" || ns is not (Soap12 or Soap11))
            throw OnvifFault.WellFormed("The request is not a SOAP envelope.");
        var body = envelope.Element(XName.Get("Body", ns))?.Elements().FirstOrDefault()
            ?? throw OnvifFault.WellFormed("The SOAP body is empty.");
        var security = envelope.Element(XName.Get("Header", ns))?.Element(XName.Get("Security", Wsse));
        return new SoapEnvelope(ns, security, body);
    }

    public static string ContentType(string envelopeNamespace) =>
        envelopeNamespace == Soap11 ? "text/xml; charset=utf-8" : "application/soap+xml; charset=utf-8";

    public static byte[] Write(string envelopeNamespace, Action<XmlWriter> writeBody)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            writer.WriteStartElement("s", "Envelope", envelopeNamespace);
            writer.WriteStartElement("s", "Body", envelopeNamespace);
            writeBody(writer);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        return stream.ToArray();
    }

    public static byte[] WriteFault(string envelopeNamespace, OnvifFault fault) =>
        Write(envelopeNamespace, writer =>
        {
            writer.WriteStartElement("s", "Fault", envelopeNamespace);
            if (envelopeNamespace == Soap11)
            {
                writer.WriteElementString("faultcode", "", fault.IsSender ? "s:Client" : "s:Server");
                writer.WriteElementString("faultstring", "", fault.Message);
            }
            else
            {
                writer.WriteStartElement("s", "Code", envelopeNamespace);
                writer.WriteElementString("s", "Value", envelopeNamespace, fault.IsSender ? "s:Sender" : "s:Receiver");
                foreach (var subcode in fault.Subcodes)
                {
                    writer.WriteStartElement("s", "Subcode", envelopeNamespace);
                    writer.WriteStartElement("s", "Value", envelopeNamespace);
                    writer.WriteAttributeString("xmlns", "ter", null, OnvifNamespaces.Errors);
                    writer.WriteString("ter:" + subcode);
                    writer.WriteEndElement();
                }
                foreach (var _ in fault.Subcodes) writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("s", "Reason", envelopeNamespace);
                writer.WriteStartElement("s", "Text", envelopeNamespace);
                writer.WriteAttributeString("xml", "lang", null, "en");
                writer.WriteString(fault.Message);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        });
}
