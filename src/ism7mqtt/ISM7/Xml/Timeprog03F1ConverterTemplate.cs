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
    /// </summary>
    public class Timeprog03F1ConverterTemplate:ConverterTemplateBase
    {
        public const int DataLength = 20;
        public const string NoPeriods = "-";

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
            // 23:59 is how the BM-2 stores the end of the day.
            if (minutes >= 1439) return "24:00";
            return $"{minutes / 60:D2}:{minutes % 60:D2}";
        }

        public override bool HasValue => _value is not null;

        public override JsonValue GetValue()
        {
            return JsonValue.Create(_value);
        }

        /// <summary>Time programmes are read only here; writing them is not supported.</summary>
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
