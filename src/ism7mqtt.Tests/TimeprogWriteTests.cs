using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using ism7mqtt.ISM7.Protocol;
using ism7mqtt.ISM7.Xml;
using Xunit;

namespace ism7mqtt.Tests;

public class TimeprogWriteTests
{
    private static Timeprog03F1ConverterTemplate Day() => new() { CTID = 320049, HeatprogNumber = 2, ProgramType = 2, HzkInstance = 0, DayOfWeek = 3 };

    [Fact]
    public void Parse_EncodesLikeSmartset()
    {
        // 05:30 = 330 = 0x014A, 08:00 = 0x01E0, 12:00 = 0x02D0, 13:15 = 795 = 0x031B, 17:00 = 0x03FC, 24:00 stored as
        // 1439 = 0x059F; little endian, the last two slots FFFF.
        var data = Timeprog03F1ConverterTemplate.Parse("05:30-08:00; 12:00-13:15, 17:00-24:00");
        Assert.Equal("4A01E001D0021B03FC039F05FFFFFFFFFFFFFFFF", Convert.ToHexString(data));
    }

    [Theory]
    [InlineData("06:00-22:00")]
    [InlineData("05:30-08:00; 12:00-13:15; 17:00-24:00")]
    [InlineData("00:00-24:00")]
    [InlineData("-")]
    public void Parse_RoundTripsThroughFormat(string value)
    {
        Assert.Equal(value, Timeprog03F1ConverterTemplate.Format(Timeprog03F1ConverterTemplate.Parse(value)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("  ")]
    public void Parse_NoPeriods_ZerosThenFFFF(string value)
    {
        Assert.Equal("000000000000000000000000FFFFFFFFFFFFFFFF", Convert.ToHexString(Timeprog03F1ConverterTemplate.Parse(value)));
    }

    [Theory]
    [InlineData("06:00-05:00")]                                   // ends before it starts
    [InlineData("06:00-06:00")]                                   // empty period
    [InlineData("10:00-12:00; 11:00-13:00")]                      // overlap
    [InlineData("12:00-13:00; 06:00-08:00")]                      // not ascending
    [InlineData("01:00-02:00; 03:00-04:00; 05:00-06:00; 07:00-08:00")] // four periods
    [InlineData("25:00-26:00")]
    [InlineData("06:60-07:00")]
    [InlineData("24:30-24:40")]
    [InlineData("6 to 22")]
    public void Parse_RejectsInvalidDays(string value)
    {
        Assert.Throws<FormatException>(() => Timeprog03F1ConverterTemplate.Parse(value));
    }

    [Theory]
    [InlineData("06:00-22:00", true)]
    [InlineData("05:30-08:00; 12:00-13:15, 17:00-24:00", true)]
    [InlineData("-", true)]
    [InlineData("", true)]
    [InlineData("25:00-26:00", false)]
    [InlineData("01:00-02:00; 03:00-04:00; 05:00-06:00; 07:00-08:00", false)]
    [InlineData("6 to 22", false)]
    public void Pattern_AgreesWithTheParserOnShape(string value, bool valid)
    {
        Assert.Equal(valid, Regex.IsMatch(value, Timeprog03F1ConverterTemplate.WritePattern));
    }

    [Fact]
    public void CreateWrite_AddressesTheDay()
    {
        var write = Day().CreateWrite("06:00-22:00");
        Assert.Equal((2, 2, 0, 3), (write.HeatprogNumber, write.ProgramType, write.HzkInstance, write.DayOfWeek));
        Assert.Equal("6801280500000000000000000000FFFFFFFFFFFF".Length, write.Data.Length);
        Assert.StartsWith("68012805", write.Data);
    }

    [Fact]
    public void Write_SerializesAsSmartsetDoes()
    {
        var write = Day().CreateWrite("06:00-22:00");
        write.Seq = "D;9";
        write.BusAddress = "0x30";
        var request = new TelegramBundleReq { BundleId = "8", GatewayId = "1", AbortOnError = true, TelegramBundleType = TelegramBundleType.write, TimeprogWriteTelegrams = new() { write } };
        var writer = new StringWriter();
        new XmlSerializer(typeof(TelegramBundleReq)).Serialize(writer, request);
        Assert.Contains($"<s03f3wr se=\"D;9\" ba=\"0x30\" hp=\"2\" ty=\"2\" hk=\"0\" d=\"3\" db=\"{write.Data}\" />", writer.ToString());
    }

    [Fact]
    public void Acknowledgement_Deserializes_AndSetsTheValue()
    {
        var data = Convert.ToHexString(Timeprog03F1ConverterTemplate.Parse("06:00-22:00"));
        var xml = $"<tbres bn=\"8\" gw=\"1\" st=\"OK\"><s03f3ac se=\"D;9\" ba=\"0x30\" st=\"OK\" hp=\"2\" ty=\"2\" hk=\"0\" d=\"3\" db=\"{data}\" /></tbres>";
        var resp = (TelegramBundleResp)new XmlSerializer(typeof(TelegramBundleResp)).Deserialize(new StringReader(xml))!;
        var ack = Assert.Single(resp.TimeprogWriteTelegrams!);
        var day = Day();
        Assert.True(day.Matches(ack));
        Assert.True(day.SetData(ack.Data));
        Assert.Equal("06:00-22:00", day.GetValue().ToString());
    }
}
