namespace Api.Net10.Models
{
    // REST request payload for /api/work.
    public sealed class WorkRequest
    {
        // Number of bytes used to generate the synthetic payload.
        public int PayloadSize { get; set; }
        // Correlation identifier used for trace enrichment only.
        public string? CorrelationId { get; set; }
    }
}
