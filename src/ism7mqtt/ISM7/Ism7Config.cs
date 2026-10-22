using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Serialization;
using ism7mqtt.ISM7.Config;
using ism7mqtt.ISM7.Protocol;
using ism7mqtt.ISM7.Xml;
using ism7mqtt.ISM7;

namespace ism7mqtt
{
    public class Ism7Config
    {
        private readonly Ism7Localizer _localizer;
        private readonly IReadOnlyList<DeviceTemplate> _deviceTemplates;
        private readonly IReadOnlyList<ConverterTemplateBase> _converterTemplates;
        private readonly IReadOnlyList<ParameterDescriptor> _parameterTemplates;
        private readonly IDictionary<byte, List<RunningDevice>> _devices;
        private readonly Dictionary<string, List<InfoRead>> _bundles = new();
        private readonly HashSet<string> _fastBundles = new();
        private readonly Dictionary<byte, HashSet<int>> _fastParameters = new();
        private readonly ConfigRoot _config;
        private readonly string _parameterXmlOverridePath;

        public Ism7Config(string filename, Ism7Localizer localizer, string parameterXmlOverridePath = null)
        {
            _localizer = localizer;
            _parameterXmlOverridePath = parameterXmlOverridePath;
            _deviceTemplates = LoadDeviceTemplates();
            _converterTemplates = LoadConverterTemplates();
            _parameterTemplates = LoadParameterTemplates();
            if (File.Exists(filename))
            {
                _config = JsonSerializer.Deserialize(File.ReadAllText(filename), JsonContext.Default.ConfigRoot);
            }
            _devices = new Dictionary<byte, List<RunningDevice>>();
        }

        public int TcpPort => _config.TcpPort;

        private List<DeviceTemplate> LoadDeviceTemplates()
        {
            var serializer = new XmlSerializer(typeof(DeviceTemplateConfig));
            using (var reader = new StringReader(Resources.DeviceTemplates))
            {
                var config = (DeviceTemplateConfig)serializer.Deserialize(reader);
                return config.DeviceTemplates;
            }
        }

        private List<ConverterTemplateBase> LoadConverterTemplates()
        {
            var serializer = new XmlSerializer(typeof(ConverterTemplateConfig));
            using (var reader = new StringReader(Resources.ConverterTemplates))
            {
                var converter = (ConverterTemplateConfig)serializer.Deserialize(reader);
                return converter.ConverterTemplates;
            }
        }

        private List<ParameterDescriptor> LoadParameterTemplates()
        {
            var serializer = new XmlSerializer(typeof(ParameterTemplateConfig));
            var xml = String.IsNullOrEmpty(_parameterXmlOverridePath)
                ? Resources.ParameterTemplates
                : File.ReadAllText(_parameterXmlOverridePath);
            using (var reader = new StringReader(xml))
            {
                var converter = (ParameterTemplateConfig)serializer.Deserialize(reader);
                return converter.ParameterList;
            }
        }

        public IEnumerable<string> AddAllDevices(string ip)
        {
            foreach (var ba in _config.Devices.Select(x=>x.ReadBusAddress).Distinct())
            {
                AddDevice(ip, ba);
                yield return ba;
            }
        }

        public void AddDevice(string ip, string ba)
        {
            if (!_devices.TryGetValue(Converter.FromHex(ba), out var devices))
            {
                devices = new List<RunningDevice>();
                _devices.Add(Converter.FromHex(ba), devices);
            }
            foreach (var configDevice in _config.Devices.Where(x => x.ReadBusAddress == ba))
            {
                var device = _deviceTemplates.First(x => x.DTID == configDevice.DeviceTemplateId);
                var fast = (configDevice.FastParameter ?? new List<int>()).ToHashSet();
                var tids = (configDevice.Parameter ?? new List<int>()).Concat(fast).ToHashSet();
                if (!_fastParameters.TryGetValue(Converter.FromHex(ba), out var fastOfBus))
                {
                    fastOfBus = new HashSet<int>();
                    _fastParameters.Add(Converter.FromHex(ba), fastOfBus);
                }
                fastOfBus.UnionWith(fast);
                devices.Add(new RunningDevice(device.Name, ip, configDevice.ReadBusAddress, configDevice.WriteBusAddress, _parameterTemplates.Where(x => tids.Contains(x.PTID)), _converterTemplates.Where(x => tids.Contains(x.CTID)), _localizer)
                {
                    DeviceTemplateId = device.DTID,
                    // The module keeps one fault list per bus address; the first device configured there owns it.
                    ReportsFaults = devices.Count == 0,
                });
            }
        }

        public IEnumerable<(string, List<InfoRead>)> GetBundlesForDevice(string ba)
        {
            var devices = _devices[Converter.FromHex(ba)];
            _fastParameters.TryGetValue(Converter.FromHex(ba), out var fast);
            var parameters = devices.SelectMany(x => x.Parameters)
                .Where(x => x.InfoReadCount > 0)
                .ToList();
            // The fast parameters go in bundles of their own, so their bundles can be pushed at the fast interval.
            foreach (var (isFast, group) in new[] { (false, parameters.Where(x => fast is null || !fast.Contains(x.PTID))),
                                                    (true, parameters.Where(x => fast is not null && fast.Contains(x.PTID))) })
            {
                foreach (var bundle in SplitIntoBundles(group))
                {
                    var bundleId = IdGenerator.GetNextBundleIdString();
                    _bundles.Add(bundleId, bundle);
                    if (isFast) _fastBundles.Add(bundleId);
                    yield return (bundleId, bundle);
                }
            }
        }

        private static List<List<InfoRead>> SplitIntoBundles(IEnumerable<RunningParameter> parameters)
        {
            var bundles = new List<List<InfoRead>>();
            //https://github.com/zivillian/ism7mqtt/issues/177#issuecomment-3852657064
            //split into bundles of 20 info reads, but keep all info reads of a parameter in one bundle
            foreach (var parameter in parameters.OrderByDescending(x => x.InfoReadCount))
            {
                var bundle = bundles.FirstOrDefault(x => x.Count <= 20 - parameter.InfoReadCount);
                if (bundle is null)
                {
                    bundles.Add(parameter.InfoReads.ToList());
                }
                else
                {
                    bundle.AddRange(parameter.InfoReads);
                }
            }
            return bundles;
        }

        /// <summary>The push interval of a bundle made of fast parameters, or null for the global interval.</summary>
        public int? GetBundleInterval(string bundleId)
        {
            return _fastBundles.Contains(bundleId) ? _config.FastInterval : null;
        }

        public List<InfoRead> GetBundle(string bundleId)
        {
            return _bundles[bundleId];
        }

        /// <summary>The ISM7 answers at most seven time programme reads per bundle.</summary>
        public const int TimeprogBundleSize = 7;

        public IEnumerable<List<TimeprogRead>> GetTimeprogBundlesForDevice(string ba)
        {
            var reads = _devices[Converter.FromHex(ba)]
                .SelectMany(x => x.Parameters)
                .Select(x => x.Timeprog)
                .Where(x => x is not null)
                .Select(x => x.CreateRead())
                .OrderBy(x => x.ProgramType).ThenBy(x => x.HzkInstance).ThenBy(x => x.HeatprogNumber).ThenBy(x => x.DayOfWeek)
                .ToList();
            for (int i = 0; i < reads.Count; i += TimeprogBundleSize)
            {
                yield return reads.Skip(i).Take(TimeprogBundleSize).ToList();
            }
        }

        public bool ProcessData(IEnumerable<TimeprogReadResp> data)
        {
            var changed = false;
            foreach (var value in data)
            {
                if (!_devices.TryGetValue(Converter.FromHex(value.BusAddress), out var devices)) continue;
                foreach (var timeprog in devices.SelectMany(x => x.Parameters).Select(x => x.Timeprog).Where(x => x is not null))
                {
                    if (timeprog.Matches(value) && timeprog.SetData(value.Data))
                        changed = true;
                }
            }
            return changed;
        }

        public bool ProcessData(IEnumerable<InfonumberReadResp> data)
        {
            foreach (var value in data)
            {
                var devices = _devices[Converter.FromHex(value.BusAddress)];
                foreach (var device in devices)
                {
                    device.ProcessReadDatapoint(value.InfoNumber, value.ServiceNumber, Converter.FromHex(value.DBLow), Converter.FromHex(value.DBHigh));   
                }
            }
            return _devices.Values.SelectMany(x => x).Any(x => x.HasValue);
        }

        public bool ProcessData(IEnumerable<InfonumberWriteResp> data)
        {
            foreach (var value in data)
            {
                var devices = _devices.Values.SelectMany(x => x)
                    .Where(x => Converter.FromHex(x.WriteAddress) == Converter.FromHex(value.BusAddress))
                    .ToList();
                foreach (var device in devices)
                {
                    device.ProcessWriteDatapoint(value.InfoNumber, value.ServiceNumber, Converter.FromHex(value.DBLow), Converter.FromHex(value.DBHigh));   
                }
            }
            return _devices.Values.SelectMany(x => x).Any(x => x.HasValue);
        }

        public IEnumerable<InfoWrite> GetWriteRequest(string mqttTopic, ReadOnlyMemory<string> propertyParts, string value)
        {
            foreach (var device in _devices.Values.SelectMany(x => x))
            {
                if (device.MqttTopic == mqttTopic)
                {
                    return device.GetWriteRequest(propertyParts, value);
                }
            }
            return Array.Empty<InfoWrite>();
        }

        public IEnumerable<InfoWrite> GetWriteRequest(string mqttTopic, JsonObject data)
        {
            foreach (var device in _devices.Values.SelectMany(x => x))
            {
                if (device.MqttTopic == mqttTopic)
                {
                    return device.GetWriteRequest(data);
                }
            }
            return Array.Empty<InfoWrite>();
        }

        /// <exception cref="FormatException">The value is not a valid day.</exception>
        public IEnumerable<TimeprogWrite> GetTimeprogWriteRequest(string mqttTopic, ReadOnlyMemory<string> propertyParts, string value)
        {
            return _devices.Values.SelectMany(x => x)
                .Where(x => x.MqttTopic == mqttTopic)
                .SelectMany(x => x.GetTimeprogWriteRequest(propertyParts, value))
                .ToList();
        }

        /// <exception cref="FormatException">A value is not a valid day.</exception>
        public IEnumerable<TimeprogWrite> GetTimeprogWriteRequest(string mqttTopic, JsonObject data)
        {
            return _devices.Values.SelectMany(x => x)
                .Where(x => x.MqttTopic == mqttTopic)
                .SelectMany(x => x.GetTimeprogWriteRequest(data))
                .ToList();
        }

        /// <summary>
        /// Takes the acknowledged writes as the days' new values (an acknowledgement carries the data written) and
        /// returns, per read bus address, the reads that confirm what the BM-2 actually stored.
        /// </summary>
        public (bool changed, List<(string busAddress, TimeprogRead read)> confirm) ProcessData(IEnumerable<TimeprogWriteResp> data)
        {
            var changed = false;
            var confirm = new List<(string, TimeprogRead)>();
            foreach (var value in data)
            {
                foreach (var (ba, devices) in _devices)
                {
                    foreach (var device in devices.Where(x => Converter.FromHex(x.WriteAddress) == Converter.FromHex(value.BusAddress)))
                    {
                        foreach (var timeprog in device.Parameters.Select(x => x.Timeprog).Where(x => x is not null && x.Matches(value)))
                        {
                            if (timeprog.SetData(value.Data)) changed = true;
                            confirm.Add(($"0x{ba:X2}", timeprog.CreateRead()));
                        }
                    }
                }
            }
            return (changed, confirm);
        }

        /// <summary>The bus addresses whose fault lists are read, as the module expects them ("0x08").</summary>
        public IEnumerable<string> ErrorStateAddresses => _devices
            .Where(x => x.Value.Any(d => d.ReportsFaults))
            .Select(x => $"0x{x.Key:X2}");

        /// <summary>Replaces the fault list of every bus address the response reports on.</summary>
        public bool ProcessData(IEnumerable<ErrorStateResp> data)
        {
            var changed = false;
            foreach (var group in data.GroupBy(x => Converter.FromHex(x.BusAddress)))
            {
                if (!_devices.TryGetValue(group.Key, out var devices)) continue;
                foreach (var device in devices.Where(x => x.ReportsFaults))
                {
                    if (device.SetFaults(group)) changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// The module lists only the devices that have entries, so after its first answer a device it did not
        /// mention has none: give it an empty list, which reads as no active fault, instead of no value at all.
        /// </summary>
        public bool SeedEmptyFaults()
        {
            var changed = false;
            foreach (var device in Devices.Where(x => x.ReportsFaults && x.Faults is null))
            {
                if (device.SetFaults(Enumerable.Empty<ErrorStateResp>())) changed = true;
            }
            return changed;
        }

        public IEnumerable<RunningDevice> Devices => _devices.SelectMany(x => x.Value);

        public IEnumerable<JsonMessage> JsonMessages => _devices.Values
            .SelectMany(x => x)
            .Where(x => x.HasValue)
            .Select(x => x.JsonMessage);

        public IEnumerable<MqttMessage> MqttMessages => _devices.Values
            .SelectMany(x => x)
            .Where(x => x.HasValue)
            .SelectMany(x => x.MqttMessages);

        public class RunningDevice
        {
            private readonly List<RunningParameter> _parameter;

            public RunningDevice(string name, string ip, string readBusAddress, string writeBusAddress, IEnumerable<ParameterDescriptor> parameter, IEnumerable<ConverterTemplateBase> converter, Ism7Localizer localizer)
            {
                Name = name;
                IP = ip;

                WriteAddress = writeBusAddress ?? $"0x{(Converter.FromHex(readBusAddress) - 5):X2}";
                MqttTopic = $"Wolf/{ip}/{name}_{readBusAddress}";

                _parameter = new List<RunningParameter>();
                foreach (var descriptor in parameter)
                {
                    var conv = converter.FirstOrDefault(x=>x.CTID == descriptor.PTID);
                    if (conv != null) _parameter.Add(new RunningParameter(descriptor, conv, localizer));
                }

                EnsureUniqueParameterNames();
            }

            public string MqttTopic { get; }

            public string Name { get; }

            public string IP { get; }

            public string WriteAddress { get; }

            public bool HasValue => _parameter.Any(x => x.HasValue) || _faults is not null;

            public int DeviceTemplateId { get; init; }

            /// <summary>Whether this device publishes the fault list of its bus address.</summary>
            public bool ReportsFaults { get; init; }

            public const string FaultTopic = "Stoermeldung";
            public const string FaultListTopic = "Stoermeldungen";
            public const string NoActiveFault = "Keine";

            /// <summary>The module reports at most ten entries per device; codes outside 1-255 are not faults.</summary>
            private const int MaxFaults = 10;

            private List<FaultEntry> _faults;

            public record FaultEntry(int Index, int Code, string Text, string Start, string Stop, bool Active)
            {
                public string Label => Text is null ? Code.ToString() : $"{Code} {Text}";
            }

            public IReadOnlyList<FaultEntry> Faults => _faults;

            public bool SetFaults(IEnumerable<ErrorStateResp> entries)
            {
                var faults = entries
                    .Where(x => x.Code > 0 && x.Code <= 255)
                    .OrderBy(x => x.Index)
                    .Take(MaxFaults)
                    .Select(x => new FaultEntry(x.Index, x.Code, FaultMessageTexts.Default.Get(x.Code, DeviceTemplateId), x.Start, x.Stop, x.Active))
                    .ToList();
                if (_faults is not null && _faults.SequenceEqual(faults)) return false;
                _faults = faults;
                return true;
            }

            /// <summary>The active faults, e.g. "12 Kesselfühler defekt", or <see cref="NoActiveFault"/>.</summary>
            public string ActiveFaultText
            {
                get
                {
                    var active = _faults?.Where(x => x.Active).Select(x => x.Label).ToList();
                    return active is null || active.Count == 0 ? NoActiveFault : String.Join("; ", active);
                }
            }

            public JsonObject FaultListJson()
            {
                var entries = new JsonArray();
                foreach (var fault in _faults ?? new List<FaultEntry>())
                {
                    entries.Add(new JsonObject
                    {
                        ["index"] = fault.Index,
                        ["code"] = fault.Code,
                        ["text"] = fault.Text,
                        ["start"] = fault.Start,
                        ["stop"] = fault.Stop,
                        ["active"] = fault.Active,
                    });
                }
                return new JsonObject
                {
                    ["active"] = _faults?.Count(x => x.Active) ?? 0,
                    ["entries"] = entries,
                };
            }

            private void EnsureUniqueParameterNames()
            {
                var duplicates = _parameter
                    .GroupBy(x => x.Name)
                    .Where(x => x.Count() > 1)
                    .ToList();
                foreach (var duplicate in duplicates.SelectMany(x=>x))
                {
                    duplicate.IsDuplicate = true;
                }
            }

            public IEnumerable<InfoRead> InfoReads => _parameter.SelectMany(x => x.InfoReads);

            public IEnumerable<RunningParameter> WritableParameters()
            {
                return _parameter.Where(x => x.IsWritable);
            }

            public IEnumerable<RunningParameter> Parameters => _parameter;

            public void ProcessReadDatapoint(ushort telegram, int? service, byte low, byte high)
            {
                ProcessDatapoint(telegram, service, low, high, false);
            }

            public void ProcessWriteDatapoint(ushort telegram, int? service, byte low, byte high)
            {
                ProcessDatapoint(telegram, service, low, high, true);
            }

            private void ProcessDatapoint(ushort telegram, int? service, byte low, byte high, bool write)
            {
                foreach (var parameter in _parameter)
                {
                    parameter.ProcessDatapoint(telegram, service, low, high, write);
                }
            }

            public IEnumerable<InfoWrite> GetWriteRequest(ReadOnlyMemory<string> propertyParts, string value)
            {
                var results = WritableParameters().SelectMany(x => x.GetWriteRequest(propertyParts, value));
                foreach (var result in results)
                {
                    result.BusAddress = WriteAddress;
                    result.Seq = "";
                    yield return result;
                }
            }

            public IEnumerable<InfoWrite> GetWriteRequest(JsonObject data)
            {
                foreach (var property in data)
                {
                    var results = GetWriteRequest(property.Key, property.Value);
                    foreach (var result in results)
                    {
                        result.BusAddress = WriteAddress;
                        result.Seq = "";
                        yield return result;
                    }
                }
            }

            private IEnumerable<InfoWrite> GetWriteRequest(string name, JsonNode node)
            {
                return WritableParameters().SelectMany(x => x.GetWriteRequest(name, node));
            }

            public IEnumerable<TimeprogWrite> GetTimeprogWriteRequest(ReadOnlyMemory<string> propertyParts, string value)
            {
                foreach (var parameter in WritableParameters())
                {
                    var write = parameter.GetTimeprogWrite(propertyParts, value);
                    if (write is null) continue;
                    write.BusAddress = WriteAddress;
                    yield return write;
                }
            }

            public IEnumerable<TimeprogWrite> GetTimeprogWriteRequest(JsonObject data)
            {
                foreach (var property in data)
                {
                    foreach (var parameter in WritableParameters())
                    {
                        var write = parameter.GetTimeprogWrite(property.Key, property.Value);
                        if (write is null) continue;
                        write.BusAddress = WriteAddress;
                        yield return write;
                    }
                }
            }

            public JsonMessage JsonMessage
            {
                get
                {
                    var parameters = _parameter
                        .Where(x => x.HasValue)
                        .Distinct()
                        .ToList();
                    if (parameters.Count == 0 && _faults is null) return null;
                    var result = new JsonObject();
                    if (_faults is not null)
                    {
                        result.Add(FaultTopic, ActiveFaultText);
                        result.Add(FaultListTopic, FaultListJson());
                    }
                    foreach (var parameter in parameters)
                    {
                        var property = parameter.GetJsonValue();
                        if (result.TryGetPropertyValue(property.Key, out var value))
                        {
                            foreach (var prop in property.Value.AsObject())
                            {
                                value.AsObject().Add(prop.Key, prop.Value.Deserialize(JsonContext.Default.JsonNode));
                            }
                        }
                        else
                        {
                            result.Add(property);
                        }
                    }
                    return new JsonMessage(MqttTopic, result);
                }
            }

            public IEnumerable<MqttMessage> MqttMessages
            {
                get
                {
                    var parameters = _parameter
                        .Where(x => x.HasValue)
                        .Distinct();
                    foreach (var parameter in parameters)
                    {
                        var properties = parameter.GetSingleValues();
                        foreach (var property in properties)
                        {
                            property.AddPrefix(MqttTopic);
                            yield return property;
                        }
                    }
                    if (_faults is not null)
                    {
                        yield return new MqttMessage(FaultTopic, ActiveFaultText).AddPrefix(MqttTopic);
                        yield return new MqttMessage(FaultListTopic, JsonSerializer.Serialize(FaultListJson(), JsonContext.Default.JsonObject)).AddPrefix(MqttTopic);
                    }
                }
            }
        }

        public class RunningParameter
        {
            private readonly ParameterDescriptor _descriptor;
            private readonly Ism7Localizer _localizer;
            private readonly ConverterTemplateBase _converter;

            public RunningParameter(ParameterDescriptor descriptor, ConverterTemplateBase converter, Ism7Localizer localizer)
            {
                _descriptor = descriptor;
                _localizer = localizer;
                _converter = converter.Clone();
            }

            public string Name => _converter is Timeprog03F1ConverterTemplate timeprog
                ? TimeprogName(timeprog, _descriptor.Name)
                : _descriptor.Name;

            /// <summary>
            /// Every day of every time programme is named only after its weekday ("Mo", "Di", ...); the programme
            /// and what it switches make the name unique and readable.
            /// </summary>
            private static string TimeprogName(Timeprog03F1ConverterTemplate timeprog, string day)
            {
                var type = timeprog.ProgramType switch
                {
                    0 => "Heizkreis",
                    1 => "Warmwasser",
                    2 => "Zirkulation",
                    3 => "Lüftung",
                    _ => $"Typ {timeprog.ProgramType}",
                };
                var circuit = timeprog.HzkInstance > 0 ? $" {timeprog.HzkInstance}" : String.Empty;
                return $"Zeitprogramm {type}{circuit} {timeprog.HeatprogNumber} {day}";
            }

            public Timeprog03F1ConverterTemplate Timeprog => _converter as Timeprog03F1ConverterTemplate;

            public string MqttName => MqttEscape(Name);

            public bool IsDuplicate { get; set; }

            public bool IsWritable => _descriptor.IsWritable;

            public bool HasValue => _converter.HasValue;

            public int PTID => _descriptor.PTID;

            public int InfoReadCount => _converter.InfoReadCount;

            public IEnumerable<InfoRead> InfoReads => _converter.InfoReads;

            public ParameterDescriptor Descriptor => _descriptor;

            public void ProcessDatapoint(ushort telegram, int? service, byte low, byte high, bool write)
            {
                if (service.HasValue)
                {
                    if (write)
                    {
                        if (_converter.ServiceWriteNumber != service.Value) return;
                    }
                    else
                    {
                        if (_converter.ServiceReadNumber != service.Value) return;
                    }
                }
                if (!_converter.CanProcess(telegram)) return;
                _converter.AddTelegram(telegram, low, high);
            }

            public IEnumerable<InfoWrite> GetWriteRequest(ReadOnlyMemory<string> propertyParts, string value)
            {
                var parts = propertyParts.Span;
                if (!IsWritable) return Array.Empty<InfoWrite>();
                if (parts.IsEmpty) return Array.Empty<InfoWrite>();
                if (MqttName != parts[0]) return Array.Empty<InfoWrite>();
                parts = parts.Slice(1);
                if (IsDuplicate)
                {
                    if (parts.IsEmpty) return Array.Empty<InfoWrite>();
                    if (_descriptor.PTID.ToString() != parts[0]) return Array.Empty<InfoWrite>();
                    parts = parts.Slice(1);
                }
                if (_descriptor is ListParameterDescriptor listDescriptor)
                {
                    if (parts.IsEmpty) return Array.Empty<InfoWrite>();
                    if (parts[0] == "text")
                    {
                        var names = listDescriptor.KeyValueList.Split(';');
                        var candidates = _localizer.Revert(value);
                        bool validName = false;
                        foreach (var name in candidates)
                        {
                            for (int i = 1; i < names.Length; i += 2)
                            {
                                if (names[i] == name)
                                {
                                    value = names[i - 1];
                                    validName = true;
                                    break;
                                }
                            }

                            if (validName) break;
                        }
                        if (!validName) return Array.Empty<InfoWrite>();
                    }
                    else if (parts[0] != "value")
                    {
                        return Array.Empty<InfoWrite>();
                    }
                }
                return _converter.GetWrite(value);
            }

            /// <summary>The time programme write for a command addressed to this parameter, or null.</summary>
            /// <exception cref="FormatException">The value is not a valid day.</exception>
            public TimeprogWrite GetTimeprogWrite(ReadOnlyMemory<string> propertyParts, string value)
            {
                if (!IsWritable || Timeprog is null) return null;
                var parts = propertyParts.Span;
                if (parts.IsEmpty || MqttName != parts[0]) return null;
                parts = parts.Slice(1);
                if (IsDuplicate)
                {
                    if (parts.IsEmpty || _descriptor.PTID.ToString() != parts[0]) return null;
                }
                return Timeprog.CreateWrite(value);
            }

            /// <exception cref="FormatException">The value is not a valid day.</exception>
            public TimeprogWrite GetTimeprogWrite(string name, JsonNode node)
            {
                if (!IsWritable || Timeprog is null) return null;
                if (Name != name) return null;
                if (!TryGetWriteValue(node, out var value)) return null;
                return Timeprog.CreateWrite(value);
            }

            public IEnumerable<InfoWrite> GetWriteRequest(string name, JsonNode node)
            {
                if (!IsWritable) return Array.Empty<InfoWrite>();
                if (Name != name) return Array.Empty<InfoWrite>();
                if (!TryGetWriteValue(node, out var value)) return Array.Empty<InfoWrite>();
                return _converter.GetWrite(value);
            }

            public bool TryGetWriteValue(JsonNode node, out string value)
            {
                value = node is JsonValue ? node.AsValue().ToString() : null;
                if (IsDuplicate)
                {
                    if (node is not JsonObject jobject) return false;
                    if (!jobject.TryGetPropertyValue(_descriptor.PTID.ToString(), out node)) return false;
                    value = node is JsonValue ? node.AsValue().ToString() : null;
                }
                if (_descriptor is ListParameterDescriptor listDescriptor)
                {
                    if (!String.IsNullOrEmpty(listDescriptor.KeyValueList))
                    {
                        if (node is JsonObject jobject)
                        {
                            if (jobject.TryGetPropertyValue("value", out var number))
                            {
                                value = number.AsValue().ToString();
                            }
                            else if (jobject.TryGetPropertyValue("text", out var text))
                            {
                                var names = listDescriptor.KeyValueList.Split(';');
                                var candidates = _localizer.Revert(text.ToString());
                                bool found = false;
                                foreach (var name in candidates)
                                {
                                    for (int i = 1; i < names.Length; i += 2)
                                    {
                                        if (names[i] == name)
                                        {
                                            value = names[i - 1];
                                            found = true;
                                            break;
                                        }
                                    }

                                    if (found) break;
                                }
                            }
                        }
                    }
                }
                return value is not null;
            }

            public KeyValuePair<string, JsonNode> GetJsonValue()
            {
                JsonNode value = _converter.GetValue();
                if (value is not null && _descriptor is ListParameterDescriptor listDescriptor)
                {
                    if (listDescriptor.Options.Any())
                    {
                        var key = value.ToString();
                        var text = listDescriptor.Options.Where(x => x.Key == key).Select(x => x.Value).FirstOrDefault();
                        if (!String.IsNullOrEmpty(text))
                        {
                            value = new JsonObject
                            {
                                ["value"] = value,
                                ["text"] = _localizer[text]
                            };
                        }
                    }
                }
                if (IsDuplicate)
                {
                    value = new JsonObject
                    {
                        [_descriptor.PTID.ToString()] = value,
                    };
                }
                return new KeyValuePair<string,JsonNode>(Name, value);
            }

            public IEnumerable<MqttMessage> GetSingleValues()
            {
                var json = _converter.GetValue();
                // Home Assistant reads the payload "None" as an unknown state.
                var text = json is null ? "None" : JsonSerializer.Serialize(json, JsonContext.Default.JsonValue);
                var result = new MqttMessage(MqttName, text);
                if (IsDuplicate)
                {
                    result.AddSuffix(_descriptor.PTID.ToString());
                }
                return HandleListParameter(result);
            }

            private IEnumerable<MqttMessage> HandleListParameter(MqttMessage value)
            {
                if (_descriptor is ListParameterDescriptor listDescriptor)
                {
                    if (listDescriptor.Options.Any())
                    {
                        var key = value.Content;
                        var text = listDescriptor.Options.Where(x => x.Key == key).Select(x => x.Value).FirstOrDefault();
                        if (!String.IsNullOrEmpty(text))
                        {
                            yield return value
                                .Clone()
                                .AddSuffix("value");
                            yield return value
                                .Clone()
                                .AddSuffix("text")
                                .SetContent(_localizer[text]);
                        }
                        else
                        {
                            yield return value;
                        }
                    }
                }
                else
                {
                    yield return value;
                }
            }

            private static string MqttEscape(string topic)
            {
                return topic
                    .Replace("ä", "ae")
                    .Replace("ö", "oe")
                    .Replace("ü", "ue")
                    .Replace("Ä", "Ae")
                    .Replace("Ö", "Oe")
                    .Replace("Ü", "Ue")
                    .Replace("ß", "ss")
                    .Replace('/', '_')
                    .Replace(' ', '_')
                    .Replace('+', '_')
                    .Replace('#', '_');
            }
        }
    }
}