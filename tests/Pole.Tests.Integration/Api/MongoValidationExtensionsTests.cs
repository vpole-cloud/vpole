// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using MongoDB.Bson;
using Pole.Validation;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class MongoValidationExtensionsTests
{
    [Fact]
    public void MongoObjectIdRequiresAValidValue()
    {
        var validator = new TestRequestValidator();

        var missing = validator.Validate(new TestRequest(null, null));
        var malformed = validator.Validate(new TestRequest("not-an-id", null));
        var valid = validator.Validate(new TestRequest(ObjectId.GenerateNewId().ToString(), null));

        Assert.Contains(missing.Errors, error => error.PropertyName == nameof(TestRequest.RequiredId));
        Assert.Contains(malformed.Errors, error => error.PropertyName == nameof(TestRequest.RequiredId));
        Assert.DoesNotContain(valid.Errors, error => error.PropertyName == nameof(TestRequest.RequiredId));
    }

    [Fact]
    public void OptionalMongoObjectIdAcceptsMissingButRejectsMalformedValue()
    {
        var validator = new TestRequestValidator();

        var missing = validator.Validate(new TestRequest(ObjectId.GenerateNewId().ToString(), null));
        var malformed = validator.Validate(new TestRequest(ObjectId.GenerateNewId().ToString(), "not-an-id"));

        Assert.DoesNotContain(missing.Errors, error => error.PropertyName == nameof(TestRequest.OptionalId));
        Assert.Contains(
            malformed.Errors,
            error => error.PropertyName == nameof(TestRequest.OptionalId) &&
                error.ErrorMessage == "Optional ID must be a MongoDB ObjectId.");
    }

    private sealed record TestRequest(
        string? RequiredId,
        string? OptionalId
    );

    private sealed class TestRequestValidator : AbstractValidator<TestRequest>
    {
        public TestRequestValidator()
        {
            RuleFor(request => request.RequiredId).MongoObjectId("Required ID");
            RuleFor(request => request.OptionalId).OptionalMongoObjectId("Optional ID");
        }
    }
}
