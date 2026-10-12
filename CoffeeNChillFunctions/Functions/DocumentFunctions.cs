using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace CoffeeNChillFunctions.Functions
{
    /// <summary>
    /// Staff document storage backed by the "staff-docs" Blob container
    /// (works against Azurite locally and Azure Storage in the cloud).
    /// </summary>
    public class DocumentFunctions(ILoggerFactory loggerFactory)
    {
        private readonly ILogger _logger = loggerFactory.CreateLogger<DocumentFunctions>();

        private const string ContainerName = "staff-docs";
        private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

        private static readonly Dictionary<string, string[]> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ["application/pdf"],
            [".txt"] = ["text/plain"],
            [".csv"] = ["text/csv", "application/vnd.ms-excel"],
            [".png"] = ["image/png"],
            [".jpg"] = ["image/jpeg"],
            [".jpeg"] = ["image/jpeg"],
            [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            [".xlsx"] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
        };

        private static BlobContainerClient GetContainerClient()
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var container = new BlobContainerClient(connectionString, ContainerName);
            container.CreateIfNotExists();
            return container;
        }

        private static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode status, string message)
        {
            var res = req.CreateResponse(status);
            await res.WriteAsJsonAsync(new { error = message });
            res.StatusCode = status;
            return res;
        }

        [Function("UploadStaffDocument")]
        public async Task<HttpResponseData> UploadStaffDocument(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "documents/upload")] HttpRequestData req)
        {
            try
            {
                if (!req.Headers.TryGetValues("Content-Type", out var ctValues) ||
                    !ctValues.First().Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    return await Error(req, HttpStatusCode.BadRequest, "Expected multipart/form-data with a file part.");
                }

                var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(ctValues.First()).Boundary).Value;
                if (string.IsNullOrWhiteSpace(boundary))
                    return await Error(req, HttpStatusCode.BadRequest, "Multipart boundary missing.");

                var reader = new MultipartReader(boundary, req.Body);
                MultipartSection? section;

                while ((section = await reader.ReadNextSectionAsync()) != null)
                {
                    var disposition = section.GetContentDispositionHeader();
                    if (disposition is null || !disposition.IsFileDisposition()) continue;

                    // Strip any path components so callers cannot write outside the container namespace.
                    var fileName = Path.GetFileName(disposition.FileName.Value?.Trim('"') ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(fileName))
                        return await Error(req, HttpStatusCode.BadRequest, "File name is missing.");

                    var extension = Path.GetExtension(fileName);
                    if (!AllowedTypes.TryGetValue(extension, out var allowedMimes))
                    {
                        return await Error(req, HttpStatusCode.UnsupportedMediaType,
                            $"File type '{extension}' is not allowed. Allowed: {string.Join(", ", AllowedTypes.Keys)}");
                    }

                    var mimeType = section.ContentType?.Split(';')[0].Trim() ?? "application/octet-stream";
                    if (!allowedMimes.Contains(mimeType, StringComparer.OrdinalIgnoreCase))
                    {
                        return await Error(req, HttpStatusCode.UnsupportedMediaType,
                            $"MIME type '{mimeType}' does not match the '{extension}' extension.");
                    }

                    // Buffer with a hard size cap (stream is read once, never loaded unbounded).
                    var buffer = new MemoryStream();
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await section.Body.ReadAsync(chunk)) > 0)
                    {
                        buffer.Write(chunk, 0, read);
                        if (buffer.Length > MaxFileSizeBytes)
                            return await Error(req, HttpStatusCode.RequestEntityTooLarge,
                                $"File exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB limit.");
                    }

                    if (buffer.Length == 0)
                        return await Error(req, HttpStatusCode.BadRequest, "Uploaded file is empty.");

                    buffer.Position = 0;
                    var blob = GetContainerClient().GetBlobClient(fileName);
                    var uploadedAt = DateTimeOffset.UtcNow;

                    await blob.UploadAsync(buffer, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = mimeType },
                        Metadata = new Dictionary<string, string>
                        {
                            ["originalName"] = fileName,
                            ["uploadedAt"] = uploadedAt.ToString("o"),
                            ["sizeBytes"] = buffer.Length.ToString()
                        }
                    });

                    _logger.LogInformation("Uploaded staff document {FileName} ({Size} bytes, {Mime})", fileName, buffer.Length, mimeType);

                    var created = req.CreateResponse(HttpStatusCode.Created);
                    await created.WriteAsJsonAsync(new { fileName, sizeBytes = buffer.Length, contentType = mimeType, uploadedAt });
                    created.StatusCode = HttpStatusCode.Created;
                    return created;
                }

                return await Error(req, HttpStatusCode.BadRequest, "No file found in request.");
            }
            catch (Exception ex) when (ex is RequestFailedException or IOException or InvalidDataException)
            {
                _logger.LogError(ex, "Upload failed");
                return await Error(req, HttpStatusCode.InternalServerError, "Upload failed. See server logs.");
            }
        }

        [Function("ListStaffDocuments")]
        public async Task<HttpResponseData> ListStaffDocuments(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents")] HttpRequestData req)
        {
            try
            {
                var results = new List<object>();
                await foreach (var item in GetContainerClient().GetBlobsAsync(new GetBlobsOptions { Traits = BlobTraits.Metadata }))
                {
                    results.Add(new
                    {
                        fileName = item.Name,
                        sizeBytes = item.Properties.ContentLength,
                        contentType = item.Properties.ContentType,
                        uploadedAt = item.Properties.CreatedOn,
                        lastModified = item.Properties.LastModified
                    });
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(results);
                return response;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Listing documents failed");
                return await Error(req, HttpStatusCode.InternalServerError, "Could not list documents.");
            }
        }

        [Function("DownloadStaffDocument")]
        public async Task<HttpResponseData> DownloadStaffDocument(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/download/{fileName}")] HttpRequestData req,
            string fileName)
        {
            try
            {
                var safeName = Path.GetFileName(fileName);
                var blob = GetContainerClient().GetBlobClient(safeName);

                if (!await blob.ExistsAsync())
                {
                    _logger.LogWarning("Download requested for missing document {FileName}", safeName);
                    return await Error(req, HttpStatusCode.NotFound, $"'{safeName}' not found.");
                }

                BlobDownloadStreamingResult download = (await blob.DownloadStreamingAsync()).Value;

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", download.Details.ContentType ?? "application/octet-stream");
                response.Headers.Add("Content-Disposition", $"attachment; filename=\"{safeName}\"");
                await download.Content.CopyToAsync(response.Body); // streamed, not buffered
                return response;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Download failed for {FileName}", fileName);
                return await Error(req, HttpStatusCode.InternalServerError, "Download failed.");
            }
        }
    }
}
