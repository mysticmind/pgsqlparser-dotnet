using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace PgSqlParser.Utils;

/// <summary>
/// Reads and writes parse trees without overflowing the stack.
///
/// Protobuf reads and writes nested messages recursively, and each level of a parse tree costs several
/// kilobytes of stack, because the generated code for <see cref="Node"/> is one method with a case per
/// node type. A thread pool thread on macOS has a 512 KB stack, which a query with a few dozen chained
/// operators is enough to overflow, and a stack overflow kills the process. So the nesting depth is
/// measured first, without recursion, and anything deep runs on a thread whose stack is sized for it.
/// </summary>
internal static class DeepProtobuf
{
    // Up to this depth the work runs on the calling thread. Measured: 512 KB handles about 45 levels.
    private const int InlineDepth = 24;

    // Trees deeper than this are rejected. At StackPerLevel this needs a 64 MB stack.
    public const int MaxDepth = 4000;

    // Measured at about 8 KB per level when reading; doubled for margin.
    private const int StackPerLevel = 16 * 1024;
    private const int StackBase = 1024 * 1024;

    private static readonly OneofDescriptor NodeOneof = Node.Descriptor.Oneofs[0];

    /// <summary>
    /// Parses <paramref name="data"/>, or returns null if it is nested more than <see cref="MaxDepth"/> levels deep.
    /// </summary>
    public static T? Parse<T>(MessageParser<T> parser, MessageDescriptor descriptor, byte[] data)
        where T : class, IMessage<T>
    {
        var depth = MeasureDepth(descriptor, data);
        if (depth > MaxDepth)
            return null;

        if (depth <= InlineDepth)
            return parser.ParseFrom(data);

        return RunWithStackFor(depth, () =>
        {
            // Protobuf's own limit is 100 levels; the measured depth is known to fit the stack.
            using var stream = new MemoryStream(data, writable: false);
            return parser.ParseFrom(CodedInputStream.CreateWithLimits(stream, int.MaxValue, depth + 1));
        });
    }

    /// <summary>
    /// Serializes <paramref name="message"/>, or returns null if it is nested more than <see cref="MaxDepth"/> levels deep.
    /// </summary>
    public static byte[]? ToByteArray(IMessage message)
    {
        var depth = MeasureDepth(message);
        if (depth > MaxDepth)
            return null;

        return depth <= InlineDepth ? message.ToByteArray() : RunWithStackFor(depth, message.ToByteArray);
    }

    /// <summary>
    /// Returns how deeply the messages in <paramref name="data"/> are nested, by walking the wire
    /// format with an explicit stack. The top-level message counts as depth 1.
    /// </summary>
    internal static int MeasureDepth(MessageDescriptor descriptor, ReadOnlySpan<byte> data)
    {
        // Each entry is a message being read: its type and where it ends.
        var open = new Stack<(MessageDescriptor Descriptor, int End)>();
        open.Push((descriptor, data.Length));
        var maxDepth = 1;
        var position = 0;

        while (open.Count > 0)
        {
            var (current, end) = open.Peek();
            if (position >= end)
            {
                open.Pop();
                continue;
            }

            var tag = ReadVarint(data, ref position);
            var fieldNumber = (int)(tag >> 3);
            switch ((WireFormat.WireType)(tag & 7))
            {
                case WireFormat.WireType.Varint:
                    ReadVarint(data, ref position);
                    break;
                case WireFormat.WireType.Fixed64:
                    position += 8;
                    break;
                case WireFormat.WireType.Fixed32:
                    position += 4;
                    break;
                case WireFormat.WireType.LengthDelimited:
                    var length = checked((int)ReadVarint(data, ref position));
                    var field = current.FindFieldByNumber(fieldNumber);
                    if (field is { FieldType: FieldType.Message, IsMap: false })
                    {
                        open.Push((field.MessageType, position + length));
                        maxDepth = Math.Max(maxDepth, open.Count);
                    }
                    else
                    {
                        position += length;
                    }

                    break;
                default:
                    throw new InvalidDataException("Unsupported wire type in parse tree.");
            }

            if (position > end)
                throw new InvalidDataException("Truncated parse tree.");
        }

        return maxDepth;
    }

    /// <summary>
    /// Returns how deeply <paramref name="message"/> is nested, counting the same levels as the wire format.
    /// </summary>
    internal static int MeasureDepth(IMessage message)
    {
        var pending = new Stack<(IMessage Message, int Depth)>();
        pending.Push((message, 1));
        var maxDepth = 1;

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            maxDepth = Math.Max(maxDepth, depth);

            // A Node holds exactly one of 271 fields. Ask which one instead of reading them all.
            if (current is Node node)
            {
                var set = NodeOneof.Accessor.GetCaseFieldDescriptor(node);
                if (set?.Accessor.GetValue(node) is IMessage inner)
                    pending.Push((inner, depth + 1));
                continue;
            }

            foreach (var field in current.Descriptor.Fields.InFieldNumberOrder())
            {
                if (field.FieldType != FieldType.Message || field.IsMap)
                    continue;

                var value = field.Accessor.GetValue(current);
                if (field.IsRepeated)
                {
                    foreach (IMessage item in (System.Collections.IList)value)
                        pending.Push((item, depth + 1));
                }
                else if (value is IMessage child)
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        return maxDepth;
    }

    private static T RunWithStackFor<T>(int depth, Func<T> work)
    {
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
            }
        }, StackBase + depth * StackPerLevel)
        {
            IsBackground = true,
            Name = "PgSqlParser deep parse tree"
        };
        thread.Start();
        thread.Join();

        failure?.Throw();
        return result;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= data.Length)
                throw new InvalidDataException("Truncated parse tree.");

            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }

        throw new InvalidDataException("Malformed varint in parse tree.");
    }
}
