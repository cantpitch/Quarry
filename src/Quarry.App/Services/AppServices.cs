using Quarry.Core.Connections;
using Quarry.Core.Credentials;
using Quarry.Core.Settings;

namespace Quarry.App.Services;

/// <summary>Application-wide singletons.</summary>
public static class AppServices
{
    public static ICredentialStore Credentials { get; } = CredentialStore.CreateDefault();

    public static ProfileStore Profiles { get; } = new(Credentials);

    public static AppSettings Settings { get; set; } = AppSettings.Load();
}

public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>UI operations view models need from the window.</summary>
public interface IDialogService
{
    Task<ServerConnection?> ShowConnectDialogAsync();

    Task<SaveChoice> AskSaveChangesAsync(string documentName);

    Task ShowErrorAsync(string title, string message);

    Task<bool> ShowSettingsAsync();

    Task<string?> PickOpenFileAsync();

    Task<string?> PickSaveFileAsync(string suggestedName);

    /// <summary>Save dialog for result exports (CSV, TSV, TXT, JSON, XLSX); the extension picks the format.</summary>
    Task<string?> PickExportFileAsync(string suggestedName);

    Task SetClipboardTextAsync(string text);
}
