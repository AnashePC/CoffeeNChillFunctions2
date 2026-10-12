namespace CoffeeNChillFunctions.Models
{
    public class OrderDto
    {
        public string OrderId { get; set; } = string.Empty;
        public string OrderDate { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public List<string> SelectedItemSKUs { get; set; } = [];
        public double TotalPrice { get; set; }
        public DateTimeOffset OrderTimestamp { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
