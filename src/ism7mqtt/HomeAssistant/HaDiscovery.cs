using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using ism7mqtt.ISM7.Xml;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using ism7mqtt.ISM7.Config;

namespace ism7mqtt.HomeAssistant
{
    public class HaDiscovery
    {
        private readonly Ism7Config _config;
        private readonly IMqttClient _mqttClient;
        private readonly string _discoveryId;
        private readonly Ism7Localizer _localizer;

        public bool EnableDebug { get; set; }

        public MqttQualityOfServiceLevel QosLevel { get; set; }

        public HaDiscovery(Ism7Config config, IMqttClient mqttClient, string discoveryId, Ism7Localizer localizer)
        {
            _config = config;
            _mqttClient = mqttClient;
            _discoveryId = discoveryId;
            _localizer = localizer;
        }

        public async Task PublishDiscoveryInfo(CancellationToken cancellationToken)
        {
            if (EnableDebug)
            {
                Console.WriteLine($"Publishing HA Discovery info for ID {_discoveryId}");
            }
            foreach (var message in GetDiscoveryInfo())
            {
                var data = JsonSerializer.Serialize(message.Content, JsonContext.Default.JsonObject);
                var builder = new MqttApplicationMessageBuilder()
                    .WithTopic(message.Path)
                    .WithPayload(data)
                    .WithQualityOfServiceLevel(QosLevel);
                var payload = builder
                    .Build();
                await _mqttClient.PublishAsync(payload, cancellationToken);
            }
        }

        public IEnumerable<JsonMessage> GetDiscoveryInfo()
        {
            return _config.Devices.SelectMany(GetDiscoveryInfo);
        }

        private string LaunderHomeassistantId(string id) {
            id = id.Replace("ä", "ae")
                .Replace("ö", "oe")
                .Replace("ü", "ue")
                .Replace("Ä", "Ae")
                .Replace("Ö", "Oe")
                .Replace("Ü", "Ue")
                .Replace("ß", "ss")
                .Replace(' ', '_');
            return Regex.Replace(id, "[^a-zA-Z0-9_ ]+", String.Empty, RegexOptions.Compiled);
        }

        private string ToHaObjectId(Ism7Config.RunningDevice device, Ism7Config.RunningParameter parameter)
        {
            var objectId = $"{_discoveryId}_{device.Name}_{device.WriteAddress}_{parameter.Descriptor.PTID}_{parameter.Name}";
            return LaunderHomeassistantId(objectId);
        }

        private IEnumerable<JsonMessage> GetDiscoveryInfo(Ism7Config.RunningDevice device)
        {
            List<JsonMessage> result = new List<JsonMessage>();
            foreach (var parameter in device.Parameters)
            {
                var descriptor = parameter.Descriptor;
                // A time programme day is a text: its value is the day's periods, and writing the same text sets them.
                var isTimeprog = parameter.Timeprog is not null;
                var type = isTimeprog
                    ? (parameter.IsWritable ? "text" : "sensor")
                    : GetHomeAssistantType(descriptor, parameter.IsWritable);
                if (type == null) continue;
                if ((descriptor.ControlType == "DaySwitchTimes" && !isTimeprog) || descriptor.ControlType.Contains("NO_DISPLAY")) continue;
                var uniqueId = ToHaObjectId(device, parameter);

                // Handle parameters with name-duplicates - their ID will be part of the topic
                string deduplicator = "";
                string deduplicatorLabel = "";
                if (parameter.IsDuplicate)
                {
                    deduplicator = $"/{descriptor.PTID}";
                    deduplicatorLabel = $"_{descriptor.PTID}";
                }
                
                string discoveryTopic = $"homeassistant/{type}/{uniqueId}/config";
                var message = new JsonObject();
                message.Add("unique_id", uniqueId);

                var discoveryTopicSSuffix = GetDiscoveryTopicSuffix(descriptor);
                string stateTopic = $"{device.MqttTopic}/{parameter.MqttName}{deduplicator}{discoveryTopicSSuffix}";
                message.Add("state_topic", stateTopic);

                if (parameter.IsWritable)
                {
                    string commandTopic = $"{device.MqttTopic}/set/{parameter.MqttName}{deduplicator}{discoveryTopicSSuffix}";
                    message.Add("command_topic", commandTopic);
                }

                message.Add("name", _localizer[parameter.Name]);
                message.Add("default_entity_id", $"{(type ?? "unknown")}.{uniqueId}");

                if (isTimeprog)
                {
                    message.Add("icon", "mdi:calendar-clock");
                    // The day is published as a JSON string; the raw payload would carry its quotes into the
                    // entity's state, where they fail the pattern.
                    message.Add("value_template", "{{ value_json }}");
                    if (parameter.IsWritable)
                    {
                        message.Add("pattern", Timeprog03F1ConverterTemplate.WritePattern);
                        message.Add("max", 64);
                    }
                }
                else
                {
                    foreach (var (key, value) in GetDiscoveryProperties(descriptor, parameter.IsWritable))
                    {
                        message.Add(key, value);
                    }
                }

                // Try to guess a suitable icon if none given
                if (!message.ContainsKey("icon"))
                {
                    if (descriptor.Name.ToLower().Contains("brenner"))
                        message.Add("icon", "mdi:fire");
                    else if (descriptor.Name.ToLower().Contains("solar"))
                        message.Add("icon", "mdi:solar-panel");
                    else if (descriptor.Name.ToLower().Contains("ventil"))
                        message.Add("icon", "mdi:pipe-valve");
                    else if (descriptor.Name.ToLower().Contains("heizung"))
                        message.Add("icon", "mdi:radiator");
                    else if (descriptor.Name.ToLower().Contains("pumpe"))
                        message.Add("icon", "mdi:pump");
                    if (descriptor.Name.ToLower().Contains("druck"))
                        message.Add("icon", "mdi:gauge");
                }

                message.Add("device", GetDiscoveryDeviceInfo(device));
                result.Add(new JsonMessage(discoveryTopic, message));
            }
            if (device.ReportsFaults)
            {
                result.Add(GetFaultDiscoveryInfo(device));
            }
            return result;
        }

        /// <summary>
        /// The device's fault list: the state is the active faults ("12 Kesselfühler defekt", or "Keine"), the
        /// attributes the whole list with each fault's code, text, start, end and whether it is active.
        /// </summary>
        private JsonMessage GetFaultDiscoveryInfo(Ism7Config.RunningDevice device)
        {
            var uniqueId = LaunderHomeassistantId($"{_discoveryId}_{device.Name}_{device.WriteAddress}_{Ism7Config.RunningDevice.FaultTopic}");
            var message = new JsonObject
            {
                { "unique_id", uniqueId },
                { "state_topic", $"{device.MqttTopic}/{Ism7Config.RunningDevice.FaultTopic}" },
                { "json_attributes_topic", $"{device.MqttTopic}/{Ism7Config.RunningDevice.FaultListTopic}" },
                { "name", "Störmeldung" },
                { "default_entity_id", $"sensor.{uniqueId}" },
                { "icon", "mdi:alert-circle-outline" },
                { "device", GetDiscoveryDeviceInfo(device) },
            };
            return new JsonMessage($"homeassistant/sensor/{uniqueId}/config", message);
        }

        private JsonObject GetDiscoveryDeviceInfo(Ism7Config.RunningDevice device)
        {
            return new JsonObject
            {
                { "configuration_url", $"http://{device.IP}/" },
                { "manufacturer", "Wolf" },
                { "model", device.Name },
                { "name", $"{_discoveryId} {device.Name}" },
                {
                    "connections", new JsonArray
                    (
                        new JsonArray
                        (
                            "ip_dev",
                            $"{device.IP}_{device.Name}"
                        )
                    )
                }
            };
        }

        private string GetDiscoveryTopicSuffix(ParameterDescriptor descriptor)
        {
            if (descriptor is ListParameterDescriptor list)
            {
                return "/text";
            }
            else
            {
                return String.Empty;
            }
        }

        private string GetHomeAssistantType(ParameterDescriptor descriptor, bool writable)
        {
            switch (descriptor)
            {
                case ListParameterDescriptor list:
                    // Try to auto-detect switch and select vs sensor and binary_sensor
                    if (writable)
                    {
                        return list.IsBoolean ? "switch" : "select";
                    }

                    return list.IsBoolean ? "binary_sensor" : "sensor";
                case NumericParameterDescriptor:
                    return writable ? "number" : "sensor";
                case TextParameterDescriptor:
                    return writable ? "text" : "sensor";
                case OtherParameterDescriptor:
                    return writable ? "text" : "sensor";
                default:
                    return null;
            }
        }

        private IEnumerable<(string, JsonNode)> GetDiscoveryProperties(ParameterDescriptor descriptor, bool writable)
        {
            switch (descriptor)
            {
                case NumericParameterDescriptor numeric:
                    if (writable)
                    {
                        if (numeric.MinValueCondition != null)
                        {
                            if (Double.TryParse(numeric.MinValueCondition, NumberStyles.Number, CultureInfo.InvariantCulture, out var min))
                            {
                                yield return("min", min);
                            }
                            else if (EnableDebug)
                            {
                                Console.WriteLine($"Cannot parse MinValueCondition '{numeric.MinValueCondition}' for PTID {descriptor.PTID}");
                            }
                        }
                        if (numeric.MaxValueCondition != null)
                        {
                            if (Double.TryParse(numeric.MaxValueCondition, NumberStyles.Number, CultureInfo.InvariantCulture, out var max))
                            {
                                yield return ("max", max);
                            }
                            else if (EnableDebug)
                            {
                                Console.WriteLine($"Cannot parse MaxValueCondition '{numeric.MaxValueCondition}' for PTID {descriptor.PTID}");
                            }
                        }
                        if (numeric.StepWidth != null)
                            yield return ("step", numeric.StepWidth);
                    }
                    if (numeric.UnitName == null)
                    {
                        var stateClass = HaStateClass.ForUnitless(numeric.Name);
                        if (stateClass != null)
                            yield return ("state_class", stateClass);
                    }
                    else
                    {
                        string unit = _localizer[numeric.UnitName];

                        switch(numeric.UnitName)
                        {
                        case "°C":
                            yield return ("icon", "mdi:thermometer");
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "temperature");
                            break;

                        case "%":
                            yield return ("state_class", "measurement");
                            break;

                        case "%rH":
                        case "%r.H.":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "humidity");
                            unit = "%";
                            break;

                        case "Wh":
                        case "kWh":
                        case "GWh":
                            var energyStateClass = HaStateClass.ForEnergy(numeric.Name);
                            if (energyStateClass != null)
                                yield return ("state_class", energyStateClass);
                            yield return ("device_class", "energy");
                            break;

                        case "W":
                        case "kW":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "power");
                            break;

                        case "Hz":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "frequency");
                            break;

                        case "l/min":
                        case "L/min":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "volume_flow_rate");
                            unit = "L/min";
                            break;

                        case "m³/h":
                        case "m3/h":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "volume_flow_rate");
                            unit = "m³/h";
                            break;

                        case "ppm":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "carbon_dioxide");
                            break;

                        case "U/min":
                        case "UPM":
                            yield return ("state_class", "measurement");
                            unit = "rpm";
                            break;

                        case "sec":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "duration");
                            unit = "s";
                            break;

                        case "ms":
                        case "s":
                        case "min":
                        case "h":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "duration");
                            break;

                        case "Std":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "duration");
                            unit = "h";
                            break;

                        case "Tage":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "duration");
                            unit = "d";
                            break;

                        case "A":
                        case "mA":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "current");
                            break;

                        case "bar":
                        case "Pa":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "pressure");
                            break;

                        case "V":
                            yield return ("state_class", "measurement");
                            yield return ("device_class", "voltage");
                            break;
                        }

                        yield return ("unit_of_measurement", unit);
                    }
                    break;
                case ListParameterDescriptor list:
                    if (list.IsBoolean)
                    {
                        foreach (var option in list.Options)
                        {
                            var localizedValue = _localizer[option.Value];
                            if (option.Value is "Ein" or "Aktiviert")
                                yield return ("payload_on", localizedValue);
                            else if (option.Value is "Aus" or "Deaktiviert")
                                yield return ("payload_off", localizedValue);
                        }
                    }
                    else
                    {
                        var options = new JsonArray();
                        foreach (var value in list.Options)
                        {
                            options.Add((JsonNode)_localizer[value.Value]);
                        }
                        yield return ("options", options);
                        yield return ("device_class", "enum");

                    }
                    break;
                default:
                    yield break;
            }
        }
    }
}
