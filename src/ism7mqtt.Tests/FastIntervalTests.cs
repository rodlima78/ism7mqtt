using Xunit;

namespace ism7mqtt.Tests;

public class FastIntervalTests
{
    [Fact]
    public void FastParameters_GetBundlesOfTheirOwn_WithTheFastInterval()
    {
        var path = TestFixtures.WriteParameterFile("fast-parameter.json", 9092);
        try
        {
            var config = new Ism7Config(path, new Ism7Localizer("DEU"));
            config.AddDevice("127.0.0.1", "0x29");
            var bundles = config.GetBundlesForDevice("0x29").ToList();

            Assert.Equal(2, bundles.Count);
            var fast = Assert.Single(bundles, b => config.GetBundleInterval(b.Item1) is not null);
            Assert.Equal(10, config.GetBundleInterval(fast.Item1));
            // 270011 Heizkreispumpe is telegram 12409; it is the only parameter in the fast bundle.
            Assert.All(fast.Item2, ir => Assert.Equal(12409, ir.InfoNumber));
            var slow = Assert.Single(bundles, b => config.GetBundleInterval(b.Item1) is null);
            Assert.DoesNotContain(slow.Item2, ir => ir.InfoNumber == 12409);
            Assert.Contains(slow.Item2, ir => ir.InfoNumber == 12);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutFastParameters_EveryBundleUsesTheGlobalInterval()
    {
        var path = TestFixtures.WriteParameterFile("happy-path.json", 9092);
        try
        {
            var config = new Ism7Config(path, new Ism7Localizer("DEU"));
            config.AddDevice("127.0.0.1", "0x29");
            Assert.All(config.GetBundlesForDevice("0x29"), b => Assert.Null(config.GetBundleInterval(b.Item1)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
