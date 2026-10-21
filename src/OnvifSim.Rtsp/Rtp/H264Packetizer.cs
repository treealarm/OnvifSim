namespace OnvifSim.Rtsp.Rtp;

// RTP payload format for H.264 (RFC 6184, packetization-mode=1): a NAL unit that fits goes in a packet of
// its own, a larger one is split into FU-A fragments. SPS and PPS travel in-band before every key frame,
// so no aggregation packets are needed.
public static class H264Packetizer
{
    // Dynamic payload type, announced in the SDP.
    public const byte PayloadType = 96;

    private const int FuA = 28;
    private const int FuHeadersSize = 2;

    // Appends the packets of one access unit (its NAL units without start codes, captured at a Stopwatch
    // timestamp); the last packet of the access unit carries the marker bit.
    public static void Packetize(RtpStream stream, IReadOnlyList<ReadOnlyMemory<byte>> nals, long captureTimestamp, RtpPacketBatch batch)
    {
        uint timestamp = stream.TimestampOf(captureTimestamp);
        int maxPayload = stream.MaxPayloadSize;
        for (int n = 0; n < nals.Count; n++)
        {
            var nal = nals[n].Span;
            bool lastNal = n == nals.Count - 1;
            if (nal.Length <= maxPayload)
            {
                nal.CopyTo(stream.AppendPacket(batch, nal.Length, lastNal, timestamp));
                continue;
            }

            // The NAL header is not sent as such: its F/NRI bits go into the FU indicator, its type into
            // the FU header.
            byte indicator = (byte)((nal[0] & 0xE0) | FuA);
            byte type = (byte)(nal[0] & 0x1F);
            var body = nal[1..];
            for (int offset = 0; offset < body.Length;)
            {
                int chunk = Math.Min(body.Length - offset, maxPayload - FuHeadersSize);
                bool first = offset == 0, last = offset + chunk == body.Length;
                var payload = stream.AppendPacket(batch, FuHeadersSize + chunk, lastNal && last, timestamp);
                payload[0] = indicator;
                payload[1] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | type);
                body.Slice(offset, chunk).CopyTo(payload[FuHeadersSize..]);
                offset += chunk;
            }
        }
    }

    // The SDP fmtp of a stream: profile and level from the SPS, and both parameter sets for clients that
    // want them before the first key frame.
    public static string FormatParameters(byte[] sps, byte[] pps) =>
        $"packetization-mode=1;profile-level-id={Convert.ToHexString(sps, 1, 3)}" +
        $";sprop-parameter-sets={Convert.ToBase64String(sps)},{Convert.ToBase64String(pps)}";

    // The SDP fmtp before the parameter sets are known: the profile and level the encoder is asked for.
    public static string FormatParameters(string profileLevelId) =>
        $"packetization-mode=1;profile-level-id={profileLevelId}";
}
