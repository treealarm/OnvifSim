using System.Text;
using System.Xml;
using System.Xml.Linq;
using OnvifSim.Onvif.Soap;
using OnvifSim.Onvif.Wsdl;
using DeviceContract = OnvifSim.Onvif.Wsdl.Device;
using MediaContract = OnvifSim.Onvif.Wsdl.Media;
using Media2Contract = OnvifSim.Onvif.Wsdl.Media2;

namespace OnvifSim.Tests;

public class OnvifOperationTests
{
    private static XElement Write(OnvifOperation operation, object response)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("Body");
            operation.WriteResponse(writer, response);
            writer.WriteEndElement();
        }
        return XElement.Parse(text.ToString()).Elements().Single();
    }

    private static XNamespace Device => "http://www.onvif.org/ver10/device/wsdl";
    private static XNamespace Media => "http://www.onvif.org/ver10/media/wsdl";
    private static XNamespace Media2 => "http://www.onvif.org/ver20/media/wsdl";
    private static XNamespace Schema => "http://www.onvif.org/ver10/schema";

    [Fact]
    public void Response_IsWrapped_WithPartsInTheServiceNamespace()
    {
        var operation = OnvifOperation.Create(typeof(DeviceContract.Device), "GetDeviceInformation");
        var xml = Write(operation, new DeviceContract.GetDeviceInformationResponse
        {
            Manufacturer = "OnvifSim", Model = "Fixed", FirmwareVersion = "1", SerialNumber = "cam1", HardwareId = "sim",
        });
        Assert.Equal(Device + "GetDeviceInformationResponse", xml.Name);
        Assert.Equal("OnvifSim", xml.Element(Device + "Manufacturer")?.Value);
        Assert.Equal("cam1", xml.Element(Device + "SerialNumber")?.Value);
    }

    [Fact]
    public void SchemaTypesInsideAPart_KeepTheSchemaNamespace()
    {
        var operation = OnvifOperation.Create(typeof(DeviceContract.Device), "GetSystemDateAndTime");
        var xml = Write(operation, new DeviceContract.GetSystemDateAndTimeResponse
        {
            SystemDateAndTime = new SystemDateTime
            {
                DateTimeType = SetDateTimeType.NTP,
                UTCDateTime = new OnvifSim.Onvif.Wsdl.DateTime
                {
                    Date = new Date { Year = 2026, Month = 10, Day = 13 },
                    Time = new Time { Hour = 12, Minute = 0, Second = 1 },
                },
            },
        });
        Assert.Equal(Device + "GetSystemDateAndTimeResponse", xml.Name);
        var value = xml.Element(Device + "SystemDateAndTime")!;
        Assert.Equal("NTP", value.Element(Schema + "DateTimeType")?.Value);
        Assert.Equal("2026", value.Element(Schema + "UTCDateTime")?.Element(Schema + "Date")?.Element(Schema + "Year")?.Value);
    }

    [Fact]
    public void RepeatedParts_AreWrittenOncePerItem()
    {
        var operation = OnvifOperation.Create(typeof(Media2Contract.Media2), "GetProfiles");
        var xml = Write(operation, new Media2Contract.GetProfilesResponse
        {
            Profiles = [new() { token = "Profile_1", Name = "Main", @fixed = true }, new() { token = "Profile_2", Name = "Sub" }],
        });
        var profiles = xml.Elements(Media2 + "Profiles").ToList();
        Assert.Equal(["Profile_1", "Profile_2"], profiles.Select(p => p.Attribute("token")?.Value));
        // Media2 defines MediaProfile in its own namespace, unlike Media1's tt:Profile.
        Assert.Equal("Main", profiles[0].Element(Media2 + "Name")?.Value);
    }

    [Fact]
    public void Request_IsReadIntoItsClass_PartByPart()
    {
        var operation = OnvifOperation.Create(typeof(MediaContract.Media), "GetStreamUri");
        var body = XElement.Parse($"""
            <GetStreamUri xmlns="{Media}" xmlns:tt="{Schema}">
              <StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></StreamSetup>
              <ProfileToken>Profile_1</ProfileToken>
            </GetStreamUri>
            """);
        var request = Assert.IsType<MediaContract.GetStreamUriRequest>(operation.ReadRequest(body));
        Assert.Equal(StreamType.RTPUnicast, request.StreamSetup.Stream);
        Assert.Equal(TransportProtocol.RTSP, request.StreamSetup.Transport.Protocol);
        Assert.Equal("Profile_1", request.ProfileToken);
    }

    [Fact]
    public void ScalarRequestParts_AreRead()
    {
        var operation = OnvifOperation.Create(typeof(DeviceContract.Device), "GetServices");
        var request = operation.ReadRequest(XElement.Parse($"""<GetServices xmlns="{Device}"><IncludeCapability>true</IncludeCapability></GetServices>"""));
        Assert.True(Assert.IsType<DeviceContract.GetServicesRequest>(request).IncludeCapability);
    }
}
