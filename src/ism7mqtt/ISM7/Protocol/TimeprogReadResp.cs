using System;
using System.Xml.Serialization;

namespace ism7mqtt.ISM7.Protocol
{
    /// <summary>The answer to a <see cref="TimeprogRead"/>: the day's switching periods as 20 bytes in hex.</summary>
    [XmlRoot("s03f1rs")]
    public class TimeprogReadResp
    {
        [XmlAttribute("se")]
        public string Seq { get; set; }

        [XmlAttribute("ba")]
        public string BusAddress { get; set; }

        [XmlAttribute("st")]
        public TelegrResponseState State { get; set; }

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
