using ism7mqtt.HomeAssistant;
using Xunit;

namespace ism7mqtt.Tests;

public class HaStateClassTests
{
    [Theory]
    [InlineData("Energiemenge HZ", "total_increasing")]
    [InlineData("Energiemenge WW", "total_increasing")]
    [InlineData("Erzeugte Wärmemenge aktueller Tag", "total_increasing")]
    [InlineData("Erzeugte Wärmemenge aktueller Monat", "total_increasing")]
    [InlineData("Erzeugte Wärmemenge aktuelles Jahr", "total_increasing")]
    [InlineData("Verbrauch aktueller Tag", "total_increasing")]
    [InlineData("Erzeugte Wärmemenge Vortag", null)]
    [InlineData("Erzeugte Wärmemenge Vorjahr", null)]
    [InlineData("Erzeugte Wärmemenge Vorvorjahr", null)]
    [InlineData("Verbrauch Vortag", null)]
    [InlineData("Energie VT (th.)", null)]
    [InlineData("Energie VJ (el.)", null)]
    public void Energy(string name, string? expected)
    {
        Assert.Equal(expected, HaStateClass.ForEnergy(name));
    }

    [Theory]
    [InlineData("EEV HZ", "measurement")]
    [InlineData("EEV K", "measurement")]
    [InlineData("Verdichterstarts", "total_increasing")]
    [InlineData("Anzahl Netz Ein", "total_increasing")]
    [InlineData("HCM-4 Firmware", null)]
    public void Unitless(string name, string? expected)
    {
        Assert.Equal(expected, HaStateClass.ForUnitless(name));
    }
}
