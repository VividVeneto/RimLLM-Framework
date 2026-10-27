using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using RimLLM_Framework;
using RimLLM_Framework.Manager;
    public class MockSettings : IRimLLMSettings, IDailyTokenBudget
    {
        public List<string> FallbackChain { get; set; } = new List<string>();
        public float ApiTimeout { get; set; } = 30f;
        public bool DetailedLogging { get; set; } = true;
        public ReasoningEffort? DefaultReasoningEffort { get; set; } = null;
        public int MaxRetries { get; set; } = 3;
        public float RetryDelay { get; set; } = 3f;
        public int MaxConcurrentRequests { get; set; } = 2;
        public long TotalPromptTokens { get; set; } = 0;
        public long TotalCompletionTokens { get; set; } = 0;
        public float TotalEstimatedCost { get; set; } = 0f;
        public float DailyBudgetLimit { get; set; } = 0f;
        public long DailyTokenBudgetLimit { get; set; } = 0;
        public int BudgetPolicy { get; set; } = 0;
        public bool EnableAntiAbuse { get; set; } = true;
        public int MaxRequestsPerWindow { get; set; } = 10;
        public int ThrottlingWindowSeconds { get; set; } = 10;
        public int CoolDownDurationSeconds { get; set; } = 60;
        public float DailyAccumulatedCost { get; set; } = 0f;
        public long DailyAccumulatedTokens { get; set; } = 0;
        public string DailyBudgetResetDate { get; set; } = "";
        public int RoutingStrategy { get; set; } = 0;
        public bool EnableNativeSchema { get; set; } = true;
        public bool EnableJsonRepair { get; set; } = true;

        public string EmbeddingProvider { get; set; } = "Disabled";
        public string EmbeddingModel { get; set; } = "text-embedding-004";
        public string EmbeddingEndpoint { get; set; } = "";
        public string EmbeddingApiKey { get; set; } = "";

        public Dictionary<string, string> ApiKeys = new Dictionary<string, string>();
        public Dictionary<string, string> Endpoints = new Dictionary<string, string>();
        public Dictionary<string, bool> EnabledProviders = new Dictionary<string, bool>();
        public Dictionary<string, List<string>> ModelLists = new Dictionary<string, List<string>>();

        public string GetApiKey(string providerId) => ApiKeys.TryGetValue(providerId, out var val) ? val : "";
        public string GetActiveApiKey(string providerId) => GetApiKey(providerId);
        public string GetEndpoint(string providerId, string defaultVal) => Endpoints.TryGetValue(providerId, out var val) ? val : defaultVal;
        public bool IsProviderEnabled(string providerId) => EnabledProviders.TryGetValue(providerId, out var val) ? val : true;
        public List<string> GetModelList(string providerId) => ModelLists.TryGetValue(providerId, out var val) ? val : new List<string>();

        public string GetDefaultModel(string providerId, string defaultVal)
        {
            var list = GetModelList(providerId);
            return list.Count > 0 ? list[0] : defaultVal;
        }

        public void SetModelList(string providerId, List<string> models) => ModelLists[providerId] = models;

        public Dictionary<string, int> ModelLevelOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int GetModelLevelOverride(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return 0;
            if (ModelLevelOverrides.TryGetValue(modelName, out var level)) return level;
            int colonIndex = modelName.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < modelName.Length - 1)
            {
                if (ModelLevelOverrides.TryGetValue(modelName.Substring(colonIndex + 1), out level)) return level;
            }
            return 0;
        }

        public List<RimLLMManager.RequestLogEntry> RequestLogs { get; set; } = new List<RimLLMManager.RequestLogEntry>();

        public void MarkTelemetryDirty() {}

        public void SaveTelemetry() {}

        public void Write() {}
    }
