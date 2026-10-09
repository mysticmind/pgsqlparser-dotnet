using Google.Protobuf;

namespace PgSqlParser;

/// <summary>
/// A set of handlers, each for one node type, to run over a parse tree with
/// <see cref="NodeVisitorExtensions.Walk(IMessage, NodeVisitor[])"/>. Several visitors can share one walk.
/// </summary>
public sealed class NodeVisitor
{
    private readonly Dictionary<Type, List<Func<IMessage, NodeVisit, WalkAction>>> _handlers = [];

    /// <summary>Calls <paramref name="handler"/> for every node of type <typeparamref name="T"/>.</summary>
    public NodeVisitor On<T>(Action<T, NodeVisit> handler) where T : class, IMessage
    {
        ArgumentNullException.ThrowIfNull(handler);

        return On<T>((node, visit) =>
        {
            handler(node, visit);
            return WalkAction.Continue;
        });
    }

    /// <summary>
    /// Calls <paramref name="handler"/> for every node of type <typeparamref name="T"/>. What it returns
    /// applies to this visitor only: it can skip the node's children or stop, while other visitors in
    /// the same walk carry on.
    /// </summary>
    public NodeVisitor On<T>(Func<T, NodeVisit, WalkAction> handler) where T : class, IMessage
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryGetValue(typeof(T), out var handlers))
            _handlers[typeof(T)] = handlers = [];

        handlers.Add((node, visit) => handler((T)node, visit));
        return this;
    }

    // Runs this visitor's handlers for the node. With several handlers the strongest action wins.
    internal WalkAction Visit(NodeVisit visit)
    {
        if (!_handlers.TryGetValue(visit.Node.GetType(), out var handlers))
            return WalkAction.Continue;

        var result = WalkAction.Continue;
        foreach (var handler in handlers)
        {
            var action = handler(visit.Node, visit);
            if (action > result)
                result = action;
        }

        return result;
    }
}

/// <summary>
/// Walks a parse tree with typed visitors.
/// </summary>
public static class NodeVisitorExtensions
{
    /// <summary>
    /// Walks the nodes below <paramref name="root"/> once, in the order of
    /// <see cref="ParseTreeExtensions.Walk(IMessage)"/>, and runs every visitor's handlers on each node.
    /// A visitor that skips a subtree or stops does so for itself only.
    /// </summary>
    public static void Walk(this IMessage root, params NodeVisitor[] visitors)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(visitors);

        // Per visitor: the depth of the node whose children it is skipping, or -1; and whether it stopped.
        var skipDepth = new int[visitors.Length];
        Array.Fill(skipDepth, -1);
        var stopped = new bool[visitors.Length];
        var running = visitors.Length;

        root.Walk(visit =>
        {
            var anyoneGoesDeeper = false;
            for (var i = 0; i < visitors.Length; i++)
            {
                if (stopped[i])
                    continue;

                // Still below the skipped node.
                if (skipDepth[i] >= 0 && visit.Depth > skipDepth[i])
                    continue;

                skipDepth[i] = -1;
                switch (visitors[i].Visit(visit))
                {
                    case WalkAction.Stop:
                        stopped[i] = true;
                        running--;
                        break;
                    case WalkAction.SkipChildren:
                        skipDepth[i] = visit.Depth;
                        break;
                    default:
                        anyoneGoesDeeper = true;
                        break;
                }
            }

            if (running == 0)
                return WalkAction.Stop;

            return anyoneGoesDeeper ? WalkAction.Continue : WalkAction.SkipChildren;
        });
    }
}
