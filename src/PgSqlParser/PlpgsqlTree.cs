using System.Text.Json;

namespace PgSqlParser;

/// <summary>
/// A node of a parsed PL/pgSQL function: the function itself, a variable, a statement or an expression.
/// libpg_query has no schema for this output, so a node exposes its kind and its JSON rather than
/// typed properties, plus the navigation that is the same for every kind.
/// </summary>
public sealed class PlpgsqlNode
{
    private const string KindPrefix = "PLpgSQL_";

    private IReadOnlyList<PlpgsqlNode>? _children;

    internal PlpgsqlNode(string kind, JsonElement json)
    {
        Kind = kind;
        Json = json;
    }

    /// <summary>The node kind as libpg_query names it, for example <c>PLpgSQL_stmt_return</c>.</summary>
    public string Kind { get; }

    /// <summary>The node's own JSON object, with its fields and child nodes.</summary>
    public JsonElement Json { get; }

    /// <summary>The line of the function body the node is on, if it records one.</summary>
    public int? LineNo => Json.TryGetProperty("lineno", out var value) && value.TryGetInt32(out var lineNo) ? lineNo : null;

    /// <summary>The nodes directly inside this one, in document order.</summary>
    public IReadOnlyList<PlpgsqlNode> Children => _children ??= FindChildren(Json);

    /// <summary>Every node below this one, depth first, parents before their children.</summary>
    public IEnumerable<PlpgsqlNode> Descendants()
    {
        var pending = new Stack<PlpgsqlNode>();
        PushReversed(pending, Children);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            PushReversed(pending, node.Children);
        }
    }

    /// <summary>Returns a string field of the node, such as <c>refname</c> or <c>query</c>, or null if it has none.</summary>
    public string? GetString(string name)
    {
        return Json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Parses the SQL of a <c>PLpgSQL_expr</c> node, in the mode PL/pgSQL itself uses for it: a query, an
    /// expression (parsed as <c>SELECT expression</c>) or an assignment. Fails for any other kind of node.
    /// </summary>
    public Result<ParseResult> ParseQuery()
    {
        if (Kind != "PLpgSQL_expr" || GetString("query") is not { } query)
            return Result<ParseResult>.Failure(new Error($"a {Kind} node holds no SQL to parse", null, null, 0, 0, null));

        // parseMode is PostgreSQL's RawParseMode, which ParserOptions uses the same numbers for.
        var mode = Json.TryGetProperty("parseMode", out var value) && value.TryGetInt32(out var number) ? number : 0;
        return Parser.Parse(query, (ParserOptions)mode);
    }

    public override string ToString() => Kind;

    // A node is serialized as an object with one property: its kind, holding the node's fields.
    internal static bool TryCreate(JsonElement element, out PlpgsqlNode node)
    {
        node = null!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        using var properties = element.EnumerateObject();
        if (!properties.MoveNext())
            return false;

        var property = properties.Current;
        if (properties.MoveNext()
            || property.Value.ValueKind != JsonValueKind.Object
            || !property.Name.StartsWith(KindPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        node = new PlpgsqlNode(property.Name, property.Value);
        return true;
    }

    private static IReadOnlyList<PlpgsqlNode> FindChildren(JsonElement json)
    {
        var children = new List<PlpgsqlNode>();
        CollectChildren(json, children);
        return children;
    }

    private static void CollectChildren(JsonElement element, List<PlpgsqlNode> children)
    {
        if (TryCreate(element, out var node))
        {
            children.Add(node);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                CollectChildren(property.Value, children);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectChildren(item, children);
        }
    }

    private static void PushReversed(Stack<PlpgsqlNode> pending, IReadOnlyList<PlpgsqlNode> nodes)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
            pending.Push(nodes[i]);
    }
}

/// <summary>
/// A PL/pgSQL function parsed by <see cref="Parser.ParsePlpgsqlFunctions"/>.
/// </summary>
public sealed class PlpgsqlFunction
{
    internal PlpgsqlFunction(PlpgsqlNode root)
    {
        Root = root;
        Datums = root.Json.TryGetProperty("datums", out var datums) && datums.ValueKind == JsonValueKind.Array
            ? datums.EnumerateArray().Select(ToNode).OfType<PlpgsqlNode>().ToList()
            : [];
        Body = root.Json.TryGetProperty("action", out var action) ? ToNode(action) : null;
    }

    /// <summary>The <c>PLpgSQL_function</c> node, for anything not covered by the properties here.</summary>
    public PlpgsqlNode Root { get; }

    /// <summary>The function's variables, parameters and other data items, such as <c>PLpgSQL_var</c> nodes.</summary>
    public IReadOnlyList<PlpgsqlNode> Datums { get; }

    /// <summary>The function body, a <c>PLpgSQL_stmt_block</c>, or null for a function that is not PL/pgSQL.</summary>
    public PlpgsqlNode? Body { get; }

    /// <summary>Every statement in the body, including nested ones, in document order.</summary>
    public IEnumerable<PlpgsqlNode> Statements()
    {
        if (Body is null)
            return [];

        return new[] { Body }.Concat(Body.Descendants())
            .Where(node => node.Kind.StartsWith("PLpgSQL_stmt_", StringComparison.Ordinal));
    }

    /// <summary>
    /// The SQL text of every expression and query in the function, in document order. These can be
    /// passed to <see cref="Parser.Parse"/> and the other functions.
    /// </summary>
    public IEnumerable<string> Queries()
    {
        return Root.Descendants()
            .Where(node => node.Kind == "PLpgSQL_expr")
            .Select(node => node.GetString("query"))
            .OfType<string>();
    }

    /// <summary>
    /// Parses every expression and query in the function, in document order. Each item has the node,
    /// its SQL text, and either the parse tree or the error.
    /// </summary>
    public IReadOnlyList<PlpgsqlQuery> ParseQueries()
    {
        var queries = new List<PlpgsqlQuery>();
        foreach (var node in Root.Descendants())
        {
            if (node.Kind != "PLpgSQL_expr" || node.GetString("query") is not { } sql)
                continue;

            var parsed = node.ParseQuery();
            queries.Add(new PlpgsqlQuery(node, sql, parsed.Value, parsed.Error));
        }

        return queries;
    }

    private static PlpgsqlNode? ToNode(JsonElement element) => PlpgsqlNode.TryCreate(element, out var node) ? node : null;
}

/// <summary>
/// A piece of SQL inside a PL/pgSQL function, parsed by <see cref="PlpgsqlFunction.ParseQueries"/>.
/// </summary>
/// <param name="Node">The <c>PLpgSQL_expr</c> node it comes from.</param>
/// <param name="Sql">The SQL text as written in the function.</param>
/// <param name="Tree">The parse tree, or null if it did not parse.</param>
/// <param name="Error">Why it did not parse, or null.</param>
public sealed record PlpgsqlQuery(PlpgsqlNode Node, string Sql, ParseResult? Tree, Error? Error);
