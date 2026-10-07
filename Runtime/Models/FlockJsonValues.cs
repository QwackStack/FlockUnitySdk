using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flock.Models
{
    /// <summary>Reads one value of a free-form JSON object the server sent, as the type the game asks for.</summary>
    internal static class FlockJsonValues
    {
        // JSON round-tripping into Dictionary<string, object> leaves whole numbers as long, decimals as
        // double, and anything nested as JObject/JArray — so a plain (int) cast on a JSON 7 throws. Going
        // back through JToken normalises all of those, and keeps Newtonsoft's types out of the public API.
        internal static bool TryConvert<T>(object raw, out T value)
        {
            value = default;
            if (raw == null) return false;

            if (raw is T direct)
            {
                value = direct;
                return true;
            }

            try
            {
                JToken token = raw as JToken ?? JToken.FromObject(raw);
                value = token.ToObject<T>();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
