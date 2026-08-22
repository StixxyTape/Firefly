using System;
using Newtonsoft.Json.Linq;
using Verse;

namespace Firefly
{
    public static class JsonResponseReader
    {
        public static JObject ParseObject(string rawJson, string label)
        {
            if (rawJson.NullOrEmpty()) return null;
            JObject parsed = JsonResponseCore.ParseObject(rawJson);
            if (parsed == null)
                Log.Warning($"[Firefly] {label} response was not valid JSON.");
            return parsed;
        }
    }
}
