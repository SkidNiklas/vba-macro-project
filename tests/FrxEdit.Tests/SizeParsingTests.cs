using System.Buffers.Binary;
using Xunit;

namespace FrxEdit.Tests;

/// <summary>Reads control sizes from streams built by hand, so no external forms are needed.</summary>
public class SizeParsingTests
{
    private const int Width = 17357;
    private const int Height = 15875;

    private const uint NextAvailableId = 1u << 3;
    private const uint BooleanProperties = 1u << 6;
    private const uint DisplayedSize = 1u << 10;
    private const uint LogicalSize = 1u << 11;
    private const uint Cycle = 1u << 16;
    private const uint SpecialEffect = 1u << 17;
    private const uint BorderColor = 1u << 18;
    private const uint Font = 1u << 20;
    private const uint ShapeCookie = 1u << 26;
    private const uint DrawBuffer = 1u << 27;

    private const uint Always = NextAvailableId | BooleanProperties | DisplayedSize | LogicalSize | ShapeCookie | DrawBuffer;

    [Theory]
    [InlineData(0u)]
    [InlineData(Font)]
    [InlineData(SpecialEffect)]
    [InlineData(SpecialEffect | Font)]
    [InlineData(SpecialEffect | BorderColor)]
    [InlineData(SpecialEffect | BorderColor | Font)]
    [InlineData(Cycle | Font)]
    [InlineData(Cycle | SpecialEffect | Font)]
    public void Form_size_is_read_with_padding_only_before_fields_that_are_present(uint optionalFields)
    {
        var stream = FormStream(Always | optionalFields);

        Assert.True(FormControlParser.TryRead(stream, out var properties));

        Assert.Equal(Width, properties.DisplayedWidth);
        Assert.Equal(Height, properties.DisplayedHeight);
    }

    [Fact]
    public void ActiveX_size_comes_after_the_header_of_its_object_data()
    {
        var properties = ObjectStreamParser.Read(ActiveXStream(0x1234_4321, 8), "ActiveX");

        Assert.Equal(Width, properties.Width);
        Assert.Equal(Height, properties.Height);
    }

    [Fact]
    public void ActiveX_header_is_not_read_as_a_caption()
    {
        var properties = ObjectStreamParser.Read(ActiveXStream(0x1234_4321, 8), "ActiveX");

        Assert.False(properties.Properties.ContainsKey("caption"));
    }

    [Theory]
    [InlineData(0x1234_4321, 12)]
    [InlineData(0x0000_0000, 8)]
    public void Other_object_data_gets_no_size_from_the_ActiveX_header(uint magic, int extentSize)
    {
        var properties = ObjectStreamParser.Read(ActiveXStream(magic, extentSize), "ActiveX");

        Assert.NotEqual(Width, properties.Width);
    }

    /// <summary>A FormDataBlock and FormExtraDataBlock per [MS-OFORMS] 2.2.10.2: padding comes only before a present field.</summary>
    private static StorageEntryDump FormStream(uint propMask)
    {
        var output = new List<byte>();

        void Align(int size)
        {
            while (output.Count % size != 0)
            {
                output.Add(0);
            }
        }

        void AddUInt32(uint value)
        {
            Align(4);
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            output.AddRange(buffer);
        }

        void AddUInt16(ushort value)
        {
            Align(2);
            var buffer = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            output.AddRange(buffer);
        }

        output.AddRange([0x00, 0x04, 0x00, 0x00]);  // MinorVersion, MajorVersion, cbForm written below
        AddUInt32(propMask);
        AddUInt32(0x45);                            // NextAvailableID
        AddUInt32(0xC004);                          // BooleanProperties
        if ((propMask & Cycle) != 0) output.Add(4);
        if ((propMask & SpecialEffect) != 0) output.Add(3);
        if ((propMask & BorderColor) != 0) AddUInt32(0x80000006);
        if ((propMask & Font) != 0) AddUInt16(0xFFFF);
        AddUInt32(0x3A);                            // ShapeCookie
        AddUInt32(32000);                           // DrawBuffer
        AddUInt32(Width);                           // DisplayedSize
        AddUInt32(Height);
        AddUInt32(Width);                           // LogicalSize
        AddUInt32(Height);

        var data = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)(data.Length - 4));
        return Stream(data);
    }

    private static StorageEntryDump ActiveXStream(uint magic, int extentSize)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data, magic);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), extentSize);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), Width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), Height);
        return Stream(data);
    }

    private static StorageEntryDump Stream(byte[] data) =>
        new(0, "f", "Stream", 0, (ulong)data.Length, false, string.Empty, null, null, [], data, new int[data.Length]);
}
