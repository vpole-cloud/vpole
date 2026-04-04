# Search

Pole search uses two MongoDB projections inside `Pole.Features.Search`.

## Overview

- `SearchDocuments` stores weighted key rows.
- `SearchSourceDocuments` stores one document per source entity for query-time reads.

This split keeps projection building simple while making reads cheaper than grouping row-level matches on every query.

## Projections

### `SearchDocuments`

One row per searchable key.

Example:

```json
{
  "SourceType": "WebService",
  "SourceId": "ws-1",
  "KeyType": "Name",
  "SearchValue": "blog-microservice",
  "Weight": 100000,
  "Title": "blog-microservice"
}
```

This collection is the raw weighted index used during projection rebuilds.

### `SearchSourceDocuments`

One document per source entity.

Example:

```json
{
  "SourceType": "WebService",
  "SourceId": "ws-1",
  "Title": "blog-microservice",
  "ResourceGroupId": "rg-1",
  "ResourceGroupName": "blog-prod",
  "SearchKeys": [
    { "Value": "blog-microservice", "Weight": 100000 },
    { "Value": "blog", "Weight": 50000 },
    { "Value": "microservice", "Weight": 50000 },
    { "Value": "blog.example.local", "Weight": 80000 }
  ]
}
```

`SearchKeys` are derived from the row-level projection and collapsed by normalized value to the highest weight for that value.

## Query Path

`GET /api/v1/search` queries `SearchSourceDocuments`.

The search service:

1. Matches source documents by prefix regex on `SearchKeys.Value`.
2. Filters `SearchKeys` down to the matched subset.
3. Computes `BestWeight` as the max matched key weight.
4. Sorts by:
   - `BestWeight desc`
   - `Title desc`
   - `SourceType asc`
   - `SourceId asc`
5. Applies pagination with `$facet`.

This avoids the old row-level read pattern:

- match many rows
- sort rows
- group rows back to entities
- sort grouped results again

## Projection Maintenance

Search remains event-driven.

- Consumers rebuild weighted key rows from the latest source snapshot.
- The same `SearchDocumentSet` is converted into one `SearchSourceDocument`.
- Create/update replaces both projections.
- Delete removes both projections.

## Current Constraints

- Prefix matching only, using normalized values and `^query`.
- No precomputed prefix expansion.
- No semantic search.
- `ResourceGroupName`, `StorageClassName`, and `GroupId` are metadata only and are not used as searchable keys.
