using System;
using System.Collections.Generic;
using System.Text;

namespace Betty.Entities.Twitch
{
    public class HttpCallbackMessage
    {
        public IDictionary<string, string> QueryItems { get; set; }
        public string RequestBody { get; set; }
    }
}
