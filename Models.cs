using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace IBCollective2Sync
{
    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double AvgCost { get; set; }
        public string SecType { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }

    public class PositionChangedEventArgs : EventArgs
    {
        public string Symbol { get; set; } = string.Empty;
        public double OldQuantity { get; set; }
        public double NewQuantity { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class TradeExecutedEventArgs : EventArgs
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double Price { get; set; }
        public double NewPosition { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class C2PositionsResponse
    {
        [JsonPropertyName("results")]
        public List<C2PositionDTO> Results { get; set; }
    }

    public class C2PositionDTO
    {
        [JsonPropertyName("quantity")]
        public double Quantity { get; set; }

        [JsonPropertyName("c2Symbol")]
        public C2SymbolDTO C2Symbol { get; set; }
    }

    public class C2SymbolDTO
    {
        [JsonPropertyName("fullSymbol")]
        public string FullSymbol { get; set; }

        [JsonPropertyName("symbolType")]
        public string SymbolType { get; set; }
    }
}
