using Godot;
using CWM.Scripts.Core;

namespace CWM.Scripts.Network;

public partial class NetworkManager : Node
{
    public event Action? HostingStarted;
    public event Action? ConnectedToServer;
    public event Action<long>? PeerConnected;
    public event Action<long>? PeerDisconnected;
    public event Action? ConnectionFailed;
    public event Action? ServerDisconnected;

    public bool HasActivePeer =>
        Multiplayer.MultiplayerPeer is not null &&
        Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected;

    public bool IsServer => HasActivePeer && Multiplayer.IsServer();

    public override void _Ready()
    {
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }

    public Error StartHost(int port = Constants.DefaultPort, int maxClients = 8)
    {
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(port, maxClients);
        if (error != Error.Ok)
        {
            return error;
        }

        Multiplayer.MultiplayerPeer = peer;
        HostingStarted?.Invoke();
        return Error.Ok;
    }

    public Error JoinHost(string hostIp, int port = Constants.DefaultPort)
    {
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(hostIp, port);
        if (error != Error.Ok)
        {
            return error;
        }

        Multiplayer.MultiplayerPeer = peer;
        return Error.Ok;
    }

    public void Stop()
    {
        if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer peer)
        {
            peer.Close();
        }

        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
    }

    private void OnPeerConnected(long id) => PeerConnected?.Invoke(id);

    private void OnPeerDisconnected(long id) => PeerDisconnected?.Invoke(id);

    private void OnConnectedToServer() => ConnectedToServer?.Invoke();

    private void OnConnectionFailed() => ConnectionFailed?.Invoke();

    private void OnServerDisconnected() => ServerDisconnected?.Invoke();
}
