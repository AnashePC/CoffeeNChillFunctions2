using Azure;
using Azure.Data.Tables;

namespace CoffeeNChillFunctions.Models
{
    /// <summary>Row in the "Orders" table. PartitionKey = OrderDate (yyyy-MM-dd), RowKey = OrderId.</summary>
    public class OrderEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;
        public string SelectedItemSKUs { get; set; } = string.Empty; // comma-separated
        public double TotalPrice { get; set; }
        public DateTimeOffset OrderTimestamp { get; set; }
        public string Status { get; set; } = OrderStatus.Received;
        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }

    public static class OrderStatus
    {
        public const string Received = "Received";
        public const string Preparing = "Preparing";
        public const string Ready = "Ready";
        public const string Collected = "Collected";

        /// <summary>Barista lifecycle, in order.</summary>
        public static readonly string[] Lifecycle = [Received, Preparing, Ready, Collected];
    }
}
