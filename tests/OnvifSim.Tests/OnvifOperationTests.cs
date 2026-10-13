using System.Text;
using System.Xml;
using System.Xml.Linq;
using OnvifSim.Onvif.Soap;

namespace OnvifSim.Tests;

public class OnvifOperationTests
{
    private static XElement Write(OnvifOperation operation, object? result)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("Body");
            operation.WriteResult(writer, result);
            writer.WriteEndElement();
        }
        return XElement.Parse(text.ToString()).Elements().Single();
    }

    private static XNamespace Device => "http://www.onvif.org/ver10/device/wsdl";
    private static XNamespace Media => "http://www.onvif.org/ver10/media/wsdl";
    private static XNamespace Media2 => "http://www.onvif.org/ver20/media/wsdl";
    private static XNamespace Schema => "http://www.onvif.org/ver10/schema";

    [Fact]
    public void MessageContractResponse_IsWrapped_WithPartsInTheServiceNamespace()
    {
        var operation = OnvifOperation.Create(typeof(DeviceServiceReference.Device), "GetDeviceInformation");
        var xml = Write(operation, new DeviceServiceReference.GetDeviceInformationResponse
        {
            Manufacturer = "OnvifSim", Model = "Fixed", FirmwareVersion = "1", SerialNumber = "cam1", HardwareId = "sim",
        });
        Assert.Equal(Device + "GetDeviceInformationResponse", xml.Name);
        Assert.Equal("OnvifSim", xml.Element(Device + "Manufacturer")?.Value);
        Assert.Equal("cam1", xml.Element(Device + "SerialNumber")?.Value);
    }

    [Fact]
    public void UnwrappedResponse_IsNamedAfterTheOperation_AndItsReturnParameter()
    {
        var operation = OnvifOperation.Create(typeof(DeviceServiceReference.Device), "GetSystemDateAndTime");
        var xml = Write(operation, new DeviceServiceReference.SystemDateTime
        {
            DateTimeType = DeviceServiceReference.SetDateTimeType.NTP,
            UTCDateTime = new DeviceServiceReference.DateTime
            {
                Date = new DeviceServiceReference.Date { Year = 2026, Month = 10, Day = 13 },
                Time = new DeviceServiceReference.Time { Hour = 12, Minute = 0, Second = 1 },
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
        var operation = OnvifOperation.Create(typeof(MediaServiceReference.Media2), "GetProfiles");
        var xml = Write(operation, new MediaServiceReference.GetProfilesResponse
        {
            Profiles = [new() { token = "Profile_1", Name = "Main", @fixed = true }, new() { token = "Profile_2", Name = "Sub" }],
        });
        var profiles = xml.Elements(Media2 + "Profiles").ToList();
        Assert.Equal(["Profile_1", "Profile_2"], profiles.Select(p => p.Attribute("token")?.Value));
        // Media2 defines MediaProfile in its own namespace, unlike Media1's tt:Profile.
        Assert.Equal("Main", profiles[0].Element(Media2 + "Name")?.Value);
    }

    [Fact]
    public void UnwrappedRequest_ReadsEachParameterByName()
    {
        var operation = OnvifOperation.Create(typeof(MediaServiceReference1.Media), "GetStreamUri");
        var body = XElement.Parse($"""
            <GetStreamUri xmlns="{Media}" xmlns:tt="{Schema}">
              <StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></StreamSetup>
              <ProfileToken>Profile_1</ProfileToken>
            </GetStreamUri>
            """);
        var args = operation.ReadArguments(body);
        var setup = Assert.IsType<MediaServiceReference1.StreamSetup>(args[0]);
        Assert.Equal(MediaServiceReference1.StreamType.RTPUnicast, setup.Stream);
        Assert.Equal(MediaServiceReference1.TransportProtocol.RTSP, setup.Transport.Protocol);
        Assert.Equal("Profile_1", args[1]);
    }

    [Fact]
    public void MessageContractRequest_IsReadIntoItsClass()
    {
        var operation = OnvifOperation.Create(typeof(DeviceServiceReference.Device), "GetServices");
        var args = operation.ReadArguments(XElement.Parse($"""<GetServices xmlns="{Device}"><IncludeCapability>true</IncludeCapability></GetServices>"""));
        Assert.True(Assert.IsType<DeviceServiceReference.GetServicesRequest>(args[0]).IncludeCapability);
    }
}
