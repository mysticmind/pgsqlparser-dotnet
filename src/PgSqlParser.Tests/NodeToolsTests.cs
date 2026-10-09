using Google.Protobuf;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class NodeToolsTests
{
    private const string Query =
        "WITH r AS (SELECT 1) SELECT c.name AS n, count(*) FROM customers c JOIN orders o ON o.cid = c.id, " +
        "(SELECT 2) s WHERE c.a = $1 AND c.b IN (SELECT id FROM vip) AND c.t::date > now() " +
        "ORDER BY n DESC NULLS LAST LIMIT 5";

    private static ParseResult Parse(string query) => Parser.Parse(query).GetValueOrThrow();

    private static SelectStmt Select(string query) => Parse(query).Stmts[0].Stmt.SelectStmt;

    [Fact]
    public void DeparseNodeForClausesOfAQuery()
    {
        var select = Select(Query);

        select.WhereClause.Deparse().GetValueOrThrow()
            .ShouldBe("c.a = $1 AND c.b IN (SELECT id FROM vip) AND c.t::date > now()");
        select.TargetList.Select(target => target.Deparse().GetValueOrThrow()).ShouldBe(["c.name AS n", "count(*)"]);
        select.FromClause.Select(item => item.Deparse().GetValueOrThrow())
            .ShouldBe(["customers c JOIN orders o ON o.cid = c.id", "(SELECT 2) s"]);
        select.SortClause[0].Deparse().GetValueOrThrow().ShouldBe("n DESC NULLS LAST");
        select.LimitCount.Deparse().GetValueOrThrow().ShouldBe("5");
        select.WithClause.Deparse().GetValueOrThrow().ShouldBe("WITH r AS (SELECT 1)");
        select.WithClause.Ctes[0].Deparse().GetValueOrThrow().ShouldBe("r AS (SELECT 1)");
    }

    [Fact]
    public void DeparseNodeForNodesFoundByTheWalker()
    {
        var tree = Parse(Query);

        tree.Descendants<RangeVar>().Select(table => table.Deparse().GetValueOrThrow())
            .ShouldBe(["customers c", "orders o", "vip"]);
        tree.Descendants<SubLink>().Single().Deparse().GetValueOrThrow().ShouldBe("c.b IN (SELECT id FROM vip)");
        tree.Descendants<TypeCast>().Single().Deparse().GetValueOrThrow().ShouldBe("c.t::date");
        tree.Descendants<TypeName>().Single().Deparse().GetValueOrThrow().ShouldBe("date");
        tree.Descendants<ParamRef>().Single().Deparse().GetValueOrThrow().ShouldBe("$1");
        tree.Descendants<SelectStmt>().Select(select => select.Deparse().GetValueOrThrow()).Skip(1)
            .ShouldBe(["SELECT 2", "SELECT id FROM vip", "SELECT 1"]);
    }

    [Fact]
    public void DeparseNodeForStatementsAndWrappers()
    {
        const string query = "SELECT 1; INSERT INTO t (a) VALUES (1); DROP TABLE t";
        var tree = Parse(query);

        // A whole tree, a RawStmt, a Node wrapper and the statement inside it all work.
        Parser.DeparseNode(tree).GetValueOrThrow().ShouldBe(query);
        tree.Stmts[1].Deparse().GetValueOrThrow().ShouldBe("INSERT INTO t (a) VALUES (1)");
        tree.Stmts[1].Stmt.Deparse().GetValueOrThrow().ShouldBe("INSERT INTO t (a) VALUES (1)");
        tree.Stmts[1].Stmt.InsertStmt.Deparse().GetValueOrThrow().ShouldBe("INSERT INTO t (a) VALUES (1)");
        tree.Stmts[2].Stmt.DropStmt.Deparse().GetValueOrThrow().ShouldBe("DROP TABLE t");
    }

    [Fact]
    public void DeparseNodeKeepsNonAsciiTextAndDoesNotModifyTheTree()
    {
        var tree = Parse("SELECT 1 FROM \"données\" WHERE \"é\" = '😀' AND b = 2");
        var before = tree.Clone();

        tree.Stmts[0].Stmt.SelectStmt.WhereClause.Deparse().GetValueOrThrow().ShouldBe("\"é\" = '😀' AND b = 2");
        tree.Descendants<RangeVar>().Single().Deparse().GetValueOrThrow().ShouldBe("\"données\"");

        tree.ShouldBe(before);
    }

    [Fact]
    public void DeparseNodeReportsUnsupportedNodes()
    {
        var tree = Parse("SELECT c.name FROM customers c");

        var alias = tree.Descendants<Alias>().Single().Deparse();
        alias.IsSuccess.ShouldBeFalse();
        alias.Error.Message.ShouldBe("a Alias cannot be deparsed on its own");
        tree.Descendants<String>().First().Deparse().IsSuccess.ShouldBeFalse();
        new Node().Deparse().Error.ShouldNotBeNull().Message.ShouldBe("an empty Node cannot be deparsed");
        Should.Throw<ArgumentNullException>(() => Parser.DeparseNode(null!));
    }

    [Fact]
    public void WalkReportsTheFieldAndIndexOfEachNode()
    {
        var tree = Parse("SELECT a, b FROM t WHERE x = 1");

        var visits = tree.Walk().ToList();

        visits[0].FieldName.ShouldBe(nameof(ParseResult.Stmts));
        visits[0].Index.ShouldBe(0);
        // A Node wrapper is transparent, so the statement reports the field that held the wrapper.
        visits[1].FieldName.ShouldBe(nameof(RawStmt.Stmt));
        visits[1].Index.ShouldBeNull();

        var targets = visits.Where(visit => visit.Node is ResTarget).ToList();
        targets.Select(visit => visit.FieldName).Distinct().ShouldBe([nameof(SelectStmt.TargetList)]);
        targets.Select(visit => visit.Index).ShouldBe([0, 1]);

        visits.Single(visit => visit.FieldName == nameof(SelectStmt.WhereClause)).Node.ShouldBeOfType<A_Expr>();
        visits.Single(visit => visit.FieldName == nameof(SelectStmt.FromClause)).Node.ShouldBeOfType<RangeVar>();
    }

    [Fact]
    public void WalkCanSkipChildren()
    {
        var tree = Parse("SELECT a FROM t WHERE x IN (SELECT y FROM inner_table) AND z = 1");
        var tables = new List<string>();

        // Collect tables, but do not look inside subqueries in expressions.
        tree.Walk(visit =>
        {
            if (visit.Node is SubLink)
                return WalkAction.SkipChildren;

            if (visit.Node is RangeVar table)
                tables.Add(table.Relname);

            return WalkAction.Continue;
        });

        tables.ShouldBe(["t"]);
        tree.Descendants<RangeVar>().Select(table => table.Relname).ShouldBe(["t", "inner_table"]);
    }

    [Fact]
    public void WalkCanStop()
    {
        var tree = Parse("SELECT a FROM t1, t2, t3");
        var visited = 0;
        RangeVar? first = null;

        tree.Walk(visit =>
        {
            visited++;
            if (visit.Node is not RangeVar table)
                return WalkAction.Continue;

            first = table;
            return WalkAction.Stop;
        });

        first.ShouldNotBeNull().Relname.ShouldBe("t1");
        visited.ShouldBeLessThan(tree.Walk().Count());
    }

    [Fact]
    public void WalkWithAVisitorMatchesTheEnumerableOrder()
    {
        var tree = Parse(Query);
        var seen = new List<NodeVisit>();

        tree.Walk(visit =>
        {
            seen.Add(visit);
            return WalkAction.Continue;
        });

        seen.ShouldBe(tree.Walk().ToList());
        Should.Throw<ArgumentNullException>(() => tree.Walk((Func<NodeVisit, WalkAction>)null!));
        Should.Throw<ArgumentNullException>(() => ((IMessage)null!).Walk(_ => WalkAction.Continue));
    }

    [Fact]
    public void VisitsKnowTheirAncestors()
    {
        var tree = Parse("WITH r AS (SELECT a FROM inner_table) SELECT b FROM outer_table WHERE c IN (SELECT d FROM sub_table)");

        var tables = tree.Walk().Where(visit => visit.Node is RangeVar)
            .ToDictionary(visit => ((RangeVar)visit.Node).Relname);

        // "Is this table inside a CTE?" and "inside a subquery in an expression?"
        tables["inner_table"].FindAncestor<CommonTableExpr>().ShouldNotBeNull().Ctename.ShouldBe("r");
        tables["outer_table"].FindAncestor<CommonTableExpr>().ShouldBeNull();
        tables["sub_table"].FindAncestor<SubLink>().ShouldNotBeNull();
        tables["outer_table"].FindAncestor<SubLink>().ShouldBeNull();

        var ancestors = tables["inner_table"].Ancestors.ToList();
        ancestors[0].ShouldBeSameAs(tables["inner_table"].Parent);
        ancestors.Select(node => node.Descriptor.Name)
            .ShouldBe(["SelectStmt", "CommonTableExpr", "WithClause", "SelectStmt", "RawStmt"]);
        // The nearest SelectStmt is the CTE's own query, not the outer one.
        tables["inner_table"].FindAncestor<SelectStmt>().ShouldBeSameAs(ancestors[0]);

        tree.Walk().First().Ancestors.ShouldBeEmpty();
        tree.Walk().First().FindAncestor<RawStmt>().ShouldBeNull();
    }

    [Fact]
    public void VisitsKnowTheirStatement()
    {
        var tree = Parse("SELECT a FROM t1; UPDATE t2 SET x = 1 WHERE y IN (SELECT z FROM t3); DROP TABLE t4");

        var tablesByStatement = tree.Walk()
            .Where(visit => visit.Node is RangeVar)
            .Select(visit => (((RangeVar)visit.Node).Relname, visit.StatementIndex, Kind: visit.Statement!.Stmt.Unwrap()!.Descriptor.Name));

        tablesByStatement.ShouldBe([("t1", 0, "SelectStmt"), ("t2", 1, "UpdateStmt"), ("t3", 1, "UpdateStmt")]);

        var statements = tree.Walk().Where(visit => visit.Node is RawStmt).ToList();
        statements.Select(visit => visit.StatementIndex).ShouldBe([0, 1, 2]);
        statements[2].Statement.ShouldBeSameAs(tree.Stmts[2]);

        // A walk that does not start from the whole tree has no statement to report.
        tree.Stmts[1].Walk().ShouldAllBe(visit => visit.Statement == null && visit.StatementIndex == null);
    }

    [Fact]
    public void VisitorFormAlsoReportsAncestorsAndStatement()
    {
        var tree = Parse("SELECT 1; SELECT a FROM t WHERE b IN (SELECT c FROM u)");
        var insideSubquery = new List<string>();

        tree.Walk(visit =>
        {
            if (visit.Node is RangeVar table && visit.FindAncestor<SubLink>() is not null && visit.StatementIndex == 1)
                insideSubquery.Add(table.Relname);

            return WalkAction.Continue;
        });

        insideSubquery.ShouldBe(["u"]);
    }

    [Fact]
    public void ParameterRefsInOrderWithOffsets()
    {
        const string query = "SELECT * FROM t WHERE a = $1 AND b = $2 OR c = $1 AND d > $10";

        var parameters = Parser.ParameterRefs(query).GetValueOrThrow();

        parameters.Select(parameter => parameter.Number).ShouldBe([1, 2, 1, 10]);
        parameters.Select(parameter => query[parameter.Start..parameter.End]).ShouldBe(["$1", "$2", "$1", "$10"]);
    }

    [Fact]
    public void ParameterRefsIgnoreLiteralsAndCommentsAndUseStringOffsets()
    {
        const string query = "SELECT '😀 $1', $$ $2 $$ /* $3 */ FROM \"données$4\" WHERE é = $5 -- $6";

        var parameter = Parser.ParameterRefs(query).GetValueOrThrow().ShouldHaveSingleItem();

        parameter.Number.ShouldBe(5);
        query[parameter.Start..parameter.End].ShouldBe("$5");
    }

    [Fact]
    public void ParameterRefsReportCastTypes()
    {
        const string query = "SELECT $1::int, CAST($2 AS numeric(10,2)), $3, '😀', $4::text[], $1 FROM t WHERE é = $5::timestamptz";

        var parameters = Parser.ParameterRefs(query).GetValueOrThrow();

        parameters.Select(parameter => (parameter.Number, parameter.TypeName)).ShouldBe(
        [
            (1, "int"), (2, "numeric(10, 2)"), (3, null), (4, "text[]"), (1, null), (5, "timestamptz")
        ]);
        parameters.Select(parameter => query[parameter.Start..parameter.End]).ShouldBe(["$1", "$2", "$3", "$4", "$1", "$5"]);
    }

    [Fact]
    public void ParameterRefsWithoutTypesWhenTheQueryDoesNotParse()
    {
        // Scans, but is not valid SQL.
        var parameters = Parser.ParameterRefs("SELECT $1::int FROM WHERE $2").GetValueOrThrow();

        parameters.Select(parameter => (parameter.Number, parameter.TypeName)).ShouldBe([(1, null), (2, null)]);
    }

    [Fact]
    public async Task ParameterRefsEdgeCases()
    {
        Parser.ParameterRefs("SELECT 1").GetValueOrThrow().ShouldBeEmpty();
        Parser.ParameterRefs("SELECT 'unterminated").IsSuccess.ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => Parser.ParameterRefs(null!));
        (await Parser.ParameterRefsAsync("SELECT $1")).GetValueOrThrow().ShouldBe([new ParameterRef(1, 7, 9)]);
    }
}
