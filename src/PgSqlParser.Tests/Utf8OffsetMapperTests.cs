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

        // The second statement starts at byte 16, which is UTF-16 offset 15.
        var second = statements[1];
        second.StmtLocation.ShouldBe(16);
        var start = mapper.ToCharOffset(second.StmtLocation);
        var end = mapper.ToCharOffset(second.StmtLocation + second.StmtLen);

        query.Substring(start, end - start).Trim().ShouldBe("SELECT 2");
        split[1].Location.ShouldBe(start);
    }

    [Fact]
    public void MapsAsciiCharOffsetsUnchanged()
    {
        var mapper = new Utf8OffsetMapper("SELECT 1");

        mapper.ToByteOffset(0).ShouldBe(0);
        mapper.ToByteOffset(7).ShouldBe(7);
        mapper.ToByteOffset(8).ShouldBe(8);
    }

    [Fact]
    public void MapsCharOffsetsAfterMultiByteAndSurrogatePairChars()
    {
        // '😀' is 4 UTF-8 bytes and 2 UTF-16 chars, 'û' is 2 UTF-8 bytes and 1 UTF-16 char.
        var mapper = new Utf8OffsetMapper("'😀' 'Coût' x");

        mapper.ToByteOffset(0).ShouldBe(0);
        mapper.ToByteOffset(4).ShouldBe(6);
        mapper.ToByteOffset(5).ShouldBe(7);
        mapper.ToByteOffset(11).ShouldBe(14);
        mapper.ToByteOffset(13).ShouldBe(16);
    }

    [Fact]
    public void MapsDescendingAndInterleavedOffsets()
    {
        var mapper = new Utf8OffsetMapper("'😀' 'Coût' x");

        // Both directions share one incremental position, so mix them in every order.
        mapper.ToByteOffset(13).ShouldBe(16);
        mapper.ToCharOffset(6).ShouldBe(4);
        mapper.ToByteOffset(11).ShouldBe(14);
        mapper.ToCharOffset(16).ShouldBe(13);
        mapper.ToByteOffset(4).ShouldBe(6);
    }

    [Fact]
    public void ByteAndCharOffsetsRoundTripAtEveryBoundary()
    {
        const string query = "SELECT '😀é', \"Coût😀\" FROM \"données\" -- fin 😀";
        var mapper = new Utf8OffsetMapper(query);

        for (var charOffset = 0; charOffset <= query.Length; charOffset++)
        {
            var insidePair = charOffset < query.Length && char.IsLowSurrogate(query[charOffset]);
            mapper.TryToByteOffset(charOffset, out var byteOffset).ShouldBe(!insidePair);
            if (insidePair)
                continue;

            byteOffset.ShouldBe(System.Text.Encoding.UTF8.GetByteCount(query[..charOffset]));
            mapper.ToCharOffset(byteOffset).ShouldBe(charOffset);
        }
    }

    [Theory]
    [InlineData("SELECT 1", -1)]
    [InlineData("SELECT 1", 9)]
    [InlineData("'😀'", -1)]
    [InlineData("'😀'", 5)]
    // Char 2 is the second half of the '😀' surrogate pair.
    [InlineData("'😀'", 2)]
    public void RejectsCharOffsetsThatDoNotMapToTheQuery(string query, int charOffset)
    {
        var mapper = new Utf8OffsetMapper(query);

        mapper.TryToByteOffset(charOffset, out _).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => mapper.ToByteOffset(charOffset));
    }

    [Fact]
    public void FindsParseTreeNodeAtAStringPosition()
    {
        const string query = "SELECT '😀' AS \"é\" FROM \"données\" WHERE id = 1";
        var table = Parser.Parse(query).Value!.Stmts[0].Stmt.SelectStmt.FromClause[0].RangeVar;

        // A position in the string, such as an editor caret, compared against a parse tree location.
        var mapper = new Utf8OffsetMapper(query);
        var caret = query.IndexOf("\"données\"", StringComparison.Ordinal);

        mapper.ToByteOffset(caret).ShouldBe(table.Location);
    }
}
