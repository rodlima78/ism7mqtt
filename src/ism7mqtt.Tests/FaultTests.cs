using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Serialization;
using ism7mqtt.ISM7;
using ism7mqtt.ISM7.Protocol;
using ism7mqtt.ISM7.Xml;
using Xunit;

namespace ism7mqtt.Tests;

public class FaultTests
{
    private static Ism7Config.RunningDevice Cha() =>
        new("CHA", "host", "0x8", "0x3", Enumerable.Empty<ParameterDescriptor>(), Enumerable.Empty<ConverterTemplateBase>(), new Ism7Localizer("DEU"))
        {
            DeviceTemplateId = 270000,
            ReportsFaults = true,
        };

    private static ErrorStateResp Entry(int index, int code, bool active, string? stop = null) =>
        new() { BusAddress = "0x08", Index = index, Code = code, Start = "01.02.2024 08:15:00", Stop = stop, Active = active };

    [Fact]
    public void Texts_AreLookedUpPerDevice()
    {
        Assert.Equal("Kesselfühler defekt", FaultMessageTexts.Default.Get(12, 270000));
        Assert.Equal("WW-Fühler defekt", FaultMessageTexts.Default.Get(14, 270000));
        Assert.Null(FaultMessageTexts.Default.Get(12, 220000));
    }

    [Fact]
    public void Request_SerializesAsSmartsetDoes()
    {
        var request = new TelegramBundleReq
        {
            BundleId = "5", GatewayId = "1", TelegramBundleType = TelegramBundleType.push,
            EStRead = new TelegramBundleReq.ErrorStateRead { Seq = "5", VisibleDeviceAdresses = "0x35,0x08" }
        };
        var writer = new StringWriter();
        new XmlSerializer(typeof(TelegramBundleReq)).Serialize(writer, request);
        Assert.Contains("<erd se=\"5\" li=\"0x35,0x08\" />", writer.ToString());
    }

    [Fact]
    public void Response_Deserializes()
    {
        var xml = "<tbres bn=\"5\" gw=\"1\" st=\"OK\"><ers se=\"5\" ba=\"0x08\" index=\"1\" code=\"12\" start=\"01.02.2024 08:15:00\" stop=\"\" active=\"true\" /></tbres>";
        var resp = (TelegramBundleResp)new XmlSerializer(typeof(TelegramBundleResp)).Deserialize(new StringReader(xml))!;
        var entry = Assert.Single(resp.ErrorStates!);
        Assert.Equal((1, 12, true), (entry.Index, entry.Code, entry.Active));
        Assert.Equal("01.02.2024 08:15:00", entry.Start);
    }

    [Fact]
    public void ActiveFaults_AreTheState_AndTheListIsTheAttributes()
    {
        var cha = Cha();
        Assert.False(cha.HasValue);
        Assert.True(cha.SetFaults(new[] { Entry(2, 14, false, "01.02.2024 09:30:00"), Entry(1, 12, true) }));
        Assert.True(cha.HasValue);
        Assert.Equal("12 Kesselfühler defekt", cha.ActiveFaultText);

        var list = cha.FaultListJson();
        Assert.Equal(1, list["active"]!.GetValue<int>());
        var entries = list["entries"]!.AsArray();
        Assert.Equal(new[] { 1, 2 }, entries.Select(x => x!["index"]!.GetValue<int>()));
        Assert.Equal("WW-Fühler defekt", entries[1]!["text"]!.GetValue<string>());
        Assert.Equal("01.02.2024 09:30:00", entries[1]!["stop"]!.GetValue<string>());

        var messages = cha.MqttMessages.ToDictionary(x => x.Path, x => x.Content);
        Assert.Equal("12 Kesselfühler defekt", messages["Wolf/host/CHA_0x8/Stoermeldung"]);
        Assert.Contains("\"code\":14", messages["Wolf/host/CHA_0x8/Stoermeldungen"]);
    }

    [Fact]
    public void DevicesTheModuleDoesNotMention_ReadKeine_AfterTheFirstAnswer()
    {
        var config = new Ism7Config(TestFixtures.WriteParameterFile("happy-path.json", 1), new Ism7Localizer("DEU"));
        config.AddAllDevices("host").ToList();
        var reporting = config.Devices.Where(x => x.ReportsFaults).ToList();
        Assert.NotEmpty(reporting);
        Assert.All(reporting, x => Assert.Null(x.Faults));

        Assert.True(config.SeedEmptyFaults());
        Assert.All(reporting, x => Assert.Equal(Ism7Config.RunningDevice.NoActiveFault, x.ActiveFaultText));
        Assert.False(config.SeedEmptyFaults());
    }

    [Fact]
    public void NoActiveFault_ReadsKeine_AndRepeatsAreNoChange()
    {
        var cha = Cha();
        Assert.True(cha.SetFaults(new[] { Entry(1, 12, false, "01.02.2024 09:30:00") }));
        Assert.Equal(Ism7Config.RunningDevice.NoActiveFault, cha.ActiveFaultText);
        Assert.False(cha.SetFaults(new[] { Entry(1, 12, false, "01.02.2024 09:30:00") }));
        Assert.True(cha.SetFaults(System.Array.Empty<ErrorStateResp>()));
        Assert.Empty(cha.Faults);
    }

    [Fact]
    public void InvalidCodes_AreDropped_AndAtMostTenKept()
    {
        var cha = Cha();
        var entries = Enumerable.Range(1, 12).Select(i => Entry(i, 12, false)).Append(Entry(20, 0, true)).Append(Entry(21, 300, true));
        cha.SetFaults(entries);
        Assert.Equal(10, cha.Faults.Count);
        Assert.Equal(Ism7Config.RunningDevice.NoActiveFault, cha.ActiveFaultText);
    }
}
