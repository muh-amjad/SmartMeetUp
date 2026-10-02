namespace MeetUp.Api.Services.Ai;

public sealed class AnalysisProviderFactory : IAnalysisProviderFactory
{
    private readonly AnalysisProviderRegistry _registry;

    public AnalysisProviderFactory(AnalysisProviderRegistry registry)
    {
        _registry = registry;
    }

    public IAnalysisProvider? GetDefault() => _registry.Find(_registry.DefaultKey);

    public IAnalysisProvider? Resolve(string? requested, string? userPreference) =>
        _registry.Find(requested)
        ?? _registry.Find(userPreference)
        ?? GetDefault();

    public IReadOnlyList<IAnalysisProvider> GetFallbacks(string failedKey) =>
        _registry.GetAvailable()
            .Where(p => !string.Equals(p.Key, failedKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.IsDefault)
            .ThenByDescending(p => p.IsFree)
            .Select(p => _registry.Find(p.Key))
            .OfType<IAnalysisProvider>()
            .ToList();
}
