using System;
using System.Xml.Serialization;

namespace ism7mqtt.ISM7.Protocol
{
    /// <summary>
    /// Reads one day of a BM-2 time programme (eBus service 0x03F1). Sent in bundles of its own, never mixed
    /// with info number reads.
    /// </summary>
    [XmlRoot("s03f1rd")]
    public class TimeprogRead
    {
        [XmlAttribute("se")]
        public string Seq { get; set; } = String.Empty;

        [XmlAttribute("ba")]
        public string BusAddress { get; set; }

        /// <summary>Programme number, 1 to 3.</summary>
        [XmlAttribute("hp")]
        public int HeatprogNumber { get; set; }

        /// <summary>0 heating circuit, 1 hot water, 2 circulation, 3 ventilation.</summary>
        [XmlAttribute("ty")]
        public int ProgramType { get; set; }

        [XmlAttribute("hk")]
        public int HzkInstance { get; set; }

        /// <summary>0 Sunday, 1 Monday ... 6 Saturday (the .NET DayOfWeek numbering).</summary>
        [XmlAttribute("d")]
        public int DayOfWeek { get; set; }
    }
}
