namespace Pole.Features.Functions.Runtime.Coordination;

public interface IFunctionAdmissionCoordinator
{
    Task<FunctionAdmissionDecision> AcquireAsync(
        FunctionRevisionCoordinationKey key,
        FunctionAdmissionRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionAdmissionRenewalDecision> RenewAsync(
        FunctionRevisionCoordinationKey key,
        FunctionAdmissionRenewalRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionAdmissionReleaseDecision> ReleaseAsync(
        FunctionRevisionCoordinationKey key,
        FunctionAdmissionReleaseRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionRevisionCoordinatorSnapshot> GetSnapshotAsync(
        FunctionRevisionCoordinationKey key,
        CancellationToken cancellationToken = default);
}

public interface IFunctionTrafficCoordinator
{
    Task<FunctionTrafficDecision> TryAcquireAsync(
        FunctionAppCoordinationKey key,
        FunctionTrafficRequest request,
        CancellationToken cancellationToken = default);
}

public interface IFunctionActivationCoordinator
{
    Task<FunctionActivationBeginDecision> BeginActivationAsync(
        FunctionRevisionCoordinationKey key,
        FunctionActivationBeginRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionActivationCompleteDecision> CompleteActivationAsync(
        FunctionRevisionCoordinationKey key,
        FunctionActivationCompleteRequest request,
        CancellationToken cancellationToken = default);
}

public interface IFunctionScalingCoordinator
{
    Task<FunctionScalerOwnershipDecision> TryAcquireOwnershipAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScalerOwnershipRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionScalerOwnershipDecision> RenewOwnershipAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScalerOwnershipToken token,
        CancellationToken cancellationToken = default);

    Task<FunctionScalerOwnershipReleaseDecision> ReleaseOwnershipAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScalerOwnershipToken token,
        CancellationToken cancellationToken = default);

    Task<FunctionQuiescingDecision> BeginQuiescingAsync(
        FunctionRevisionCoordinationKey key,
        FunctionQuiescingBeginRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionQuiescingCompleteDecision> CompleteQuiescingAsync(
        FunctionRevisionCoordinationKey key,
        FunctionQuiescingCompleteRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionScaleBeginDecision> BeginScaleAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScaleBeginRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionScaleCompleteDecision> CompleteScaleAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScaleCompleteRequest request,
        CancellationToken cancellationToken = default);

    Task<FunctionRevisionCoordinatorSnapshot> GetSnapshotAsync(
        FunctionRevisionCoordinationKey key,
        CancellationToken cancellationToken = default);
}

public sealed class FunctionCoordinationClient(
    IGrainFactory grainFactory,
    FunctionCoordinationOptions options,
    TimeProvider timeProvider
) : IFunctionAdmissionCoordinator, IFunctionTrafficCoordinator, IFunctionActivationCoordinator,
    IFunctionScalingCoordinator
{
    public Task<FunctionActivationBeginDecision> BeginActivationAsync(
        FunctionRevisionCoordinationKey key,
        FunctionActivationBeginRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).BeginActivationAsync(request),
        cancellationToken);

    public Task<FunctionActivationCompleteDecision> CompleteActivationAsync(
        FunctionRevisionCoordinationKey key,
        FunctionActivationCompleteRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).CompleteActivationAsync(request),
        cancellationToken);

    public Task<FunctionAdmissionDecision> AcquireAsync(
        FunctionRevisionCoordinationKey key,
        FunctionAdmissionRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).AcquireAdmissionAsync(request),
        cancellationToken);

    public Task<FunctionAdmissionRenewalDecision> RenewAsync(
        FunctionRevisionCoordinationKey key,
        FunctionAdmissionRenewalRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).RenewAdmissionAsync(request),
        cancellationToken);

    public Task<FunctionAdmissionReleaseDecision> ReleaseAsync(
        FunctionRevisionCoordinationKey key,
        FunctionAdmissionReleaseRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).ReleaseAdmissionAsync(request),
        cancellationToken);

    public Task<FunctionRevisionCoordinatorSnapshot> GetSnapshotAsync(
        FunctionRevisionCoordinationKey key,
        CancellationToken cancellationToken = default) =>
        AwaitAsync(Revision(key).GetSnapshotAsync(), cancellationToken);

    public Task<FunctionScalerOwnershipDecision> TryAcquireOwnershipAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScalerOwnershipRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).TryAcquireScalerOwnershipAsync(request),
        cancellationToken);

    public Task<FunctionScalerOwnershipDecision> RenewOwnershipAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScalerOwnershipToken token,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).RenewScalerOwnershipAsync(token),
        cancellationToken);

    public Task<FunctionScalerOwnershipReleaseDecision> ReleaseOwnershipAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScalerOwnershipToken token,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).ReleaseScalerOwnershipAsync(token),
        cancellationToken);

    public Task<FunctionQuiescingDecision> BeginQuiescingAsync(
        FunctionRevisionCoordinationKey key,
        FunctionQuiescingBeginRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).BeginQuiescingAsync(request),
        cancellationToken);

    public Task<FunctionQuiescingCompleteDecision> CompleteQuiescingAsync(
        FunctionRevisionCoordinationKey key,
        FunctionQuiescingCompleteRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).CompleteQuiescingAsync(request),
        cancellationToken);

    public Task<FunctionScaleBeginDecision> BeginScaleAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScaleBeginRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).BeginScaleAsync(request),
        cancellationToken);

    public Task<FunctionScaleCompleteDecision> CompleteScaleAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScaleCompleteRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        Revision(key).CompleteScaleAsync(request),
        cancellationToken);

    public Task<FunctionTrafficDecision> TryAcquireAsync(
        FunctionAppCoordinationKey key,
        FunctionTrafficRequest request,
        CancellationToken cancellationToken = default) => AwaitAsync(
        grainFactory.GetGrain<IFunctionAppTrafficGrain>(key.ToGrainKey()).TryAcquireAsync(request),
        cancellationToken);

    private IFunctionRevisionCoordinatorGrain Revision(FunctionRevisionCoordinationKey key) =>
        grainFactory.GetGrain<IFunctionRevisionCoordinatorGrain>(key.ToGrainKey());

    private Task<T> AwaitAsync<T>(Task<T> call, CancellationToken cancellationToken) => call.WaitAsync(
        TimeSpan.FromSeconds(options.CallTimeoutSeconds),
        timeProvider,
        cancellationToken);
}
