using PgSqlParser.Utils;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class Utf8OffsetMapperTests
{
    [Fact]
    public void MapsAsciiOffsetsUnchanged()
    {
        var mapper = new Utf8OffsetMapper("SELECT 1");

        mapper.ToCharOffset(0).ShouldBe(0);
        mapper.ToCharOffset(7).ShouldBe(7);
        mapper.ToCharOffset(8).ShouldBe(8);
    }

    [Fact]
    public void MapsOffsetsAfterMultiByteAndSurrogatePairChars()
    {
        // '😀' is 4 UTF-8 bytes and 2 UTF-16 chars, 'û' is 2 UTF-8 bytes and 1 UTF-16 char.
        var mapper = new Utf8OffsetMapper("'😀' 'Coût' x");

        mapper.ToCharOffset(0).ShouldBe(0);
        mapper.ToCharOffset(6).ShouldBe(4);
        mapper.ToCharOffset(7).ShouldBe(5);
        mapper.ToCharOffset(14).ShouldBe(11);
        mapper.ToCharOffset(16).ShouldBe(13);
    }

    [Fact]
    public void MapsDescendingOffsets()
    {
        var mapper = new Utf8OffsetMapper("'😀' 'Coût' x");

        mapper.ToCharOffset(16).ShouldBe(13);
        mapper.ToCharOffset(6).ShouldBe(4);
        mapper.ToCharOffset(14).ShouldBe(11);
    }

    [Theory]
    [InlineData("SELECT 1", -1)]
    [InlineData("SELECT 1", 9)]
    [InlineData("'Coût'", -1)]
    [InlineData("'Coût'", 8)]
    // Byte 4 is the second byte of 'û'.
    [InlineData("'Coût'", 4)]
    public void RejectsOffsetsThatDoNotMapToTheQuery(string query, int byteOffset)
    {
        var mapper = new Utf8OffsetMapper(query);

        mapper.TryToCharOffset(byteOffset, out _).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => mapper.ToCharOffset(byteOffset));
    }

    [Fact]
    public void ConvertsParseTreeLocationsToMatchScanTokens()
    {
        // The last statement of a parse tree has StmtLen 0, so a third one keeps the second one's length set.
        const string query = "SELECT 'Coût'; SELECT 2; SELECT 3";

        var statements = Parser.Parse(query).Value!.Stmts;
        var split = Parser.SplitWithParser(query).Value!.Statements;
        var mapper = new Utf8OffsetMapper(query);

        // The second statement starts at byte 15, which is UTF-16 offset 14.
        var second = statements[1];
        second.StmtLocation.ShouldBe(15);
        var start = mapper.ToCharOffset(second.StmtLocation);
        var end = mapper.ToCharOffset(second.StmtLocation + second.StmtLen);

        query.Substring(start, end - start).Trim().ShouldBe("SELECT 2");
        split[1].Location.ShouldBe(start);
    }
}
