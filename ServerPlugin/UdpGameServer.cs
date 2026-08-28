using System;
using VRage.GameServices;
using VRage.Network;
using Shared.Transport;

namespace ServerPlugin;

// A non-Steam replacement for the dedicated server's IMyGameServer. It keeps
// the engine's server bring-up happy (Start/WaitStart/ServerId/public IP) and
// admits every client without Steam authentication, so clients using the
// direct UDP transport can join. All Steam master-server chatter (tags,
// heartbeats, key-values) degrades to no-ops.
public sealed class UdpGameServer : IMyGameServer
{
    public string GameDescription { get; set; } = "DirectTransport";
    public ulong ServerId => DirectTransport.ServerId;
    public bool Running { get; private set; }

    public event Action PlatformConnected;
    public event Action<ulong, JoinResult, ulong, string> ValidateAuthTicketResponse;
    public event Action<ulong, ulong, bool, bool> UserGroupStatusResponse;

    // Never raised here, but required by IMyGameServer: there is no platform to
    // drop us or refuse us (Start always succeeds locally) and no Steam policy
    // to report. Accessors are empty rather than field-like so the unraised
    // backing delegates do not warn.
    public event Action<string> PlatformDisconnected { add { } remove { } }
    public event Action<string> PlatformConnectionFailed { add { } remove { } }
    public event Action<sbyte> PolicyResponse { add { } remove { } }

    public bool Start(System.Net.IPEndPoint serverEndpoint, ushort steamPort, string versionString)
    {
        Running = true;
        DirectTransport.Log($"UdpGameServer started for {serverEndpoint} (v{versionString})");
        // Signal the engine that the platform is up; MyDedicatedServerBase
        // waits on this to consider the server responsive.
        PlatformConnected?.Invoke();
        return true;
    }

    // Non-zero public IP so MyDedicatedServerBase.WaitStart succeeds. 127.0.0.1
    // in host order; the value is only used for logging in this path.
    public uint GetPublicIP() => 0x7F000001u;

    public bool WaitStart(int timeOut) => true;

    // Admit the client. The auth callback must fire AFTER the caller
    // (OnConnectedClient) has finished registering the pending member, so it
    // is deferred to the next server tick via DirectServer's main-thread queue
    // rather than invoked synchronously here. Reporting owner == user makes the
    // family-sharing check pass.
    public bool BeginAuthSession(ulong userId, byte[] token, string serviceName)
    {
        DirectTransport.Log($"Admitting client {userId} (service {serviceName}) without Steam auth");
        DirectServer.RunOnMainThread(() =>
            ValidateAuthTicketResponse?.Invoke(userId, JoinResult.OK, userId, serviceName));
        return true;
    }

    public bool RequestGroupStatus(ulong userId, ulong groupId)
    {
        DirectServer.RunOnMainThread(() =>
            UserGroupStatusResponse?.Invoke(userId, groupId, true, true));
        return true;
    }

    public bool UserHasLicenseForApp(ulong steamId, uint appId) => true;

    // --- Remaining members degrade to no-ops -----------------------------

    public void SetKeyValue(string key, string value) { }
    public void ClearAllKeyValues() { }
    public void SetGameTags(string tags) { }
    public void SetGameData(string data) { }
    public void SetModDir(string directory) { }
    public void SetDedicated(bool isDedicated) { }
    public void SetMapName(string mapName) { }
    public void SetServerName(string serverName) { }
    public void SetMaxPlayerCount(int count) { }
    public void SetBotPlayerCount(int count) { }
    public void SetPasswordProtected(bool passwdProtected) { }
    public void LogOnAnonymous() { }
    public void LogOff() { }
    public void Shutdown() { Running = false; }
    public void EndAuthSession(ulong userId) { }
    public void SendUserDisconnect(ulong userId) { }
    public void EnableHeartbeats(bool enable) { }
    public void BrowserUpdateUserData(ulong userId, string playerName, int score) { }
    public void SetServerModTemporaryDirectory() { }
    public void SetGameReady(bool state) { }
}
