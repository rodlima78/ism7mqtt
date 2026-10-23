using System;
using System.IO;
using System.Linq;
using ism7mqtt.HomeAssistant;
using ism7mqtt.ISM7;
using Xunit;

namespace ism7mqtt.Tests;

public class WritableParameterTests
{
    private static string WriteConfig(string devices)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, $$"""{"TcpPort":9092,"Devices":[{{devices}}]}""");
        return path;
    }

    // 270069 Funktion Ausgang A1 is read-only in the template; 270015 Ausgang A1 is its read-only state.
    private const string Cha = """{"DeviceTemplateId":270000,"ReadBusAddress":"0x8","WriteBusAddress":"0x3","Parameter":[270069,270015]%W%}""";

    private static Ism7Config Load(string path)
    {
        var config = new Ism7Config(path, new Ism7Localizer("DEU"));
        config.AddAllDevices("host").ToList();
        return config;
    }

    [Fact]
    public void ListedParameter_IsWritten()
    {
        var path = WriteConfig(Cha.Replace("%W%", ""","WritableParameter":[270069]"""));
        try
        {
            var config = Load(path);
            var write = Assert.Single(config.GetWriteRequest("Wolf/host/CHA_0x8", new[] { "Funktion_Ausgang_A1", "text" }, "Zirkulationspumpe 20%"));
            Assert.Equal(13002, write.InfoNumber);
            Assert.Equal("0x01", write.DBLow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnlistedReadOnlyParameter_IsNotWritten()
    {
        var path = WriteConfig(Cha.Replace("%W%", ""","WritableParameter":[270069]"""));
        try
        {
            var config = Load(path);
            Assert.Empty(config.GetWriteRequest("Wolf/host/CHA_0x8", new[] { "Ausgang_A1", "text" }, "Ein"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutTheList_ReadOnlyStaysReadOnly()
    {
        var path = WriteConfig(Cha.Replace("%W%", ""));
        try
        {
            var config = Load(path);
            Assert.Empty(config.GetWriteRequest("Wolf/host/CHA_0x8", new[] { "Funktion_Ausgang_A1", "text" }, "Zirkulationspumpe 20%"));
            var discovery = new HaDiscovery(config, null, "Wolf", new Ism7Localizer("DEU"));
            Assert.DoesNotContain(discovery.GetDiscoveryInfo(), x => x.Path.StartsWith("homeassistant/select/"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ListedParameter_IsDiscoveredWithACommandTopic()
    {
        var path = WriteConfig(Cha.Replace("%W%", ""","WritableParameter":[270069]"""));
        try
        {
            var discovery = new HaDiscovery(Load(path), null, "Wolf", new Ism7Localizer("DEU"));
            var message = Assert.Single(discovery.GetDiscoveryInfo(), x => x.Path.StartsWith("homeassistant/select/"));
            Assert.Contains("270069", message.Path);
            Assert.EndsWith("/set/Funktion_Ausgang_A1/text", message.Content["command_topic"]!.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ListedTime_IsWrittenAsHourAndMinute()
    {
        var path = WriteConfig("""{"DeviceTemplateId":220000,"ReadBusAddress":"0x35","WriteBusAddress":"0x30","Parameter":[220032],"WritableParameter":[220032]}""");
        try
        {
            var config = Load(path);
            var write = Assert.Single(config.GetWriteRequest("Wolf/host/BM-2_0x35", new[] { "Uhrzeit" }, "07:30"));
            Assert.Equal("0x07", write.DBHigh);
            Assert.Equal("0x1E", write.DBLow);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
