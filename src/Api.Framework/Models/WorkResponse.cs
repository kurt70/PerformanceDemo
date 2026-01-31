using System.Collections.Generic;

namespace Api.Framework.Models
{
    // REST response payload for /api/work.
    public sealed class WorkResponse
    {
        // Large string payload sized by the request.
        public string BigString { get; set; }
        // Collection of small strings to simulate list payloads.
        public List<string> Items { get; set; }
        // Key-value metadata to simulate headers/body metadata.
        public Dictionary<string, string> Metadata { get; set; }
    }
}
