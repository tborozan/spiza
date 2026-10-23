# Spiza API

**Croatian grocery prices, in one API.**

Spiza collects public retailer price lists and serves them through a lightweight REST API and an MCP endpoint. Look up products by barcode or text, compare prices by city and store, and explore price history.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![SQLite](https://img.shields.io/badge/storage-SQLite-003B57?logo=sqlite&logoColor=white)
[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![CI](https://github.com/tborozan/spiza/actions/workflows/ci.yml/badge.svg)](https://github.com/tborozan/spiza/actions/workflows/ci.yml)

## Quick start

Requires Docker Compose.

```sh
cp .env.example .env
docker compose up --build -d
```

Spiza listens on **http://localhost:7492**. On startup it applies EF Core migrations, creates an API key if `SPIZA_API_KEY` is unset, and begins refreshing enabled public sources. The image checks `/health/ready` every 30 seconds and reports the container unhealthy until the database is reachable, the initial pass across all configured sources has finished, and at least one price observation has been imported. A source failure still counts as a finished attempt and is reported in coverage. The database and generated key persist in the `spiza_data` volume.

```sh
curl http://localhost:7492/health/ready

API_KEY="$(docker compose exec -T spiza cat /data/.spiza-api-key)"
curl -H "Authorization: Bearer ${API_KEY}" \
  'http://localhost:7492/api/v1/prices/current?barcode=REPLACE_WITH_BARCODE&city=Split&fallbackAreas=Solin&fallbackAreas=Ka%C5%A1tela'
```

Set `SPIZA_API_KEY` in `.env` to provide your own key; use that value in requests instead of reading the generated key file. Health checks are public; REST and MCP endpoints require the bearer key.

For quick testing in Rider, open [`Spiza.Api.http`](Spiza.Api.http), set `@apiKey` and `@barcode`, and run a request with the HTTP Client.

## What you can do

- Search products by barcode or text and compare prices across retailers, cities, and stores.
- Read regular, promotional, effective, and unit prices, plus price history.
- Filter by location and pass ordered fallback areas with each lookup.
- Check source freshness and retailer and area coverage.
- Look up barcodes in batches or export prices as JSON and CSV.
- Use the same data through REST or MCP.

Price results include clear statuses such as `unknown_product`, `no_local_price`, `incomplete_data`, and `source_error`. Quality flags identify issues such as duplicate products, barcode mismatches, and inconsistent units. Each price row also includes `effectivePriceUsable`: it is true for a valid EUR price when the only quality issue is `unit_inconsistent`; the unit metadata still needs review. A missing regular and promotional price stays null even when the source provides a unit price.

Product `quantity` is normalized to `kg`, `l`, or `piece`; `unitPriceUnit` uses the same canonical denominator, so a price unit is expressed per kilogram, liter, or piece. Coverage status is reported separately from a valid product-price record.

## API

All REST routes are under `/api/v1` and require `Authorization: Bearer <API_KEY>`.

| Method and route | Description |
| --- | --- |
| `GET /products?q=...` or `?barcode=...` | Search products; optional `retailer` and `limit` filters |
| `GET /prices/current?barcode=...` | Current prices; also accepts `q`, `city`, repeated `fallbackAreas`, `storeId`, and `retailer` |
| `POST /prices/batch` | Current prices for multiple barcodes |
| `GET /prices/history?barcode=...` | History; optional `city`, repeated `fallbackAreas`, `storeId`, `retailer`, `from`, and `to` |
| `GET /coverage` | Data freshness and retailer and city coverage |
| `GET /export/prices?format=json` | Export current prices; accepts `city`, repeated `fallbackAreas`, and `retailer`; use `format=csv` for CSV |
| `POST /admin/refresh` | Queue a source refresh |
| `GET /health/live`, `GET /health/ready` | Public liveness and data readiness checks |

For Split and Solin lookups, both cities are primary areas. Spiza checks fallback areas only for a retailer with no observation in either primary city; each retailer makes that choice independently. dm's national prices are included in local results. Pass `fallbackAreas` in priority order on the request; batch requests take the same list as a JSON array. History applies these filters before returning observations, so Hermes can request local history without retrieving every branch nationally.

A price result describes the returned product and observations. It does not claim that all retailers have completed ingestion. Check `/api/v1/coverage` for each retailer's `pending`, `running`, `partial`, `failed`, `stale`, or `succeeded` state and its observed store and product counts before comparing market-wide prices.

Batch request example:

```sh
curl -X POST 'http://localhost:7492/api/v1/prices/batch' \
  -H "Authorization: Bearer ${API_KEY}" \
  -H 'Content-Type: application/json' \
  -d '{"barcodes":["REPLACE_WITH_BARCODE_1","REPLACE_WITH_BARCODE_2"],"city":"Split","fallbackAreas":["Kaštela"]}'
```

## MCP

Connect an MCP client to **http://localhost:7492/mcp** using Streamable HTTP and the same bearer key. Available tools cover product search, current and batch prices, price history, and coverage. Current, batch, and history tools accept request-level location fallback areas.

## Retailer data

Adapters use publicly available price lists for **Kaufland, Konzum, dm, Eurospin, Lidl, Metro, Plodine, Ribola, SPAR, and Tommy**. dm provides a nationwide list; other sources include branch-level data where published. Coverage varies by retailer and area; check `/api/v1/coverage` for the latest results. Retailer source formats were researched with [`senko/cijene-api`](https://github.com/senko/cijene-api); Spiza's adapters are implemented independently in C#.

Spiza keeps price history for 30 days by default. Its default effective-price rule uses an active promotional price when available, otherwise the regular price.

## Configuration

Every runtime setting has a default, so no variable is required for a basic Compose deployment. Copy [.env.example](.env.example) to `.env` and change the values you need. The full list and defaults are in the sample file; Compose wiring is in [docker-compose.yml](docker-compose.yml).

| Common setting | Default | Description |
| --- | --- | --- |
| `SPIZA_HOST_PORT` | `7492` | Host port exposed by Compose |
| `SPIZA_PORT` | `7492` | Container listen port |
| `SPIZA_API_KEY` | Generated | API and MCP bearer key; generated key is saved in the data volume |
| `SPIZA_IMAGE` | `spiza-api:local` | Image to run with Compose |
| `SPIZA_REFRESH_ON_START` | `true` | Refresh public sources on startup |
| `SPIZA_REFRESH_INTERVAL_MINUTES` | `1440` | Refresh cadence; the default daily cadence is anchored to the configured local time |
| `SPIZA_REFRESH_DAILY_AT` | `00:05` | Daily refresh time, in `SPIZA_TIME_ZONE_ID`, with a short buffer after midnight |
| `SPIZA_REFRESH_RETRY_INTERVAL_MINUTES` | `15` | First retry delay for failed, incomplete, or outdated source data |
| `SPIZA_REFRESH_RETRY_MAX_INTERVAL_MINUTES` | `240` | Maximum retry delay; it doubles between retries until data is current |
| `SPIZA_TIME_ZONE_ID` | `Europe/Zagreb` | Time zone used for daily scheduling and deciding which price-list date is current |
| `SPIZA_PRIMARY_CITIES` | `Split,Solin` | Comma-separated cities imported for each retailer when it has a store in a primary city |
| `SPIZA_FALLBACK_CITIES` | `Kaštela` | Comma-separated fallback cities, in priority order; used only for a retailer with no primary-city store |
| `SPIZA_EFFECTIVE_PRICE_RULE` | `promo-if-active` | Use `regular` to always prefer the regular price |
| `SPIZA_LOG_LEVEL` | `Information` | Set to `Debug` to log source fetch and per-list import timings |
| `SPIZA_SOURCE_ARCHIVE_MAX_EXPANDED_BYTES` | `1000000000` | Maximum combined expanded size of the selected CSV files from retailer ZIP feeds |

With the default 1,440-minute interval, Spiza refreshes daily at `SPIZA_REFRESH_DAILY_AT`; other interval values use a fixed delay after each run. At the daily run, Spiza selects each retailer's newest public price list dated on or before the current local date. If a source fails, returns incomplete lists, or only has an older list, Spiza retries that source with increasing delays until current data arrives or the next daily run. `/api/v1/coverage` reports each retailer's latest refresh status and latest imported price date.

Each retailer can be enabled or disabled with `SPIZA_<RETAILER>_ENABLED`, for example `SPIZA_KAUFLAND_ENABLED=false`. Other settings control database storage, request retries and pacing, history retention, batch and search limits, export size, SQLite lock waits, and logging.

Refreshes request per-store files only for the configured primary cities. A retailer's fallback data are requested only when it has no primary-city store, using the first configured fallback city with a store. The nationwide dm price list is retained. Eurospin and Plodine publish one ZIP for all stores, so Spiza downloads the bundle but expands only selected store files. Ribola's XML links do not identify their city, so Spiza must fetch each XML to select the relevant stores. These settings limit future imports; changing them does not remove older observations already in the database.

The full T9 spelling of **SPIZA** is `77492`, above the TCP port limit of `65535`; the default port is `7492`.

## Build and publish

Run the test suite locally:

```sh
dotnet test Spiza.Api.Tests/Spiza.Api.Tests.csproj
```

The [GitHub Actions workflow](.github/workflows/ci.yml) runs tests and builds the application and container for pull requests and pushes to `main` or version tags. Pushes to `main` publish [ghcr.io/tborozan/spiza-api:edge](https://github.com/tborozan/spiza/pkgs/container/spiza-api); `v*` tags publish versioned images. After the first publish, set the GHCR package visibility to public if public image pulls are intended. To run a published image, set `SPIZA_IMAGE=ghcr.io/tborozan/spiza-api:edge` in `.env`, then run `docker compose up -d --pull always`.

Contributions are welcome. Please run the test suite before opening a pull request.

## License

[MIT](LICENSE)
