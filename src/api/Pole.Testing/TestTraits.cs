namespace Pole.Testing;

public static class TestTraits
{
    public const string Layer = nameof(Layer);
    public const string Concern = nameof(Concern);
    public const string Feature = nameof(Feature);
    public const string Scope = nameof(Scope);

    public static class Layers
    {
        public const string Fast = nameof(Fast);
        public const string Integration = nameof(Integration);
        public const string Kubernetes = nameof(Kubernetes);
        public const string Manifest = nameof(Manifest);
        public const string E2E = nameof(E2E);
    }

    public static class Scopes
    {
        public const string ManifestOnly = nameof(ManifestOnly);
        public const string SingleCluster = nameof(SingleCluster);
        public const string MultiCluster = nameof(MultiCluster);
        public const string Topology = nameof(Topology);
        public const string FullE2E = nameof(FullE2E);
    }
}
