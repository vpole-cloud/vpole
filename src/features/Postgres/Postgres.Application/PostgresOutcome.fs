// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application

/// Internal typed outcome for PostgreSQL persistence capabilities.
/// Provider-owned C# contract results remain at feature boundaries; F# persistence uses a
/// native discriminated union so each caller must handle its known error cases explicitly.
type PostgresOutcome<'value, 'error> =
    | PostgresSuccess of 'value
    | PostgresError of 'error
