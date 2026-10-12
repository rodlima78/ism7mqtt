using System;
using System.Xml.Serialization;

namespace ism7mqtt.ISM7.Protocol
{
    /// <summary>The acknowledgement of a <see cref="TimeprogWrite"/>, carrying the data written.</summary>
    [XmlRoot("s03f3ac")]
    public class TimeprogWriteResp
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
