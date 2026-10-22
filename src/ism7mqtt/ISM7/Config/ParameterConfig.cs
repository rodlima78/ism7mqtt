using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ism7mqtt.ISM7.Config
{
    public class ConfigRoot
    {
        public ushort TcpPort { get; set; } = 9092;

        /// <summary>
        /// Push interval in seconds for the parameters a device lists in <see cref="ConfigDevice.FastParameter"/>; the
        /// rest use the global interval.
        /// </summary>
        public int FastInterval { get; set; } = 10;

        public List<ConfigDevice> Devices { get; set; }
    }

    public class ConfigDevice
    {
        public string ReadBusAddress { get; set; }

        public string WriteBusAddress { get; set; }

        public int DeviceTemplateId { get; set; }

        public List<int> Parameter { get; set; }

        /// <summary>
        /// Parameters read at <see cref="ConfigRoot.FastInterval"/> instead of the global interval: values that switch
        /// (pumps, valves, operating states) and should reach MQTT soon after they change. Subscribed in bundles of
        /// their own; they need not be repeated in <see cref="Parameter"/>.
        /// </summary>
        public List<int> FastParameter { get; set; }
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(ConfigRoot))]
    [JsonSerializable(typeof(JsonObject))]
    [JsonSerializable(typeof(JsonNode))]
    [JsonSerializable(typeof(JsonValue))]
    internal partial class JsonContext : JsonSerializerContext
    {
    }
}
