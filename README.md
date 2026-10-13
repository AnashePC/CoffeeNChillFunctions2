# Coffee N Chill – Azure Functions (CLDV6212 Parts 1 & 2)

.NET 8 isolated Azure Functions API for the Coffee N Chill cafe:
menu management (Azure Table Storage), staff document storage (Azure Blob Storage) and **asynchronous order processing (Azure Storage Queue → Orders table)**, runnable locally against **Azurite**, as a standalone **Docker** image, or as a full stack with **Docker Compose**.

* Part 1 video: **<paste unlisted YouTube link here>**
* Part 2 video (docker-compose up + queue testing): **<paste unlisted YouTube link here>**
* Docker Hub images: **apchoruma/coffeenchill-functions:v1.0** (Part 1) and **:v2.0** (Part 2)
* Architecture & endpoint table: [docs/architecture.md](docs/architecture.md)

## Repository layout
```
CoffeeNChillFunctions/      Function app (Functions/, Models/, Program.cs)
docs/                       Architecture + notes
postman/                    Postman collection + Local/Docker environments
Dockerfile                  Multi-stage build (build context = repo root)
docker-compose.yml          Azurite + Functions host (image pulled from Docker Hub)
.env.example / .env         DOCKERHUB_USERNAME used by docker-compose.yml
```

## Prerequisites
.NET 8 SDK · Azure Functions Core Tools v4 · Node.js (for Azurite) · Docker Desktop · Postman

## Run locally (Azurite + Functions)
1. Start Azurite (new terminal): `npm install -g azurite` then
   `azurite --silent --location ./azurite-data --skipApiVersionCheck`
   (Visual Studio users can use the built-in Azurite instead.)
2. Check `CoffeeNChillFunctions/local.settings.json` contains:
   `"AzureWebJobsStorage": "UseDevelopmentStorage=true"` and `"FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"`.
3. `cd CoffeeNChillFunctions` then `func start` (or press F5 in Visual Studio).
4. API is at `http://localhost:7071/api`. The `MenuItems` table and `staff-docs` container are created on first request.

## Run with Docker (standalone, no Compose)
```bash
# 1. Azurite must accept connections from the container
azurite --silent --location ./azurite-data --skipApiVersionCheck --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0

# 2. Build and tag
docker build -t apchoruma/coffeenchill-functions:v1.0 .

# 3. Run – storage connection string is passed via the CLI, port 8080 -> 80
docker run --rm -p 8080:80 \
  -e "AzureWebJobsStorage=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://host.docker.internal:10000/devstoreaccount1;TableEndpoint=http://host.docker.internal:10002/devstoreaccount1;QueueEndpoint=http://host.docker.internal:10001/devstoreaccount1;" \
  apchoruma/coffeenchill-functions:v1.0
```
On Linux add `--add-host=host.docker.internal:host-gateway`. The API is then at `http://localhost:8080/api`.
(The account key above is Azurite's public well-known development key, not a secret.)

### Publish to Docker Hub
```bash
docker login
docker push apchoruma/coffeenchill-functions:v1.0
docker tag  apchoruma/coffeenchill-functions:v1.0 apchoruma/coffeenchill-functions:latest
docker push apchoruma/coffeenchill-functions:latest
```
Make sure the repository is set to **Public** on Docker Hub.

## Testing with Postman
1. Import `postman/CoffeeNChill.postman_collection.json` and one of the environments in `postman/`.
2. Select the environment (all URLs use `{{baseUrl}}`).
3. For the two upload requests, click **Select Files** on the `file` row and choose a file.
4. Run the collection top-to-bottom (Collection Runner) – every request has saved tests.

## Endpoints
| Method | Route | Description |
|---|---|---|
| POST | /api/menu | Create menu item |
| GET | /api/menu | All items |
| GET | /api/menu/category/{category} | Items in a category |
| GET | /api/menu/{category}/{id} | Single item |
| PUT | /api/menu/{category}/{id} | Update item |
| DELETE | /api/menu/{category}/{id} | Delete item |
| POST | /api/documents/upload | Upload a file (multipart/form-data, field `file`) |
| GET | /api/documents | List files with size/date/type |
| GET | /api/documents/download/{fileName} | Download a file |

Sample create body:
```json
{ "category": "Hot Drinks", "sku": "COF-001", "name": "Flat White", "description": "Double shot", "price": 42.5, "isAvailable": true }
```

---

# Part 2 – Asynchronous order queue + Docker Compose

During break, hundreds of students order at once. `POST /api/orders/queue` therefore **does not wait for the barista**: it validates the order, puts it on `order-processing-queue` and returns `202 Accepted`. A queue-triggered function then writes the order to the `Orders` table and advances its status.

```
Client ─POST /api/orders/queue─► QueueOrder ──(Base64 JSON)──► order-processing-queue
                                                                   │ [QueueTrigger]
                                                                   ▼
                                            ProcessOrderQueue ─► Orders table
                                            Received → Preparing → Ready → Collected
                                                                   │ 3 failed attempts
                                                                   ▼
                              order-processing-queue-poison ─► ProcessPoisonOrders ─► FailedOrders table + critical log
```

### Run everything with one command (Docker Compose)
```bash
cp .env.example .env            # then set DOCKERHUB_USERNAME=<your Docker Hub username>
docker compose up               # pulls <username>/coffeenchill-functions:v2.0 + Azurite and starts both
```
* Functions API: `http://localhost:8080/api` (use the **Docker** Postman environment)
* Azurite: Blob `10000`, Queue `10001`, Table `10002`; data persists in the `azurite-data` volume
* Services share the custom bridge network `coffeenchill-net`; the Functions container starts only after Azurite is healthy
* `docker compose down` stops the stack (`down -v` also wipes the Azurite data)

### Build & publish the v2.0 image (do this before `docker compose up`)
```bash
docker build -t apchoruma/coffeenchill-functions:v2.0 .
docker login
docker push apchoruma/coffeenchill-functions:v2.0
```
`docker-compose.yml` uses **only** `image:` (no `build:`), so the image must be on Docker Hub (public repo).

### Part 2 endpoints
| Method | Route | Description |
|---|---|---|
| POST | /api/orders/queue | Validate order, push JSON to `order-processing-queue` → 202 |
| GET | /api/orders/{orderDate}/{orderId} | Read one order from the Orders table (live status) |
| GET | /api/orders?date=yyyy-MM-dd | List orders (optionally for one date) |
| GET | /api/orders/failed | Messages that ended in the poison queue |
| POST | /api/orders/queue/test-poison | **Demo only** – pushes a raw bad message; active only when `ENABLE_TEST_ENDPOINTS=true` |

Order message (field names exactly as specified):
```json
{ "OrderId": "ORD-2026-8801", "CustomerName": "Jane Smith", "SelectedItemSKUs": ["COF-001", "PAS-104"], "TotalPrice": 65.00, "OrderTimestamp": "2026-10-12T10:15:00Z" }
```
`OrderTimestamp` is generated server-side if omitted. Orders table: `PartitionKey = OrderDate (yyyy-MM-dd)`, `RowKey = OrderId`, `Status`.

### Settings
| Setting | Default | Purpose |
|---|---|---|
| `ORDER_STEP_DELAY_SECONDS` | 3 | Pause between simulated barista steps |
| `ENABLE_TEST_ENDPOINTS` | false (true in compose) | Enables the poison-queue demo endpoint |
| `host.json → queues.maxDequeueCount` | 3 | Attempts before a message is moved to the poison queue |

### Testing Part 2 in Postman
Import the updated collection and the **Docker** environment, then run the **Orders (Part 2)** folder in order (Collection Runner, ~1.5 min because some requests deliberately wait for the background function):
1. *Producer* – valid order (202), server-generated timestamp, and 400s for each validation rule.
2. *Lifecycle* – the same order is polled at ~4s, ~8s and ~14s: Received/Preparing → Ready → **Collected**.
3. *Poison queue* – a malformed message is pushed; after ~45s it appears in `/api/orders/failed`.

You can also watch it live: `docker compose logs -f functions`.

## Changelog
### v2.0 – Part 2 (async orders + Compose)
* Added `POST /api/orders/queue` producer: field validation, JSON serialisation, Base64 queue encoding, server-side timestamp, 202 response, 503 when the queue is unreachable.
* Added `ProcessOrderQueue` (`[QueueTrigger("order-processing-queue")]`): parses JSON, writes the `Orders` table and simulates Received → Preparing → Ready → Collected.
* Added poison-queue handling (`maxDequeueCount = 3`), `ProcessPoisonOrders` logger and `FailedOrders` table.
* Added order read endpoints, `OrderMessage` / `OrderEntity` / `OrderDto` models.
* Added `docker-compose.yml` (Azurite + Functions from Docker Hub `:v2.0`, bridge network, persistent volume, health check) and `.env.example`.
* Extended the Postman collection with the Orders folder (producer, lifecycle, poison queue) and saved tests.
* Added `Azure.Storage.Queues` and the Storage Queues Functions extension.

### v1.0 – Part 1
* Menu CRUD + category filter on Azure Table Storage with validation and DTO mapping.
* Staff document upload/list/download on Azure Blob Storage (`staff-docs`) with type/size validation and streamed downloads.
* Standalone multi-stage Dockerfile, Postman collection with `{{baseUrl}}`, documentation.

## Team contributions
| Member | Student no. | Role / contribution |
|---|---|---|
| _fill in_ | | |
