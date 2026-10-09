using Google.Protobuf;
using Google.Protobuf.Reflection;
using PgSqlParser.Utils;

namespace PgSqlParser;

/// <summary>
/// A node reached while walking a parse tree.
/// </summary>
/// <param name="Node">The node, for example a <see cref="SelectStmt"/> or a <see cref="RangeVar"/>.</param>
/// <param name="Parent">The node that contains it, or null for a node directly below the root.</param>
/// <param name="Depth">How far below the root the node is; 1 for the root's own children.</param>
public readonly record struct NodeVisit(IMessage Node, IMessage? Parent, int Depth)
{
    /// <summary>
    /// The property of the containing node that holds this node, for example <c>WhereClause</c> or
    /// <c>TargetList</c>. Compare it with <c>nameof</c>, as in <c>nameof(SelectStmt.WhereClause)</c>.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>The node's position if that property is a list, otherwise null.</summary>
    public int? Index { get; init; }

    /// <summary>
    /// The top-level statement the node belongs to, when the walk started from a <see cref="ParseResult"/>.
    /// For the statement's own <see cref="RawStmt"/> this is that node.
    /// </summary>
    public RawStmt? Statement { get; init; }

    /// <summary>The position of <see cref="Statement"/> in <see cref="ParseResult.Stmts"/>, or null.</summary>
    public int? StatementIndex { get; init; }

    internal WalkFrame? ParentFrame { get; init; }

    /// <summary>
    /// The nodes that contain this one, nearest first: <see cref="Parent"/>, then its parent, and so on
    /// up to the node directly below the root of the walk.
    /// </summary>
    public IEnumerable<IMessage> Ancestors
    {
        get
        {
            for (var frame = ParentFrame; frame is not null; frame = frame.Parent)
                yield return frame.Node;
        }
    }

    /// <summary>
    /// Returns the nearest containing node of type <typeparamref name="T"/>, or null. For example
    /// <c>visit.FindAncestor&lt;CommonTableExpr&gt;()</c> tells whether a node is inside a CTE.
    /// </summary>
    public T? FindAncestor<T>() where T : class, IMessage
    {
        for (var frame = ParentFrame; frame is not null; frame = frame.Parent)
        {
            if (frame.Node is T match)
                return match;
        }

        return null;
    }

    /// <summary>
    /// Two visits are equal when they are the same node in the same place. Nodes are compared by
    /// reference, since a tree can hold equal-looking nodes in different places.
    /// </summary>
    public bool Equals(NodeVisit other)
    {
        return ReferenceEquals(Node, other.Node)
               && ReferenceEquals(Parent, other.Parent)
               && Depth == other.Depth
               && FieldName == other.FieldName
               && Index == other.Index
               && StatementIndex == other.StatementIndex;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Node), Depth, FieldName, Index);
    }
}

// One link of the chain from a visited node up to the root of the walk.
internal sealed class WalkFrame(IMessage node, WalkFrame? parent)
{
    public IMessage Node { get; } = node;
    public WalkFrame? Parent { get; } = parent;
}

/// <summary>
/// What <see cref="ParseTreeExtensions.Walk(IMessage, Func{NodeVisit, WalkAction})"/> does after visiting a node.
/// </summary>
public enum WalkAction
{
    /// <summary>Go on to the node's children.</summary>
    Continue,

    /// <summary>Do not visit anything below this node, and go on to the next one.</summary>
    SkipChildren,

    /// <summary>End the walk.</summary>
    Stop
}

/// <summary>
/// Helpers for navigating a parse tree without spelling out the path to each node.
/// </summary>
public static class ParseTreeExtensions
{
    private const string LocationFieldName = "location";

    /// <summary>
    /// Returns the concrete node a <see cref="Node"/> wraps, for example a <see cref="SelectStmt"/>,
    /// or null if it is empty. Useful for pattern matching:
    /// <c>switch (rawStmt.Stmt.Unwrap()) { case SelectStmt select: ... }</c>
    /// </summary>
    public static IMessage? Unwrap(this Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var field = Node.Descriptor.Oneofs[0].Accessor.GetCaseFieldDescriptor(node);
        return field?.Accessor.GetValue(node) as IMessage;
    }

    /// <summary>
    /// Walks every node below <paramref name="root"/>, depth first, parents before their children and
    /// siblings in field order. <see cref="Node"/> wrappers are skipped: the nodes they wrap are
    /// returned in their place.
    /// </summary>
    public static IEnumerable<NodeVisit> Walk(this IMessage root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return WalkIterator(root);
    }

    /// <summary>
    /// Walks the nodes below <paramref name="root"/> in the same order, calling <paramref name="visitor"/>
    /// for each one. Its return value decides whether the walk goes into the node's children, skips
    /// them, or stops, which the enumerable form cannot do.
    /// </summary>
    public static void Walk(this IMessage root, Func<NodeVisit, WalkAction> visitor)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(visitor);

        var pending = new Stack<NodeVisit>();
        PushChildren(pending, root is Node rootWrapper ? rootWrapper.Unwrap() : root, null);

        while (pending.Count > 0)
        {
            var visit = pending.Pop();
            var action = visitor(visit);
            if (action == WalkAction.Stop)
                return;

            if (action == WalkAction.Continue)
                PushChildren(pending, visit.Node, visit);
        }
    }

    /// <summary>
    /// Returns every node below <paramref name="root"/>, in the order of <see cref="Walk(IMessage)"/>.
    /// </summary>
    public static IEnumerable<IMessage> Descendants(this IMessage root)
    {
        return root.Walk().Select(visit => visit.Node);
    }

    /// <summary>
    /// Returns every node of type <typeparamref name="T"/> below <paramref name="root"/>, in the order
    /// of <see cref="Walk(IMessage)"/>. For example <c>parseResult.Descendants&lt;RangeVar&gt;()</c> finds every
    /// table reference.
    /// </summary>
    public static IEnumerable<T> Descendants<T>(this IMessage root) where T : class, IMessage
    {
        return root.Walk().Select(visit => visit.Node).OfType<T>();
    }

    /// <summary>
    /// Returns the node's location in the query, or null if this kind of node has none or the location
    /// is unknown. Like all parse tree locations it is a UTF-8 byte offset; convert it with
    /// <see cref="Utf8OffsetMapper"/> to index the query string.
    /// </summary>
    public static int? GetLocation(this IMessage node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node is Node wrapper)
            return wrapper.Unwrap()?.GetLocation();

        if (node is RawStmt rawStmt)
            return rawStmt.StmtLocation;

        var field = node.Descriptor.FindFieldByName(LocationFieldName);
        if (field is null || field.FieldType != FieldType.Int32 || field.IsRepeated)
            return null;

        // PostgreSQL uses -1 for "unknown".
        var location = (int)field.Accessor.GetValue(node);
        return location < 0 ? null : location;
    }

    /// <summary>
    /// Returns the text of a statement, taken from the <paramref name="query"/> it was parsed from.
    /// Statements are the only nodes that record their length, so this is not available for other nodes.
    /// </summary>
    /// <param name="rawStmt">A statement from <see cref="ParseResult.Stmts"/>.</param>
    /// <param name="query">The query passed to <see cref="Parser.Parse"/>.</param>
    /// <param name="mapper">
    /// A mapper for <paramref name="query"/> to reuse across several statements. One is created if omitted.
    /// </param>
    public static string GetText(this RawStmt rawStmt, string query, Utf8OffsetMapper? mapper = null)
    {
        ArgumentNullException.ThrowIfNull(rawStmt);
        ArgumentNullException.ThrowIfNull(query);

        mapper ??= new Utf8OffsetMapper(query);
        var start = mapper.ToCharOffset(rawStmt.StmtLocation);
        // A length of 0 means the statement runs to the end of the query.
        var end = rawStmt.StmtLen > 0 ? mapper.ToCharOffset(rawStmt.StmtLocation + rawStmt.StmtLen) : query.Length;
        return query[start..end];
    }

    /// <summary>
    /// Turns this node back into SQL. See <see cref="Parser.DeparseNode"/> for which nodes are supported.
    /// </summary>
    public static Result<string> Deparse(this IMessage node)
    {
        return Parser.DeparseNode(node);
    }

    // An explicit stack instead of recursion, so a deeply nested tree cannot overflow the call stack.
    private static IEnumerable<NodeVisit> WalkIterator(IMessage root)
    {
        var pending = new Stack<NodeVisit>();
        PushChildren(pending, root is Node rootWrapper ? rootWrapper.Unwrap() : root, null);

        while (pending.Count > 0)
        {
            var visit = pending.Pop();
            yield return visit;
            PushChildren(pending, visit.Node, visit);
        }
    }

    // Pushes the children of message. owner is the visit of message itself, or null when message is the root.
    private static void PushChildren(Stack<NodeVisit> pending, IMessage? message, NodeVisit? owner)
    {
        if (message is null)
            return;

        var frame = owner is { } visit ? new WalkFrame(visit.Node, visit.ParentFrame) : null;
        var depth = (owner?.Depth ?? 0) + 1;
        // Statements are the direct children of a ParseResult that the walk started from.
        var startsStatements = owner is null && message is ParseResult;

        // Pushed in reverse so they are popped, and so visited, in field order.
        var fields = message.Descriptor.Fields.InFieldNumberOrder();
        for (var i = fields.Count - 1; i >= 0; i--)
        {
            var field = fields[i];
            if (field.FieldType != FieldType.Message || field.IsMap)
                continue;

            var value = field.Accessor.GetValue(message);
            if (field.IsRepeated)
            {
                var items = (System.Collections.IList)value;
                for (var j = items.Count - 1; j >= 0; j--)
                    Push((IMessage)items[j]!, j);
            }
            else if (value is IMessage child)
            {
                Push(child, null);
            }

            continue;

            void Push(IMessage child, int? index)
            {
                // A Node wrapper is transparent: the field and index are those of the wrapper.
                var node = child is Node wrapper ? wrapper.Unwrap() : child;
                if (node is null)
                    return;

                var statement = startsStatements ? node as RawStmt : owner?.Statement;
                pending.Push(new NodeVisit(node, owner?.Node, depth)
                {
                    FieldName = field.PropertyName,
                    Index = index,
                    Statement = statement,
                    StatementIndex = startsStatements && statement is not null ? index : owner?.StatementIndex,
                    ParentFrame = frame
                });
            }
        }
    }
}
