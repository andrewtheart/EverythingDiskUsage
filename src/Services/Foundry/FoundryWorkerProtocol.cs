using System.Text.Json;

namespace EverythingDiskUsage.Services.Foundry;

internal static class FoundryWorkerProtocol
{
    internal static class Operations
    {
        public const string ListModels = "listModels";
        public const string PrepareModel = "prepareModel";
        public const string Recommend = "recommend";
        public const string Cancel = "cancel";
        public const string Shutdown = "shutdown";
    }

    internal static class MessageTypes
    {
        public const string Ready = "ready";
        public const string Progress = "progress";
        public const string Result = "result";
        public const string Models = "models";
        public const string Ack = "ack";
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class FoundryWorkerRequest
{
    public string Operation { get; set; } = string.Empty;

    public int Id { get; set; }

    public string? ModelAlias { get; set; }

    public string? PayloadJson { get; set; }

    public int TimeoutSeconds { get; set; }
}

internal sealed class FoundryWorkerMessage
{
    public string Type { get; set; } = string.Empty;

    public int Id { get; set; }

    public bool Ok { get; set; }

    public string? Error { get; set; }

    public string? PayloadJson { get; set; }

    public string? ModelKey { get; set; }

    public string? Stage { get; set; }

    public string? Detail { get; set; }

    public double? Percent { get; set; }
}
