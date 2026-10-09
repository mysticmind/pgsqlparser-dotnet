using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class ErrorCursorTests
{
    [Theory]
    [InlineData("SELECT FROM WHERE", "WHERE")]
    // 'û' is 2 UTF-8 bytes, '😀' is 4 bytes and 2 chars: neither bytes nor code points index the string.
    [InlineData("SELECT 'Coût', '😀' FROM WHERE", "WHERE")]
    [InlineData("SELECT '😀😀😀' AS \"é\" FROM t WHERE ORDER", "ORDER")]
    [InlineData("SELECT 1;\nSELECT '😀' FROM WHERE", "WHERE")]
    public void CursorCharOffsetPointsAtTheOffendingToken(string query, string token)
    {
        var error = Parser.Parse(query).Error.ShouldNotBeNull();

        var charOffset = error.GetCursorCharOffset(query);

        charOffset.ShouldBe(query.LastIndexOf(token, StringComparison.Ordinal));
        query[charOffset..].ShouldStartWith(token);
    }

    [Fact]
    public void CursorCharOffsetAtEndOfInput()
    {
        const string query = "SELECT '😀' FROM";

        var error = Parser.Parse(query).Error.ShouldNotBeNull();

        error.Message.ShouldBe("syntax error at end of input");
        error.GetCursorCharOffset(query).ShouldBe(query.Length);
    }

    [Fact]
    public void CursorCharOffsetForUnterminatedString()
    {
        const string query = "SELECT '😀', 'é😀";

        var error = Parser.Scan(query).Error.ShouldNotBeNull();

        query[error.GetCursorCharOffset(query)..].ShouldBe("'é😀");
    }

    [Fact]
    public void CursorCharOffsetForNulCharacter()
    {
        const string query = "SELECT '😀'\0 FROM";

        var error = Parser.Parse(query).Error.ShouldNotBeNull();

        error.GetCursorCharOffset(query).ShouldBe(query.IndexOf('\0'));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    // Past the end: "SELECT 1" has 8 code points, so 9 is the end of input and 10 is outside.
    [InlineData(10)]
    public void CursorCharOffsetIsMinusOneWithoutAPositionInTheQuery(int cursorPos)
    {
        var error = new Error("message", null, null, 0, cursorPos, null);

        error.GetCursorCharOffset("SELECT 1").ShouldBe(-1);
    }

    [Fact]
    public void CursorCharOffsetRejectsNullQuery()
    {
        var error = new Error("message", null, null, 0, 1, null);

        Should.Throw<ArgumentNullException>(() => error.GetCursorCharOffset(null!));
    }
}
