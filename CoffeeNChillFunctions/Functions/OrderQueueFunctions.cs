using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Queues;
using CoffeeNChillFunctions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CoffeeNChillFunctions.Functions
{
    /// <summary>Queue producer: POST /api/orders/queue pushes an order onto order-processing-queue.</summary>
    public partial class OrderQueueFunctions(ILoggerFactory loggerFactory)
    {
        public const string QueueName = "order-processing-queue";

        private readonly ILogger _logger = loggerFactory.CreateLogger<OrderQueueFunctions>();

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        // Keep PascalCase property names so the message matches the spec exactly.
        private static readonly JsonSerializerOptions WriteOptions = new() { PropertyNamingPolicy = null };

        [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_]{2,49}$")]
        private static partial Regex OrderIdPattern();

        /// <summary>
        /// Messages are Base64 encoded: the Functions queue trigger expects Base64 payloads,
        /// and it also decodes them for us on the consuming side.
        /// </summary>
        private static QueueClient GetQueueClient()
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var queue = new QueueClient(connectionString, QueueName,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
            queue.CreateIfNotExists();
            return queue;
        }

        private static async Task<HttpResponseData> Reply(HttpRequestData req, HttpStatusCode status, object body)
        {
            var res = req.CreateResponse(status);
            await res.WriteAsJsonAsync(body);
            res.StatusCode = status;
            return res;
        }

        /// <summary>Returns a list of problems; empty means valid.</summary>
        private static List<string> Validate(OrderMessage o)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(o.OrderId) || !OrderIdPattern().IsMatch(o.OrderId.Trim()))
                errors.Add("OrderId is required (3-50 chars: letters, digits, '-' or '_', e.g. ORD-2026-8801).");

            if (string.IsNullOrWhiteSpace(o.CustomerName))
                errors.Add("CustomerName is required.");
            else if (o.CustomerName.Length > 100)
                errors.Add("CustomerName must be 100 characters or fewer.");

            if (o.SelectedItemSKUs is null || o.SelectedItemSKUs.Count == 0)
                errors.Add("SelectedItemSKUs must contain at least one SKU.");
            else if (o.SelectedItemSKUs.Any(string.IsNullOrWhiteSpace))
                errors.Add("SelectedItemSKUs cannot contain blank values.");
            else if (o.SelectedItemSKUs.Count > 50)
                errors.Add("SelectedItemSKUs cannot contain more than 50 items.");

            if (o.TotalPrice <= 0)
                errors.Add("TotalPrice must be greater than zero.");

            return errors;
        }

        /// <summary>
        /// TEST HOOK (demo of poison-queue handling): pushes the raw request body onto the queue with NO validation.
        /// Only active when ENABLE_TEST_ENDPOINTS=true (set in docker-compose.yml); otherwise returns 404.
        /// </summary>
        [Function("QueueRawOrderForTesting")]
        public async Task<HttpResponseData> QueueRawOrderForTesting(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/queue/test-poison")] HttpRequestData req)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ENABLE_TEST_ENDPOINTS"), "true", StringComparison.OrdinalIgnoreCase))
                return await Reply(req, HttpStatusCode.NotFound, new { error = "Not found." });

            var raw = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrEmpty(raw))
                return await Reply(req, HttpStatusCode.BadRequest, new { error = "Body is required." });

            await GetQueueClient().SendMessageAsync(raw);
            _logger.LogWarning("TEST: raw message pushed to {Queue} without validation: {Raw}", QueueName, raw);
            return await Reply(req, HttpStatusCode.Accepted,
                new { message = "Raw message queued. It should fail processing and land in order-processing-queue-poison." });
        }

        [Function("QueueOrder")]
        public async Task<HttpResponseData> QueueOrder(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/queue")] HttpRequestData req)
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return await Reply(req, HttpStatusCode.BadRequest, new { error = "Request body is required." });

            OrderMessage? order;
            try
            {
                order = JsonSerializer.Deserialize<OrderMessage>(body, ReadOptions);
            }
            catch (JsonException)
            {
                return await Reply(req, HttpStatusCode.BadRequest, new { error = "Malformed JSON body." });
            }

            if (order is null)
                return await Reply(req, HttpStatusCode.BadRequest, new { error = "Request body is required." });

            var errors = Validate(order);
            if (errors.Count > 0)
                return await Reply(req, HttpStatusCode.BadRequest, new { error = "Validation failed.", details = errors });

            // Normalise + generate the timestamp server-side when the client did not supply one.
            order.OrderId = order.OrderId.Trim();
            order.CustomerName = order.CustomerName.Trim();
            order.SelectedItemSKUs = order.SelectedItemSKUs.Select(s => s.Trim()).ToList();
            order.OrderTimestamp ??= DateTimeOffset.UtcNow;

            var json = JsonSerializer.Serialize(order, WriteOptions);

            try
            {
                await GetQueueClient().SendMessageAsync(json);
            }
            catch (Exception ex) when (ex is RequestFailedException or AggregateException or HttpRequestException)
            {
                _logger.LogError(ex, "Could not place order {OrderId} on {Queue}", order.OrderId, QueueName);
                return await Reply(req, HttpStatusCode.ServiceUnavailable,
                    new { error = "The order queue is currently unavailable. Please try again shortly." });
            }

            var orderDate = order.OrderTimestamp.Value.UtcDateTime.ToString("yyyy-MM-dd");
            _logger.LogInformation("Queued order {OrderId} for {Customer} ({Items} items, R{Total})",
                order.OrderId, order.CustomerName, order.SelectedItemSKUs.Count, order.TotalPrice);

            // 202 Accepted: the client is NOT blocked while the barista prepares the order.
            return await Reply(req, HttpStatusCode.Accepted, new
            {
                message = "Order accepted and queued for processing.",
                orderId = order.OrderId,
                orderDate,
                orderTimestamp = order.OrderTimestamp,
                statusUrl = $"/api/orders/{orderDate}/{order.OrderId}"
            });
        }
    }
}
