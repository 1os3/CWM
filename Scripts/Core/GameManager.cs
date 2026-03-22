using System;
using System.Collections.Generic;
using Godot;
using CWM.Scripts.Ecology;
using CWM.Scripts.Network;
using CWM.Scripts.Player;
using CWM.Scripts.UI;
using CWM.Scripts.World;
using CWM.Scripts.Weather;

namespace CWM.Scripts.Core;

public partial class GameManager : Node
{
    private readonly PackedScene _playerScene = GD.Load<PackedScene>("res://Scenes/Player/Player.tscn");

    private GameWorld _gameWorld = null!;
    private MainMenuController _mainMenu = null!;
    private HudController _hud = null!;
    private DayNightCycle _dayNightCycle = null!;
    private SeasonManager _seasonManager = null!;
    private WeatherSystem _weatherSystem = null!;
    private WorldGenerator _worldGenerator = null!;
    private EcosystemSimulator _ecosystemSimulator = null!;
    private FaunaManager _faunaManager = null!;
    private NetworkManager _networkManager = null!;
    private SyncManager _syncManager = null!;
    private FileDialog _exportFileDialog = null!;
    private FileDialog _importFileDialog = null!;

    private readonly Dictionary<long, PlayerController> _players = [];
    private readonly Dictionary<long, Vector2> _spawnPositions = [];
    private readonly Dictionary<long, int> _peerDisplayOrdinal = new();
    private int _nextPlayerDisplayOrdinal;
    private float _miniMapOverlayAccumulator;
    private const float MiniMapOverlayRefreshSeconds = 0.18f;
    private readonly Dictionary<long, Dictionary<Vector2I, Godot.Collections.Dictionary>> _pendingSnapshotCatchupTiles = [];

    private WorldData? _worldData;
    private int _nextSnapshotTransferId = 1;
    private int _incomingSnapshotTransferId = -1;
    private int _incomingSnapshotChunkCount;
    private int _incomingSnapshotReceivedChunks;
    private int _incomingSnapshotTotalBytes;
    private int _incomingSnapshotReceivedBytes;
    private byte[][]? _incomingSnapshotChunks;
    private int _currentSeed;
    private int _currentMapSize;
    private bool _worldLoaded;
    private bool _authoritativeWorld;
    private Godot.Timer? _hostTileSyncTimer;
    private Godot.Timer? _hostFaunaSyncTimer;
    private Godot.Timer? _hostTimeWeatherSyncTimer;
    private bool _brushModeEnabled;
    private int _brushRadius = Constants.DefaultBrushRadius;
    private float _brushPaintAccumulator;
    private Vector2I _lastBrushCell = new(int.MinValue, int.MinValue);
    private BiomeType _selectedBrushBiome = BiomeType.Forest;
    private ClimateBrushField _selectedClimateBrushField = ClimateBrushField.Rainfall;
    private float _selectedClimateBrushValue = Constants.DefaultClimateBrushTargetValue;
    private float _selectedClimateBrushStrength = Constants.DefaultClimateBrushStrength;
    private bool _successionAccelerationEnabled;
    private float _successionAccelerationMultiplier = Constants.DefaultSuccessionAccelerationMultiplier;
    private float _successionStepHours = Constants.DefaultSuccessionStepHours;
    private bool _farmingModeEnabled;
    private FarmBrushTool _farmTool = FarmBrushTool.PlowFarmland;
    private float _granaryUnits;

    private readonly Queue<long> _snapshotPeerQueue = new();
    private bool _snapshotSendWorkerRunning;
    private bool _worldSyncSimulationPaused;
    private bool _storedDayNightRunning;
    private Node.ProcessModeEnum _ecoProcessModeBefore;
    private Node.ProcessModeEnum _faunaProcessModeBefore;
    private Node.ProcessModeEnum _weatherProcessModeBefore;
    private Node.ProcessModeEnum _npcProcessModeBefore;

    private NpcManager _npcManager = null!;
    private int _npcInteractId = -1;
    private string _npcInteractName = string.Empty;
    private float _npcInteractMood;

    public override void _Ready()
    {
        EnsureInputActions();

        _gameWorld = GetNode<GameWorld>("GameWorld");
        _mainMenu = GetNode<MainMenuController>("MainMenu");
        _hud = GetNode<HudController>("HUD");
        _dayNightCycle = GetNode<DayNightCycle>("DayNightCycle");
        _seasonManager = GetNode<SeasonManager>("SeasonManager");
        _weatherSystem = GetNode<WeatherSystem>("WeatherSystem");
        _worldGenerator = GetNode<WorldGenerator>("WorldGenerator");
        _ecosystemSimulator = GetNode<EcosystemSimulator>("EcosystemSimulator");
        _faunaManager = GetNode<FaunaManager>("FaunaManager");
        _npcManager = GetNode<NpcManager>("NpcManager");
        _networkManager = GetNode<NetworkManager>("NetworkManager");
        _syncManager = GetNode<SyncManager>("SyncManager");
        _exportFileDialog = GetNode<FileDialog>("ExportFileDialog");
        _importFileDialog = GetNode<FileDialog>("ImportFileDialog");

        ConfigureFileDialogs();

        _gameWorld.Visible = false;
        _hud.Visible = false;

        _mainMenu.StartLocalRequested += StartSoloGame;
        _mainMenu.HostRequested += StartHostGame;
        _mainMenu.JoinRequested += JoinLanGame;
        _mainMenu.ImportRequested += OpenImportDialog;

        _hud.ExportRequested += ExportWorld;
        _hud.ImportRequested += OpenImportDialog;
        _hud.BrushToggleRequested += ToggleBrushMode;
        _hud.BrushRadiusStepRequested += AdjustBrushRadius;
        _hud.BrushBiomeChanged += biome =>
        {
            _selectedBrushBiome = biome;
            _hud.SetBrushState(_brushModeEnabled, _brushRadius, _selectedBrushBiome);
            _hud.ShowMessage($"Brush preset set to {biome}.");
        };
        _hud.ClimateBrushFieldChanged += field =>
        {
            _selectedClimateBrushField = field;
            _hud.SetClimateBrushState(_selectedClimateBrushField, _selectedClimateBrushValue, _selectedClimateBrushStrength);
            RefreshClimateEditorHeatmap();
            _hud.ShowMessage($"Climate editor switched to {field}.");
        };
        _hud.ClimateBrushValueChanged += targetValue =>
        {
            _selectedClimateBrushValue = Mathf.Clamp(targetValue, 0.0f, 1.0f);
            _hud.SetClimateBrushState(_selectedClimateBrushField, _selectedClimateBrushValue, _selectedClimateBrushStrength);
        };
        _hud.ClimateBrushStrengthChanged += strength =>
        {
            _selectedClimateBrushStrength = Mathf.Clamp(strength, 0.05f, 1.0f);
            _hud.SetClimateBrushState(_selectedClimateBrushField, _selectedClimateBrushValue, _selectedClimateBrushStrength);
        };
        _hud.ClimateEditorVisibilityChanged += isOpen =>
        {
            if (isOpen)
            {
                RefreshClimateEditorHeatmap(force: true);
            }
        };
        _hud.SuccessionAccelerationToggled += enabled =>
        {
            if (!_authoritativeWorld)
            {
                _hud.ShowMessage("Succession speed can only be changed by the host.");
                _hud.SetSuccessionAccelerationState(_successionAccelerationEnabled, _successionAccelerationMultiplier, false);
                return;
            }

            SetSuccessionAcceleration(enabled, _successionAccelerationMultiplier, true);
        };
        _hud.SuccessionAccelerationMultiplierChanged += multiplier =>
        {
            if (!_authoritativeWorld)
            {
                _hud.ShowMessage("Succession speed can only be changed by the host.");
                _hud.SetSuccessionAccelerationState(_successionAccelerationEnabled, _successionAccelerationMultiplier, false);
                return;
            }

            SetSuccessionAcceleration(_successionAccelerationEnabled, multiplier, true);
        };
        _hud.SuccessionStepHoursChanged += stepHours =>
        {
            if (!_authoritativeWorld)
            {
                _hud.ShowMessage("Succession step can only be changed by the host.");
                _hud.SetSuccessionStepState(_successionStepHours, false);
                return;
            }

            SetSuccessionStepHours(stepHours, true);
        };

        _npcManager.PlayerWorldPositionsProvider = BuildNpcPlayerWorldPositions;

        _networkManager.PeerConnected += OnPeerConnected;
        _networkManager.PeerDisconnected += OnPeerDisconnected;
        _networkManager.ConnectedToServer += OnConnectedToServer;
        _networkManager.ConnectionFailed += () => _mainMenu.SetStatus("Connection failed.");
        _networkManager.ServerDisconnected += OnServerDisconnected;

        _syncManager.WorldSettingsReceived += OnWorldSettingsReceived;
        _syncManager.ClientReadyReceived += OnClientReadyReceived;
        _syncManager.SpawnPlayerReceived += OnSpawnPlayerReceived;
        _syncManager.DespawnPlayerReceived += DespawnPlayer;
        _syncManager.PlayerStateReceived += OnPlayerStateReceived;
        _syncManager.BrushBiomeRequested += ApplyAuthoritativeBiomeBrush;
        _syncManager.ClimateBrushRequested += ApplyAuthoritativeClimateBrush;
        _syncManager.FarmBrushRequested += ApplyAuthoritativeFarmBrush;
        _syncManager.WorldSnapshotStartedReceived += OnWorldSnapshotStartedReceived;
        _syncManager.WorldSnapshotChunkReceived += OnWorldSnapshotChunkReceived;
        _syncManager.WorldSnapshotCompletedReceived += OnWorldSnapshotCompletedReceived;
        _syncManager.TileDeltasReceived += ApplyRemoteTileDeltas;
        _syncManager.FaunaSnapshotsReceived += snapshots => _faunaManager.ApplyRemoteSnapshots(snapshots);
        _syncManager.NpcSnapshotsReceived += snapshots => _npcManager.ApplyRemoteSnapshots(snapshots);
        _syncManager.NpcInteractRequested += OnNpcInteractRequested;
        _syncManager.WeatherSyncReceived += (state, intensity, wind, temperatureModifier) =>
        {
            if (!_authoritativeWorld)
            {
                _weatherSystem.SetRemoteState(state, intensity, wind, temperatureModifier);
            }
        };
        _syncManager.TimeSyncReceived += totalHours =>
        {
            if (!_authoritativeWorld)
            {
                _dayNightCycle.SetRemoteTime(totalHours);
                _seasonManager.UpdateFromDays(_dayNightCycle.ElapsedDays);
            }
        };
        _syncManager.SuccessionAccelerationSyncReceived += (enabled, multiplier) =>
        {
            _successionAccelerationEnabled = enabled;
            _successionAccelerationMultiplier = multiplier;
            _ecosystemSimulator.SetSuccessionAcceleration(enabled, multiplier);
            _hud.SetSuccessionAccelerationState(enabled, multiplier, _authoritativeWorld);
        };
        _syncManager.SuccessionStepHoursSyncReceived += stepHours =>
        {
            _successionStepHours = stepHours;
            _ecosystemSimulator.SetSimulationStepHours(stepHours);
            _hud.SetSuccessionStepState(stepHours, _authoritativeWorld);
        };
        _syncManager.SnapshotSyncStateReceived += OnSnapshotSyncStateReceived;

        _dayNightCycle.TimeAdvanced += (_, totalHours) => _seasonManager.UpdateFromDays(totalHours / 24.0f);
        _ecosystemSimulator.TilesChanged += OnWorldTilesChanged;
        _hud.SetClimateBrushState(_selectedClimateBrushField, _selectedClimateBrushValue, _selectedClimateBrushStrength);
        _hud.FarmingModeToggleRequested += ToggleFarmingMode;
        _hud.FarmToolChanged += tool => _farmTool = tool;
        _hud.NpcTalkRequested += OnNpcTalkPressed;
        _hud.NpcGiftRequested += OnNpcGiftPressed;
        _hud.NpcHelpRequested += OnNpcHelpPressed;

        _mainMenu.Visible = true;
        _mainMenu.SetStatus("Ready to create or join a world.");
    }

    private void ConfigureFileDialogs()
    {
        ConfigureFileDialog(_exportFileDialog, "Export World", FileDialog.FileModeEnum.SaveFile);
        ConfigureFileDialog(_importFileDialog, "Import World", FileDialog.FileModeEnum.OpenFile);
        _exportFileDialog.FileSelected += OnExportFileSelected;
        _importFileDialog.FileSelected += OnImportFileSelected;
    }

    private static void ConfigureFileDialog(FileDialog dialog, string title, FileDialog.FileModeEnum mode)
    {
        dialog.Title = title;
        dialog.Access = FileDialog.AccessEnum.Filesystem;
        dialog.FileMode = mode;
        dialog.UseNativeDialog = true;
        dialog.Filters = ["*.json ; JSON World", "*.bin ; Binary World"];
    }

    public override void _Process(double delta)
    {
        if (!_worldLoaded || _worldData is null)
        {
            return;
        }

        if (_worldSyncSimulationPaused)
        {
            return;
        }

        HandleRuntimeEditingInput((float)delta);
        UpdateFocusAndHud();

        if (_worldLoaded)
        {
            _miniMapOverlayAccumulator += (float)delta;
            if (_miniMapOverlayAccumulator >= MiniMapOverlayRefreshSeconds)
            {
                _miniMapOverlayAccumulator = 0f;
                _gameWorld.ScheduleMiniMapRebuild();
            }
        }

        if (_authoritativeWorld)
        {
            var focusPositions = _players.Values.Select(player => player.GlobalPosition).ToArray();
            _ecosystemSimulator.SetFocusPoints(focusPositions);
            _faunaManager.SetFocusPositions(focusPositions);
        }

        if (_networkManager.IsServer)
        {
            ProcessHostTileUrgentDrain();
        }
    }

    private void StartSoloGame(int seed, int mapSize)
    {
        _networkManager.Stop();
        LoadWorld(seed, mapSize, authoritative: true);
        SpawnLocalPlayerIfNeeded();
        _hud.ShowMessage("Solo world ready.");
    }

    private void StartHostGame(int seed, int mapSize, int port)
    {
        var result = _networkManager.StartHost(port);
        if (result != Error.Ok)
        {
            _mainMenu.SetStatus($"Host failed: {result}");
            return;
        }

        LoadWorld(seed, mapSize, authoritative: true);
        SpawnLocalPlayerIfNeeded();
        _mainMenu.SetStatus($"Hosting on port {port}");
        _hud.ShowMessage("LAN host started.");
    }

    private void JoinLanGame(string ip, int port)
    {
        var result = _networkManager.JoinHost(ip, port);
        if (result != Error.Ok)
        {
            _mainMenu.SetStatus($"Join failed: {result}");
            return;
        }

        _mainMenu.SetStatus("Connecting to host...");
    }

    private void OnConnectedToServer()
    {
        _mainMenu.SetStatus("Connected. Waiting for world settings...");
    }

    private void OnWorldSettingsReceived(int seed, int mapSize)
    {
        _currentSeed = seed;
        _currentMapSize = mapSize;
        ResetIncomingSnapshotState();
        _mainMenu.SetStatus($"World settings received ({mapSize}x{mapSize}). Waiting for snapshot...");
    }

    private void OnClientReadyReceived(long peerId)
    {
        if (!_networkManager.IsServer || _worldData is null)
        {
            return;
        }

        SendPendingSnapshotCatchup(peerId);

        foreach (var existing in _players)
        {
            _syncManager.SendSpawnPlayer(peerId, existing.Key, existing.Value.GlobalPosition);
        }

        var spawnPosition = AllocateSpawnPosition(peerId);
        SpawnPlayer(peerId, spawnPosition, peerId == GetLocalPeerId());
        _syncManager.BroadcastSpawnPlayer(peerId, spawnPosition);
        _syncManager.BroadcastSuccessionAcceleration(_successionAccelerationEnabled, _successionAccelerationMultiplier);
        _syncManager.BroadcastSuccessionStepHours(_successionStepHours);
        _syncManager.BroadcastTime(_dayNightCycle.TotalGameHours);
        _syncManager.BroadcastWeatherState(
            _weatherSystem.CurrentState,
            _weatherSystem.CurrentPrecipitationIntensity,
            _weatherSystem.CurrentWind,
            _weatherSystem.TemperatureModifier);
    }

    private void OnPeerConnected(long peerId)
    {
        if (_networkManager.IsServer && _worldLoaded)
        {
            _syncManager.SendWorldSettings(peerId, _currentSeed, _currentMapSize);
            _snapshotPeerQueue.Enqueue(peerId);
            ProcessSnapshotSendQueue();
        }
    }

    private void OnPeerDisconnected(long peerId)
    {
        DespawnPlayer(peerId);
        _pendingSnapshotCatchupTiles.Remove(peerId);
        if (_networkManager.IsServer)
        {
            _syncManager.BroadcastDespawnPlayer(peerId);
        }
    }

    private void OnServerDisconnected()
    {
        _networkManager.Stop();
        ResetIncomingSnapshotState();
        _pendingSnapshotCatchupTiles.Clear();
        _mainMenu.Visible = true;
        _mainMenu.SetStatus("Disconnected from host.");
        StopHostDedicatedSyncTimers();
        _hud.ClearSnapshotSyncBanner();
        if (_worldSyncSimulationPaused)
        {
            PopWorldSyncPauseOnce();
        }

        _hud.Visible = false;
        _gameWorld.Visible = false;
        _gameWorld.ClearBrushPreview();
    }

    private async void ProcessSnapshotSendQueue()
    {
        if (_snapshotSendWorkerRunning) return;
        _snapshotSendWorkerRunning = true;
        try
        {
            try
            {
                while (_snapshotPeerQueue.Count > 0)
                {
                    var peerId = _snapshotPeerQueue.Dequeue();
                    _syncManager.BroadcastSnapshotSyncState(true, peerId);
                    await SendWorldSnapshotBytesAsync(peerId);
                }

                // 给网络层一点时间把末尾区块送到客户端，再结束暂停，减轻 RPC 乱序。
                for (var i = 0; i < 3; i++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                _syncManager.BroadcastSnapshotSyncState(false, 0L);
            }
            catch (Exception ex)
            {
                GD.PushError($"World snapshot send failed: {ex.Message}");
                if (_networkManager.IsServer)
                {
                    _syncManager.BroadcastSnapshotSyncState(false, 0L);
                }
            }
        }
        finally
        {
            _snapshotSendWorkerRunning = false;
            if (_snapshotPeerQueue.Count > 0)
            {
                ProcessSnapshotSendQueue();
            }
        }
    }

    private async System.Threading.Tasks.Task SendWorldSnapshotBytesAsync(long peerId)
    {
        if (!_networkManager.IsServer || _worldData is null)
        {
            return;
        }

        var snapshotBytes = _worldData.ToBinary();
        BeginSnapshotCatchupBuffer(peerId);
        var transferId = _nextSnapshotTransferId++;
        var chunkSize = Constants.WorldSnapshotChunkSizeBytes;
        var chunkCount = Mathf.CeilToInt((float)snapshotBytes.Length / chunkSize);
        _syncManager.SendWorldSnapshotStart(peerId, transferId, chunkCount, snapshotBytes.Length);

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var offset = chunkIndex * chunkSize;
            var length = Math.Min(chunkSize, snapshotBytes.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(snapshotBytes, offset, chunk, 0, length);
            _syncManager.SendWorldSnapshotChunk(peerId, transferId, chunkIndex, chunk);

            if ((chunkIndex + 1) % Constants.WorldSnapshotChunksPerFrame == 0)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }

        _syncManager.SendWorldSnapshotComplete(peerId, transferId);
    }

    private void OnSnapshotSyncStateReceived(bool active, long syncingPeerId)
    {
        if (active)
        {
            if (_worldLoaded && _worldData is not null)
            {
                PushWorldSyncPauseOnce();
            }

            var msg = $"客户端 {syncingPeerId} 同步中";
            if (_hud.Visible)
            {
                _hud.SetSnapshotSyncBanner(msg);
            }

            if (_mainMenu.Visible)
            {
                _mainMenu.SetStatus(msg);
            }

            return;
        }

        if (!_worldLoaded && IsReceivingSnapshotIncomplete())
        {
            return;
        }

        if (_worldLoaded && _worldData is not null)
        {
            PopWorldSyncPauseOnce();
        }

        _hud.ClearSnapshotSyncBanner();
    }

    private bool IsReceivingSnapshotIncomplete()
    {
        return _incomingSnapshotTransferId >= 0 &&
            _incomingSnapshotChunkCount > 0 &&
            (_incomingSnapshotChunks is null || _incomingSnapshotReceivedChunks < _incomingSnapshotChunkCount);
    }

    private void PushWorldSyncPauseOnce()
    {
        if (_worldSyncSimulationPaused || !_worldLoaded || _worldData is null)
        {
            return;
        }

        _worldSyncSimulationPaused = true;
        _storedDayNightRunning = _dayNightCycle.Running;
        _ecoProcessModeBefore = _ecosystemSimulator.ProcessMode;
        _faunaProcessModeBefore = _faunaManager.ProcessMode;
        _weatherProcessModeBefore = _weatherSystem.ProcessMode;
        _npcProcessModeBefore = _npcManager.ProcessMode;

        _dayNightCycle.Running = false;
        _ecosystemSimulator.ProcessMode = Node.ProcessModeEnum.Disabled;
        _faunaManager.ProcessMode = Node.ProcessModeEnum.Disabled;
        _weatherSystem.ProcessMode = Node.ProcessModeEnum.Disabled;
        _npcManager.ProcessMode = Node.ProcessModeEnum.Disabled;

        if (_networkManager.IsServer)
        {
            StopHostDedicatedSyncTimers();
        }

        foreach (var player in _players.Values)
        {
            player.SetMovementFrozen(true);
        }
    }

    private void PopWorldSyncPauseOnce()
    {
        if (!_worldSyncSimulationPaused || !_worldLoaded || _worldData is null)
        {
            return;
        }

        _worldSyncSimulationPaused = false;
        _dayNightCycle.Running = _storedDayNightRunning;
        _ecosystemSimulator.ProcessMode = _ecoProcessModeBefore;
        _faunaManager.ProcessMode = _faunaProcessModeBefore;
        _weatherSystem.ProcessMode = _weatherProcessModeBefore;
        _npcManager.ProcessMode = _npcProcessModeBefore;

        if (_networkManager.IsServer)
        {
            ConfigureHostDedicatedSyncTimers();
        }

        foreach (var player in _players.Values)
        {
            player.SetMovementFrozen(false);
        }
    }

    private void OnWorldSnapshotStartedReceived(int transferId, int chunkCount, int totalBytes)
    {
        ResetIncomingSnapshotState();
        _incomingSnapshotTransferId = transferId;
        _incomingSnapshotChunkCount = chunkCount;
        _incomingSnapshotTotalBytes = totalBytes;
        _incomingSnapshotReceivedBytes = 0;
        _incomingSnapshotChunks = new byte[Math.Max(chunkCount, 0)][];
        UpdateSnapshotProgressStatus();
    }

    private void OnWorldSnapshotChunkReceived(int transferId, int chunkIndex, byte[] chunkData)
    {
        if (transferId != _incomingSnapshotTransferId ||
            _incomingSnapshotChunks is null ||
            chunkIndex < 0 ||
            chunkIndex >= _incomingSnapshotChunks.Length ||
            _incomingSnapshotChunks[chunkIndex] is not null)
        {
            return;
        }

        _incomingSnapshotChunks[chunkIndex] = chunkData;
        _incomingSnapshotReceivedChunks++;
        _incomingSnapshotReceivedBytes += chunkData.Length;
        UpdateSnapshotProgressStatus();
    }

    private void OnWorldSnapshotCompletedReceived(int transferId)
    {
        if (transferId != _incomingSnapshotTransferId || _incomingSnapshotChunks is null)
        {
            return;
        }

        if (_incomingSnapshotReceivedChunks != _incomingSnapshotChunkCount ||
            _incomingSnapshotChunks.Any(chunk => chunk is null))
        {
            _mainMenu.SetStatus("World snapshot incomplete.");
            return;
        }

        var snapshotBytes = new byte[_incomingSnapshotTotalBytes];
        var offset = 0;
        foreach (var chunk in _incomingSnapshotChunks)
        {
            if (chunk is null)
            {
                _mainMenu.SetStatus("World snapshot missing chunk data.");
                return;
            }

            Buffer.BlockCopy(chunk, 0, snapshotBytes, offset, chunk.Length);
            offset += chunk.Length;
        }

        if (!WorldData.TryImportFromBytes(snapshotBytes, out var importedWorld, out var errorMessage) || importedWorld is null)
        {
            _mainMenu.SetStatus($"World snapshot import failed: {errorMessage}");
            return;
        }

        _hud.ClearSnapshotSyncBanner();
        ResetIncomingSnapshotState();
        LoadWorld(importedWorld, authoritative: false);
        _mainMenu.SetStatus("World snapshot received. Loading player...");
        // 等一帧再通知主机：避免 TileMap/相机尚未提交变换时即生成角色，导致 HUD 格坐标与视觉不同步。
        CallDeferred(nameof(DeferredNotifyClientReadyAfterSnapshot));
    }

    private void DeferredNotifyClientReadyAfterSnapshot()
    {
        if (!_worldLoaded || _authoritativeWorld || !_networkManager.HasActivePeer)
        {
            return;
        }

        _syncManager.NotifyClientReady();
    }

    private void ResetIncomingSnapshotState()
    {
        _incomingSnapshotTransferId = -1;
        _incomingSnapshotChunkCount = 0;
        _incomingSnapshotReceivedChunks = 0;
        _incomingSnapshotTotalBytes = 0;
        _incomingSnapshotReceivedBytes = 0;
        _incomingSnapshotChunks = null;
    }

    private void UpdateSnapshotProgressStatus()
    {
        var totalBytes = Math.Max(_incomingSnapshotTotalBytes, 1);
        var percent = Mathf.Clamp((float)_incomingSnapshotReceivedBytes / totalBytes, 0.0f, 1.0f) * 100.0f;
        _mainMenu.SetStatus(
            $"Receiving world snapshot {percent:0.0}% " +
            $"({_incomingSnapshotReceivedChunks}/{_incomingSnapshotChunkCount}, {FormatByteCount(_incomingSnapshotReceivedBytes)}/{FormatByteCount(_incomingSnapshotTotalBytes)})");
    }

    private void BeginSnapshotCatchupBuffer(long peerId)
    {
        _pendingSnapshotCatchupTiles[peerId] = new Dictionary<Vector2I, Godot.Collections.Dictionary>();
    }

    private void SendAndTrackTileDeltas(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
    {
        if (payload.Count == 0)
        {
            return;
        }

        BufferTilePayloadForJoiningPeers(payload);
        _syncManager.BroadcastTileDeltas(payload);
    }

    /// <summary>演替爆发等导致脏格极多时，在主循环里额外抢跑 drain（平时由 <see cref="OnHostTileSyncTimerTimeout"/> 定时泵送）。</summary>
    private void ProcessHostTileUrgentDrain()
    {
        if (!_networkManager.IsServer || _worldData is null)
        {
            return;
        }

        if (_ecosystemSimulator.PendingTileSyncCount >= Constants.HostTileSyncUrgentPendingTiles)
        {
            HostDrainTileSyncSlice();
        }
    }

    private void EnsureHostTileSyncTimer()
    {
        if (_hostTileSyncTimer is not null)
        {
            return;
        }

        _hostTileSyncTimer = new Godot.Timer
        {
            Name = "HostTileSyncTimer",
            OneShot = false,
            Autostart = false,
            WaitTime = Constants.HostTileSyncPumpInterval
        };
        _hostTileSyncTimer.Timeout += OnHostTileSyncTimerTimeout;
        AddChild(_hostTileSyncTimer);
    }

    private void OnHostTileSyncTimerTimeout()
    {
        if (!_networkManager.IsServer || _worldData is null || !_worldLoaded)
        {
            return;
        }

        HostDrainTileSyncSlice();
    }

    private void ConfigureHostDedicatedSyncTimers()
    {
        EnsureHostTileSyncTimer();
        EnsureHostFaunaSyncTimer();
        EnsureHostTimeWeatherSyncTimer();

        _hostTileSyncTimer!.Stop();
        _hostFaunaSyncTimer!.Stop();
        _hostTimeWeatherSyncTimer!.Stop();

        if (_networkManager.IsServer && _worldLoaded && _worldData is not null)
        {
            _hostTileSyncTimer.WaitTime = Constants.HostTileSyncPumpInterval;
            _hostTileSyncTimer.Start();

            _hostFaunaSyncTimer.WaitTime = Constants.HostFaunaSyncInterval;
            _hostFaunaSyncTimer.Start();

            _hostTimeWeatherSyncTimer.WaitTime = Constants.HostTimeWeatherSyncInterval;
            _hostTimeWeatherSyncTimer.Start();
        }
    }

    private void StopHostDedicatedSyncTimers()
    {
        _hostTileSyncTimer?.Stop();
        _hostFaunaSyncTimer?.Stop();
        _hostTimeWeatherSyncTimer?.Stop();
    }

    private void EnsureHostFaunaSyncTimer()
    {
        if (_hostFaunaSyncTimer is not null)
        {
            return;
        }

        _hostFaunaSyncTimer = new Godot.Timer
        {
            Name = "HostFaunaSyncTimer",
            OneShot = false,
            Autostart = false,
            WaitTime = Constants.HostFaunaSyncInterval
        };
        _hostFaunaSyncTimer.Timeout += OnHostFaunaSyncTimerTimeout;
        AddChild(_hostFaunaSyncTimer);
    }

    private void OnHostFaunaSyncTimerTimeout()
    {
        if (!_networkManager.IsServer || _worldData is null || !_worldLoaded)
        {
            return;
        }

        _syncManager.BroadcastFaunaSnapshots(_faunaManager.BuildSnapshots());
        _syncManager.BroadcastNpcSnapshots(_npcManager.BuildSnapshots());
    }

    private void EnsureHostTimeWeatherSyncTimer()
    {
        if (_hostTimeWeatherSyncTimer is not null)
        {
            return;
        }

        _hostTimeWeatherSyncTimer = new Godot.Timer
        {
            Name = "HostTimeWeatherSyncTimer",
            OneShot = false,
            Autostart = false,
            WaitTime = Constants.HostTimeWeatherSyncInterval
        };
        _hostTimeWeatherSyncTimer.Timeout += OnHostTimeWeatherSyncTimerTimeout;
        AddChild(_hostTimeWeatherSyncTimer);
    }

    private void OnHostTimeWeatherSyncTimerTimeout()
    {
        if (!_networkManager.IsServer || _worldData is null || !_worldLoaded)
        {
            return;
        }

        _syncManager.BroadcastTime(_dayNightCycle.TotalGameHours);
        _syncManager.BroadcastWeatherState(
            _weatherSystem.CurrentState,
            _weatherSystem.CurrentPrecipitationIntensity,
            _weatherSystem.CurrentWind,
            _weatherSystem.TemperatureModifier);
    }

    /// <summary>单“切片”内发送若干批；若仍有积压则下一帧继续（异步分帧，避免单帧卡顿）。</summary>
    private void HostDrainTileSyncSlice()
    {
        for (var b = 0; b < Constants.HostTileSyncMaxBatchesPerSlice; b++)
        {
            var payload = _ecosystemSimulator.ConsumeDirtyTilePayload(Constants.HostTileSyncBatchSize);
            if (payload.Count == 0)
            {
                return;
            }

            SendAndTrackTileDeltas(payload);
        }

        if (_ecosystemSimulator.PendingTileSyncCount > 0)
        {
            CallDeferred(nameof(HostDrainTileSyncSliceDeferred));
        }
    }

    private void HostDrainTileSyncSliceDeferred()
    {
        HostDrainTileSyncSlice();
    }

    /// <summary>主机本地操作（刷子、演替爆发等）后立即把脏格同步给客户端；内部会多批发送直至队列清空。</summary>
    private void FlushPendingTileDeltasForHostAction()
    {
        if (!_networkManager.IsServer)
        {
            return;
        }

        HostDrainTileSyncSlice();
    }

    private void BufferTilePayloadForJoiningPeers(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
    {
        if (_pendingSnapshotCatchupTiles.Count == 0)
        {
            return;
        }

        foreach (var entry in payload)
        {
            var cell = new Vector2I((int)entry["x"], (int)entry["y"]);
            foreach (var pendingBuffer in _pendingSnapshotCatchupTiles.Values)
            {
                pendingBuffer[cell] = CloneTileDelta(entry);
            }
        }
    }

    private void SendPendingSnapshotCatchup(long peerId)
    {
        if (!_pendingSnapshotCatchupTiles.TryGetValue(peerId, out var pendingBuffer))
        {
            return;
        }

        if (pendingBuffer.Count > 0)
        {
            const int batchSize = 1024;
            var batch = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var entry in pendingBuffer.Values)
            {
                batch.Add(entry);
                if (batch.Count >= batchSize)
                {
                    _syncManager.SendTileDeltas(peerId, batch);
                    batch = new Godot.Collections.Array<Godot.Collections.Dictionary>();
                }
            }

            if (batch.Count > 0)
            {
                _syncManager.SendTileDeltas(peerId, batch);
            }
        }

        _pendingSnapshotCatchupTiles.Remove(peerId);
    }

    private static Godot.Collections.Dictionary CloneTileDelta(Godot.Collections.Dictionary source)
    {
        var clone = new Godot.Collections.Dictionary();
        foreach (var key in source.Keys)
        {
            clone[key] = source[key];
        }

        return clone;
    }

    private void LoadWorld(int seed, int mapSize, bool authoritative)
    {
        var generatedWorld = _worldGenerator.GenerateWorld(seed, mapSize);
        LoadWorld(generatedWorld, authoritative);
    }

    private void LoadWorld(WorldData world, bool authoritative)
    {
        ClearPlayers();
        _pendingSnapshotCatchupTiles.Clear();

        _currentSeed = world.Seed;
        _currentMapSize = world.Width;
        _authoritativeWorld = authoritative;
        _worldData = world;
        _worldLoaded = true;
        _brushPaintAccumulator = 0.0f;
        _lastBrushCell = new Vector2I(int.MinValue, int.MinValue);
        _gameWorld.ClearBrushPreview();

        _gameWorld.Initialize(_worldData, world.Seed);
        _gameWorld.MinimapOverlayProvider = BuildMinimapOverlayEntities;
        _gameWorld.Visible = true;
        _mainMenu.Visible = false;
        _hud.Visible = true;
        _hud.SetMiniMap(_gameWorld.MiniMapTexture);
        _hud.SetMiniMapMode(_gameWorld.GetMiniMapViewModeDisplayName());

        _dayNightCycle.Authoritative = authoritative;
        _dayNightCycle.Running = true;
        _dayNightCycle.ResetToHour(8.0f);
        _seasonManager.UpdateFromDays(0.0f);

        _weatherSystem.Authoritative = authoritative;
        _weatherSystem.Initialize(_gameWorld, _dayNightCycle, _seasonManager, world.Seed);

        _ecosystemSimulator.Authoritative = authoritative;
        _ecosystemSimulator.Initialize(_worldData, _dayNightCycle, _seasonManager, _weatherSystem, world.Seed);
        _gameWorld.SetClimateFieldSamplers(_ecosystemSimulator.SampleTemperatureFieldAt, _ecosystemSimulator.SampleMoistureFieldAt);
        _gameWorld.RebuildMiniMap();
        SetSuccessionAcceleration(false, Constants.DefaultSuccessionAccelerationMultiplier, false);
        SetSuccessionStepHours(Constants.DefaultSuccessionStepHours, false);

        _faunaManager.Authoritative = authoritative;
        _faunaManager.Initialize(_worldData, _gameWorld, world.Seed);

        _npcManager.Authoritative = authoritative;
        _npcManager.Initialize(
            _worldData,
            _gameWorld,
            _ecosystemSimulator,
            _worldGenerator,
            world.Seed,
            FlushPendingTileDeltasForHostAction,
            () => _ecosystemSimulator.SimulatedWorldHours);

        ConfigureHostDedicatedSyncTimers();

        _hud.UpdateNetwork(GetNetworkText());
        _hud.SetMiniMap(_gameWorld.MiniMapTexture);
        _hud.SetBrushState(_brushModeEnabled, _brushRadius, _selectedBrushBiome);
        _hud.SetClimateBrushState(_selectedClimateBrushField, _selectedClimateBrushValue, _selectedClimateBrushStrength);
        RefreshClimateEditorHeatmap();
        _hud.SetSuccessionAccelerationState(_successionAccelerationEnabled, _successionAccelerationMultiplier, _authoritativeWorld);
        _hud.SetSuccessionStepState(_successionStepHours, _authoritativeWorld);
        _farmingModeEnabled = false;
        _farmTool = FarmBrushTool.PlowFarmland;
        _granaryUnits = 0.0f;
        _hud.SetFarmingModeUi(false, _farmTool);
        _hud.UpdateGranary(_granaryUnits);
    }

    private void OnWorldTilesChanged(IReadOnlyList<Vector2I> tiles)
    {
        _gameWorld.RefreshTiles(tiles);
        RefreshClimateEditorHeatmap();
    }

    private void RefreshClimateEditorHeatmap(bool force = false)
    {
        if (_worldData is null || !_worldLoaded || (!force && !_hud.IsClimateEditorOpen))
        {
            return;
        }

        _hud.SetClimateMapImage(_gameWorld.BuildClimateFieldImage(_selectedClimateBrushField, Constants.ClimateEditorTextureSize));
    }

    private void HandleRuntimeEditingInput(float delta)
    {
        if (_worldData is not null && Input.IsActionJustPressed("cycle_minimap_view"))
        {
            CycleMiniMapViewMode();
        }

        if (_worldData is not null && Input.IsActionJustPressed("toggle_farming_mode"))
        {
            ToggleFarmingMode();
        }

        if (_worldData is not null && _farmingModeEnabled && Input.IsActionJustPressed("cycle_farm_tool"))
        {
            CycleFarmTool();
        }

        if (!_players.TryGetValue(GetLocalPeerId(), out var localPlayer) || _worldData is null)
        {
            _gameWorld.ClearBrushPreview();
            return;
        }

        if (Input.IsActionJustPressed("interact_npc") && !_worldSyncSimulationPaused && !IsPointerOverHud() && !_hud.IsNpcInteractionOpen)
        {
            TryOpenNearbyNpcPanel(localPlayer);
        }

        if (Input.IsActionJustPressed("toggle_brush_mode"))
        {
            ToggleBrushMode();
        }

        if (Input.IsActionJustPressed("brush_radius_down"))
        {
            AdjustBrushRadius(-1);
        }

        if (Input.IsActionJustPressed("brush_radius_up"))
        {
            AdjustBrushRadius(1);
        }

        if (Input.IsActionJustPressed("toggle_succession_acceleration"))
        {
            if (_authoritativeWorld)
            {
                SetSuccessionAcceleration(!_successionAccelerationEnabled, _successionAccelerationMultiplier, true);
            }
            else
            {
                _hud.ShowMessage("Succession speed can only be changed by the host.");
            }
        }

        if (Input.IsActionJustPressed("succession_multiplier_down") && _authoritativeWorld)
        {
            SetSuccessionAcceleration(
                _successionAccelerationEnabled,
                Mathf.Max(Constants.MinSuccessionAccelerationMultiplier, _successionAccelerationMultiplier - 1.0f),
                true);
        }

        if (Input.IsActionJustPressed("succession_multiplier_up") && _authoritativeWorld)
        {
            SetSuccessionAcceleration(
                _successionAccelerationEnabled,
                Mathf.Min(Constants.MaxSuccessionAccelerationMultiplier, _successionAccelerationMultiplier + 1.0f),
                true);
        }

        if (_hud.IsClimateEditorOpen)
        {
            HandleClimateMapPainting(delta, localPlayer);
            return;
        }

        if (_farmingModeEnabled)
        {
            HandleFarmBrushPainting(delta, localPlayer);
            return;
        }

        HandleBrushPainting(delta, localPlayer);
    }

    private void HandleBrushPainting(float delta, PlayerController localPlayer)
    {
        var modifierHeld = Input.IsActionPressed("brush_modifier");
        var modifierJustPressed = Input.IsActionJustPressed("brush_modifier");
        var brushActive = _brushModeEnabled || modifierHeld;

        if (!brushActive || _worldData is null)
        {
            _brushPaintAccumulator = 0.0f;
            _lastBrushCell = new Vector2I(int.MinValue, int.MinValue);
            _gameWorld.ClearBrushPreview();
            return;
        }

        var brushCell = _gameWorld.WorldToCell(GetMouseWorldPosition());
        if (_worldData.Contains(brushCell))
        {
            _gameWorld.SetBrushPreview(brushCell, _brushRadius, GetBrushPreviewColor(_selectedBrushBiome));
        }
        else
        {
            _gameWorld.ClearBrushPreview();
        }

        if (IsPointerOverHud())
        {
            return;
        }

        var applyPressed = modifierHeld || (_brushModeEnabled && Input.IsActionPressed("brush_apply"));
        var justPressed = modifierJustPressed || (_brushModeEnabled && Input.IsActionJustPressed("brush_apply"));
        if (!applyPressed)
        {
            _brushPaintAccumulator = 0.0f;
            _lastBrushCell = new Vector2I(int.MinValue, int.MinValue);
            return;
        }

        _brushPaintAccumulator += delta;
        var movedToNewCell = brushCell != _lastBrushCell;
        var repeatInterval = modifierHeld ? 0.02f : 0.05f;
        var canPaint = justPressed || movedToNewCell || _brushPaintAccumulator >= repeatInterval;
        if (!canPaint || !_worldData.Contains(brushCell))
        {
            return;
        }

        _brushPaintAccumulator = 0.0f;
        _lastBrushCell = brushCell;
        ApplyBrushBiome(localPlayer.PeerId, brushCell, _brushRadius, _selectedBrushBiome);
    }

    private void HandleClimateMapPainting(float delta, PlayerController localPlayer)
    {
        _gameWorld.ClearBrushPreview();
        if (_worldData is null)
        {
            _hud.ClearClimateMapBrushPreview();
            return;
        }

        if (!_hud.TryGetClimateMapCell(_worldData.Width, _worldData.Height, out var brushCell))
        {
            _brushPaintAccumulator = 0.0f;
            _lastBrushCell = new Vector2I(int.MinValue, int.MinValue);
            _hud.ClearClimateMapBrushPreview();
            return;
        }

        _hud.UpdateClimateMapBrushPreview(
            brushCell,
            _brushRadius,
            _worldData.Width,
            _worldData.Height,
            GetClimateBrushPreviewColor(_selectedClimateBrushField, _selectedClimateBrushValue));

        var modifierHeld = Input.IsActionPressed("brush_modifier");
        var modifierJustPressed = Input.IsActionJustPressed("brush_modifier");
        if (!modifierHeld)
        {
            _brushPaintAccumulator = 0.0f;
            _lastBrushCell = new Vector2I(int.MinValue, int.MinValue);
            return;
        }

        _brushPaintAccumulator += delta;
        var movedToNewCell = brushCell != _lastBrushCell;
        var canPaint = modifierJustPressed || movedToNewCell || _brushPaintAccumulator >= 0.02f;
        if (!canPaint)
        {
            return;
        }

        _brushPaintAccumulator = 0.0f;
        _lastBrushCell = brushCell;
        ApplyBrushClimate(
            localPlayer.PeerId,
            brushCell,
            _brushRadius,
            _selectedClimateBrushField,
            _selectedClimateBrushValue,
            _selectedClimateBrushStrength);
    }

    private void ToggleBrushMode()
    {
        _brushModeEnabled = !_brushModeEnabled;
        _hud.SetBrushState(_brushModeEnabled, _brushRadius, _selectedBrushBiome);
        if (!_brushModeEnabled)
        {
            _gameWorld.ClearBrushPreview();
        }

        _hud.ShowMessage(_brushModeEnabled
            ? "Brush mode enabled. Hold Q and move the mouse, or use left mouse in toggle mode."
            : "Brush mode disabled.");
    }

    private void AdjustBrushRadius(int step)
    {
        var nextRadius = Mathf.Clamp(_brushRadius + step, Constants.MinBrushRadius, Constants.MaxBrushRadius);
        if (nextRadius == _brushRadius)
        {
            return;
        }

        _brushRadius = nextRadius;
        _hud.SetBrushState(_brushModeEnabled, _brushRadius, _selectedBrushBiome);
        _hud.ShowMessage($"Brush radius set to {_brushRadius}.");
    }

    private void CycleMiniMapViewMode()
    {
        _gameWorld.CycleMiniMapViewMode();
        _hud.SetMiniMap(_gameWorld.MiniMapTexture);
        _hud.SetMiniMapMode(_gameWorld.GetMiniMapViewModeDisplayName());
        _hud.ShowMessage($"Mini map view switched to {_gameWorld.GetMiniMapViewModeDisplayName()}.");
    }

    private void SetSuccessionAcceleration(bool enabled, float multiplier, bool broadcastToPeers)
    {
        _successionAccelerationEnabled = enabled;
        _successionAccelerationMultiplier = Mathf.Clamp(
            multiplier,
            Constants.MinSuccessionAccelerationMultiplier,
            Constants.MaxSuccessionAccelerationMultiplier);
        _ecosystemSimulator.SetSuccessionAcceleration(_successionAccelerationEnabled, _successionAccelerationMultiplier);
        _hud.SetSuccessionAccelerationState(_successionAccelerationEnabled, _successionAccelerationMultiplier, _authoritativeWorld);

        if (broadcastToPeers && _networkManager.IsServer)
        {
            _syncManager.BroadcastSuccessionAcceleration(_successionAccelerationEnabled, _successionAccelerationMultiplier);
        }

        if (broadcastToPeers)
        {
            _hud.ShowMessage(_successionAccelerationEnabled
                ? $"Succession acceleration enabled at {_successionAccelerationMultiplier:0.#}x."
                : "Succession acceleration disabled.");
        }

        if (_successionAccelerationEnabled)
        {
            var burstHours = Mathf.Clamp(_successionAccelerationMultiplier * 0.5f, 2.0f, 18.0f);
            var changedTiles = _ecosystemSimulator.RunAccelerationBurst(Mathf.CeilToInt(burstHours));
            if (changedTiles.Count > 0 && _networkManager.IsServer)
            {
                FlushPendingTileDeltasForHostAction();
            }

            if (broadcastToPeers)
            {
                _hud.ShowMessage(changedTiles.Count > 0
                    ? $"Succession burst updated {changedTiles.Count} tiles at {_successionAccelerationMultiplier:0.#}x."
                    : $"Succession acceleration enabled at {_successionAccelerationMultiplier:0.#}x.");
            }
        }
    }

    private void SetSuccessionStepHours(float stepHours, bool broadcastToPeers)
    {
        _successionStepHours = Mathf.Clamp(
            stepHours,
            Constants.MinSuccessionStepHours,
            Constants.MaxSuccessionStepHours);
        _ecosystemSimulator.SetSimulationStepHours(_successionStepHours);
        _hud.SetSuccessionStepState(_successionStepHours, _authoritativeWorld);

        if (broadcastToPeers && _networkManager.IsServer)
        {
            _syncManager.BroadcastSuccessionStepHours(_successionStepHours);
        }

        if (broadcastToPeers)
        {
            _hud.ShowMessage($"Succession step set to {_successionStepHours:0.####}h.");
        }
    }

    private void ApplyBrushBiome(long peerId, Vector2I centerCell, int radius, BiomeType biome)
    {
        if (_worldData is null || !_worldData.Contains(centerCell))
        {
            return;
        }

        if (_authoritativeWorld)
        {
            ApplyAuthoritativeBiomeBrush(peerId, centerCell, radius, biome);
            return;
        }

        _syncManager.RequestBrushBiome(centerCell, radius, biome);
        if (peerId == GetLocalPeerId())
        {
            _hud.ShowMessage($"Brush request sent for {biome} at {centerCell.X},{centerCell.Y}.");
        }
    }

    private void ApplyAuthoritativeBiomeBrush(long peerId, Vector2I centerCell, int radius, BiomeType biome)
    {
        if (_worldData is null)
        {
            return;
        }

        var candidateCells = EnumerateBrushCells(centerCell, radius)
            .Where(_worldData.Contains)
            .ToArray();
        if (candidateCells.Length == 0)
        {
            return;
        }

        var changedCells = _ecosystemSimulator.PaintBiome(candidateCells, biome);
        if (changedCells.Count == 0)
        {
            if (peerId == GetLocalPeerId())
            {
                _hud.ShowMessage($"{biome} brush had no visible changes in radius {radius}.");
            }

            return;
        }

        if (_networkManager.IsServer)
        {
            FlushPendingTileDeltasForHostAction();
        }

        if (peerId == GetLocalPeerId())
        {
            _hud.ShowMessage($"Brush painted {biome} over {changedCells.Count} tiles with radius {radius}.");
        }
    }

    private void ApplyBrushClimate(
        long peerId,
        Vector2I centerCell,
        int radius,
        ClimateBrushField field,
        float targetValue,
        float blendStrength)
    {
        if (_worldData is null || !_worldData.Contains(centerCell))
        {
            return;
        }

        if (_authoritativeWorld)
        {
            ApplyAuthoritativeClimateBrush(peerId, centerCell, radius, field, targetValue, blendStrength);
            return;
        }

        _syncManager.RequestClimateBrush(centerCell, radius, field, targetValue, blendStrength);
    }

    private void ApplyAuthoritativeClimateBrush(
        long peerId,
        Vector2I centerCell,
        int radius,
        ClimateBrushField field,
        float targetValue,
        float blendStrength)
    {
        if (_worldData is null)
        {
            return;
        }

        var candidateCells = EnumerateBrushCells(centerCell, radius)
            .Where(_worldData.Contains)
            .ToArray();
        if (candidateCells.Length == 0)
        {
            return;
        }

        var changedCells = _ecosystemSimulator.PaintClimateControl(candidateCells, field, targetValue, blendStrength);
        if (changedCells.Count == 0)
        {
            if (peerId == GetLocalPeerId())
            {
                _hud.ShowMessage($"{field} brush had no visible changes in radius {radius}.");
            }

            return;
        }

        if (_networkManager.IsServer)
        {
            FlushPendingTileDeltasForHostAction();
        }
    }

    private void ToggleFarmingMode()
    {
        _farmingModeEnabled = !_farmingModeEnabled;
        _hud.SetFarmingModeUi(_farmingModeEnabled, _farmTool);
        _hud.ShowMessageHeld(
            _farmingModeEnabled
                ? "耕种模式：按住 Q 涂抹；R 切换工具；水源用浅滩表示并可影响周边湿度。"
                : "已退出耕种模式（地形预设中仍可选「耕地」群系）。",
            2.4f);
    }

    private void CycleFarmTool()
    {
        var values = (FarmBrushTool[])Enum.GetValues(typeof(FarmBrushTool));
        var idx = (Array.IndexOf(values, _farmTool) + 1) % values.Length;
        _farmTool = values[idx];
        _hud.SetFarmToolSelection(_farmTool);
        _hud.ShowMessageHeld($"耕种工具：{GetFarmToolDisplayName(_farmTool)}", 2.0f);
    }

    private static string GetFarmToolDisplayName(FarmBrushTool tool) => tool switch
    {
        FarmBrushTool.PlowFarmland => "翻土耕地",
        FarmBrushTool.PlaceWater => "浅水源",
        FarmBrushTool.WaterCrops => "浇水",
        FarmBrushTool.Fertilize => "施肥",
        FarmBrushTool.Weed => "除草",
        FarmBrushTool.PlantSeed => "播种",
        FarmBrushTool.Harvest => "收获",
        _ => tool.ToString()
    };

    private void HandleFarmBrushPainting(float delta, PlayerController localPlayer)
    {
        var modifierHeld = Input.IsActionPressed("brush_modifier");
        var modifierJustPressed = Input.IsActionJustPressed("brush_modifier");
        var brushActive = _brushModeEnabled || modifierHeld;

        if (!brushActive || _worldData is null)
        {
            _brushPaintAccumulator = 0.0f;
            _lastBrushCell = new Vector2I(int.MinValue, int.MinValue);
            _gameWorld.ClearBrushPreview();
            return;
        }

        var brushCell = _gameWorld.WorldToCell(GetMouseWorldPosition());
        if (_worldData.Contains(brushCell))
        {
            _gameWorld.SetBrushPreview(brushCell, _brushRadius, GetFarmToolPreviewColor(_farmTool));
        }
        else
        {
            _gameWorld.ClearBrushPreview();
        }

        if (IsPointerOverHud())
        {
            return;
        }

        var applyPressed = modifierHeld || (_brushModeEnabled && Input.IsActionPressed("brush_apply"));
        var justPressed = modifierJustPressed || (_brushModeEnabled && Input.IsActionJustPressed("brush_apply"));
        if (!applyPressed)
        {
            _brushPaintAccumulator = 0.0f;
            _lastBrushCell = new Vector2I(int.MinValue, int.MinValue);
            return;
        }

        _brushPaintAccumulator += delta;
        var movedToNewCell = brushCell != _lastBrushCell;
        var repeatInterval = modifierHeld ? 0.02f : 0.05f;
        var canPaint = justPressed || movedToNewCell || _brushPaintAccumulator >= repeatInterval;
        if (!canPaint || !_worldData.Contains(brushCell))
        {
            return;
        }

        _brushPaintAccumulator = 0.0f;
        _lastBrushCell = brushCell;
        QueueFarmBrushRequest(localPlayer.PeerId, brushCell, _brushRadius, _farmTool);
    }

    private void QueueFarmBrushRequest(long peerId, Vector2I centerCell, int radius, FarmBrushTool tool)
    {
        if (_worldData is null || !_worldData.Contains(centerCell))
        {
            return;
        }

        if (_authoritativeWorld)
        {
            ApplyAuthoritativeFarmBrush(peerId, centerCell, radius, tool);
            return;
        }

        _syncManager.RequestFarmBrush(centerCell, radius, tool);
        if (peerId == GetLocalPeerId())
        {
            _hud.ShowMessageHeld($"已向主机发送耕种请求：{GetFarmToolDisplayName(tool)}", 2.2f);
        }
    }

    private void ApplyAuthoritativeFarmBrush(long peerId, Vector2I centerCell, int radius, FarmBrushTool tool)
    {
        if (_worldData is null)
        {
            return;
        }

        var candidateCells = EnumerateBrushCells(centerCell, radius)
            .Where(_worldData.Contains)
            .ToArray();
        if (candidateCells.Length == 0)
        {
            return;
        }

        var result = _ecosystemSimulator.ApplyFarmBrush(
            candidateCells,
            tool,
            _ecosystemSimulator.SimulatedWorldHours);
        if (result.ChangedCells.Count == 0)
        {
            if (peerId == GetLocalPeerId())
            {
                var hint = GetFarmBrushFeedbackText(result.FeedbackWhenUnchanged);
                if (!string.IsNullOrEmpty(hint))
                {
                    _hud.ShowMessageHeld(hint, 3.6f);
                }
            }

            return;
        }

        _granaryUnits += result.HarvestCollected;
        _hud.UpdateGranary(_granaryUnits);
        if (peerId == GetLocalPeerId())
        {
            if (result.HarvestCollected > 0.5f)
            {
                _hud.ShowMessageHeld($"收获 +{result.HarvestCollected:0.#} 单位粮食（已入库）。", 3.8f);
            }
            else
            {
                _hud.ShowMessageHeld(
                    $"已应用「{GetFarmToolDisplayName(tool)}」：{result.ChangedCells.Count} 格",
                    2.8f);
            }
        }

        if (_networkManager.IsServer)
        {
            FlushPendingTileDeltasForHostAction();
        }
    }

    private static string GetFarmBrushFeedbackText(FarmBrushUserFeedback feedback) => feedback switch
    {
        FarmBrushUserFeedback.NoFarmlandInSelection =>
            "范围内没有耕地：请先用「翻土耕地」开垦，再浇水/施肥等。",
        FarmBrushUserFeedback.SoilMoistureAtMaximum =>
            "土壤湿度已达上限，无法继续浇水（或需先等待作物消耗水分）。",
        FarmBrushUserFeedback.NutrientsAndOrganicAtMaximum =>
            "养分与有机质均已饱和，施肥无效。",
        FarmBrushUserFeedback.WeedsAlreadyMinimal =>
            "杂草压力已很低，无需再除草。",
        FarmBrushUserFeedback.CropAlreadySeeded =>
            "该地块已播种，无需重复播种。",
        FarmBrushUserFeedback.NotReadyToHarvest =>
            "作物尚未成熟（进度需达到约 0.78 以上），无法收获。",
        FarmBrushUserFeedback.TerrainAlreadyMatches =>
            "地形已是当前工具目标状态（无需重复翻土/放置水源）。",
        FarmBrushUserFeedback.Unknown =>
            "本次操作没有产生可同步的变化。",
        FarmBrushUserFeedback.Ok => string.Empty,
        _ => "本次操作没有产生可同步的变化。"
    };

    private static Color GetFarmToolPreviewColor(FarmBrushTool tool) => tool switch
    {
        FarmBrushTool.PlowFarmland => new Color(0.55f, 0.38f, 0.22f, 0.28f),
        FarmBrushTool.PlaceWater => new Color(0.2f, 0.45f, 0.85f, 0.28f),
        FarmBrushTool.WaterCrops => new Color(0.35f, 0.65f, 0.95f, 0.28f),
        FarmBrushTool.Fertilize => new Color(0.45f, 0.72f, 0.35f, 0.28f),
        FarmBrushTool.Weed => new Color(0.55f, 0.82f, 0.35f, 0.28f),
        FarmBrushTool.PlantSeed => new Color(0.95f, 0.82f, 0.35f, 0.28f),
        FarmBrushTool.Harvest => new Color(0.95f, 0.55f, 0.2f, 0.3f),
        _ => new Color(1.0f, 0.2f, 0.2f, 0.22f)
    };

    private void SpawnLocalPlayerIfNeeded()
    {
        var peerId = GetLocalPeerId();
        if (_players.ContainsKey(peerId))
        {
            return;
        }

        var spawnPosition = AllocateSpawnPosition(peerId);
        SpawnPlayer(peerId, spawnPosition, true);
    }

    private void SpawnPlayer(long peerId, Vector2 spawnPosition, bool isLocalPlayer)
    {
        var displayOrdinal = GetOrCreatePlayerDisplayOrdinal(peerId);
        if (_players.TryGetValue(peerId, out var existing))
        {
            // 主机再次下发同一 peer 的出生点（或 RPC 顺序异常）时校正位置，避免客户端 HUD 格坐标与小人脱节。
            existing.GlobalPosition = spawnPosition;
            existing.Initialize(peerId, isLocalPlayer, displayOrdinal);
            _spawnPositions[peerId] = spawnPosition;
            if (isLocalPlayer)
            {
                _gameWorld.UpdateFocus(existing.GlobalPosition);
            }

            return;
        }

        var player = _playerScene.Instantiate<PlayerController>();
        player.Name = $"Player_{peerId}";
        player.GlobalPosition = spawnPosition;
        _gameWorld.PlayersRoot.AddChild(player);
        player.Initialize(peerId, isLocalPlayer, displayOrdinal);
        player.MovementModifierProvider = GetMovementModifierAt;
        player.LocalStateUpdated += (id, position, velocity, facing) => _syncManager.SubmitLocalPlayerState(id, position, velocity, facing);

        _players[peerId] = player;
        _spawnPositions[peerId] = spawnPosition;
        _npcManager.SeedMoodsForNewPeer(peerId);
        if (isLocalPlayer)
        {
            _gameWorld.UpdateFocus(player.GlobalPosition);
        }
    }

    private void OnSpawnPlayerReceived(long peerId, Vector2 spawnPosition)
    {
        SpawnPlayer(peerId, spawnPosition, peerId == GetLocalPeerId());
    }

    private void DespawnPlayer(long peerId)
    {
        if (!_players.TryGetValue(peerId, out var player))
        {
            return;
        }

        player.QueueFree();
        _players.Remove(peerId);
        _spawnPositions.Remove(peerId);
        _peerDisplayOrdinal.Remove(peerId);
    }

    private void OnPlayerStateReceived(long peerId, Vector2 position, Vector2 velocity, Vector2 facing)
    {
        if (_players.TryGetValue(peerId, out var player))
        {
            if (player.IsLocalPlayer)
            {
                return;
            }

            player.ApplyRemoteState(position, velocity, facing);
        }
    }

    private void ApplyRemoteTileDeltas(Godot.Collections.Array<Godot.Collections.Dictionary> payload)
    {
        if (_authoritativeWorld || _worldData is null)
        {
            return;
        }

        var changedCells = new List<Vector2I>();
        var controlCells = new List<Vector2I>();
        foreach (var entry in payload)
        {
            var cell = new Vector2I((int)entry["x"], (int)entry["y"]);
            if (!_worldData.Contains(cell))
            {
                continue;
            }

            ref var tile = ref _worldData.GetTileRef(cell);
            var rainfallChanged = entry.ContainsKey("rainfall") && Mathf.Abs(tile.Rainfall - (float)entry["rainfall"]) >= 0.002f;
            var sunlightChanged = entry.ContainsKey("sunlight") && Mathf.Abs(tile.Sunlight - (float)entry["sunlight"]) >= 0.002f;
            tile.Biome = (BiomeType)(int)entry["biome"];
            tile.Elevation = (float)entry["elevation"];
            tile.Moisture = (float)entry["ambient_moisture"];
            tile.Temperature = (float)entry["temperature"];
            tile.Rainfall = entry.ContainsKey("rainfall") ? (float)entry["rainfall"] : tile.Rainfall;
            tile.Sunlight = entry.ContainsKey("sunlight") ? (float)entry["sunlight"] : tile.Sunlight;
            tile.Flora = (FloraType)(int)entry["flora"];
            tile.Succession = (SuccessionStage)(int)entry["succession"];
            tile.FloraGrowth = (float)entry["growth"];
            tile.SoilMoisture = (float)entry["moisture"];
            tile.Nutrients = (float)entry["nutrients"];
            tile.OrganicMatter = (float)entry["organic"];
            tile.SnowCover = (float)entry["snow"];
            if (entry.ContainsKey("disturbance"))
            {
                tile.Disturbance = (int)entry["disturbance"];
            }

            if (entry.ContainsKey("crop_growth"))
            {
                tile.CropGrowth = (float)entry["crop_growth"];
            }

            if (entry.ContainsKey("weed_pressure"))
            {
                tile.WeedPressure = (float)entry["weed_pressure"];
            }

            if (entry.ContainsKey("farm_care_hours"))
            {
                tile.LastFarmCareGameHours = (float)entry["farm_care_hours"];
            }

            changedCells.Add(cell);
            if (rainfallChanged || sunlightChanged)
            {
                controlCells.Add(cell);
            }
        }

        if (controlCells.Count > 0)
        {
            _ecosystemSimulator.RefreshClimateFieldAnchors(controlCells, syncStateFromTiles: false);
        }

        _ecosystemSimulator.RefreshClimateFieldSamples(changedCells);
        _gameWorld.RefreshTiles(changedCells);
        RefreshClimateEditorHeatmap();
    }

    private float GetMovementModifierAt(Vector2 worldPosition)
    {
        if (_worldData is null)
        {
            return 1.0f;
        }

        var cell = _gameWorld.WorldToCell(worldPosition);
        if (!_worldData.Contains(cell))
        {
            return 0.0f;
        }

        var tile = _worldData.GetTile(cell);
        return tile.Biome switch
        {
            BiomeType.DeepWater => 0.0f,
            BiomeType.ShallowWater => 0.45f,
            BiomeType.Swamp => 0.68f,
            BiomeType.Snow => 0.84f,
            BiomeType.Farmland => 0.88f,
            _ => 1.0f
        };
    }

    private Vector2 GetMouseWorldPosition()
    {
        var camera = GetViewport().GetCamera2D();
        return camera?.GetGlobalMousePosition() ?? Vector2.Zero;
    }

    private bool IsPointerOverHud()
    {
        var hoveredControl = GetViewport().GuiGetHoveredControl();
        return hoveredControl is not null && hoveredControl.IsVisibleInTree();
    }

    private IEnumerable<Vector2I> EnumerateBrushCells(Vector2I centerCell, int radius)
    {
        var clampedRadius = Mathf.Clamp(radius, Constants.MinBrushRadius, Constants.MaxBrushRadius);
        var radiusSquared = clampedRadius * clampedRadius;
        for (var y = centerCell.Y - clampedRadius; y <= centerCell.Y + clampedRadius; y++)
        {
            for (var x = centerCell.X - clampedRadius; x <= centerCell.X + clampedRadius; x++)
            {
                var cell = new Vector2I(x, y);
                var offset = cell - centerCell;
                if (offset.LengthSquared() <= radiusSquared)
                {
                    yield return cell;
                }
            }
        }
    }

    private static Color GetBrushPreviewColor(BiomeType biome)
    {
        var color = BiomeClassifier.GetMiniMapColor(biome);
        return new Color(color.R, color.G, color.B, 0.22f);
    }

    private static Color GetClimateBrushPreviewColor(ClimateBrushField field, float targetValue)
    {
        var clamped = Mathf.Clamp(targetValue, 0.0f, 1.0f);
        var color = field switch
        {
            ClimateBrushField.Rainfall => new Color("6d4b29").Lerp(new Color("4a93d8"), clamped),
            ClimateBrushField.Sunlight => new Color("3759b6").Lerp(new Color("f28b28"), clamped),
            _ => Colors.Magenta
        };
        return new Color(color.R, color.G, color.B, 0.22f);
    }

    private void UpdateFocusAndHud()
    {
        if (!_players.TryGetValue(GetLocalPeerId(), out var localPlayer) || _worldData is null)
        {
            return;
        }

        if (_authoritativeWorld)
        {
            _ecosystemSimulator.SyncChunksNearFocusPoints(new[] { localPlayer.GlobalPosition });
        }

        _gameWorld.UpdateFocus(localPlayer.GlobalPosition);

        var cell = _gameWorld.WorldToCell(localPlayer.GlobalPosition);
        if (_worldData.Contains(cell))
        {
            _hud.UpdateTileInfo(cell, localPlayer.GlobalPosition, _worldData.GetTile(cell));
            _hud.UpdateMiniMapMarker(cell, _worldData.Width, _worldData.Height);
        }
        else
        {
            _hud.UpdateWorldPositionOnly(localPlayer.GlobalPosition, cell);
        }

        _hud.UpdateClock($"Time: Day {_dayNightCycle.ElapsedDays:0.0}  {FormatHour(_dayNightCycle.CurrentHour)}");
        _hud.UpdateWeather($"Weather: {_weatherSystem.CurrentState}  Season: {_seasonManager.GetDisplayName()}");
        _hud.UpdateNetwork(GetNetworkText());
    }

    private string GetNetworkText()
    {
        if (!_networkManager.HasActivePeer)
        {
            return "Network: Solo";
        }

        return _networkManager.IsServer
            ? $"Network: Host  Peers {_players.Count}"
            : $"Network: Client  ID {GetLocalPeerId()}";
    }

    private void ExportWorld()
    {
        if (_worldData is null)
        {
            _hud.ShowMessage("No world loaded to export.");
            return;
        }

        _exportFileDialog.CurrentFile = _worldData.GetSuggestedFileName();
        _exportFileDialog.PopupCenteredRatio(0.8f);
    }

    private void OpenImportDialog()
    {
        if (_networkManager.HasActivePeer && (!_networkManager.IsServer || _players.Count > 1))
        {
            ShowImportExportMessage("Import is only available in solo mode or before other LAN peers join.");
            return;
        }

        _importFileDialog.PopupCenteredRatio(0.8f);
    }

    private void OnExportFileSelected(string path)
    {
        if (_worldData is null)
        {
            _hud.ShowMessage("No world loaded to export.");
            return;
        }

        var result = _worldData.ExportToPath(path, out var savedPath, out var errorMessage);
        if (result != Error.Ok)
        {
            ShowImportExportMessage($"Export failed: {errorMessage}");
            return;
        }

        ShowImportExportMessage($"World exported to {savedPath}");
    }

    private void OnImportFileSelected(string path)
    {
        if (_networkManager.HasActivePeer && (!_networkManager.IsServer || _players.Count > 1))
        {
            ShowImportExportMessage("Import is only available in solo mode or before other LAN peers join.");
            return;
        }

        if (!WorldData.TryImportFromPath(path, out var importedWorld, out var errorMessage) || importedWorld is null)
        {
            ShowImportExportMessage($"Import failed: {errorMessage}");
            return;
        }

        _networkManager.Stop();
        LoadWorld(importedWorld, authoritative: true);
        SpawnLocalPlayerIfNeeded();
        ShowImportExportMessage($"Imported world: {Path.GetFileName(path)}");
    }

    private void ShowImportExportMessage(string message)
    {
        _mainMenu.SetStatus(message);
        if (_hud.Visible)
        {
            _hud.ShowMessage(message);
        }
    }

    private Vector2 AllocateSpawnPosition(long peerId)
    {
        if (_spawnPositions.TryGetValue(peerId, out var existing))
        {
            return existing;
        }

        if (_worldData is null)
        {
            return Vector2.Zero;
        }

        var cell = _worldGenerator.FindSpawnCell(_worldData);
        var angle = (float)(peerId * 0.72f);
        var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 22.0f;
        return _gameWorld.CellToWorld(cell) + offset;
    }

    private long GetLocalPeerId() => _networkManager.HasActivePeer ? Multiplayer.GetUniqueId() : 1L;

    private int GetOrCreatePlayerDisplayOrdinal(long peerId)
    {
        if (_peerDisplayOrdinal.TryGetValue(peerId, out var ordinal))
        {
            return ordinal;
        }

        ordinal = ++_nextPlayerDisplayOrdinal;
        _peerDisplayOrdinal[peerId] = ordinal;
        return ordinal;
    }

    private IReadOnlyList<MinimapOverlayEntity> BuildMinimapOverlayEntities()
    {
        var list = new List<MinimapOverlayEntity>();
        if (!_worldLoaded)
        {
            return list;
        }

        foreach (var pos in _npcManager.GetNpcWorldPositionsForMinimap())
        {
            list.Add(new MinimapOverlayEntity(pos, MinimapEntityKind.VillageNpc));
        }

        var localId = GetLocalPeerId();
        PlayerController? localPlayer = null;
        foreach (var kv in _players)
        {
            if (kv.Key == localId)
            {
                localPlayer = kv.Value;
                continue;
            }

            list.Add(new MinimapOverlayEntity(kv.Value.GlobalPosition, MinimapEntityKind.RemotePlayer));
        }

        if (localPlayer is not null)
        {
            list.Add(new MinimapOverlayEntity(localPlayer.GlobalPosition, MinimapEntityKind.LocalPlayer));
        }

        return list;
    }

    private IReadOnlyList<(long PeerId, Vector2 Position)> BuildNpcPlayerWorldPositions()
    {
        var list = new List<(long, Vector2)>(_players.Count);
        foreach (var kv in _players)
        {
            list.Add((kv.Key, kv.Value.GlobalPosition));
        }

        return list;
    }

    private void ClearPlayers()
    {
        foreach (var player in _players.Values)
        {
            player.QueueFree();
        }

        _players.Clear();
        _spawnPositions.Clear();
        _peerDisplayOrdinal.Clear();
        _nextPlayerDisplayOrdinal = 0;
    }

    private static string FormatHour(float hour)
    {
        var wholeHour = Mathf.FloorToInt(hour);
        var minutes = Mathf.FloorToInt((hour - wholeHour) * 60.0f);
        return $"{wholeHour:00}:{minutes:00}";
    }

    private static string FormatByteCount(int bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0f:0.0} KB";
        }

        return $"{bytes / (1024.0f * 1024.0f):0.00} MB";
    }

    private void TryOpenNearbyNpcPanel(PlayerController localPlayer)
    {
        if (!_npcManager.TryGetNearestNpc(
                localPlayer.GlobalPosition,
                NpcManager.InteractionRangePixels,
                localPlayer.PeerId,
                out var npcId,
                out var displayName,
                out var mood))
        {
            _hud.ShowMessage("附近没有村民。");
            return;
        }

        _npcInteractId = npcId;
        _npcInteractName = displayName;
        _npcInteractMood = mood;
        _hud.OpenNpcInteractionPanel(displayName, mood, NpcManager.GiftCropCost);
    }

    private void OnNpcTalkPressed()
    {
        if (_npcInteractId < 0)
        {
            return;
        }

        var line = NpcManager.FormatTalkLine(_npcInteractName, _npcInteractMood);
        _hud.ShowMessageHeld(line, 5f);
        _hud.CloseNpcInteractionPanel();
    }

    private void OnNpcGiftPressed()
    {
        if (_npcInteractId < 0)
        {
            return;
        }

        var id = _npcInteractId;
        if (!_networkManager.HasActivePeer)
        {
            if (_npcManager.TryLocalGiftCrop(id, ref _granaryUnits, GetLocalPeerId(), out var msg))
            {
                _hud.UpdateGranary(_granaryUnits);
            }

            _hud.ShowMessage(msg);
            _hud.CloseNpcInteractionPanel();
            return;
        }

        if (_networkManager.IsServer)
        {
            if (_npcManager.TryLocalGiftCrop(id, ref _granaryUnits, GetLocalPeerId(), out var msg))
            {
                _hud.UpdateGranary(_granaryUnits);
            }

            _hud.ShowMessage(msg);
        }
        else
        {
            if (_granaryUnits < NpcManager.GiftCropCost)
            {
                _hud.ShowMessage($"粮仓不足：需要 ≥ {NpcManager.GiftCropCost:0.#} 单位。");
                return;
            }

            _granaryUnits -= NpcManager.GiftCropCost;
            _hud.UpdateGranary(_granaryUnits);
            _syncManager.RequestNpcInteract(id, (int)NpcInteractionKind.GiftCrop);
            _hud.ShowMessage("你已赠送作物。");
        }

        _hud.CloseNpcInteractionPanel();
    }

    private void OnNpcHelpPressed()
    {
        if (_npcInteractId < 0)
        {
            return;
        }

        var id = _npcInteractId;
        if (!_networkManager.HasActivePeer)
        {
            if (_npcManager.TryLocalHelpFarm(id, out var msg))
            {
                _hud.ShowMessage(msg);
            }

            _hud.CloseNpcInteractionPanel();
            return;
        }

        if (_networkManager.IsServer)
        {
            if (_npcManager.TryLocalHelpFarm(id, out var msg))
            {
                _hud.ShowMessage(msg);
            }
        }
        else
        {
            _syncManager.RequestNpcInteract(id, (int)NpcInteractionKind.HelpFarm);
            _hud.ShowMessage("已请求协助耕种。");
        }

        _hud.CloseNpcInteractionPanel();
    }

    private void OnNpcInteractRequested(long actingPeerId, int npcId, int actionId)
    {
        if (!_networkManager.IsServer || _worldData is null)
        {
            return;
        }

        var kind = (NpcInteractionKind)actionId;
        if (kind is not (NpcInteractionKind.GiftCrop or NpcInteractionKind.HelpFarm))
        {
            return;
        }

        _npcManager.ApplyAuthoritativeOrder(npcId, kind, _ecosystemSimulator.SimulatedWorldHours, actingPeerId);
    }

    private static void EnsureInputActions()
    {
        EnsureAction("move_up", Key.W);
        EnsureAction("move_down", Key.S);
        EnsureAction("move_left", Key.A);
        EnsureAction("move_right", Key.D);
        EnsureAction("cycle_minimap_view", Key.V);
        EnsureAction("brush_modifier", Key.Q);
        EnsureAction("toggle_brush_mode", Key.B);
        EnsureAction("brush_radius_down", Key.Bracketleft);
        EnsureAction("brush_radius_up", Key.Bracketright);
        EnsureAction("toggle_succession_acceleration", Key.F);
        EnsureAction("succession_multiplier_down", Key.Pagedown);
        EnsureAction("succession_multiplier_up", Key.Pageup);
        EnsureAction("toggle_farming_mode", Key.G);
        EnsureAction("cycle_farm_tool", Key.R);
        EnsureAction("interact_npc", Key.E);
        EnsureMouseAction("brush_apply", MouseButton.Left);
    }

    private static void EnsureAction(string actionName, Key key)
    {
        if (!InputMap.HasAction(actionName))
        {
            InputMap.AddAction(actionName);
        }

        var eventKey = new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key
        };

        if (!InputMap.ActionHasEvent(actionName, eventKey))
        {
            InputMap.ActionAddEvent(actionName, eventKey);
        }
    }

    private static void EnsureMouseAction(string actionName, MouseButton button)
    {
        if (!InputMap.HasAction(actionName))
        {
            InputMap.AddAction(actionName);
        }

        var mouseEvent = new InputEventMouseButton
        {
            ButtonIndex = button
        };

        if (!InputMap.ActionHasEvent(actionName, mouseEvent))
        {
            InputMap.ActionAddEvent(actionName, mouseEvent);
        }
    }
}
