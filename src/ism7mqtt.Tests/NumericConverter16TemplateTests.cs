using ism7mqtt.ISM7.Xml;
using Xunit;

namespace ism7mqtt.Tests;

public class NumericConverter16TemplateTests
{
    private static NumericConverter16Template CreateConverter(string type)
    {
        return new NumericConverter16Template
        {
            CTID = 1,
            Type = type,
            TelegramNr = 100,
        };
    }

    [Theory]
    [InlineData("SS")]
    [InlineData("SS10")]
    [InlineData("SS100")]
    [InlineData("SSPR")]
    public void Signed_NotAvailableMarker_HasNoValue(string type)
    {
        var converter = CreateConverter(type);

        converter.AddTelegram(100, low: 0x00, high: 0x80);
        Assert.True(converter.HasValue);

        Assert.Null(converter.GetValue());
        Assert.False(converter.HasValue);
    }

    [Fact]
    public void SS10_NegativeValue_IsConverted()
    {
        var converter = CreateConverter("SS10");

        converter.AddTelegram(100, low: 0x9C, high: 0xFF); // -100
        Assert.Equal(-10.0, converter.GetValue().GetValue<double>());
    }

    [Fact]
    public void Unsigned_0x8000_IsAValue()
    {
        var converter = CreateConverter("US");

        converter.AddTelegram(100, low: 0x00, high: 0x80);
        Assert.Equal(32768, converter.GetValue().GetValue<ushort>());
    }
}
