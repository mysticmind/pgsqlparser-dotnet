using Google.Protobuf;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class TreeEditingTests
{
    private static ParseResult Parse(string query) => Parser.Parse(query).GetValueOrThrow();

    private static string Deparse(IMessage node) => node.Deparse().GetValueOrThrow();

    [Theory]
    [InlineData("SELECT a FROM t WHERE x = 1", "select  a\nfrom t -- comment\n where x=1")]
    [InlineData("CREATE INDEX i ON t (lower(name))", "CREATE INDEX i ON t ((lower(name)))")]
    [InlineData("SELECT * FROM t WHERE a <> 1", "SELECT * FROM t WHERE a != 1")]
    [InlineData("CREATE TABLE t (a varchar(10), b int)", "CREATE TABLE t (a character varying(10), b integer)")]
    [InlineData("SELECT 1; SELECT 2", "SELECT 1;\n\n   SELECT 2;")]
    [InlineData("SELECT 1 WHERE a IN (1, 2)", "SELECT 1 WHERE a  IN ( 1 ,2 )")]
    [InlineData("SELECT ARRAY[1, 2], ROW(1, 2)", "SELECT ARRAY[ 1,2 ],  ROW( 1,2 )")]
    public void EqualsIgnoringLocationsForTheSameSql(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);

        a.EqualsIgnoringLocations(b).ShouldBeTrue();
        b.EqualsIgnoringLocations(a).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE x = 1", "SELECT a FROM t WHERE x = 2")]
    [InlineData("SELECT a FROM t", "SELECT b FROM t")]
    [InlineData("SELECT a FROM t", "SELECT a FROM t WHERE true")]
    [InlineData("SELECT a, b FROM t", "SELECT a FROM t")]
    [InlineData("CREATE INDEX i ON t (a)", "CREATE UNIQUE INDEX i ON t (a)")]
    [InlineData("CREATE INDEX i ON t (a)", "CREATE INDEX i ON t (a DESC)")]
    [InlineData("SELECT 'a'", "SELECT 'A'")]
    [InlineData("SELECT 1", "SELECT 1; SELECT 1")]
    public void EqualsIgnoringLocationsSeesRealDifferences(string left, string right)
    {
        Parse(left).EqualsIgnoringLocations(Parse(right)).ShouldBeFalse();
    }

    [Fact]
    public void PlainEqualityIsSensitiveToLocations()
    {
        var a = Parse("SELECT a FROM t");
        var b = Parse("SELECT  a  FROM  t");

        a.Equals(b).ShouldBeFalse();
        a.EqualsIgnoringLocations(b).ShouldBeTrue();
    }

    [Fact]
    public void EqualsIgnoringLocationsOnNodesAndNulls()
    {
        var where1 = Parse("SELECT 1 FROM t WHERE a = 1 AND b = 'x'").Stmts[0].Stmt.SelectStmt.WhereClause;
        var where2 = Parse("DELETE FROM other WHERE (a = 1) AND (b = 'x')").Stmts[0].Stmt.DeleteStmt.WhereClause;

        where1.EqualsIgnoringLocations(where2).ShouldBeTrue();
        // A wrapper and what it wraps are different things to compare.
        where1.EqualsIgnoringLocations(where1.Unwrap()).ShouldBeFalse();
        where1.EqualsIgnoringLocations(null).ShouldBeFalse();
        ((IMessage?)null).EqualsIgnoringLocations(null).ShouldBeTrue();
    }

    [Fact]
    public void RewriteReplacesNodes()
    {
        var tree = Parse("SELECT a FROM old_table WHERE b = 1 AND c IN (SELECT d FROM old_table)");

        tree.Rewrite(visit => visit.Node is RangeVar { Relname: "old_table" }
            ? NodeEdit.ReplaceWith(new RangeVar { Relname = "new_table", Inh = true, Relpersistence = "p" })
            : NodeEdit.Keep);

        Deparse(tree).ShouldBe("SELECT a FROM new_table WHERE b = 1 AND c IN (SELECT d FROM new_table)");
    }

    [Fact]
    public void RewriteCanUnwrapANodeIntoItsChild()
    {
        // PostgreSQL adds casts like this when it stores an expression.
        var tree = Parse("SELECT lower(data ->> 'name'::text) FROM t WHERE kind = 'a'::text AND n = '1'::int");

        tree.Rewrite(visit => visit.Node is TypeCast { Arg: { } arg, TypeName: { } type }
                              && arg.Unwrap() is A_Const { Sval: not null }
                              && Deparse(type) == "text"
            ? NodeEdit.ReplaceWith(arg)
            : NodeEdit.Keep);

        Deparse(tree).ShouldBe("SELECT lower(data ->> 'name') FROM t WHERE kind = 'a' AND n = '1'::int");
    }

    [Fact]
    public void RewriteRemovesListItemsAndSingleProperties()
    {
        var tree = Parse("SELECT a, secret, b FROM t WHERE x = 1 ORDER BY a LIMIT 5");

        tree.Rewrite(visit => visit switch
        {
            { Node: ResTarget target } when Deparse(target) == "secret" => NodeEdit.Remove,
            { FieldName: nameof(SelectStmt.WhereClause) } => NodeEdit.Remove,
            { FieldName: nameof(SelectStmt.LimitCount) } => NodeEdit.Remove,
            _ => NodeEdit.Keep
        });

        Deparse(tree).ShouldBe("SELECT a, b FROM t ORDER BY a");
    }

    [Fact]
    public void RewriteVisitsChildrenBeforeParents()
    {
        var tree = Parse("SELECT f(g(1))");
        var order = new List<string>();

        tree.Rewrite(visit =>
        {
            if (visit.Node is FuncCall call)
                order.Add(call.Funcname[0].String.Sval);

            return NodeEdit.Keep;
        });

        order.ShouldBe(["g", "f"]);
    }

    [Fact]
    public void RewriteGivesTheSameContextAsWalk()
    {
        var tree = Parse("SELECT 1; WITH r AS (SELECT a FROM inner_table) SELECT b FROM r");
        NodeVisit? seen = null;

        tree.Rewrite(visit =>
        {
            if (visit.Node is RangeVar { Relname: "inner_table" })
                seen = visit;

            return NodeEdit.Keep;
        });

        var expected = tree.Walk().Single(visit => visit.Node is RangeVar { Relname: "inner_table" });
        seen.ShouldNotBeNull().ShouldBe(expected);
        seen.Value.StatementIndex.ShouldBe(1);
        seen.Value.FindAncestor<CommonTableExpr>().ShouldNotBeNull();
    }

    [Fact]
    public void RewriteRejectsAReplacementThatDoesNotFit()
    {
        var tree = Parse("INSERT INTO t (a) VALUES (1)");

        // InsertStmt.Relation holds a RangeVar, not any node.
        var exception = Should.Throw<InvalidOperationException>(() => tree.Rewrite(visit =>
            visit.Node is RangeVar ? NodeEdit.ReplaceWith(new A_Const()) : NodeEdit.Keep));

        exception.Message.ShouldBe("InsertStmt.Relation holds a RangeVar, so it cannot be replaced with a A_Const.");
        Should.Throw<ArgumentNullException>(() => NodeEdit.ReplaceWith(null!));
        Should.Throw<ArgumentNullException>(() => tree.Rewrite(null!));
    }

    [Fact]
    public void RewriteThenCompare()
    {
        var written = Parse("CREATE INDEX i ON t (lower(data ->> 'name'))");
        var stored = Parse("CREATE INDEX i ON t USING btree (lower((data ->> 'name'::text)))");
        written.EqualsIgnoringLocations(stored).ShouldBeFalse();

        stored.Rewrite(visit => visit.Node is TypeCast { Arg: { } arg } && arg.Unwrap() is A_Const { Sval: not null }
            ? NodeEdit.ReplaceWith(arg)
            : NodeEdit.Keep);

        written.EqualsIgnoringLocations(stored).ShouldBeTrue();
    }

    [Fact]
    public void AsNodeWrapsAnyNodeType()
    {
        var table = new RangeVar { Relname = "t" };

        var node = table.AsNode();

        node.RangeVar.ShouldBeSameAs(table);
        node.AsNode().ShouldBeSameAs(node);
        new SelectStmt().AsNode().NodeCase.ShouldBe(Node.NodeOneofCase.SelectStmt);
        // A ParseResult is the container of a tree, not a node in it.
        Should.Throw<ArgumentException>(() => new ParseResult().AsNode());
    }

    [Theory]
    [InlineData("(a > 0) AND b = 'x'::text", "a > 0 AND b = 'x'::text")]
    [InlineData("nextval('seq'::regclass)", "nextval('seq'::regclass)")]
    [InlineData("'😀' || \"é\" -- trailing comment", "'😀' || \"é\"")]
    [InlineData("CASE WHEN a THEN 1 ELSE 2 END", "CASE WHEN a THEN 1 ELSE 2 END")]
    [InlineData("(SELECT max(id) FROM t)", "(SELECT max(id) FROM t)")]
    public void ParseExpression(string expression, string deparsed)
    {
        Deparse(Parser.ParseExpression(expression).GetValueOrThrow()).ShouldBe(deparsed);
    }

    [Theory]
    [InlineData("1, 2")]
    [InlineData("1 AS x")]
    [InlineData("a FROM t")]
    [InlineData("a ORDER BY 1")]
    [InlineData("a UNION SELECT 1")]
    [InlineData("1) FROM t WHERE (true")]
    [InlineData("1); DROP TABLE t; SELECT (1")]
    [InlineData("a >")]
    [InlineData("")]
    public void ParseExpressionRejectsAnythingElse(string expression)
    {
        Parser.ParseExpression(expression).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("numeric(10,2)", "numeric(10, 2)")]
    [InlineData("character varying(20)", "varchar(20)")]
    [InlineData("text[]", "text[]")]
    [InlineData("public.my_type", "public.my_type")]
    [InlineData("timestamptz", "timestamptz")]
    public void ParseTypeName(string typeName, string deparsed)
    {
        Deparse(Parser.ParseTypeName(typeName).GetValueOrThrow()).ShouldBe(deparsed);
    }

    [Theory]
    [InlineData("int, 2")]
    [InlineData("int FROM t")]
    [InlineData("int; DROP TABLE t")]
    [InlineData("")]
    public void ParseTypeNameRejectsAnythingElse(string typeName)
    {
        Parser.ParseTypeName(typeName).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("users", "users")]
    [InlineData("_x1", "_x1")]
    [InlineData("name", "name")]
    [InlineData("type", "type")]
    [InlineData("Users", "\"Users\"")]
    [InlineData("user", "\"user\"")]
    [InlineData("select", "\"select\"")]
    [InlineData("order", "\"order\"")]
    [InlineData("between", "\"between\"")]
    [InlineData("my table", "\"my table\"")]
    [InlineData("a$b", "\"a$b\"")]
    [InlineData("1abc", "\"1abc\"")]
    [InlineData("é", "\"é\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("", "\"\"")]
    public void QuoteIdentifier(string name, string quoted)
    {
        PgIdentifier.Quote(name).ShouldBe(quoted);
        PgIdentifier.NeedsQuoting(name).ShouldBe(quoted != name);

        // What comes out must scan back as one identifier, unless it was empty.
        if (name.Length > 0)
            Parser.Parse($"SELECT 1 FROM {quoted}").GetValueOrThrow().Descendants<RangeVar>().Single().Relname.ShouldBe(name);
    }

    [Fact]
    public void QuoteQualifiedIdentifier()
    {
        PgIdentifier.Quote("public", "users").ShouldBe("public.users");
        PgIdentifier.Quote("My Schema", "order", "col").ShouldBe("\"My Schema\".\"order\".col");
        Should.Throw<ArgumentNullException>(() => PgIdentifier.Quote((string)null!));
    }
}
