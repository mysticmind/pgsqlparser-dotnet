using System.Text;

namespace PgSqlParser.Utils;

/// <summary>
/// Maps UTF-8 byte offsets of a query, as reported by libpg_query, to UTF-16 offsets of that query.
/// Ascending offsets are counted incrementally, so mapping all offsets of a query is linear.
/// </summary>
internal sealed class Utf8OffsetMapper
{
    private const byte Utf8ContinuationByteMask = 0xC0;
    private const byte Utf8ContinuationBytePrefix = 0x80;

    private readonly byte[] _utf8Bytes;
    private readonly bool _isAscii;
    private int _lastByte;
    private int _lastChar;

    public Utf8OffsetMapper(string query)
    {
        _utf8Bytes = Encoding.UTF8.GetBytes(query);
        // Every non-ASCII UTF-16 char encodes to at least two UTF-8 bytes, so equal lengths mean all ASCII.
        _isAscii = _utf8Bytes.Length == query.Length;
    }

    /// <summary>
    /// Maps <paramref name="byteOffset"/> to a UTF-16 offset. Returns false if it is outside the query
    /// or does not start a UTF-8 sequence.
    /// </summary>
    public bool TryToCharOffset(int byteOffset, out int charOffset)
    {
        charOffset = 0;
        if (byteOffset < 0 || byteOffset > _utf8Bytes.Length || !IsCharBoundary(byteOffset))
            return false;

        if (_isAscii)
        {
            charOffset = byteOffset;
            return true;
        }

        if (byteOffset < _lastByte)
        {
            _lastByte = 0;
            _lastChar = 0;
        }

        _lastChar += Encoding.UTF8.GetCharCount(_utf8Bytes, _lastByte, byteOffset - _lastByte);
        _lastByte = byteOffset;
        charOffset = _lastChar;
        return true;
    }

    private bool IsCharBoundary(int byteOffset) =>
        byteOffset == _utf8Bytes.Length
        || (_utf8Bytes[byteOffset] & Utf8ContinuationByteMask) != Utf8ContinuationBytePrefix;
}
