using System;
using System.Collections.Generic;

namespace Api.Net10.Services
{
    // Simple DTO for generated payload content.
    public sealed class PayloadData
    {
        public string BigString { get; set; } = string.Empty;
        public List<string> Items { get; set; } = new List<string>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    // Deterministic payload generation to keep responses comparable across runs.
    public static class PayloadGenerator
    {
        public static PayloadData Generate(int payloadSize)
        {
            var size = Math.Max(0, payloadSize);
            var bigString = new string('x', size);
            var itemCount = Math.Max(10, Math.Min(200, size / 32));
            var items = new List<string>(itemCount);
            for (var i = 0; i < itemCount; i++)
            {
                items.Add("item-" + i + "-size-" + size);
            }

            return new PayloadData
            {
                BigString = bigString,
                Items = items,
                Metadata = new Dictionary<string, string>
                {
                    ["payloadSize"] = size.ToString(),
                    ["items"] = itemCount.ToString(),
                    ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                },
            };
        }
    }
}
