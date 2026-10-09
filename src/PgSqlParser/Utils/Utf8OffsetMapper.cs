using System.Text;

namespace PgSqlParser.Utils;

/// <summary>
/// Maps UTF-8 byte offsets of a query, as reported by libpg_query, to UTF-16 offsets of that query.
/// Use it to convert parse tree locations, which <see cref="Parser.Parse"/> returns as UTF-8 byte offsets.
/// Ascending offsets are counted incrementally, so mapping all offsets of a query is linear.
/// Instances are not thread-safe.
/// </summary>
public sealed class Utf8OffsetMapper
{
    private const byte Utf8ContinuationByteMask = 0xC0;
    private const byte Utf8ContinuationBytePrefix = 0x80;

    // Null for an all-ASCII query, where byte offsets and UTF-16 offsets are the same.
    private readonly byte[]? _utf8Bytes;
    private readonly int _byteLength;
    private int _lastByte;
    private int _lastChar;

    public Utf8OffsetMapper(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Every non-ASCII UTF-16 char encodes to at least two UTF-8 bytes, so equal lengths mean all ASCII.
        _byteLength = Encoding.UTF8.GetByteCount(query);
        if (_byteLength != query.Length)
            _utf8Bytes = Encoding.UTF8.GetBytes(query);
    }

    /// <summary>
    /// Maps <paramref name="byteOffset"/> to a UTF-16 offset. Returns false if it is outside the query
    /// or does not start a UTF-8 sequence.
    /// </summary>
    public bool TryToCharOffset(int byteOffset, out int charOffset)
    {
        charOffset = 0;
        if (byteOffset < 0 || byteOffset > _byteLength)
            return false;

        if (_utf8Bytes is null)
        {
            charOffset = byteOffset;
            return true;
        }

        if (!IsCharBoundary(_utf8Bytes, byteOffset))
            return false;

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

    /// <summary>
    /// Maps <paramref name="byteOffset"/> to a UTF-16 offset.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="byteOffset"/> is outside the query or does not start a UTF-8 sequence.
    /// </exception>
    public int ToCharOffset(int byteOffset)
    {
        if (!TryToCharOffset(byteOffset, out var charOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), byteOffset,
                $"UTF-8 byte offset is outside the query ({_byteLength} bytes) or does not start a UTF-8 sequence.");
        }

        return charOffset;
    }

    private static bool IsCharBoundary(byte[] utf8Bytes, int byteOffset) =>
        byteOffset == utf8Bytes.Length
        || (utf8Bytes[byteOffset] & Utf8ContinuationByteMask) != Utf8ContinuationBytePrefix;
}
