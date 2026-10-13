namespace OnvifSim.Streaming.H264;

// One coded picture: its NAL units without start codes (and without the access unit delimiter), stamped
// with the render time of the frame it encodes.
public sealed record H264AccessUnit(IReadOnlyList<ReadOnlyMemory<byte>> Nals, long CaptureTimestamp)
{
    public bool IsKeyFrame => Nals.Any(n => AnnexB.NalType(n.Span) == AnnexB.IdrSlice);
}

// The H.264 Annex B byte stream that ffmpeg writes: NAL units, each behind a 00 00 01 or 00 00 00 01
// start code.
public static class AnnexB
{
    public const int IdrSlice = 5;
    public const int Sps = 7;
    public const int Pps = 8;
    public const int AccessUnitDelimiter = 9;

    public static int NalType(ReadOnlySpan<byte> nal) => nal[0] & 0x1F;

    // The NAL units of a stream, without their start codes. The zero byte of a four-byte start code
    // belongs to the code, not to the NAL before it.
    public static List<ReadOnlyMemory<byte>> SplitNals(ReadOnlyMemory<byte> stream)
    {
        var nals = new List<ReadOnlyMemory<byte>>();
        var span = stream.Span;
        int start = -1;
        for (int i = FindStartCode(span, 0); i >= 0; i = FindStartCode(span, start))
        {
            if (start >= 0) nals.Add(stream[start..TrimZero(span, start, i)]);
            start = i + 3;
        }
        if (start >= 0 && start < span.Length) nals.Add(stream[start..]);
        return nals;
    }

    // Index of the next 00 00 01 at or after from, or -1.
    public static int FindStartCode(ReadOnlySpan<byte> data, int from)
    {
        for (int i = from; i + 2 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) return i;
        return -1;
    }

    private static int TrimZero(ReadOnlySpan<byte> data, int nalStart, int startCode) =>
        startCode > nalStart && data[startCode - 1] == 0 ? startCode - 1 : startCode;
}

// Cuts ffmpeg's output into access units. x264 starts every access unit with a delimiter (aud=1), so one
// is complete when the next delimiter arrives; that costs one frame of latency and needs no parsing of
// the slices themselves.
public sealed class AccessUnitReader
{
    private byte[] _buffer = new byte[256 * 1024];
    private int _length;
    // Where the access unit being collected starts, and how far the search for the next one got.
    private int _unitStart = -1;
    private int _searchFrom;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_length + data.Length, _buffer.Length * 2));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    // The next complete access unit (Annex B, from its delimiter up to the next one), if there is one.
    public bool TryTake(out byte[] accessUnit)
    {
        var data = _buffer.AsSpan(0, _length);
        for (int i = AnnexB.FindStartCode(data, _searchFrom); i >= 0; i = AnnexB.FindStartCode(data, i + 3))
        {
            if (i + 3 >= _length)
            {
                // The NAL type is not here yet; look at this start code again next time.
                _searchFrom = i;
                break;
            }
            _searchFrom = i + 3;
            if (AnnexB.NalType(data[(i + 3)..]) != AnnexB.AccessUnitDelimiter) continue;
            int boundary = i > 0 && data[i - 1] == 0 ? i - 1 : i;
            if (_unitStart < 0)
            {
                _unitStart = boundary;
                continue;
            }
            accessUnit = data[_unitStart..boundary].ToArray();
            Discard(boundary);
            return true;
        }
        if (_unitStart < 0) _searchFrom = Math.Max(0, _length - 3);
        accessUnit = [];
        return false;
    }

    // Drops everything before offset, which becomes the start of the access unit being collected.
    private void Discard(int offset)
    {
        _buffer.AsSpan(offset, _length - offset).CopyTo(_buffer);
        _length -= offset;
        _searchFrom -= offset;
        _unitStart = 0;
    }
}
