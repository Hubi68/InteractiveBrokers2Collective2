using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using IBApi;

namespace IBCollective2Sync
{
    public class Configuration
    {
        public string C2ApiKey { get; set; } = "YOUR_C2_API_KEY";
        public string C2StrategyId { get; set; } = "YOUR_STRATEGY_ID";
        public string IbHost { get; set; } = "127.0.0.1";
        public int IbPort { get; set; } = 7497;
        public int IbClientId { get; set; } = 987;
        public int BackupSyncIntervalMinutes { get; set; } = 5;
        public int PositionChangeDebounceMs { get; set; } = 500;
        public int TradeExecutionDelayMs { get; set; } = 1000;
        public double MinimumQuantityThreshold { get; set; } = 0.01;
        public int SignalSubmissionDelayMs { get; set; } = 100;
        public int PostTradeCheckDelaySeconds { get; set; } = 60;
        public int HttpTimeoutSeconds { get; set; } = 30;
        public int MaxRetryAttempts { get; set; } = 3;

        public Dictionary<string, string> SymbolMappings { get; set; } = new Dictionary<string, string>();

        public string GetC2Symbol(Contract contract)
        {
            if (contract.SecType == "FUT")
            {
                // Check for configurable mapping first (based on root symbol)
                // Assuming contract.Symbol is the root (e.g. MGC, ES)
                if (SymbolMappings.TryGetValue(contract.Symbol, out var mappedRoot))
                {
                   // Try to reconstruct symbol with mapped root
                   // IB LocalSymbol: MGCG6
                   // We want to replace MGC with QMGC (if mapped)
                   // Or simply prefix if that is the strategy.

                   // Robust approach: If LocalSymbol starts with IB root, replace it with C2 root.
                   if (!string.IsNullOrEmpty(contract.LocalSymbol) && contract.LocalSymbol.StartsWith(contract.Symbol))
                   {
                       return mappedRoot + contract.LocalSymbol.Substring(contract.Symbol.Length);
                   }

                   // Fallback logic if LocalSymbol doesn't match expected pattern
                   return mappedRoot + contract.LastTradeDateOrContractMonth;
                }

                // Default logic if no mapping found
                var symbol = !string.IsNullOrEmpty(contract.LocalSymbol)
                    ? contract.LocalSymbol
                    : $"{contract.Symbol}{contract.LastTradeDateOrContractMonth}";

                // Special Mappings (Hardcoded legacy fallback or remove if fully config driven)
                // Special Mappings (Hardcoded legacy fallback or remove if fully config driven)
                // Keeping Micro Gold hardcode just in case config is missing, but config takes precedence above.
                if (symbol.StartsWith("MGC"))
                {
                    symbol = "QMGC" + symbol.Substring(3);
                }

                // FIX: Ensure 2-digit years for C2 compatibility (e.g., MESH6 -> MESH26)
                // Matches a letter followed by a single digit at the end of the string.
                // Replace with Letter + "2" + Digit.
                // usage of ${1} creates unambiguous reference to group 1
                // DISABLED: This logic breaks MBT (Micro Bitcoin) which expects @MBTG6 (1-digit year)
                // symbol = Regex.Replace(symbol, @"([A-Z])([0-9])$", "${1}2$2");

                if (!symbol.StartsWith("@"))
                    return "@" + symbol;

                return symbol;
            }

            return contract.Symbol;
        }

        public static Configuration Load()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    return JsonSerializer.Deserialize<Configuration>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new Configuration();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not load config file: {ex.Message}. Using defaults.");
                }
            }

            return new Configuration();
        }

        public void Save()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(configPath, json);
        }
    }
}
