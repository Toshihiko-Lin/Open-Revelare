using System.Buffers.Binary;
using System.Text;

namespace OpenRevelare.Core;

/// <summary>ISO/IEC 23008-12 tmap packaging of two images produced by our HEVC encoder.
/// SDR stays primary; the altr group prefers the ISO 21496-1 derived rendition.
/// This is an internal adapter for our native output, not an arbitrary HEIF editor.</summary>
internal static class GainMapHeicContainer
{
    private sealed record Box(string Type, byte[] Data);
    private sealed record Item(ushort Id, byte[] Info, byte[] Data, List<ushort> Properties);
    private sealed record Reference(string Type, ushort From, ushort[] To);
    private sealed record Image(ushort Primary, List<Item> Items, List<Box> Properties, List<Reference> References, byte[] Handler);

    internal static byte[] Compose(byte[] sdr, byte[] gain, GainMapMetadata metadata,
        ColorSpaceDef baseSpace, int width, int height)
    {
        Image a = Read(sdr), b = Read(gain);
        var items = new List<Item>(a.Items);
        var properties = new List<Box>(a.Properties);
        var references = new List<Reference>(a.References);
        int nextId = a.Items.Max(i => i.Id) + 1;
        var ids = b.Items.ToDictionary(i => i.Id, _ => checked((ushort)nextId++));
        int propertyOffset = properties.Count;
        properties.AddRange(b.Properties); // Gain data has unspecified NCLX, no ICC.
        if (properties.Count + 3 > 0x7fff) throw new NotSupportedException("Too many HEIC properties.");
        foreach (Item item in b.Items)
        {
            byte[] info = (byte[])item.Info.Clone();
            info[3] |= 1; // Hide the auxiliary image and all its tiles.
            BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(4), ids[item.Id]);
            items.Add(new Item(ids[item.Id], info, item.Data, item.Properties.Select(p =>
                checked((ushort)((p & 0x8000) | ((p & 0x7fff) + propertyOffset)))).ToList()));
        }
        references.AddRange(b.References.Select(r => new Reference(r.Type, ids[r.From], r.To.Select(id => ids[id]).ToArray())));
        ushort tmap = checked((ushort)nextId);
        byte[] infoTmap = Payload(w => { w.Write(new byte[] { 2, 0, 0, 0 }); U16(w, tmap); U16(w, 0); Four(w, "tmap"); w.Write((byte)0); });
        byte[] iso = GainMapJpegContainer.IsoMetadataPayload(metadata);
        byte[] toneMap = new byte[1 + iso.Length]; // ToneMapImage version = 0.
        iso.CopyTo(toneMap, 1);
        var alternateProperties = new List<ushort>();
        // tmap sees the cropped base dimensions, not padded standalone HEVC dimensions.
        AddAlternateProperty("ispe", Payload(w => { U32(w, 0); U32(w, checked((uint)width)); U32(w, checked((uint)height)); }));
        AddAlternateProperty("colr", Payload(w =>
        {
            Four(w, "nclx"); U16(w, baseSpace == ColorSpaces.DisplayP3 ? 12 : 1);
            U16(w, 8); U16(w, 0); w.Write((byte)128); // Linear alternate in base primaries.
        }));
        AddAlternateProperty("pixi", new byte[] { 0, 0, 0, 0, 3, 16, 16, 16 });
        items.Add(new Item(tmap, infoTmap, toneMap, alternateProperties));
        references.Add(new Reference("dimg", tmap, new[] { a.Primary, ids[b.Primary] }));
        references.AddRange(a.References.Where(r => r.Type == "cdsc" && r.To.Contains(a.Primary))
            .Select(r => new Reference("cdsc", r.From, new[] { tmap })));

        byte[] ftyp = MakeBox("ftyp", Payload(w => { Four(w, "heix"); U32(w, 0); Four(w, "mif1"); Four(w, "heix"); Four(w, "tmap"); }));
        byte[] Meta(uint dataOffset) => MakeBox("meta", Payload(w =>
        {
            U32(w, 0); w.Write(MakeBox("hdlr", a.Handler));
            w.Write(MakeBox("pitm", Payload(v => { U32(v, 0); U16(v, a.Primary); })));
            w.Write(MakeBox("iloc", Payload(v =>
            {
                U32(v, 0); v.Write((byte)0x44); v.Write((byte)0); U16(v, items.Count);
                uint offset = dataOffset;
                foreach (Item item in items)
                {
                    U16(v, item.Id); U16(v, 0); U16(v, 1); U32(v, offset); U32(v, checked((uint)item.Data.Length));
                    offset = checked(offset + (uint)item.Data.Length);
                }
            })));
            w.Write(MakeBox("iinf", Payload(v => { U32(v, 0); U16(v, items.Count); foreach (Item i in items) v.Write(MakeBox("infe", i.Info)); })));
            w.Write(MakeBox("iprp", Payload(v =>
            {
                v.Write(MakeBox("ipco", Payload(p => { foreach (Box box in properties) p.Write(MakeBox(box.Type, box.Data)); })));
                v.Write(MakeBox("ipma", Payload(p =>
                {
                    U32(p, 1); U32(p, checked((uint)items.Count)); // 16-bit property associations.
                    foreach (Item i in items)
                    {
                        U16(p, i.Id); p.Write(checked((byte)i.Properties.Count));
                        foreach (ushort association in i.Properties) U16(p, association);
                    }
                })));
            })));
            w.Write(MakeBox("iref", Payload(v =>
            {
                U32(v, 0);
                foreach (var r in references.GroupBy(r => (r.Type, r.From)))
                    v.Write(MakeBox(r.Key.Type, Payload(p =>
                    {
                        ushort[] targets = r.SelectMany(x => x.To).Distinct().ToArray();
                        U16(p, r.Key.From); U16(p, targets.Length); foreach (ushort id in targets) U16(p, id);
                    })));
            })));
            w.Write(MakeBox("grpl", MakeBox("altr", Payload(v =>
            {
                U32(v, 0); U32(v, checked((uint)tmap + 1)); U32(v, 2); U32(v, tmap); U32(v, a.Primary);
            }))));
        }));
        byte[] meta = Meta(0);
        meta = Meta(checked((uint)(ftyp.Length + meta.Length + 8)));
        return Payload(w => { w.Write(ftyp); w.Write(meta); w.Write(MakeBox("mdat", Payload(v => { foreach (Item i in items) v.Write(i.Data); }))); });

        void AddAlternateProperty(string type, byte[] data)
        { properties.Add(new Box(type, data)); alternateProperties.Add(checked((ushort)properties.Count)); }
    }

    private static Image Read(byte[] file)
    {
        List<Box> children = Boxes(Boxes(file).Single(b => b.Type == "meta").Data, 4);
        byte[] pitm = children.Single(b => b.Type == "pitm").Data;
        if (pitm[0] != 0) throw new InvalidDataException("Unexpected HEIC primary item version.");
        ushort primary = Get16(pitm, 4);
        List<Box> iprp = Boxes(children.Single(b => b.Type == "iprp").Data);
        List<Box> properties = Boxes(iprp.Single(b => b.Type == "ipco").Data);
        var associations = new Dictionary<ushort, List<ushort>>();
        foreach (Box box in iprp.Where(b => b.Type == "ipma"))
        {
            byte[] d = box.Data; int o = 4;
            if (d[0] != 0) throw new InvalidDataException("Unexpected HEIC property version.");
            uint count = Get32(d, o); o += 4;
            for (int i = 0; i < count; i++)
            {
                ushort id = Get16(d, o); o += 2; int n = d[o++]; var list = new List<ushort>();
                for (int j = 0; j < n; j++)
                {
                    ushort value;
                    if ((d[3] & 1) != 0) { value = Get16(d, o); o += 2; }
                    else { byte v = d[o++]; value = (ushort)((v & 127) | ((v & 128) << 8)); }
                    if ((value & 0x7fff) != 0) list.Add(value);
                }
                associations.Add(id, list);
            }
        }
        var data = new Dictionary<ushort, byte[]>();
        byte[] loc = children.Single(b => b.Type == "iloc").Data;
        int version = loc[0], offsetSize = loc[4] >> 4, lengthSize = loc[4] & 15, baseSize = loc[5] >> 4;
        int indexSize = version == 1 ? loc[5] & 15 : 0, at = 6;
        if (version > 1) throw new InvalidDataException("Unexpected HEIC location version.");
        int itemCount = Get16(loc, at); at += 2;
        for (int i = 0; i < itemCount; i++)
        {
            ushort id = Get16(loc, at); at += 2;
            int method = 0;
            if (version == 1) { method = Get16(loc, at) & 15; at += 2; }
            int reference = Get16(loc, at); at += 2;
            if (reference != 0 || method > 1) throw new InvalidDataException("Unsupported HEIC data location.");
            ulong baseOffset = Number(loc, ref at, baseSize);
            int extents = Get16(loc, at); at += 2;
            using var content = new MemoryStream();
            byte[] source = method == 1 ? children.Single(b => b.Type == "idat").Data : file;
            for (int j = 0; j < extents; j++)
            {
                Number(loc, ref at, indexSize);
                ulong offset = checked(baseOffset + Number(loc, ref at, offsetSize));
                int length = checked((int)Number(loc, ref at, lengthSize));
                content.Write(source.AsSpan(checked((int)offset), length));
            }
            data.Add(id, content.ToArray());
        }
        byte[] iinf = children.Single(b => b.Type == "iinf").Data;
        if (iinf[0] != 0) throw new InvalidDataException("Unexpected HEIC item table version.");
        var items = Boxes(iinf, 6).Select(b =>
        {
            if (b.Type != "infe" || b.Data[0] != 2) throw new InvalidDataException("Unexpected HEIC item info.");
            ushort id = Get16(b.Data, 4);
            return new Item(id, b.Data, data[id], associations.GetValueOrDefault(id) ?? new());
        }).ToList();
        var references = new List<Reference>();
        foreach (Box box in children.Where(b => b.Type == "iref"))
        {
            if (box.Data[0] != 0) throw new InvalidDataException("Unexpected HEIC reference version.");
            foreach (Box r in Boxes(box.Data, 4))
            {
                int count = Get16(r.Data, 2);
                references.Add(new Reference(r.Type, Get16(r.Data, 0), Enumerable.Range(0, count).Select(i => Get16(r.Data, 4 + i * 2)).ToArray()));
            }
        }
        return new Image(primary, items, properties, references, children.Single(b => b.Type == "hdlr").Data);
    }

    private static List<Box> Boxes(byte[] data, int offset = 0)
    {
        var boxes = new List<Box>();
        while (offset < data.Length)
        {
            int size = checked((int)Get32(data, offset));
            if (size < 8 || size > data.Length - offset) throw new InvalidDataException("Invalid HEIC box size.");
            boxes.Add(new Box(Encoding.ASCII.GetString(data, offset + 4, 4), data[(offset + 8)..(offset + size)]));
            offset += size;
        }
        return boxes;
    }
    private static ulong Number(byte[] data, ref int offset, int count)
    {
        if (count > 8) throw new InvalidDataException("Invalid HEIC integer width.");
        ulong n = 0; for (int i = 0; i < count; i++) n = (n << 8) | data[offset++]; return n;
    }
    private static byte[] MakeBox(string type, byte[] data) => Payload(w =>
    { U32(w, checked((uint)data.Length + 8)); Four(w, type); w.Write(data); });
    private static byte[] Payload(Action<BinaryWriter> write)
    { using var s = new MemoryStream(); using var w = new BinaryWriter(s); write(w); return s.ToArray(); }
    private static ushort Get16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    private static uint Get32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static void U16(BinaryWriter w, int n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, checked((ushort)n)); w.Write(b); }
    private static void U32(BinaryWriter w, uint n) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); w.Write(b); }
    private static void Four(BinaryWriter w, string s) => w.Write(Encoding.ASCII.GetBytes(s));
}
