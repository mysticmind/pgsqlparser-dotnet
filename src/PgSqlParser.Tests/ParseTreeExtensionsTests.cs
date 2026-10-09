using Google.Protobuf;
using PgSqlParser.Utils;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class ParseTreeExtensionsTests
{
    private const string Query =
        "WITH recent AS (SELECT * FROM orders WHERE placed > now()) " +
        "SELECT c.name FROM customers c JOIN recent r ON r.customer_id = c.id " +
        "WHERE c.id IN (SELECT customer_id FROM vip)";

    private static ParseResult Parse(string query) => Parser.Parse(query).GetValueOrThrow();

    [Fact]
    public void DescendantsFindsEveryNodeOfAType()
    {
        var tree = Parse(Query);

        // Field order, not text order: a SELECT's FROM and WHERE come before its WITH clause.
        tree.Descendants<RangeVar>().Select(table => table.Relname)
            .ShouldBe(["customers", "recent", "vip", "orders"]);
        tree.Descendants<SelectStmt>().Count().ShouldBe(3);
        tree.Descendants<FuncCall>().Single().Funcname[0].String.Sval.ShouldBe("now");
        tree.Descendants<InsertStmt>().ShouldBeEmpty();
    }

    [Fact]
    public void DescendantsStartsBelowTheGivenNode()
    {
        var select = Parse(Query).Stmts[0].Stmt.SelectStmt;

        select.Descendants<SelectStmt>().Count().ShouldBe(2);
        select.WithClause.Descendants<RangeVar>().Single().Relname.ShouldBe("orders");
    }

    [Fact]
    public void WalkSkipsNodeWrappersAndReportsParentAndDepth()
    {
        var tree = Parse("SELECT a FROM t");

        var visits = tree.Walk().ToList();

        visits.ShouldAllBe(visit => !(visit.Node is Node));
        visits[0].Node.ShouldBeOfType<RawStmt>();
        visits[0].Parent.ShouldBeNull();
        visits[0].Depth.ShouldBe(1);
        visits[1].Node.ShouldBeOfType<SelectStmt>();
        visits[1].Parent.ShouldBeSameAs(visits[0].Node);
        visits[1].Depth.ShouldBe(2);

        var table = visits.Single(visit => visit.Node is RangeVar);
        table.Parent.ShouldBeOfType<SelectStmt>();
        table.Depth.ShouldBe(3);

        // Parents come before their children, siblings in field order: target list before FROM.
        visits.FindIndex(visit => visit.Node is ResTarget)
            .ShouldBeLessThan(visits.FindIndex(visit => visit.Node is RangeVar));
        tree.Descendants().ShouldBe(visits.Select(visit => visit.Node));
    }

    [Fact]
    public void UnwrapReturnsTheConcreteNode()
    {
        var tree = Parse("SELECT 1; INSERT INTO t VALUES (1); DROP TABLE t");

        var kinds = tree.Stmts.Select(stmt => stmt.Stmt.Unwrap() switch
        {
            SelectStmt => "select",
            InsertStmt insert => $"insert into {insert.Relation.Relname}",
            _ => "other"
        });

        kinds.ShouldBe(["select", "insert into t", "other"]);
        new Node().Unwrap().ShouldBeNull();
    }

    [Fact]
    public void WalkStartingFromANodeWrapper()
    {
        var wrapper = Parse("SELECT a FROM t").Stmts[0].Stmt;

        wrapper.Descendants<RangeVar>().Single().Relname.ShouldBe("t");
        wrapper.Walk().First().Node.ShouldBeOfType<ResTarget>();
    }

    [Fact]
    public void WalkHandlesDeeplyNestedTrees()
    {
        // Deep enough to matter, and still within what Parse can read (see the nesting limit).
        const int levels = 20;
        var query = string.Concat(Enumerable.Repeat("SELECT * FROM (", levels)) + "SELECT 1"
                    + string.Concat(Enumerable.Range(0, levels).Select(i => $") s{i}"));

        var tree = Parse(query);

        tree.Descendants<SelectStmt>().Count().ShouldBe(levels + 1);
        tree.Walk().Max(visit => visit.Depth).ShouldBeGreaterThan(levels);
    }

    [Fact]
    public void GetLocationIsAByteOffset()
    {
        const string query = "SELECT '😀' FROM \"données\" WHERE \"é\" = 1";
        var tree = Parse(query);
        var mapper = new Utf8OffsetMapper(query);

        var table = tree.Descendants<RangeVar>().Single();
        var location = table.GetLocation().ShouldNotBeNull();
        location.ShouldBe(table.Location);
        mapper.ToCharOffset(location).ShouldBe(query.IndexOf("\"données\"", StringComparison.Ordinal));

        var column = tree.Descendants<ColumnRef>().Single();
        mapper.ToCharOffset(column.GetLocation().ShouldNotBeNull())
            .ShouldBe(query.IndexOf("\"é\"", StringComparison.Ordinal));
    }

    [Fact]
    public void GetLocationForWrappersStatementsAndNodesWithoutOne()
    {
        var tree = Parse("SELECT 1; SELECT a FROM t");

        tree.Stmts[1].GetLocation().ShouldBe(10);
        // A Node wrapper reports the location of the node it wraps.
        tree.Stmts[1].Stmt.SelectStmt.FromClause[0].GetLocation().ShouldBe(24);
        // SelectStmt has no location field.
        tree.Stmts[1].Stmt.SelectStmt.GetLocation().ShouldBeNull();
        // PostgreSQL reports -1 for an unknown location.
        new RangeVar { Location = -1 }.GetLocation().ShouldBeNull();
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT '😀' AS \"é\";\n\n  SELECT 'Coût' FROM \"données\"; DROP TABLE t")]
    [InlineData("/* lead */ SELECT 1 /* trail */; -- next\nSELECT 2;")]
    public void GetTextMatchesSplitWithParser(string query)
    {
        var tree = Parse(query);
        var expected = Parser.SplitWithParser(query).GetValueOrThrow().Statements.Select(s => s.Text);
        var mapper = new Utf8OffsetMapper(query);

        tree.Stmts.Select(stmt => stmt.GetText(query)).ShouldBe(expected);
        tree.Stmts.Select(stmt => stmt.GetText(query, mapper)).ShouldBe(expected);
    }

    [Fact]
    public void RejectsNullArguments()
    {
        Should.Throw<ArgumentNullException>(() => ((IMessage)null!).Walk());
        Should.Throw<ArgumentNullException>(() => ((Node)null!).Unwrap());
        Should.Throw<ArgumentNullException>(() => ((IMessage)null!).GetLocation());
        Should.Throw<ArgumentNullException>(() => new RawStmt().GetText(null!));
    }
}
