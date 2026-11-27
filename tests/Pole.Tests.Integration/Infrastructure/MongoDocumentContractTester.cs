// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class MongoDocumentContractTester<TDocument>(
    IMongoDatabase database,
    string collectionName
) : Testing.MongoDocumentContractTester<TDocument>(database, collectionName)
    where TDocument : notnull;
