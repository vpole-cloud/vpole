using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Runtime.Scaling;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Runtime.Scaling;

public sealed record FunctionScaleTarget(
    FunctionApp App,
    FunctionRevision Revision,
    bool IsServing,
    IReadOnlyDictionary<string, int> DurableDemandByHandler
);

public interface IFunctionScaleTargetSource
{
    Task<IReadOnlyList<FunctionScaleTarget>> ListAsync(CancellationToken cancellationToken);

    Task<FunctionRevisionLifecycleOutcome> MarkRetiredAsync(
        FunctionScaleTarget target,
        CancellationToken cancellationToken);
}

public sealed class MongoFunctionScaleTargetSource(
    LoadFunctionScaleSubjects loadScaleSubjects,
    IServiceScopeFactory scopeFactory
) : IFunctionScaleTargetSource
{
    public async Task<IReadOnlyList<FunctionScaleTarget>> ListAsync(CancellationToken cancellationToken)
    {
        var subjects = await loadScaleSubjects(cancellationToken);
        return subjects.Select(subject => new FunctionScaleTarget(
                subject.App,
                subject.Revision,
                subject.Revision.Status is ActiveFunctionRevisionStatus &&
                subject.App.DesiredState == FunctionAppDesiredState.Enabled &&
                subject.App.Status == FunctionAppStatus.Ready &&
                string.Equals(subject.App.ActiveRevisionId, subject.Revision.Id, StringComparison.Ordinal),
                subject.DurableDemandByHandler))
            .ToArray();
    }

    public async Task<FunctionRevisionLifecycleOutcome> MarkRetiredAsync(
        FunctionScaleTarget target,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var retireFunctionRevision = scope.ServiceProvider.GetRequiredService<RetireFunctionRevision>();
        return await retireFunctionRevision(
            target.Revision.TenantId,
            target.Revision.FunctionAppId,
            target.Revision.Id,
            cancellationToken);
    }
}
