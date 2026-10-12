# Architecture – Coffee N Chill Functions (Parts 1 & 2)

```
 Postman / client
        │  HTTP (http://localhost:7071/api/...)
        ▼
 ┌──────────────────────────────────────────┐
 │ Azure Functions (.NET 8 isolated worker) │
 │  MenuFunctions      DocumentFunctions    │
 └──────────┬───────────────────┬───────────┘
            │ Azure.Data.Tables │ Azure.Storage.Blobs
            ▼                   ▼
   Table "MenuItems"     Blob container "staff-docs"
        └──────── Azurite (local) / Azure Storage ────────┘
```

## Menu storage (Table Storage)
| Column | Mapping | Example |
|---|---|---|
| PartitionKey | Category | `Hot Drinks` |
| RowKey | SKU | `COF-001` |
| Name, Description, Price, IsAvailable | properties | |

Partitioning by category makes `GET /api/menu/category/{category}` a single-partition query (fast and cheap).
`MenuItem` (table entity) is mapped to `MenuItemDto` (API contract) so storage keys are never leaked directly.

## Endpoints
| Method | Route | Purpose | Status codes |
|---|---|---|---|
| POST | /api/menu | Create item | 201, 400, 409 |
| GET | /api/menu | List all | 200 |
| GET | /api/menu/category/{category} | Filter by category | 200, 404 |
| GET | /api/menu/{category}/{id} | Get one | 200, 404 |
| PUT | /api/menu/{category}/{id} | Update item | 200, 400, 404, 409 |
| DELETE | /api/menu/{category}/{id} | Delete item | 204, 404 |
| POST | /api/documents/upload | Upload (multipart/form-data) | 201, 400, 413, 415 |
| GET | /api/documents | List documents + metadata | 200 |
| GET | /api/documents/download/{fileName} | Stream download | 200, 404 |

## Document storage (Blob Storage)
* Container `staff-docs`, created automatically on first use.
* Uploads validated for extension + matching MIME type (pdf, txt, csv, png, jpg, docx, xlsx) and a 10 MB cap.
* Metadata stored per blob: original name, upload time, size.
* Downloads are streamed straight from storage to the HTTP response.
* Errors are logged through `ILogger`.

> Why Blob and not Azure Files? Azurite emulates Blob, Queue and Table only – Azure Files shares are not supported locally.

## Part 2 – order queue
| Component | Detail |
|---|---|
| Queue | `order-processing-queue` (Base64 JSON messages), poison queue `order-processing-queue-poison` |
| Producer | `POST /api/orders/queue` → validates, serialises, enqueues, returns 202 |
| Consumer | `ProcessOrderQueue` (`[QueueTrigger]`) → Orders table (`PartitionKey` = OrderDate, `RowKey` = OrderId) |
| Lifecycle | Received → Preparing → Ready → Collected (delay set by `ORDER_STEP_DELAY_SECONDS`) |
| Failures | 3 attempts (`maxDequeueCount`), then poison queue → `ProcessPoisonOrders` logs critical + stores in `FailedOrders` |

```
 docker compose up
   ├─ azurite   (Blob 10000 / Queue 10001 / Table 10002, volume azurite-data)
   └─ functions (<user>/coffeenchill-functions:v2.0 from Docker Hub, host port 8080)
        network: coffeenchill-net (bridge)
```

## Team contributions
| Member | Student no. | Contribution |
|---|---|---|
| _fill in_ | | |
