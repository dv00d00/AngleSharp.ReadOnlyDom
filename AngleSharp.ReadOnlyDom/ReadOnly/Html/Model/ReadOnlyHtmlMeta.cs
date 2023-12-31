﻿using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Construction;

namespace AngleSharp.ReadOnlyDom.ReadOnly.Html.Model;

class ReadOnlyHtmlMeta : ReadOnlyHtmlElement, IConstructableMetaElement
{
    public ReadOnlyHtmlMeta(ReadOnlyDocument? owner, StringOrMemory prefix = default)
        : base(owner, TagNames.Meta, prefix, NodeFlags.Special | NodeFlags.SelfClosing)
    {
    }

    public void Handle()
    {
        // do nothing
    }

    public IConstructableNode ShallowCopy()
    {
        var readOnlyElement = new ReadOnlyHtmlMeta(Owner);
        PopulateAttributes(readOnlyElement);
        return readOnlyElement;
    }
}