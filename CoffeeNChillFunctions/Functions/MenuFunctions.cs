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
    /// Menu management CRUD against the "MenuItems" table.
    /// PartitionKey = Category, RowKey = SKU.
    /// </summary>
    public class MenuFunctions(ILoggerFactory loggerFactory)
    {
        private readonly ILogger _logger = loggerFactory.CreateLogger<MenuFunctions>();
        private const string TableName = "MenuItems";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static TableClient GetTableClient()
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var tableClient = new TableServiceClient(connectionString).GetTableClient(TableName);
            tableClient.CreateIfNotExists();
            return tableClient;
        }

        // ---------- helpers ----------

        private static MenuItemDto ToDto(MenuItem e) => new()
        {
            Category = e.PartitionKey,
            Sku = e.RowKey,
            Name = e.Name,
            Description = e.Description,
            Price = e.Price,
            IsAvailable = e.IsAvailable
        };

        private static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode status, string message)
        {
            var res = req.CreateResponse(status);
            await res.WriteAsJsonAsync(new { error = message });
            res.StatusCode = status;
            return res;
        }

        private static async Task<HttpResponseData> Json(HttpRequestData req, HttpStatusCode status, object body)
        {
            var res = req.CreateResponse(status);
            await res.WriteAsJsonAsync(body);
            res.StatusCode = status;
            return res;
        }

        /// <summary>Returns a validation message, or null when the DTO is valid.</summary>
        private static string? Validate(MenuItemDto dto, bool requireKeys)
        {
            if (requireKeys && (string.IsNullOrWhiteSpace(dto.Category) || string.IsNullOrWhiteSpace(dto.Sku)))
                return "Category and Sku are required.";
            if (string.IsNullOrWhiteSpace(dto.Name))
                return "Name is required.";
            if (dto.Name.Length > 100)
                return "Name must be 100 characters or fewer.";
            if (dto.Price < 0 || double.IsNaN(dto.Price) || double.IsInfinity(dto.Price))
                return "Price must be a non-negative number.";
            if (requireKeys && (dto.Category.Any(IsForbiddenKeyChar) || dto.Sku.Any(IsForbiddenKeyChar)))
                return "Category and Sku cannot contain '/', '\\', '#', '?' or control characters.";
            return null;
        }

        private static bool IsForbiddenKeyChar(char c) => c is '/' or '\\' or '#' or '?' || char.IsControl(c);

        private static async Task<(MenuItemDto? Dto, string? Error)> ReadBody(HttpRequestData req)
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return (null, "Request body is required.");
            try
            {
                var dto = JsonSerializer.Deserialize<MenuItemDto>(body, JsonOptions);
                return dto is null ? (null, "Request body is required.") : (dto, null);
            }
            catch (JsonException)
            {
                return (null, "Malformed JSON body.");
            }
        }

        // ---------- CREATE ----------

        [Function("CreateMenuItem")]
        public async Task<HttpResponseData> CreateMenuItem(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "menu")] HttpRequestData req)
        {
            var (dto, bodyError) = await ReadBody(req);
            if (dto is null) return await Error(req, HttpStatusCode.BadRequest, bodyError!);

            var validation = Validate(dto, requireKeys: true);
            if (validation is not null) return await Error(req, HttpStatusCode.BadRequest, validation);

            var entity = new MenuItem
            {
                PartitionKey = dto.Category.Trim(),
                RowKey = dto.Sku.Trim(),
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim() ?? string.Empty,
                Price = dto.Price,
                IsAvailable = dto.IsAvailable
            };

            try
            {
                await GetTableClient().AddEntityAsync(entity);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                return await Error(req, HttpStatusCode.Conflict,
                    $"Item '{entity.RowKey}' already exists in category '{entity.PartitionKey}'.");
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Create failed for {Sku}", entity.RowKey);
                return await Error(req, HttpStatusCode.InternalServerError, "Could not create the menu item.");
            }

            _logger.LogInformation("Created menu item {Sku} in category {Category}", entity.RowKey, entity.PartitionKey);
            return await Json(req, HttpStatusCode.Created, ToDto(entity));
        }

        // ---------- READ ----------

        [Function("GetAllMenuItems")]
        public async Task<HttpResponseData> GetAllMenuItems(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menu")] HttpRequestData req)
        {
            var items = new List<MenuItemDto>();
            await foreach (var item in GetTableClient().QueryAsync<MenuItem>())
                items.Add(ToDto(item));

            return await Json(req, HttpStatusCode.OK, items);
        }

        [Function("GetMenuItemsByCategory")]
        public async Task<HttpResponseData> GetMenuItemsByCategory(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menu/category/{category}")] HttpRequestData req,
            string category)
        {
            var items = new List<MenuItemDto>();
            await foreach (var item in GetTableClient().QueryAsync<MenuItem>(x => x.PartitionKey == category))
                items.Add(ToDto(item));

            if (items.Count == 0)
                return await Error(req, HttpStatusCode.NotFound, $"No items found in category '{category}'.");

            return await Json(req, HttpStatusCode.OK, items);
        }

        [Function("GetMenuItem")]
        public async Task<HttpResponseData> GetMenuItem(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menu/{category}/{id}")] HttpRequestData req,
            string category, string id)
        {
            try
            {
                var result = await GetTableClient().GetEntityAsync<MenuItem>(category, id);
                return await Json(req, HttpStatusCode.OK, ToDto(result.Value));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return await Error(req, HttpStatusCode.NotFound, $"Item '{id}' in category '{category}' not found.");
            }
        }

        // ---------- UPDATE ----------

        [Function("UpdateMenuItem")]
        public async Task<HttpResponseData> UpdateMenuItem(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "menu/{category}/{id}")] HttpRequestData req,
            string category, string id)
        {
            var (updates, bodyError) = await ReadBody(req);
            if (updates is null) return await Error(req, HttpStatusCode.BadRequest, bodyError!);

            var validation = Validate(updates, requireKeys: false);
            if (validation is not null) return await Error(req, HttpStatusCode.BadRequest, validation);

            var tableClient = GetTableClient();
            MenuItem existing;
            try
            {
                existing = (await tableClient.GetEntityAsync<MenuItem>(category, id)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return await Error(req, HttpStatusCode.NotFound, $"Item '{id}' in category '{category}' not found.");
            }

            // Category (PartitionKey) and Sku (RowKey) are immutable; everything else can change.
            existing.Name = updates.Name.Trim();
            existing.Description = updates.Description?.Trim() ?? string.Empty;
            existing.Price = updates.Price;
            existing.IsAvailable = updates.IsAvailable;

            try
            {
                await tableClient.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace);
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                return await Error(req, HttpStatusCode.Conflict, "The item was modified by someone else. Please retry.");
            }

            _logger.LogInformation("Updated menu item {Sku} in category {Category}", existing.RowKey, existing.PartitionKey);
            return await Json(req, HttpStatusCode.OK, ToDto(existing));
        }

        // ---------- DELETE ----------

        [Function("DeleteMenuItem")]
        public async Task<HttpResponseData> DeleteMenuItem(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "menu/{category}/{id}")] HttpRequestData req,
            string category, string id)
        {
            var tableClient = GetTableClient();

            try
            {
                await tableClient.GetEntityAsync<MenuItem>(category, id);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return await Error(req, HttpStatusCode.NotFound, $"Item '{id}' in category '{category}' not found.");
            }

            await tableClient.DeleteEntityAsync(category, id);
            _logger.LogInformation("Deleted menu item {Sku} in category {Category}", id, category);

            return req.CreateResponse(HttpStatusCode.NoContent);
        }
    }
}
