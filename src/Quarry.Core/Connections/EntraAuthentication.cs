using Microsoft.Data.SqlClient;

namespace Quarry.Core.Connections;

/// <summary>Registers the Entra ID authentication provider with SqlClient.</summary>
public static class EntraAuthentication
{
    private static int _configured;

    /// <param name="deviceCodeCallback">
    /// Receives the device code message ("go to https://microsoft.com/devicelogin and enter …")
    /// so the UI can show it.
    /// </param>
    public static void Configure(Func<string, Task> deviceCodeCallback)
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
            return;

        var provider = new ActiveDirectoryAuthenticationProvider(new ActiveDirectoryAuthenticationProviderOptions
        {
            DeviceCodeFlowCallback = result => deviceCodeCallback(result.Message),
        });

        foreach (var method in new[]
                 {
                     SqlAuthenticationMethod.ActiveDirectoryInteractive,
                     SqlAuthenticationMethod.ActiveDirectoryDefault,
                     SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
                     SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow,
                 })
        {
            SqlAuthenticationProvider.SetProvider(method, provider);
        }
    }
}
