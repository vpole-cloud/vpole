// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.WebServices.Application.Configuration;

namespace Pole.Features.WebServices.Application.Common;

// Stable provider diagnostic codes are exposed only when producing an HTTP or stored diagnostic.
public static class WebServiceDependencyDiagnostics
{
    public static string Code(WebServiceDependencyUnavailable error) => error.Cause switch
    {
        ResourceConnectionReferencesUnavailable unavailable => unavailable.ReasonCode,
        ConsumerSecretReferenceUnavailable unavailable => ConsumerSecretReferenceErrorCodes.Code(unavailable.Reason),
        WebServicePublicExposureRequired => "CustomHostnamesRequirePublicExposure",
    };
}
