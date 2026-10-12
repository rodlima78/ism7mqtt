using System;
using System.Xml.Serialization;

namespace ism7mqtt.ISM7.Protocol
{
    /// <summary>
    /// Writes one day of a BM-2 time programme (eBus service 0x03F3), to the BM-2's write address, in a write
    /// bundle of its own. The data has the layout of <see cref="TimeprogReadResp.Data"/>.
    /// </summary>
    [XmlRoot("s03f3wr")]
    public class TimeprogWrite
    {
        [XmlAttribute("se")]
        public string Seq { get; set; } = String.Empty;

        [XmlAttribute("ba")]
        public string BusAddress { get; set; }

        [XmlAttribute("hp")]
        public int HeatprogNumber { get; set; }

        [XmlAttribute("ty")]
        public int ProgramType { get; set; }

        [XmlAttribute("hk")]
        public int HzkInstance { get; set; }

        [XmlAttribute("d")]
        public int DayOfWeek { get; set; }

        [XmlAttribute("db")]
        public string Data { get; set; }
    }
}
