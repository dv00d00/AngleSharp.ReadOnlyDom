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
    private static readonly ObjectPool<StringBuilder> _sbPool = 
        ObjectPool.Create(new StringBuilderPooledObjectPolicy());

    private static readonly ObjectPool<Stack<IReadOnlyNode>> _stackPool = 
        new DefaultObjectPool<Stack<IReadOnlyNode>>(new DefaultPooledObjectPolicy<Stack<IReadOnlyNode>>());

    public static IEnumerable<IReadOnlyNode> AllDescendants(this IReadOnlyNode node)
    {
        for (var i = 0; i < node.ChildNodes.Length; i++)
        {
            var child = node.ChildNodes[i];
            yield return child;

            foreach (var next in child.AllDescendants())
                yield return next;
        }
    }

    public static bool Id(this IReadOnlyNode node, ReadOnlySpan<char> id)
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
    private static readonly SearchValues<char> _whitespaces = SearchValues.Create("\t\n\r\f ");

    public static bool Class(this IReadOnlyNode node, ReadOnlySpan<char> className)
    {
        var element = node as IReadOnlyElement;
        if (element == null)
            return false;

        var classAttr = element.Attributes["class"];
        if (classAttr == null)
            return false;

        if (classAttr.Value == className)
            return true;

        foreach (var part in classAttr.Value.Memory.Span.Split(_whitespaces))
            if (part.SequenceEqual(className))
                return true;

        return false;
    }
    
    public static bool Classes(
        this IReadOnlyNode node, 
        ReadOnlySpan<char> className1, 
        ReadOnlySpan<char> className2)
    {
        var element = node as IReadOnlyElement;
        if (element == null)
            return false;

        var classAttr = element.Attributes["class"];
        if (classAttr == null)
            return false;
        
        bool found1 = false;
        bool found2 = false;

        foreach (var part in classAttr.Value.Memory.Span.Split(_whitespaces))
        {
            if (part.SequenceEqual(className1))
            {
                found1 = true;
            }
            else if (part.SequenceEqual(className2))
            {
                found2 = true;
            }

            if (found1 && found2)
                return true;
        }

        return found1 && found2;
    }

    public static bool Attr(this IReadOnlyNode node, StringOrMemory name, string? value = null)
    {
        var element = node as IReadOnlyElement;
        var attr = element?.Attributes[name];
        if (attr == null)
        {
            return false;
        }

        return value == null || attr.Value == value;
    }

    public static bool Tag(this IReadOnlyNode node, StringOrMemory name)
    {
        var element = node as IReadOnlyElement;
        if (element == null)
        {
            return false;
        }

        return element.LocalName == name;
    }

    public static bool TagId(this IReadOnlyNode node, StringOrMemory name, StringOrMemory id)
    {
       return node.Tag(name) && node.Id(id);
    }

    public static bool TagClass(this IReadOnlyNode node, StringOrMemory tag, StringOrMemory className)
    {
        return node.Tag(tag) && node.Class(className);
    }
    
    public static bool TagClasses(this IReadOnlyNode node, StringOrMemory tag, StringOrMemory className1, StringOrMemory className2)
    {
        return node.Tag(tag) && node.Classes(className1, className2);
    }

    public static T WithTextContent<T>(this IReadOnlyNode node, Func<StringBuilder, T> consumeText)
    {
        var sb = _sbPool.Get();
        try
        {
            var text = node.AllDescendants().OfType<IReadOnlyTextNode>();
            foreach (var textNode in text)
            {
                var span = textNode.Content.Memory.Span;
                sb.Append(span);
            }
            
            return consumeText(sb);
        }
        finally
        {
            sb.Clear();
            _sbPool.Return(sb);
        }
    }

    public static string GetTextContent(this IReadOnlyNode node, TrimMode trim = TrimMode.None)
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

    public static string GetTextContent(this IReadOnlyNode node, StringBuilder sb, TrimMode trimMode = TrimMode.None)
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

    public static IEnumerable<IReadOnlyNode> QueryAll(this IReadOnlyNode node, params Func<IReadOnlyNode, bool>[] chain)
    {
        var stack = _stackPool.Get();
        try
        {
            foreach (var result in node.ChainInner(stack, chain))
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

    public static IReadOnlyNode? QueryOne(this IReadOnlyNode node, params Func<IReadOnlyNode, bool>[] chain)
    {
        return node.QueryAll(chain).FirstOrDefault();
    }
    
    private static bool ChainMatches(this IReadOnlyNode node, Func<IReadOnlyNode, bool>[] chain)
    {
        if (chain.Length == 0)
            return true;

        var last = chain[^1];
        if (!last(node))
            return false;

        if (chain.Length == 1)
            return true;

        int i = chain.Length - 2;

        // find that all items in chain match distinct parent nodes
        while (i >= 0 && node.Parent != null)
        {
            node = node.Parent;
            if (chain[i](node))
                i--;
        }

        return i < 0;
    }

    private static IEnumerable<IReadOnlyNode> ChainInner(
        this IReadOnlyNode parent,
        Stack<IReadOnlyNode> stack,
        Func<IReadOnlyNode, bool>[] chain)
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