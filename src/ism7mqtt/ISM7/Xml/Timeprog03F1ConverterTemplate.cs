using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Xml.Serialization;
using ism7mqtt.ISM7.Protocol;

namespace ism7mqtt.ISM7.Xml
{
    /// <summary>
    /// One day of a BM-2 time programme. It is not read through info numbers but with a <see cref="TimeprogRead"/>
    /// of its own; the answer carries five switching periods of four bytes each (start and end in minutes after
    /// midnight, little endian). The BM-2 uses the first three; a period of 0xFFFF, or with equal start and end,
    /// is not set. The value is the set periods, e.g. "06:00-08:30; 16:00-22:00", or "-" for a day without any.
    /// The same text, with up to three periods in ascending order, writes the day (<see cref="CreateWrite"/>).
    /// </summary>
    public class Timeprog03F1ConverterTemplate:ConverterTemplateBase
    {
        public const int DataLength = 20;
        public const string NoPeriods = "-";
        public const int MaxPeriods = 3;

        /// <summary>The BM-2 stores the end of the day, 24:00, as 23:59.</summary>
        private const ushort EndOfDay = 1439;

        /// <summary>What a value to write must look like; also the Home Assistant text entity's pattern.</summary>
        public const string WritePattern =
            @"^\s*(-|((([01][0-9]|2[0-3]):[0-5][0-9]|24:00)-(([01][0-9]|2[0-3]):[0-5][0-9]|24:00))(\s*[;,]\s*(([01][0-9]|2[0-3]):[0-5][0-9]|24:00)-(([01][0-9]|2[0-3]):[0-5][0-9]|24:00)){0,2})?\s*$";

        private string _value;

        [XmlElement("DayOfWeek")]
        public byte DayOfWeek { get; set; }

        [XmlElement("HeatprogNumber")]
        public int HeatprogNumber { get; set; }

        [XmlElement("ProgramType")]
        public int ProgramType { get; set; }

        [XmlElement("HzkInstance")]
        public int HzkInstance { get; set; }

        public override int InfoReadCount => 0;

        public override IEnumerable<InfoRead> InfoReads => Array.Empty<InfoRead>();

        public override bool CanProcess(ushort telegram)
        {
            return false;
        }

        public override void AddTelegram(ushort telegram, byte low, byte high)
        {
        }

        public TimeprogRead CreateRead()
        {
            return new TimeprogRead
            {
                HeatprogNumber = HeatprogNumber,
                ProgramType = ProgramType,
                HzkInstance = HzkInstance,
                DayOfWeek = DayOfWeek,
            };
        }

        public bool Matches(TimeprogWriteResp response)
        {
            return response.HeatprogNumber == HeatprogNumber &&
                   response.ProgramType == ProgramType &&
                   response.HzkInstance == HzkInstance &&
                   response.DayOfWeek == DayOfWeek;
        }

        public bool Matches(TimeprogReadResp response)
        {
            return response.HeatprogNumber == HeatprogNumber &&
                   response.ProgramType == ProgramType &&
                   response.HzkInstance == HzkInstance &&
                   response.DayOfWeek == DayOfWeek;
        }

        /// <summary>Takes the response's data; returns false (and keeps the last value) when it is malformed.</summary>
        public bool SetData(string hex)
        {
            byte[] data;
            try
            {
                data = Convert.FromHexString(hex ?? String.Empty);
            }
            catch (FormatException)
            {
                return false;
            }
            if (data.Length != DataLength) return false;
            _value = Format(data);
            return true;
        }

        public static string Format(ReadOnlySpan<byte> data)
        {
            var periods = new List<string>();
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                var start = (ushort)(data[i] | data[i + 1] << 8);
                var end = (ushort)(data[i + 2] | data[i + 3] << 8);
                if (start == UInt16.MaxValue || end == UInt16.MaxValue) continue;
                if (start == end) continue;
                periods.Add($"{FormatTime(start)}-{FormatTime(end)}");
            }
            return periods.Count == 0 ? NoPeriods : String.Join("; ", periods);
        }

        private static string FormatTime(ushort minutes)
        {
            if (minutes >= EndOfDay) return "24:00";
            return $"{minutes / 60:D2}:{minutes % 60:D2}";
        }

        /// <summary>
        /// Encodes a day as Wolf's own software does: up to three periods in the first three slots, an unused one of
        /// those as zeros, the last two slots 0xFFFF. Periods must be in ascending order and must not overlap.
        /// </summary>
        /// <exception cref="FormatException">The text is not a valid day; the message says why.</exception>
        public static byte[] Parse(string value)
        {
            var text = (value ?? String.Empty).Trim();
            var data = new byte[DataLength];
            for (int i = MaxPeriods * 4; i < DataLength; i++) data[i] = 0xFF;
            if (text.Length == 0 || text == NoPeriods) return data;

            var parts = text.Split(new[] { ';', ',' }, StringSplitOptions.TrimEntries);
            if (parts.Length > MaxPeriods)
                throw new FormatException($"at most {MaxPeriods} periods per day, got {parts.Length}");
            int previousEnd = -1;
            for (int i = 0; i < parts.Length; i++)
            {
                var times = parts[i].Split('-', StringSplitOptions.TrimEntries);
                if (times.Length != 2)
                    throw new FormatException($"'{parts[i]}' is not a period like 06:00-22:00");
                var start = ParseTime(times[0]);
                var end = ParseTime(times[1]);
                if (start >= end)
                    throw new FormatException($"'{parts[i]}' ends before it starts");
                if (start < previousEnd)
                    throw new FormatException($"'{parts[i]}' overlaps or precedes the period before it");
                previousEnd = end;
                var stored = (ushort)Math.Min(end, EndOfDay);
                data[i * 4] = (byte)(start & 0xFF);
                data[i * 4 + 1] = (byte)(start >> 8);
                data[i * 4 + 2] = (byte)(stored & 0xFF);
                data[i * 4 + 3] = (byte)(stored >> 8);
            }
            return data;
        }

        private static int ParseTime(string text)
        {
            var parts = text.Split(':');
            if (parts.Length != 2 ||
                !Int32.TryParse(parts[0], out var hours) || !Int32.TryParse(parts[1], out var minutes) ||
                hours < 0 || minutes < 0 || minutes > 59 || hours > 24 || (hours == 24 && minutes != 0))
            {
                throw new FormatException($"'{text}' is not a time between 00:00 and 24:00");
            }
            return hours * 60 + minutes;
        }

        /// <exception cref="FormatException">The value is not a valid day.</exception>
        public TimeprogWrite CreateWrite(string value)
        {
            return new TimeprogWrite
            {
                HeatprogNumber = HeatprogNumber,
                ProgramType = ProgramType,
                HzkInstance = HzkInstance,
                DayOfWeek = DayOfWeek,
                Data = Convert.ToHexString(Parse(value)),
            };
        }

        public override bool HasValue => _value is not null;

        public override JsonValue GetValue()
        {
            return JsonValue.Create(_value);
        }

        /// <summary>A time programme is not written with info numbers but with <see cref="CreateWrite"/>.</summary>
        public override IEnumerable<InfoWrite> GetWrite(string value)
        {
            return Array.Empty<InfoWrite>();
        }

        public override ConverterTemplateBase Clone()
        {
            return Clone(new Timeprog03F1ConverterTemplate
            {
                DayOfWeek = DayOfWeek,
                HeatprogNumber = HeatprogNumber,
                ProgramType = ProgramType,
                HzkInstance = HzkInstance,
            });
        }
    }
}
