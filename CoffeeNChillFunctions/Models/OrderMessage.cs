namespace CoffeeNChillFunctions.Models
{
    /// <summary>JSON payload placed on the order-processing-queue.</summary>
    public class OrderMessage
    {
        public string OrderId { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public List<string> SelectedItemSKUs { get; set; } = [];
        public decimal TotalPrice { get; set; }
        public DateTimeOffset? OrderTimestamp { get; set; }
    }
}
