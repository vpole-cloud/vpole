using Microsoft.Extensions.DependencyInjection;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Runtime.Gateway;
using Pole.Features.Functions.Application.Runtime.Metering;
using Pole.Features.Functions.Application.Runtime.Provisioning;
using Pole.Features.Functions.Application.Runtime.Scaling;
using Pole.Features.Functions.Application.Runtime.Timers;
using Pole.Features.Functions.Application.Usage;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionAppPersistenceRegistrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public void Feature_registration_resolves_application_and_runtime_persistence_capabilities()
    {
        Assert.NotNull(Services.GetRequiredService<LoadFunctionAppById>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionAppPage>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionAppRevisions>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionAppRevisionReferences>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionInvocations>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionUsage>());
        Assert.NotNull(Services.GetRequiredService<PersistCreatedFunctionApp>());
        Assert.NotNull(Services.GetRequiredService<PersistFunctionAppRevision>());
        Assert.NotNull(Services.GetRequiredService<BeginFunctionAppDeletion>());
        Assert.NotNull(Services.GetRequiredService<RetryFunctionInvocation>());
        Assert.NotNull(Services.GetRequiredService<ClaimNextFunctionTimer>());
        Assert.NotNull(Services.GetRequiredService<MaterializeFunctionTimerOccurrence>());
        Assert.NotNull(Services.GetRequiredService<ReleaseFunctionTimerLease>());
        Assert.NotNull(Services.GetRequiredService<LoadExpiredFunctionInvocations>());
        Assert.NotNull(Services.GetRequiredService<ClaimNextFunctionInvocation>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionInvocationDispatchSubject>());
        Assert.NotNull(Services.GetRequiredService<PersistFunctionInvocationTransition>());
        Assert.NotNull(Services.GetRequiredService<LoadUnaggregatedFunctionMeterEvents>());
        Assert.NotNull(Services.GetRequiredService<AggregateFunctionMeterEvent>());
        Assert.NotNull(Services.GetRequiredService<PersistFunctionMeterEvent>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionRuntimeRouteTarget>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionScaleSubjects>());
        Assert.NotNull(Services.GetRequiredService<LoadFunctionRevisionProvisioningSubject>());
        Assert.NotNull(Services.GetRequiredService<RunMongoOutcomeTransaction<
            FunctionRevisionLifecycleOutcome>>());
        Assert.NotNull(Services.GetRequiredService<MarkFunctionRevisionReady>());
        Assert.NotNull(Services.GetRequiredService<PromoteFunctionRevision>());
        Assert.NotNull(Services.GetRequiredService<FailFunctionRevision>());
        Assert.NotNull(Services.GetRequiredService<RetireFunctionRevision>());
    }
}
