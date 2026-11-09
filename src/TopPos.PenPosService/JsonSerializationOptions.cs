using System.Text.Json.Serialization;

namespace TopPos.PenPosService
{
    internal class JsonSerializationOptions
    {
        public object PropertyNamingPolicy { get; set; }
        public JsonIgnoreCondition DefaultIgnoreCondition { get; set; }
        public bool WriteIndented { get; set; }
    }
}