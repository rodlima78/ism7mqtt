using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using ism7mqtt.ISM7.Protocol;
using ism7mqtt.ISM7.Xml;
using Xunit;

namespace ism7mqtt.Tests;

public class TimeprogTests
{
    // Five periods of start/end minutes, little endian; FFFF = not set.
    private static string Hex(params (int start, int end)[] periods)
    {
        var bytes = Enumerable.Repeat((byte)0xFF, Timeprog03F1ConverterTemplate.DataLength).ToArray();
        for (int i = 0; i < periods.Length; i++)
        {
            bytes[i * 4] = (byte)(periods[i].start & 0xFF);
            bytes[i * 4 + 1] = (byte)(periods[i].start >> 8);
            bytes[i * 4 + 2] = (byte)(periods[i].end & 0xFF);
            bytes[i * 4 + 3] = (byte)(periods[i].end >> 8);
        }
        return Convert.ToHexString(bytes);
    }

    private static Timeprog03F1ConverterTemplate Day() => new() { CTID = 320042, HeatprogNumber = 1, ProgramType = 2, HzkInstance = 0, DayOfWeek = 1 };

    [Fact]
    public void OnePeriod()
    {
        var day = Day();
        Assert.False(day.HasValue);
        Assert.True(day.SetData(Hex((6 * 60, 22 * 60))));
        Assert.True(day.HasValue);
        Assert.Equal("06:00-22:00", day.GetValue().ToString());
    }

    [Fact]
    public void ThreePeriods_EndOfDay()
    {
        var day = Day();
        day.SetData(Hex((5 * 60 + 30, 8 * 60), (12 * 60, 13 * 60 + 15), (17 * 60, 1439)));
        Assert.Equal("05:30-08:00; 12:00-13:15; 17:00-24:00", day.GetValue().ToString());
    }

    [Fact]
    public void NoPeriods()
    {
        var day = Day();
        day.SetData(Hex());
        Assert.Equal(Timeprog03F1ConverterTemplate.NoPeriods, day.GetValue().ToString());
        day.SetData(Hex((0, 0), (0, 0), (0, 0)));
        Assert.Equal(Timeprog03F1ConverterTemplate.NoPeriods, day.GetValue().ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0102")]
    [InlineData("not hex")]
    public void MalformedData_KeepsLastValue(string data)
    {
        var day = Day();
        day.SetData(Hex((6 * 60, 22 * 60)));
        Assert.False(day.SetData(data));
        Assert.Equal("06:00-22:00", day.GetValue().ToString());
    }

    [Fact]
    public void MatchesOnlyItsOwnDay()
    {
        var day = Day();
        Assert.True(day.Matches(new TimeprogReadResp { HeatprogNumber = 1, ProgramType = 2, HzkInstance = 0, DayOfWeek = 1 }));
        Assert.False(day.Matches(new TimeprogReadResp { HeatprogNumber = 1, ProgramType = 2, HzkInstance = 0, DayOfWeek = 2 }));
        Assert.False(day.Matches(new TimeprogReadResp { HeatprogNumber = 2, ProgramType = 2, HzkInstance = 0, DayOfWeek = 1 }));
        Assert.False(day.Matches(new TimeprogReadResp { HeatprogNumber = 1, ProgramType = 1, HzkInstance = 0, DayOfWeek = 1 }));
    }

    [Fact]
    public void Clone_KeepsTheDayAndStartsEmpty()
    {
        var day = Day();
        day.SetData(Hex((6 * 60, 22 * 60)));
        var clone = (Timeprog03F1ConverterTemplate)day.Clone();
        Assert.Equal((1, 2, 0, (byte)1), (clone.HeatprogNumber, clone.ProgramType, clone.HzkInstance, clone.DayOfWeek));
        Assert.False(clone.HasValue);
        Assert.Empty(day.GetWrite("06:00-22:00"));
    }

    [Fact]
    public void Request_SerializesAsSmartsetDoes()
    {
        var request = new TelegramBundleReq
        {
            BundleId = "7",
            GatewayId = "1",
            TelegramBundleType = TelegramBundleType.pull,
            TimeprogReadTelegrams = new() { new TimeprogRead { Seq = "D;3", BusAddress = "0x35", HeatprogNumber = 1, ProgramType = 2, HzkInstance = 0, DayOfWeek = 1 } }
        };
        var writer = new StringWriter();
        new XmlSerializer(typeof(TelegramBundleReq)).Serialize(writer, request);
        var xml = writer.ToString();
        Assert.Contains("<s03f1rd se=\"D;3\" ba=\"0x35\" hp=\"1\" ty=\"2\" hk=\"0\" d=\"1\" />", xml);
        Assert.DoesNotContain("<ird", xml);
    }

    [Fact]
    public void Response_Deserializes()
    {
        var data = Hex((6 * 60, 22 * 60));
        var xml = $"<tbres bn=\"7\" gw=\"1\" st=\"OK\"><s03f1rs se=\"D;3\" ba=\"0x35\" st=\"OK\" hp=\"1\" ty=\"2\" hk=\"0\" d=\"1\" db=\"{data}\" /></tbres>";
        var resp = (TelegramBundleResp)new XmlSerializer(typeof(TelegramBundleResp)).Deserialize(new StringReader(xml))!;
        var day = Assert.Single(resp.TimeprogTelegrams!);
        Assert.Equal(TelegrResponseState.OK, day.State);
        Assert.Equal(data, day.Data);
        Assert.True(Day().Matches(day));
    }
}
