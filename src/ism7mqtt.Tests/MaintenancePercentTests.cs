using ism7mqtt.ISM7.Xml;
using Xunit;

namespace ism7mqtt.Tests;

public class MaintenancePercentTests
{
    [Theory]
    [InlineData(NumericConverter16Template.MaintenancePercentCtid, 0x80, 0x80, null)]
    [InlineData(NumericConverter16Template.MaintenancePercentCtid, 42, 0, "42")]
    [InlineData(220104, 0x80, 0x80, "32896")]
    public void NoIntervalIsUnknown_OnlyForTheMaintenancePercentage(int ctid, byte low, byte high, string? expected)
    {
        var converter = new NumericConverter16Template { CTID = ctid, Type = "US", TelegramNr = 5249 };
        converter.AddTelegram(5249, low, high);
        Assert.Equal(expected, converter.GetValue()?.ToString());
    }
}
