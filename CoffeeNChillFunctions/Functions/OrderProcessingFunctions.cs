using System.Net;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using CoffeeNChillFunctions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CoffeeNChillFunctions.Functions
{
    /// <summary>
    /// Queue-triggered order processing, poison-queue logging, and read endpoints for the Orders table.
    /// </summary>
    public class OrderProcessingFunctions(ILoggerFactory loggerFactory)
    {
        private const string OrdersTable = "Orders";
        private const string FailedOrdersTable = "FailedOrders";

        private readonly ILogger _logger = loggerFactory.CreateLogger<OrderProcessingFunctions>();

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private static TableClient GetTable(string name)
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var table = new TableServiceClient(connectionString).GetTableClient(name);
            table.CreateIfNotExists();
            return table;
        }

        /// <summary>Delay between simulated barista steps. Override with ORDER_STEP_DELAY_SECONDS.</summary>
        private static TimeSpan StepDelay =>
            int.TryParse(Environment.GetEnvironmentVariable("ORDER_STEP_DELAY_SECONDS"), out var s) && s >= 0
                ? TimeSpan.FromSeconds(s)
                : TimeSpan.FromSeconds(3);

        /// <summary>Reads queue metadata (Id, DequeueCount...) from the trigger binding data.</summary>
        private static string Meta(FunctionContext context, string key) =>
            context.BindingContext.BindingData.TryGetValue(key, out var v) && v is not null
                ? v.ToString()!.Trim('"')
                : "?";

        private static OrderDto ToDto(OrderEntity e) => new()
        {
            OrderId = e.RowKey,
            OrderDate = e.PartitionKey,
            CustomerName = e.CustomerName,
            SelectedItemSKUs = e.SelectedItemSKUs.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            TotalPrice = e.TotalPrice,
            OrderTimestamp = e.OrderTimestamp,
            Status = e.Status,
            UpdatedAt = e.UpdatedAt
        };

        // ------------------------------------------------------------------
        // Queue trigger: order-processing-queue -> Orders table + lifecycle
        // ------------------------------------------------------------------
        [Function("ProcessOrderQueue")]
        public async Task ProcessOrderQueue(
            [QueueTrigger(OrderQueueFunctions.QueueName)] string messageText,
            FunctionContext context)
        {
            var dequeueCount = Meta(context, "DequeueCount");
            _logger.LogInformation("ProcessOrderQueue triggered (attempt {Attempt}): {Message}", dequeueCount, messageText);

            // 1. Parse JSON. Any failure throws, so the runtime retries and finally moves the
            //    message to order-processing-queue-poison (see host.json maxDequeueCount).
            OrderMessage order;
            try
            {
                order = JsonSerializer.Deserialize<OrderMessage>(messageText, JsonOptions)
                        ?? throw new InvalidOperationException("Queue message deserialised to null.");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Order message is not valid JSON (attempt {Attempt}).", dequeueCount);
                throw;
            }

            if (string.IsNullOrWhiteSpace(order.OrderId) || string.IsNullOrWhiteSpace(order.CustomerName) ||
                order.SelectedItemSKUs is null || order.SelectedItemSKUs.Count == 0 || order.TotalPrice <= 0)
            {
                throw new InvalidOperationException($"Order message is missing required fields: {messageText}");
            }

            var timestamp = order.OrderTimestamp ?? DateTimeOffset.UtcNow;
            var table = GetTable(OrdersTable);

            // 2. Write the order to the Orders table: PartitionKey = OrderDate, RowKey = OrderId, Status = Received.
            //    Upsert keeps the function idempotent if the message is delivered more than once.
            var entity = new OrderEntity
            {
                PartitionKey = timestamp.UtcDateTime.ToString("yyyy-MM-dd"),
                RowKey = order.OrderId.Trim(),
                CustomerName = order.CustomerName.Trim(),
                SelectedItemSKUs = string.Join(",", order.SelectedItemSKUs),
                TotalPrice = (double)order.TotalPrice,
                OrderTimestamp = timestamp,
                Status = OrderStatus.Received,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            _logger.LogInformation("Order {OrderId} stored with status {Status}", entity.RowKey, entity.Status);

            // 3. Simulate barista lifecycle: Received -> Preparing -> Ready -> Collected.
            foreach (var next in OrderStatus.Lifecycle.Skip(1))
            {
                await Task.Delay(StepDelay);

                entity.Status = next;
                entity.UpdatedAt = DateTimeOffset.UtcNow;
                await table.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge);

                _logger.LogInformation("Order {OrderId} status -> {Status}", entity.RowKey, next);
            }
        }

        // ------------------------------------------------------------------
        // Poison queue: log the failure and keep the raw message for review
        // ------------------------------------------------------------------
        [Function("ProcessPoisonOrders")]
        public async Task ProcessPoisonOrders(
            [QueueTrigger(OrderQueueFunctions.QueueName + "-poison")] string poisonMessage,
            FunctionContext context)
        {
            var id = Meta(context, "Id");
            if (id == "?") id = Guid.NewGuid().ToString();
            _logger.LogCritical("POISON order message {MessageId} moved to poison queue after repeated failures: {Message}",
                id, poisonMessage);

            var failed = new TableEntity(DateTime.UtcNow.ToString("yyyy-MM-dd"), id)
            {
                ["RawMessage"] = poisonMessage.Length > 30000 ? poisonMessage[..30000] : poisonMessage,
                ["FailedAt"] = DateTimeOffset.UtcNow
            };
            await GetTable(FailedOrdersTable).UpsertEntityAsync(failed, TableUpdateMode.Replace);
        }

        // ------------------------------------------------------------------
        // Read endpoints (used by Postman to verify status updates live)
        // ------------------------------------------------------------------
        [Function("GetOrder")]
        public async Task<HttpResponseData> GetOrder(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{orderDate}/{orderId}")] HttpRequestData req,
            string orderDate, string orderId)
        {
            try
            {
                var result = await GetTable(OrdersTable).GetEntityAsync<OrderEntity>(orderDate, orderId);
                return await Reply(req, HttpStatusCode.OK, ToDto(result.Value));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return await Reply(req, HttpStatusCode.NotFound,
                    new { error = $"Order '{orderId}' for date '{orderDate}' not found (it may still be waiting in the queue)." });
            }
        }

        [Function("GetOrders")]
        public async Task<HttpResponseData> GetOrders(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var date = query["date"];

            var orders = new List<OrderDto>();
            var table = GetTable(OrdersTable);
            var pages = string.IsNullOrWhiteSpace(date)
                ? table.QueryAsync<OrderEntity>()
                : table.QueryAsync<OrderEntity>(o => o.PartitionKey == date);

            await foreach (var o in pages) orders.Add(ToDto(o));

            return await Reply(req, HttpStatusCode.OK, orders.OrderByDescending(o => o.OrderTimestamp));
        }

        [Function("GetFailedOrders")]
        public async Task<HttpResponseData> GetFailedOrders(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/failed")] HttpRequestData req)
        {
            var failed = new List<object>();
            await foreach (var e in GetTable(FailedOrdersTable).QueryAsync<TableEntity>())
            {
                failed.Add(new
                {
                    messageId = e.RowKey,
                    failedAt = e.GetDateTimeOffset("FailedAt"),
                    rawMessage = e.GetString("RawMessage")
                });
            }
            return await Reply(req, HttpStatusCode.OK, failed);
        }

        private static async Task<HttpResponseData> Reply(HttpRequestData req, HttpStatusCode status, object body)
        {
            var res = req.CreateResponse(status);
            await res.WriteAsJsonAsync(body);
            res.StatusCode = status;
            return res;
        }
    }
}
