using System;
using System.Xml.Serialization;

namespace ism7mqtt.ISM7.Protocol
{
    /// <summary>
    /// One entry of a device's fault list, answering the bundle's <see cref="TelegramBundleReq.ErrorStateRead"/>:
    /// the fault code, when it came and went (local time, as the module reports it), and whether it is still active.
    /// </summary>
    [XmlRoot("ers")]
    public class ErrorStateResp
    {
        [XmlAttribute("se")]
        public string Seq { get; set; }

        [XmlAttribute("ba")]
        public string BusAddress { get; set; }

        [XmlAttribute("index")]
        public int Index { get; set; }

        [XmlAttribute("code")]
        public int Code { get; set; }

        [XmlAttribute("start")]
        public string Start { get; set; }

        [XmlAttribute("stop")]
        public string Stop { get; set; }

        [XmlAttribute("active")]
        public bool Active { get; set; }
    }
}
