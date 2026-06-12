namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| BrokerProtocol                                                             |
|                                                                            |
|   Wire constants shared by the control server and its clients.             |
|                                                                            |
|   The control channel is a single, fixed, well-known named pipe — the      |
|   broker is a system-wide service, so clients connect by name instead of   |
|   discovering a per-launch random pipe through a secret descriptor file.    |
|   There is no shared secret: authentication is by pipe DACL + peer-process  |
|   identity + Authenticode signer pin (see ClientAuthorization /             |
|   PeerIdentity / PeerSignature). The descriptor/HMAC handshake was removed  |
|   in protocol v2.                                                           |
\*---------------------------------------------------------------------------*/
internal static class BrokerProtocol
{
    /// <summary>Bumped to 2 when the HMAC handshake was dropped for the identity/signature model.</summary>
    public const int Version = 2;

    /// <summary>Fixed sensor control-pipe name (\\.\pipe\SensorBroker).</summary>
    public const string PipeName = "SensorBroker";

    /// <summary>Fixed RGB-control-service pipe name (\\.\pipe\BrokerControl) — separate, write-only service.</summary>
    public const string ControlPipeName = "BrokerControl";
}
