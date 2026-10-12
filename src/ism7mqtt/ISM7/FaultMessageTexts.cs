using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ism7mqtt.ISM7
{
    /// <summary>
    /// Wolf's texts for fault codes. The same code means different things on different devices, so a text is
    /// looked up by code and device template.
    /// </summary>
    public class FaultMessageTexts
    {
        private readonly Dictionary<(int code, int dtid), string> _texts = new();

        public FaultMessageTexts(string xml)
        {
            var doc = XDocument.Load(new StringReader(xml));
            foreach (var message in doc.Descendants("FaultMessage"))
            {
                if (!Int32.TryParse((string)message.Attribute("code"), out var code)) continue;
                var text = (string)message.Attribute("text");
                var dtids = ((string)message.Attribute("dtids") ?? String.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var dtid in dtids)
                {
                    if (Int32.TryParse(dtid, out var id))
                        _texts.TryAdd((code, id), text);
                }
            }
        }

        private static FaultMessageTexts _default;
        public static FaultMessageTexts Default => _default ??= new FaultMessageTexts(Resources.FaultMessages);

        /// <summary>The text for the code on that device, or null when Wolf defines none.</summary>
        public string Get(int code, int deviceTemplateId)
        {
            return _texts.TryGetValue((code, deviceTemplateId), out var text) ? text : null;
        }
    }
}
