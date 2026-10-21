namespace OnvifSim.Rtsp.Rtp;

// What the ONVIF replay header extension says about one frame (ONVIF Streaming Specification, 6.3):
// when it was recorded, whether a decoder can start at it (CleanPoint), whether it follows a gap in the
// recording (Discontinuity), whether a gap or the end of the requested range follows it (EndOfSection,
// Terminal), and the low byte of the CSeq of the PLAY that asked for it.
public readonly record struct ReplayHeader(
    DateTimeOffset Time, bool CleanPoint, bool Discontinuity, bool EndOfSection, bool Terminal, byte CSeq);
