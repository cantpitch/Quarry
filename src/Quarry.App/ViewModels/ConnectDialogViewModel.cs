using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quarry.App.Services;
using Quarry.Core.Connections;

namespace Quarry.App.ViewModels;

public sealed record AuthOption(AuthenticationKind Kind)
{
    public string Name => Kind.DisplayName();

    public override string ToString() => Name;
}

public sealed partial class ConnectDialogViewModel : ObservableObject
{
    private Guid _profileId = Guid.NewGuid();
    private CancellationTokenSource? _cts;

    public ConnectDialogViewModel()
    {
        AuthOptions = AuthenticationKindExtensions.Available().Select(k => new AuthOption(k)).ToList();
        _selectedAuth = AuthOptions[0];
        foreach (var p in AppServices.Profiles.Load())
            Profiles.Add(p);
        if (Profiles.Count > 0)
            SelectedProfile = Profiles[0];
    }

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    public IReadOnlyList<AuthOption> AuthOptions { get; }

    public IReadOnlyList<EncryptMode> EncryptModes { get; } = Enum.GetValues<EncryptMode>();

    public bool CanSaveSecrets => AppServices.Credentials.IsAvailable;

    public string KeychainNote => CanSaveSecrets
        ? ""
        : "No OS keychain is available, so passwords are not saved and must be entered each session.";

    /// <summary>Set when the dialog should close with this connection.</summary>
    public ServerConnection? Result { get; private set; }

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private ConnectionProfile? _selectedProfile;

    [ObservableProperty]
    private string? _name;

    [ObservableProperty]
    private string _server = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUserName), nameof(ShowSecret), nameof(UserNameLabel), nameof(SecretLabel))]
    private AuthOption _selectedAuth;

    [ObservableProperty]
    private string? _userName;

    [ObservableProperty]
    private string? _secret;

    [ObservableProperty]
    private string? _database;

    [ObservableProperty]
    private EncryptMode _encrypt = EncryptMode.Mandatory;

    [ObservableProperty]
    private bool _trustServerCertificate;

    [ObservableProperty]
    private bool _saveSecret = true;

    [ObservableProperty]
    private int _connectTimeout = 15;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(TestCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _statusIsError;

    public bool ShowUserName => SelectedAuth.Kind.UsesUserName();

    public bool ShowSecret => SelectedAuth.Kind.RequiresSecret();

    public string UserNameLabel => SelectedAuth.Kind switch
    {
        AuthenticationKind.EntraServicePrincipal => "Client ID",
        AuthenticationKind.EntraInteractive => "User (optional)",
        _ => "Login",
    };

    public string SecretLabel => SelectedAuth.Kind == AuthenticationKind.EntraServicePrincipal ? "Client secret" : "Password";

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is null)
            return;
        _profileId = value.Id;
        Name = value.Name;
        Server = value.Server;
        SelectedAuth = AuthOptions.FirstOrDefault(a => a.Kind == value.Authentication) ?? AuthOptions[0];
        UserName = value.UserName;
        Database = value.Database;
        Encrypt = value.Encrypt;
        TrustServerCertificate = value.TrustServerCertificate;
        ConnectTimeout = value.ConnectTimeoutSeconds;
        SaveSecret = value.SaveSecret;
        try
        {
            Secret = AppServices.Profiles.GetSecret(value);
        }
        catch (Exception ex)
        {
            Secret = null;
            SetStatus($"Could not read the saved password: {ex.Message}", error: true);
        }
    }

    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null;
        _profileId = Guid.NewGuid();
        Name = null;
        Server = "";
        UserName = null;
        Secret = null;
        Database = null;
        Encrypt = EncryptMode.Mandatory;
        TrustServerCertificate = false;
        ConnectTimeout = 15;
        SetStatus("", false);
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } profile)
            return;
        try
        {
            AppServices.Profiles.Delete(profile);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        Profiles.Remove(profile);
        NewProfile();
    }

    private ConnectionProfile BuildProfile() => new()
    {
        Id = _profileId,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        Server = Server.Trim(),
        Authentication = SelectedAuth.Kind,
        UserName = ShowUserName && !string.IsNullOrWhiteSpace(UserName) ? UserName.Trim() : null,
        Database = string.IsNullOrWhiteSpace(Database) ? null : Database.Trim(),
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeout, 1, 600),
        SaveSecret = SaveSecret && CanSaveSecrets,
    };

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TestAsync()
    {
        if (await TryConnectAsync() is not null)
            SetStatus("Connection succeeded.", error: false);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ConnectAsync()
    {
        var server = await TryConnectAsync();
        if (server is null)
            return;

        var profile = server.Profile;
        try
        {
            AppServices.Profiles.Save(profile, ShowSecret ? Secret : null);
        }
        catch (Exception ex)
        {
            // Connecting worked; failing to persist must not block it.
            SetStatus($"Connected, but the profile could not be saved: {ex.Message}", error: true);
        }
        Result = server;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task<ServerConnection?> TryConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            SetStatus("Enter a server name.", error: true);
            return null;
        }

        IsBusy = true;
        SetStatus(SelectedAuth.Kind is AuthenticationKind.EntraInteractive or AuthenticationKind.EntraDeviceCode
            ? "Connecting… complete the sign-in in your browser."
            : "Connecting…", error: false);
        _cts = new CancellationTokenSource();
        try
        {
            var server = new ServerConnection(BuildProfile(), ShowSecret ? Secret : null);
            await server.ConnectAsync(_cts.Token);
            return server;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(ex.Message, error: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void SetStatus(string text, bool error)
    {
        StatusText = text;
        StatusIsError = error;
    }
}
