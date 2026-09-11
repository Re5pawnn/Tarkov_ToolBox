namespace TarkovMapLocator.ModuleContracts;

public sealed record FeatureDescriptor(
    string Id,
    string DisplayName,
    string NavigationLabel,
    int Order);
