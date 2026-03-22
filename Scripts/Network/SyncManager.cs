using Godot;
using CWM.Scripts.World;
using CWM.Scripts.Weather;

namespace CWM.Scripts.Network;

public partial class SyncManager : Node
{
    public event Action<int, int>? WorldSettingsReceived;
    public event Action<long>? ClientReadyReceived;
    public event Action<long, Vector2>? SpawnPlayerReceived;
    public event Action<long>? DespawnPlayerReceived;
    public event Action<long, Vector2, Vector2, Vector2>? PlayerStateReceived;
    public event Action<long, Vector2I, int, BiomeType>? BrushBiomeRequested;
    public event Action<long, Vector2I, int, ClimateBrushField, float, float>? ClimateBrushRequested;
    public event Action<long, Vector2I, int, FarmBrushTool>? FarmBrushRequested;
    public event Action<int, int, int>? WorldSnapshotStartedReceived;
    public event Action<int, int, byte[]>? WorldSnapshotChunkReceived;
    public event Action<int>? WorldSnapshotCompletedReceived;
    public event Action<Godot.Collections.Array<Godot.Collections.Dictionary>>? TileDeltasReceived;
    public event Action<Godot.Collections.Array<Godot.Collections.Dictionary>>? FaunaSnapshotsReceived;
    public event Action<Godot.Collections.Array<Godot.Collections.Dictionary>>? NpcSnapshotsReceived;
    /// <summary>主机收到：发起者 peerId、村民 id、互动类型（1=赠送作物 2=协助耕种）。交谈为本地处理不发 RPC。</summary>
    public event Action<long, int, int>? NpcInteractRequested;
    public event Action<WeatherState, float, float, float>? WeatherSyncReceived;
    public event Action<float>? TimeSyncReceived;
    public event Action<bool, float>? SuccessionAccelerationSyncReceived;
    public event Action<float>? SuccessionStepHoursSyncReceived;
    /// <summary>主机广播地图快照同步状态：所有端（含主机）应暂停世界并显示提示；<paramref name="active"/> 为 false 时结束。</summary>
    public event Action<bool, long>? SnapshotSyncStateReceived;

    public bool HasActivePeer =>
        Multiplayer.MultiplayerPeer is not null &&
        Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected;

    public void SendWorldSettings(long peerId, int seed, int mapSize) => RpcId(peerId, nameof(ReceiveWorldSettings), seed, mapSize);

    public void NotifyClientReady()
    {
        if (!HasActivePeer || Multiplayer.IsServer())
        {
            return;
        }

        RpcId(1, nameof(ServerNotifyClientReady));
    }

    public void BroadcastSpawnPlayer(long peerId, Vector2 position)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        Rpc(nameof(ReceiveSpawnPlayer), peerId, position);
    }

    public void SendSpawnPlayer(long targetPeerId, long peerId, Vector2 position)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        RpcId(targetPeerId, nameof(ReceiveSpawnPlayer), peerId, position);
    }

    public void BroadcastDespawnPlayer(long peerId)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        Rpc(nameof(ReceiveDespawnPlayer), peerId);
    }

    public void SubmitLocalPlayerState(long peerId, Vector2 position, Vector2 velocity, Vector2 facing)
    {
        if (!HasActivePeer)
        {
            return;
        }

        if (Multiplayer.IsServer())
        {
            PlayerStateReceived?.Invoke(peerId, position, velocity, facing);
            Rpc(nameof(ReceivePlayerState), peerId, position, velocity, facing);
            return;
        }

        RpcId(1, nameof(ServerSubmitPlayerState), position, velocity, facing);
    }

    public void RequestBrushBiome(Vector2I centerCell, int radius, BiomeType biome)
    {
        if (!HasActivePeer)
        {
            return;
        }

        if (Multiplayer.IsServer())
        {
            BrushBiomeRequested?.Invoke(Multiplayer.GetUniqueId(), centerCell, radius, biome);
            return;
        }

        RpcId(1, nameof(ServerRequestBrushBiome), centerCell, radius, (int)biome);
    }

    public void RequestClimateBrush(Vector2I centerCell, int radius, ClimateBrushField field, float targetValue, float blendStrength)
    {
        if (!HasActivePeer)
        {
            return;
        }

        if (Multiplayer.IsServer())
        {
            ClimateBrushRequested?.Invoke(Multiplayer.GetUniqueId(), centerCell, radius, field, targetValue, blendStrength);
            return;
        }

        RpcId(1, nameof(ServerRequestClimateBrush), centerCell, radius, (int)field, targetValue, blendStrength);
    }

    public void RequestFarmBrush(Vector2I centerCell, int radius, FarmBrushTool tool)
    {
        if (!HasActivePeer)
        {
            return;
        }

        if (Multiplayer.IsServer())
        {
            FarmBrushRequested?.Invoke(Multiplayer.GetUniqueId(), centerCell, radius, tool);
            return;
        }

        RpcId(1, nameof(ServerRequestFarmBrush), centerCell, radius, (int)tool);
    }

    /// <summary>向所有客户端广播地块增量（可靠传输；演替脏格量大，不可靠通道会大量丢包导致地图与主机不一致）。</summary>
    public void BroadcastTileDeltas(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
    {
        if (!HasActivePeer || !Multiplayer.IsServer() || payload.Count == 0)
        {
            return;
        }

        Rpc(nameof(ReceiveTileDeltas), payload);
    }

    /// <summary>仅发给指定 peer（加入补发 catch-up）。</summary>
    public void SendTileDeltas(long peerId, Godot.Collections.Array<Godot.Collections.Dictionary> payload)
    {
        if (!HasActivePeer || !Multiplayer.IsServer() || payload.Count == 0)
        {
            return;
        }

        RpcId(peerId, nameof(ReceiveTileDeltas), payload);
    }

    public void SendWorldSnapshotStart(long peerId, int transferId, int chunkCount, int totalBytes)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        RpcId(peerId, nameof(ReceiveWorldSnapshotStart), transferId, chunkCount, totalBytes);
    }

    public void SendWorldSnapshotChunk(long peerId, int transferId, int chunkIndex, byte[] chunkData)
    {
        if (!HasActivePeer || !Multiplayer.IsServer() || chunkData.Length == 0)
        {
            return;
        }

        RpcId(peerId, nameof(ReceiveWorldSnapshotChunk), transferId, chunkIndex, chunkData);
    }

    public void SendWorldSnapshotComplete(long peerId, int transferId)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        RpcId(peerId, nameof(ReceiveWorldSnapshotComplete), transferId);
    }

    public void BroadcastFaunaSnapshots(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        Rpc(nameof(ReceiveFaunaSnapshots), payload);
    }

    public void BroadcastNpcSnapshots(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
    {
        if (!HasActivePeer || !Multiplayer.IsServer() || payload.Count == 0)
        {
            return;
        }

        Rpc(nameof(ReceiveNpcSnapshots), payload);
    }

    /// <summary>客户端请求主机执行村民互动（赠送/协助）。</summary>
    public void RequestNpcInteract(int npcId, int actionId)
    {
        if (!HasActivePeer)
        {
            return;
        }

        if (Multiplayer.IsServer())
        {
            NpcInteractRequested?.Invoke(Multiplayer.GetUniqueId(), npcId, actionId);
            return;
        }

        RpcId(1, nameof(ServerNpcInteract), npcId, actionId);
    }

    public void BroadcastWeatherState(WeatherState state, float intensity, float wind, float temperatureModifier)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        Rpc(nameof(ReceiveWeatherState), (int)state, intensity, wind, temperatureModifier);
    }

    public void BroadcastTime(float totalGameHours)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        Rpc(nameof(ReceiveTimeState), totalGameHours);
    }

    public void BroadcastSuccessionAcceleration(bool enabled, float multiplier)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        Rpc(nameof(ReceiveSuccessionAcceleration), enabled, multiplier);
    }

    public void BroadcastSuccessionStepHours(float stepHours)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        Rpc(nameof(ReceiveSuccessionStepHours), stepHours);
    }

    /// <summary>向所有已连接端（不含仅离线）广播快照同步状态；主机本地先触发事件再 RPC。</summary>
    public void BroadcastSnapshotSyncState(bool active, long syncingPeerId)
    {
        if (!HasActivePeer || !Multiplayer.IsServer())
        {
            return;
        }

        SnapshotSyncStateReceived?.Invoke(active, syncingPeerId);
        Rpc(nameof(ReceiveSnapshotSyncState), active, syncingPeerId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWorldSettings(int seed, int mapSize) => WorldSettingsReceived?.Invoke(seed, mapSize);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerNotifyClientReady() => ClientReadyReceived?.Invoke(Multiplayer.GetRemoteSenderId());

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSpawnPlayer(long peerId, Vector2 position) => SpawnPlayerReceived?.Invoke(peerId, position);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveDespawnPlayer(long peerId) => DespawnPlayerReceived?.Invoke(peerId);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerSubmitPlayerState(Vector2 position, Vector2 velocity, Vector2 facing)
    {
        var senderId = Multiplayer.GetRemoteSenderId();
        PlayerStateReceived?.Invoke(senderId, position, velocity, facing);
        Rpc(nameof(ReceivePlayerState), senderId, position, velocity, facing);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceivePlayerState(long peerId, Vector2 position, Vector2 velocity, Vector2 facing)
        => PlayerStateReceived?.Invoke(peerId, position, velocity, facing);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestBrushBiome(Vector2I centerCell, int radius, int biomeIndex)
    {
        var senderId = Multiplayer.GetRemoteSenderId();
        BrushBiomeRequested?.Invoke(senderId, centerCell, radius, (BiomeType)biomeIndex);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestClimateBrush(Vector2I centerCell, int radius, int fieldIndex, float targetValue, float blendStrength)
    {
        var senderId = Multiplayer.GetRemoteSenderId();
        ClimateBrushRequested?.Invoke(senderId, centerCell, radius, (ClimateBrushField)fieldIndex, targetValue, blendStrength);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestFarmBrush(Vector2I centerCell, int radius, int toolIndex)
    {
        var senderId = Multiplayer.GetRemoteSenderId();
        FarmBrushRequested?.Invoke(senderId, centerCell, radius, (FarmBrushTool)toolIndex);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWorldSnapshotStart(int transferId, int chunkCount, int totalBytes)
        => WorldSnapshotStartedReceived?.Invoke(transferId, chunkCount, totalBytes);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWorldSnapshotChunk(int transferId, int chunkIndex, byte[] chunkData)
        => WorldSnapshotChunkReceived?.Invoke(transferId, chunkIndex, chunkData);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWorldSnapshotComplete(int transferId)
        => WorldSnapshotCompletedReceived?.Invoke(transferId);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveTileDeltas(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
        => TileDeltasReceived?.Invoke(payload);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveFaunaSnapshots(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
        => FaunaSnapshotsReceived?.Invoke(payload);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveNpcSnapshots(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
        => NpcSnapshotsReceived?.Invoke(payload);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerNpcInteract(int npcId, int actionId)
        => NpcInteractRequested?.Invoke(Multiplayer.GetRemoteSenderId(), npcId, actionId);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWeatherState(int state, float intensity, float wind, float temperatureModifier)
        => WeatherSyncReceived?.Invoke((WeatherState)state, intensity, wind, temperatureModifier);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveTimeState(float totalGameHours)
        => TimeSyncReceived?.Invoke(totalGameHours);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSuccessionAcceleration(bool enabled, float multiplier)
        => SuccessionAccelerationSyncReceived?.Invoke(enabled, multiplier);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSuccessionStepHours(float stepHours)
        => SuccessionStepHoursSyncReceived?.Invoke(stepHours);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSnapshotSyncState(bool active, long syncingPeerId)
        => SnapshotSyncStateReceived?.Invoke(active, syncingPeerId);
}
