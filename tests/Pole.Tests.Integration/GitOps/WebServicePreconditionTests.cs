using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task ExpectedWebServiceRevisionConflictReplansInsteadOfBecomingBusinessBlocker()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var releases = await Releases();
        rejectNextWebServiceRevision = true;
        var result = await Apply(Snapshot("2"));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        Assert.False(rejectNextWebServiceRevision);
        Assert.Equal(releases + 1, await Releases());
        Assert.Equal("2", (await Service()).Spec.Image.ResolvedImage!.Tag);
        var operations = await Services.GetRequiredService<GitRepositoryQueries>().OperationsAsync(
            TestData.TenantId, source, result.Context!.RunId, 1, 20, default);
        var configure = Assert.Single(operations!.Items,
            item => item.Kind == "WebService" && item.Phase == DeclarationPhase.Configure);
        Assert.Equal(2, configure.Plan!.Revision);
        Assert.Null(configure.Blocker);
    }
}
