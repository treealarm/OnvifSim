namespace OnvifSim.Rtsp.Rtp;

// RTP payload of the ONVIF metadata stream (ONVIF Streaming spec 5.1.2.1, "vnd.onvif.metadata"): the
// XML document as is, split across packets when it is longer than one; the last carries the marker bit.
public static class MetadataPacketizer
{
    // Dynamic, as cameras use (the lab camera's is 107 too).
    public const byte PayloadType = 107;
    public const string EncodingName = "vnd.onvif.metadata";

    public static void Packetize(RtpStream stream, byte[] document, long captureTimestamp, RtpPacketBatch batch)
    {
        uint timestamp = stream.TimestampOf(captureTimestamp);
        int maxPayload = stream.MaxPayloadSize;
        int offset = 0;
        do
        {
            int chunk = Math.Min(document.Length - offset, maxPayload);
            bool last = offset + chunk == document.Length;
            document.AsSpan(offset, chunk).CopyTo(stream.AppendPacket(batch, chunk, last, timestamp));
            offset += chunk;
        }
        while (offset < document.Length);
    }
}
