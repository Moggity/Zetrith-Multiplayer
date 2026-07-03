using System;
using System.Collections.Generic;
using System.Xml;

namespace Multiplayer.Common.StateHashing;

/// <summary>
/// An <see cref="XmlWriter"/> that computes hierarchical (Merkle-style) digests of the written
/// document instead of storing it. Plugged into the Scribe saving pipeline, it turns every
/// <c>ExposeData</c> in the game into hashable state coverage with no per-class work.
///
/// Each element's digest covers its name, attributes, text and (recursively) child digests.
/// Digests of elements at depth &lt;= <see cref="recordDepth"/> are recorded together with their
/// paths, giving a comparable tree: two clients exchange root digests cheaply, then drill down
/// through recorded nodes to find the first diverged object.
///
/// Elements whose names are in <see cref="cosmeticElements"/> hash into a separate cosmetic
/// channel: their content affects <see cref="CosmeticRoot"/> but not their parent's digest or
/// <see cref="SimRoot"/>. This is for state that legitimately differs between clients, such as
/// grammar-generated strings produced from each client's language data (pawn names, art
/// descriptions). Only the element's presence is folded into the sim channel.
///
/// Only the writer surface used by <c>ScribeSaver</c> is implemented (mirrors
/// <c>Multiplayer.Client.CustomXmlWriter</c>).
/// </summary>
public sealed class HashingXmlWriter : XmlWriter
{
    // FNV-1a 64-bit
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    // Domain separation markers so e.g. text "a" + child <b> hashes differently from text "ab"
    private const byte MarkerName = 1;
    private const byte MarkerAttrName = 2;
    private const byte MarkerAttrValue = 3;
    private const byte MarkerText = 4;
    private const byte MarkerChild = 5;
    private const byte MarkerCosmeticPresence = 6;

    public readonly record struct RecordedNode(string Path, int Depth, ulong Digest);

    private struct ElementCtx
    {
        public string Name;
        public ulong Hash;
        public string? Path; // null when not recorded (too deep)
        public Dictionary<string, int>? ChildNameCounts; // sibling disambiguation, shallow levels only
        public bool CosmeticRoot;
    }

    private readonly int recordDepth;
    private readonly HashSet<string>? cosmeticElements;

    private ElementCtx[] stack = new ElementCtx[64];
    private int depth; // number of open elements

    private bool inAttribute;
    private int cosmeticDepth = -1; // depth of the open cosmetic root element, -1 when outside

    private ulong simRoot = FnvOffset;
    private ulong cosmeticRoot = FnvOffset;
    private readonly List<RecordedNode> records = new();
    private readonly List<RecordedNode> cosmeticRecords = new();

    /// <param name="recordDepth">Element depth (root = 0) up to which per-node digests are recorded.</param>
    /// <param name="cosmeticElements">Element names whose subtrees hash into the cosmetic channel.</param>
    public HashingXmlWriter(int recordDepth = 4, IEnumerable<string>? cosmeticElements = null)
    {
        this.recordDepth = recordDepth;
        if (cosmeticElements != null)
            this.cosmeticElements = new HashSet<string>(cosmeticElements);
    }

    /// <summary>Digest of all sim-critical content. Valid after the document is fully written.</summary>
    public ulong SimRoot => simRoot;

    /// <summary>Digest of all cosmetic-channel content (see class doc).</summary>
    public ulong CosmeticRoot => cosmeticRoot;

    /// <summary>Recorded sim-channel node digests, in document order (parents after their children).</summary>
    public IReadOnlyList<RecordedNode> Records => records;

    /// <summary>Recorded cosmetic-channel subtree digests, in document order.</summary>
    public IReadOnlyList<RecordedNode> CosmeticRecords => cosmeticRecords;

    private static ulong Fold(ulong hash, byte marker)
    {
        return (hash ^ marker) * FnvPrime;
    }

    private static ulong Fold(ulong hash, string str)
    {
        foreach (var c in str)
        {
            hash = (hash ^ (byte)c) * FnvPrime;
            hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
        }
        return hash;
    }

    private static ulong Fold(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash = (hash ^ (byte)value) * FnvPrime;
            value >>= 8;
        }
        return hash;
    }

    public override void WriteStartElement(string? prefix, string localName, string? ns)
    {
        if (depth == stack.Length)
            Array.Resize(ref stack, stack.Length * 2);

        ref var ctx = ref stack[depth];
        ctx.Name = localName;
        ctx.Hash = Fold(Fold(FnvOffset, MarkerName), localName);
        ctx.CosmeticRoot = false;
        ctx.Path = null;
        ctx.ChildNameCounts = null;

        if (cosmeticDepth < 0 && cosmeticElements != null && cosmeticElements.Contains(localName))
        {
            cosmeticDepth = depth;
            ctx.CosmeticRoot = true;
        }

        // Regular nodes are recorded down to recordDepth. Cosmetic roots are recorded (into
        // CosmeticRecords) whenever their parent is recorded, even one level past the cutoff -
        // they are the diagnostic unit of the cosmetic channel and are sparse.
        bool record = ctx.CosmeticRoot
            ? depth == 0 || stack[depth - 1].Path != null
            : depth <= recordDepth && cosmeticDepth < 0;

        if (record)
        {
            string name = localName;
            if (depth > 0)
            {
                ref var parent = ref stack[depth - 1];
                parent.ChildNameCounts ??= new Dictionary<string, int>();
                parent.ChildNameCounts.TryGetValue(localName, out var index);
                parent.ChildNameCounts[localName] = index + 1;
                name = index > 0 ? $"{localName}[{index}]" : localName;
                ctx.Path = parent.Path != null ? $"{parent.Path}/{name}" : name;
            }
            else
            {
                ctx.Path = name;
            }
        }

        depth++;
    }

    public override void WriteEndElement()
    {
        depth--;
        ref var ctx = ref stack[depth];
        var digest = ctx.Hash;

        if (ctx.CosmeticRoot)
        {
            // Content goes to the cosmetic channel; the parent (and sim root) only see presence.
            cosmeticRoot = Fold(Fold(cosmeticRoot, MarkerChild), digest);
            if (ctx.Path != null)
                cosmeticRecords.Add(new RecordedNode(ctx.Path, depth, digest));
            cosmeticDepth = -1;

            if (depth > 0)
                stack[depth - 1].Hash = Fold(Fold(Fold(stack[depth - 1].Hash, MarkerCosmeticPresence), ctx.Name), 0UL);
            else
                simRoot = Fold(Fold(Fold(simRoot, MarkerCosmeticPresence), ctx.Name), 0UL);
            return;
        }

        if (cosmeticDepth < 0 && ctx.Path != null)
            records.Add(new RecordedNode(ctx.Path, depth, digest));

        if (depth > 0)
            stack[depth - 1].Hash = Fold(Fold(stack[depth - 1].Hash, MarkerChild), digest);
        else if (cosmeticDepth < 0)
            simRoot = Fold(Fold(simRoot, MarkerChild), digest);
    }

    public override void WriteString(string? text)
    {
        if (text == null || depth == 0) return;
        stack[depth - 1].Hash = Fold(Fold(stack[depth - 1].Hash, inAttribute ? MarkerAttrValue : MarkerText), text);
    }

    public override void WriteStartAttribute(string? prefix, string localName, string? ns)
    {
        inAttribute = true;
        if (depth > 0)
            stack[depth - 1].Hash = Fold(Fold(stack[depth - 1].Hash, MarkerAttrName), localName);
    }

    public override void WriteEndAttribute()
    {
        inAttribute = false;
    }

    public override void WriteStartDocument()
    {
    }

    public override void WriteStartDocument(bool standalone)
    {
    }

    public override void WriteEndDocument()
    {
    }

    public override void Flush()
    {
    }

    public override WriteState WriteState => depth > 0 ? WriteState.Content : WriteState.Start;

    public override string? LookupPrefix(string ns) => throw new NotSupportedException();
    public override void WriteBase64(byte[] buffer, int index, int count) => throw new NotSupportedException();
    public override void WriteCData(string? text) => throw new NotSupportedException();
    public override void WriteCharEntity(char ch) => throw new NotSupportedException();
    public override void WriteChars(char[] buffer, int index, int count) => throw new NotSupportedException();
    public override void WriteComment(string? text) => throw new NotSupportedException();
    public override void WriteDocType(string name, string? pubid, string? sysid, string? subset) => throw new NotSupportedException();
    public override void WriteEntityRef(string name) => throw new NotSupportedException();
    public override void WriteFullEndElement() => throw new NotSupportedException();
    public override void WriteProcessingInstruction(string name, string? text) => throw new NotSupportedException();
    public override void WriteRaw(char[] buffer, int index, int count) => throw new NotSupportedException();
    public override void WriteRaw(string data) => throw new NotSupportedException();
    public override void WriteSurrogateCharEntity(char lowChar, char highChar) => throw new NotSupportedException();
    public override void WriteWhitespace(string? ws) => throw new NotSupportedException();
}
