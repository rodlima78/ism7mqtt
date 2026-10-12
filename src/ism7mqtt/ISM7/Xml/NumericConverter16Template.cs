using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using ism7mqtt.ISM7.Protocol;

namespace ism7mqtt.ISM7.Xml
{
    public class NumericConverter16Template:SingleTelegramConverterTemplateBase
    {
        private ushort? _value;

        protected override void AddTelegram(byte low, byte high)
        {
            _value = (ushort) ((high << 8) | low);
        }

        public override bool HasValue => _value.HasValue;

        private const ushort NotAvailable = 0x8000;

        private bool IsSigned => Type is "SS" or "SS10" or "SS100" or "SSPR";

        /// <summary>
        /// The BM-2's maintenance percentage (PTID 220105) reads 0x8080 while no interval is running and 0x8000
        /// while the maintenance message is off: any value with the top bit set is a marker, not a percentage.
        /// </summary>
        public const int MaintenancePercentCtid = 220105;
        private const ushort MaintenancePercentMarker = 0x8000;

        public override JsonValue GetValue()
        {
            if (!HasValue)
                throw new InvalidOperationException();
            if ((_value.Value == NotAvailable && IsSigned) ||
                ((_value.Value & MaintenancePercentMarker) != 0 && CTID == MaintenancePercentCtid))
            {
                // The controller sends the smallest signed value while it has no reading
                // (for instance a sensor during start-up), which is not a measurement.
                _value = null;
                return null;
            }
            JsonValue result;
            switch (Type)
            {
                case "US":
                    result = JsonValue.Create(_value.Value);
                    break;
                case "SS":
                    result = JsonValue.Create((short)_value.Value);
                    break;
                case "SS10":
                    result = JsonValue.Create(((short)_value.Value) / 10.0);
                    break;
                case "US10":
                    result = JsonValue.Create(_value.Value / 10.0);
                    break;
                case "SS100":
                    result = JsonValue.Create(((short)_value.Value) / 100.0);
                    break;
                case "SSPR":
                    result = JsonValue.Create(((short) _value.Value) * (1.0 / 255));
                    break;
                case "US4":
                    result = JsonValue.Create(_value.Value / 4.0);
                    break;
                case "IntDiv60":
                    result = JsonValue.Create(_value.Value / 60.0);
                    break;
                default:
                    throw new NotImplementedException($"type '{Type}' for CTID '{CTID}' is not yet implemented");
            }
            _value = null;
            return result;
        }

        public override IEnumerable<InfoWrite> GetWrite(string value)
        {
            if (!TelegramNr.HasValue) yield break;
            ushort data;
            switch (Type)
            {
                case "US":
                    if (!UInt16.TryParse(value, out data)) yield break;
                    break;
                case "SS":
                    if (!Int16.TryParse(value, out var int16)) yield break;
                    data = (ushort) int16;
                    break;
                case "SS10":
                    if (!Double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) yield break;
                    data = (ushort) (parsed * 10);
                    break;
                case "US10":
                    if (!Double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) yield break;
                    data = (ushort) (parsed * 10);
                    break;
                case "SS100":
                    if (!Double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) yield break;
                    data = (ushort) (parsed * 100);
                    break;
                case "SSPR":
                    if (!Double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) yield break;
                    data = (ushort) (parsed * (1.0/256));
                    break;
                case "US4":
                    if (!Double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) yield break;
                    data = (ushort) (parsed * 4);
                    break;
                case "IntDiv60":
                    if (!Double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) yield break;
                    data = (ushort) (parsed * 60);
                    break;
                default:
                    throw new NotImplementedException($"type '{Type}' for CTID '{CTID}' is not yet implemented");
            }
            yield return new InfoWrite{InfoNumber = TelegramNr.Value, DBLow = $"0x{(data & 0xff):X2}", DBHigh = $"0x{(data >> 8):X2}"};
        }

        public override ConverterTemplateBase Clone()
        {
            return Clone(new NumericConverter16Template());
        }
    }
}