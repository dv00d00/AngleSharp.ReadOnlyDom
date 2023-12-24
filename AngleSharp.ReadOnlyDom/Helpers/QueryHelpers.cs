using System.Buffers;
using System.Text;
using AngleSharp.Common;
using AngleSharp.ReadOnlyDom.ReadOnly.Html;
using AngleSharp.Text;
using Microsoft.Extensions.ObjectPool;

namespace AngleSharp.ReadOnlyDom.Helpers;

public enum TrimMode
{
    None,
    Ends,
    TextNodes
}

public static class QueryHelpers
{
    public static IEnumerable<IReadOnlyNode> AllDescendants(this IReadOnlyNode node, String tag = null)
    {
        for (var i = 0; i < node.ChildNodes.Length; i++)
        {
            var child = node.ChildNodes[i];
            if (IsTag(child, tag))
                yield return child;

            foreach (var next in child.AllDescendants())
                if (IsTag(next, tag))
                    yield return next;
        }

        static Boolean IsTag(IReadOnlyNode node, String tag)
        {
            if (tag == null) return true;
            return node is IReadOnlyElement e && e.LocalName.Memory.Span.SequenceEqual(tag);
        }
    }

    public static Boolean Id(this IReadOnlyNode node, ReadOnlySpan<Char> id)
    {
        var element = node as IReadOnlyElement;
        if (element == null)
            return false;

        var classAttr = element.Attributes["id"];
        if (classAttr == null)
            return false;

        if (classAttr.Value == id)
            return true;

        return false;
    }

    public static Boolean Class(this IReadOnlyNode node, ReadOnlySpan<Char> className)
    {
        var element = node as IReadOnlyElement;
        if (element == null)
            return false;

        var classAttr = element.Attributes["class"];
        if (classAttr == null)
            return false;

        if (classAttr.Value == className)
            return true;

        foreach (var part in classAttr.Value.Memory.Span.Split(" "))
            if (part.SequenceEqual(className))
                return true;

        return false;
    }

    public static Boolean Attr(this IReadOnlyNode node, StringOrMemory name, String value = null)
    {
        var element = node as IReadOnlyElement;
        var attr = element?.Attributes[name];
        if (attr == null)
        {
            return false;
        }

        return value == null || attr.Value == value;
    }

    public static Boolean Tag(this IReadOnlyNode node, StringOrMemory name)
    {
        var element = node as IReadOnlyElement;
        if (element == null)
        {
            return false;
        }

        return element.LocalName == name;
    }

    public static Boolean TagClass(this IReadOnlyNode node, StringOrMemory tag, StringOrMemory className)
    {
        return node.Tag(tag) && node.Class(className);
    }

    private static readonly ObjectPool<StringBuilder> _sbPool = ObjectPool.Create(new StringBuilderPooledObjectPolicy());

    public static String GetTextContent(this IReadOnlyNode node, TrimMode trim = TrimMode.None)
    {
        var sb = _sbPool.Get();
        try
        {
            return node.GetTextContent(sb, trim);
        }
        finally
        {
            _sbPool.Return(sb);
        }
    }

    public static String GetTextContent(this IReadOnlyNode node, StringBuilder sb, TrimMode trimMode = TrimMode.None)
    {
        var text = node.AllDescendants().OfType<IReadOnlyTextNode>();
        int i = 0;
        foreach (var textNode in text)
        {
            var span = textNode.Content.Memory.Span;
            span = trimMode switch
            {
                TrimMode.Ends => i == 0 ? span.TrimStart() : span,
                TrimMode.TextNodes => span.Trim(),
                _ => span
            };

            sb.Append(span);
            i++;
        }

        if (trimMode == TrimMode.Ends)
        {
            int j;
            for (j = sb.Length - 1; j > 0 && sb[j].IsWhiteSpaceCharacter(); j--)
            {
            }

            if (j != sb.Length - 1)
            {
                sb.Remove(j + 1, sb.Length - j - 1);
            }
        }
       
        var tmp = sb.ToString();
        sb.Clear();
        return tmp;
    }

    private static readonly ObjectPool<Stack<IReadOnlyNode>> _stackPool = new DefaultObjectPool<Stack<IReadOnlyNode>>(new DefaultPooledObjectPolicy<Stack<IReadOnlyNode>>());

    public static IEnumerable<IReadOnlyNode> QueryAll(this IReadOnlyNode node, params Func<IReadOnlyNode, Boolean>[] chain)
    {
        var stack = _stackPool.Get();
        try
        {
            foreach (var result in node.ChainInner(stack, chain.AsMemory()))
            {
                yield return result;
            }
        }
        finally
        {
            stack.Clear();
            _stackPool.Return(stack);
        }
    }

    public static IReadOnlyNode QueryOne(this IReadOnlyNode node, params Func<IReadOnlyNode, Boolean>[] chain)
    {
        return node.QueryAll(chain).FirstOrDefault();
    }
    
    private static Boolean ChainMatches(this IReadOnlyNode node, ReadOnlyMemory<Func<IReadOnlyNode, Boolean>> chain)
    {
        if (chain.Length == 0)
            return false;

        var last = chain.Span[chain.Length - 1];
        if (!last(node))
            return false;

        int i = chain.Length - 2;

        // find that all items in chain match distinct parent nodes
        while (i > 0 && node.Parent != null)
        {
            node = node.Parent;
            if (!chain.Span[i](node))
                i--;
        }

        return i == 0;
    }

    private static IEnumerable<IReadOnlyNode> ChainInner(
        this IReadOnlyNode parent,
        Stack<IReadOnlyNode> stack,
        ReadOnlyMemory<Func<IReadOnlyNode, Boolean>> chain)
    {
        stack.Push(parent);

        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (next.ChainMatches(chain))
            {
                yield return next;
            }

            var length = next.ChildNodes.Length;
            while (length > 0)
            {
                stack.Push(next.ChildNodes[--length]);
            }
        }
    }
}