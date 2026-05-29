namespace MAAUnified.App.ViewModels.TaskQueue;

public interface ITaskModulePanelViewModel
{
    bool IsAdvancedMode { get; set; }

    bool IsTaskBound { get; }

    string LastErrorMessage { get; }

    void ClearBinding();

    Task<bool> FlushPendingChangesAsync(CancellationToken cancellationToken = default);
}
