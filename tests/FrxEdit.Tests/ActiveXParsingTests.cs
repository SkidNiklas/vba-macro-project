using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace FrxEdit.Tests;

/// <summary>
/// Reads form streams built by hand from [MS-OFORMS] 2.2.10.6 and 2.2.10.12, so no external forms are needed.
/// Controls that aren't MS-Forms built-ins, such as ListView, have a ClsidCacheIndex of 0x8000 or more.
/// </summary>
public class ActiveXParsingTests
{
    private const ushort CommandButton = 0x11;
    private static readonly Guid ClassId = new("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Class_table_is_read_wherever_it_starts_in_the_stream(int bytesBeforeTable)
    {
        var stream = FormStream(bytesBeforeTable, [ClassTableEntry(ClassId)], Site("LswOne", 0x8000), Site("CmdOne", CommandButton));

        var sites = StructuredMsFormsParser.Parse(stream);

        Assert.Equal(["LswOne", "CmdOne"], sites.Select(site => site.Name));
        Assert.Equal("ActiveX", sites[0].ControlType);
        Assert.Equal(ClassId.ToString("B").ToUpperInvariant(), sites[0].ExtraProperties["classInfoClsId"]);
    }

    [Fact]
    public void Class_index_may_point_outside_the_streams_own_table()
    {
        // A control on a nested container can use a class table that only the root form has.
        var stream = FormStream(0, classTable: null, Site("LswOne", 0x8001), Site("CmdOne", CommandButton));

        var sites = StructuredMsFormsParser.Parse(stream);

        Assert.Equal(["LswOne", "CmdOne"], sites.Select(site => site.Name));
        Assert.Equal("ActiveX", sites[0].ControlType);
    }

    [Fact]
    public void Position_of_an_ActiveX_control_is_read()
    {
        var stream = FormStream(1, [ClassTableEntry(ClassId)], Site("LswOne", 0x8000, left: 1234, top: 5678));

        var site = Assert.Single(StructuredMsFormsParser.Parse(stream));

        Assert.Equal(1234, site.Left);
        Assert.Equal(5678, site.Top);
    }

    [Fact]
    public void Unknown_built_in_type_is_still_rejected()
    {
        var stream = FormStream(0, classTable: null, Site("CmdOne", 0x7E));

        Assert.Empty(StructuredMsFormsParser.Parse(stream));
    }

    private static StorageEntryDump FormStream(int bytesBeforeTable, byte[][]? classTable, params byte[][] sites)
    {
        var output = new MemoryStream();
        output.Write([0x00, 0x04]);                 // MinorVersion, MajorVersion
        WriteUInt16(output, 4);                     // cbForm
        WriteUInt32(output, 0);                     // PropMask
        output.Write(new byte[bytesBeforeTable]);   // Stands in for StreamData, whose size varies

        if (classTable is not null)
        {
            WriteUInt16(output, classTable.Length); // CountOfSiteClassInfo
            foreach (var entry in classTable)
            {
                output.Write(entry);
            }
        }

        var depthsAndTypes = sites.SelectMany(_ => new byte[] { 0x00, 0x01 }).ToArray();
        var padding = new byte[(4 - depthsAndTypes.Length % 4) % 4];
        WriteUInt32(output, (uint)sites.Length);    // CountOfSites
        WriteUInt32(output, (uint)(depthsAndTypes.Length + padding.Length + sites.Sum(site => site.Length))); // CountOfBytes
        output.Write(depthsAndTypes);
        output.Write(padding);
        foreach (var site in sites)
        {
            output.Write(site);
        }

        var data = output.ToArray();
        return new StorageEntryDump(0, "f", "Stream", 0, (ulong)data.Length, false, string.Empty, null, null, [], data, new int[data.Length]);
    }

    /// <summary>A SiteClassInfo with fClsID, fDispEvent, fDefaultProg, fCountOfMethods, fDispidBind and fDispidRowset set.</summary>
    private static byte[] ClassTableEntry(Guid classId)
    {
        var output = new MemoryStream();
        WriteUInt16(output, 0);                     // Version
        WriteUInt16(output, 64);                    // cbClassTable
        WriteUInt32(output, 0x206B);                // PropMask
        WriteUInt32(output, 0);                     // CountOfMethods
        WriteUInt32(output, 0xFFFFFFFF);            // DispidBind
        WriteUInt32(output, 0xFFFFFFFF);            // DispidRowset
        output.Write(classId.ToByteArray());        // ClsID
        output.Write(Guid.Empty.ToByteArray());     // DispEvent
        output.Write(Guid.Empty.ToByteArray());     // DefaultProg
        return output.ToArray();
    }

    /// <summary>An OleSiteConcreteControl with Name, ID, ObjectStreamSize, TabIndex, ClsidCacheIndex and Position set.</summary>
    private static byte[] Site(string name, ushort classIndex, int left = 10, int top = 20)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var paddedName = new byte[(nameBytes.Length + 3) & ~3];
        nameBytes.CopyTo(paddedName, 0);

        var output = new MemoryStream();
        WriteUInt16(output, 0);                     // Version
        WriteUInt16(output, 4 + 16 + paddedName.Length + 8); // cbSite
        WriteUInt32(output, 0x1E5);                 // PropMask
        WriteUInt32(output, (uint)nameBytes.Length | 0x8000_0000); // NameData, compressed
        WriteUInt32(output, 1);                     // ID
        WriteUInt32(output, 0);                     // ObjectStreamSize
        WriteUInt16(output, 0);                     // TabIndex
        WriteUInt16(output, classIndex);            // ClsidCacheIndex
        output.Write(paddedName);                   // Name
        WriteUInt32(output, (uint)left);            // SitePosition
        WriteUInt32(output, (uint)top);
        return output.ToArray();
    }

    private static void WriteUInt16(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value);
        output.Write(buffer);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }
}
