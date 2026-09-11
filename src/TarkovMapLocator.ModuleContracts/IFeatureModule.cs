using System.Windows;

namespace TarkovMapLocator.ModuleContracts;

public interface IFeatureModule
{
    FeatureDescriptor Descriptor { get; }

    FrameworkElement CreateView(IFeatureHost host, string route);
}

public interface IFeatureModuleSelfTest
{
    void Verify();
}

public interface IFeatureApplicationLifecycle
{
    bool TryHandleStartup(IReadOnlyList<string> arguments, FeatureApplicationContext context);

    void OnApplicationStarting(FeatureApplicationContext context);
}

public interface IFeatureViewLifecycle : IAsyncDisposable
{
    void OnActivated();
}

public interface IFeatureMouseNavigationGuard
{
    bool ShouldSuppressNavigation(System.Windows.Input.MouseButton button, System.Windows.DependencyObject? source);
}

public interface IFeatureNavigationHandler
{
    bool NavigateBack();

    bool NavigateForward();
}

public interface ITaskTrackingFeature : IAsyncDisposable
{
    event EventHandler? AutomaticTaskRecognitionChanged;

    event EventHandler? AutoCompletePrerequisitesChanged;

    event EventHandler? TaskRecognitionRequested;

    event EventHandler? TaskModeChanged;

    bool AutomaticTaskRecognitionEnabled { get; set; }

    bool AutoCompletePrerequisitesEnabled { get; set; }

    string SelectedTaskMode { get; }

    IReadOnlyDictionary<string, string[]> TrackingTaskIdsByGameId { get; }

    void EnsureInitialized();

    void SetTaskRecognitionBusy(bool isBusy);

    int ApplyDetectedTaskStatuses(IReadOnlyList<FeatureDetectedTaskStatusChange> changes);

    int ApplyPrerequisiteCompletionToExistingStatuses();
}

public interface ITeamSyncFeature : IAsyncDisposable
{
    bool IsRunning { get; }

    IReadOnlyList<FeatureTeamPeerPosition> GetPeerPositions(string? mapId);

    string? HandleSyncRequest(string requestJson, string remoteAddress);

    Task PublishCurrentLocationAsync();

    Task StopAsync();

    Task StopHostingAsync();
}

public interface IMobileMapFeature : IAsyncDisposable
{
    bool IsRunning { get; }

    int Port { get; }

    bool EnsureRunning();
}

public enum FeatureTaskTrackingStatus
{
    NotStarted = 0,
    Accepted = 1,
    Completed = 2
}

public sealed record FeatureDetectedTaskStatusChange(
    string TrackingTaskId,
    FeatureTaskTrackingStatus Status,
    string GameTaskId,
    DateTimeOffset ObservedAt,
    string SourceFileName);

public sealed record FeatureApplicationContext(
    string DataDirectory,
    Action<FeatureLogLevel, string, string, string?> WriteLog,
    Action<Func<Task<int>>> RunBackgroundAndShutdown,
    Action<int> Shutdown);
