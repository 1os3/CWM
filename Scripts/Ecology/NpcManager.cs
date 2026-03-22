using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using CWM.Scripts.Core;
using CWM.Scripts.World;

namespace CWM.Scripts.Ecology;

public enum NpcInteractionKind
{
    Talk,
    GiftCrop,
    HelpFarm
}

/// <summary>主机权威：村民游荡、建立聚落、开垦耕种、清理林地；客户端仅接收快照。</summary>
public partial class NpcManager : Node
{
    public const int DefaultNpcCount = 5;
    public const float InteractionRangePixels = 56.0f;
    /// <summary>赠送村民消耗的粮仓单位（已翻倍，单次赠送更有分量）。</summary>
    public const float GiftCropCost = 20.0f;
    public const float HelpFarmDurationHours = 48.0f;

    /// <summary>好感 ≥ 此值（百分制）时优先跟随该玩家并协助除草/照料。</summary>
    public const float FollowPlayerMoodThreshold = 80f;

    /// <summary>好感 &lt; 此值时可能破坏该玩家附近的耕地。</summary>
    public const float HostilePlayerMoodThreshold = 10f;

    /// <summary>跟随玩家时在玩家周围搜寻耕地的半径（格）。</summary>
    public const int PlayerAssistFarmlandRadiusTiles = 8;

    /// <summary>破坏耕地时在目标玩家周围搜寻的半径（格）。</summary>
    public const int PlayerSabotageFarmlandRadiusTiles = 6;

    private const float FollowRetargetSeconds = 14f;
    private const float HostileSabotageIntervalSeconds = 3.2f;
    private const float HostileSabotageAttemptChance = 0.42f;
    private const float AbandonVillageMinSeconds = 38f;
    private const float AbandonVillageChanceAfterMin = 0.22f;

    private sealed class NpcAgent
    {
        public required int Id { get; init; }
        public required Sprite2D Sprite { get; init; }
        public Vector2 Velocity;
        public Vector2 WanderTarget;
        public float StateTimer;
        public float ActionCooldown;
        public NpcAiPhase Phase;
        public Vector2I? VillageCenter;
        public int VillageRadius = 7;
        public long? AssistingPeerId;
        public float FollowRetargetTimer;
        public float HostileFarmCooldown;
        public float AbandonVillageTimer;
        /// <summary>每位玩家对该村民的好感度（0–100），主机权威。</summary>
        public readonly Dictionary<long, float> MoodByPeer = new();
        public float HelpBoostUntilGameHours;
        public string DisplayName = string.Empty;
        public float EstablishCountdown;
    }

    private enum NpcAiPhase
    {
        Wandering,
        VillageLife,
        FollowingPlayer
    }

    private WorldData? _world;
    private GameWorld? _gameWorld;
    private EcosystemSimulator? _ecosystem;
    private WorldGenerator? _worldGenerator;
    private readonly RandomNumberGenerator _rng = new();
    private readonly Dictionary<int, NpcAgent> _agents = [];
    private int _nextId = 1;
    private Action? _flushTileSync;
    private Func<float>? _worldGameHours;
    private IReadOnlyList<(long PeerId, Vector2 Position)>? _playerPositionsThisFrame;

    /// <summary>主机每帧提供玩家位置，供跟随/破坏耕地等 AI 使用。</summary>
    public Func<IReadOnlyList<(long PeerId, Vector2 Position)>>? PlayerWorldPositionsProvider { get; set; }

    public bool Authoritative { get; set; } = true;

    public void Initialize(
        WorldData world,
        GameWorld gameWorld,
        EcosystemSimulator ecosystem,
        WorldGenerator worldGenerator,
        int seed,
        Action flushTileDeltas,
        Func<float> getSimulatedWorldHours)
    {
        _world = world;
        _gameWorld = gameWorld;
        _ecosystem = ecosystem;
        _worldGenerator = worldGenerator;
        _flushTileSync = flushTileDeltas;
        _worldGameHours = getSimulatedWorldHours;
        _rng.Seed = (ulong)(Mathf.Abs(seed) + 91331);
        ClearAgents();

        if (!Authoritative)
        {
            return;
        }

        for (var i = 0; i < DefaultNpcCount; i++)
        {
            var pos = FindLandWorldPosition();
            SpawnNpc(pos, GetRandomName(i));
        }
    }

    public override void _Process(double delta)
    {
        if (!Authoritative || _world is null || _gameWorld is null || _ecosystem is null)
        {
            return;
        }

        var dt = (float)delta;
        var hours = _worldGameHours?.Invoke() ?? 0.0f;
        _playerPositionsThisFrame = PlayerWorldPositionsProvider?.Invoke();
        foreach (var agent in _agents.Values)
        {
            UpdateNpc(agent, dt, hours);
        }
    }

    public Godot.Collections.Array<Godot.Collections.Dictionary> BuildSnapshots()
    {
        var snapshots = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var agent in _agents.Values)
        {
            var moodsArr = new Godot.Collections.Array();
            foreach (var kv in agent.MoodByPeer)
            {
                moodsArr.Add(new Godot.Collections.Dictionary
                {
                    ["peer"] = kv.Key,
                    ["m"] = kv.Value
                });
            }

            var d = new Godot.Collections.Dictionary
            {
                ["id"] = agent.Id,
                ["x"] = agent.Sprite.GlobalPosition.X,
                ["y"] = agent.Sprite.GlobalPosition.Y,
                ["vx"] = agent.Velocity.X,
                ["vy"] = agent.Velocity.Y,
                ["moods"] = moodsArr,
                ["name"] = agent.DisplayName,
                ["phase"] = (int)agent.Phase
            };

            if (agent.VillageCenter is { } vc)
            {
                d["vcx"] = vc.X;
                d["vcy"] = vc.Y;
            }
            else
            {
                d["vcx"] = int.MinValue;
                d["vcy"] = int.MinValue;
            }

            snapshots.Add(d);
        }

        return snapshots;
    }

    /// <summary>小地图叠加：村民世界像素坐标（主机与接收快照的客户端均可遍历）。</summary>
    public IReadOnlyList<Vector2> GetNpcWorldPositionsForMinimap()
    {
        var list = new List<Vector2>(_agents.Count);
        foreach (var agent in _agents.Values)
        {
            list.Add(agent.Sprite.GlobalPosition);
        }

        return list;
    }

    public void ApplyRemoteSnapshots(Godot.Collections.Array<Godot.Collections.Dictionary> snapshots)
    {
        if (Authoritative || _gameWorld is null)
        {
            return;
        }

        var received = new HashSet<int>();
        foreach (var snapshot in snapshots)
        {
            var id = (int)snapshot["id"];
            received.Add(id);
            if (!_agents.TryGetValue(id, out var agent))
            {
                var name = snapshot.ContainsKey("name") ? (string)snapshot["name"] : $"村民{id}";
                agent = SpawnNpc(new Vector2((float)snapshot["x"], (float)snapshot["y"]), name, id);
            }

            agent.Sprite.GlobalPosition = new Vector2((float)snapshot["x"], (float)snapshot["y"]);
            agent.Velocity = new Vector2((float)snapshot["vx"], (float)snapshot["vy"]);
            ApplyMoodsFromSnapshot(agent, snapshot);
            agent.DisplayName = snapshot.ContainsKey("name") ? (string)snapshot["name"] : agent.DisplayName;
            agent.Phase = snapshot.ContainsKey("phase") ? (NpcAiPhase)(int)snapshot["phase"] : NpcAiPhase.Wandering;
            var vcx = (int)snapshot["vcx"];
            var vcy = (int)snapshot["vcy"];
            agent.VillageCenter = vcx == int.MinValue ? null : new Vector2I(vcx, vcy);
        }

        foreach (var staleId in _agents.Keys.Where(id => !received.Contains(id)).ToArray())
        {
            _agents[staleId].Sprite.QueueFree();
            _agents.Remove(staleId);
        }
    }

    private static void ApplyMoodsFromSnapshot(NpcAgent agent, Godot.Collections.Dictionary snapshot)
    {
        agent.MoodByPeer.Clear();
        if (snapshot.ContainsKey("moods"))
        {
            var moodsVar = snapshot["moods"];
            if (moodsVar.VariantType != Variant.Type.Array)
            {
                return;
            }

            var arr = moodsVar.AsGodotArray();
            for (var i = 0; i < arr.Count; i++)
            {
                var itemVar = arr[i];
                if (itemVar.VariantType != Variant.Type.Dictionary)
                {
                    continue;
                }

                var entry = itemVar.AsGodotDictionary();
                var peer = ReadPeerId(entry["peer"]);
                var m = entry["m"].AsSingle();
                agent.MoodByPeer[peer] = Mathf.Clamp(m, 0f, 100f);
            }
        }
        else if (snapshot.ContainsKey("mood"))
        {
            // 旧版：单一 mood，仅兼容单机 peer 1
            agent.MoodByPeer[1] = Mathf.Clamp((float)snapshot["mood"], 0f, 100f);
        }
    }

    private static long ReadPeerId(Variant v)
    {
        return v.VariantType switch
        {
            Variant.Type.Int => v.AsInt32(),
            Variant.Type.Float => (long)v.AsSingle(),
            _ => (long)v.AsDouble()
        };
    }

    /// <summary>主机：新玩家加入时为所有村民生成该玩家的初始好感（并随 NPC 快照同步）。</summary>
    public void SeedMoodsForNewPeer(long peerId)
    {
        if (!Authoritative)
        {
            return;
        }

        foreach (var agent in _agents.Values)
        {
            EnsureMoodInitialized(agent, peerId);
        }
    }

    private void EnsureMoodInitialized(NpcAgent agent, long peerId)
    {
        if (agent.MoodByPeer.ContainsKey(peerId))
        {
            return;
        }

        agent.MoodByPeer[peerId] = _rng.RandfRange(35f, 65f);
    }

    private float GetMoodForViewer(NpcAgent agent, long viewerPeerId)
    {
        if (agent.MoodByPeer.TryGetValue(viewerPeerId, out var m))
        {
            return m;
        }

        if (!Authoritative)
        {
            // 客户端尚未收到该 peer 的好感条目时显示中性值
            return 50f;
        }

        EnsureMoodInitialized(agent, viewerPeerId);
        return agent.MoodByPeer[viewerPeerId];
    }

    /// <summary>本地交谈文案（客户端也可用快照中的 mood/name 调用）。</summary>
    public static string FormatTalkLine(string displayName, float mood)
    {
        var lines = mood switch
        {
            >= 70f => new[]
            {
                $"{displayName}：今年收成看起来不错，你要一起下地吗？",
                $"{displayName}：林子边上我又清了一块地，准备种些东西。"
            },
            >= 45f => new[]
            {
                $"{displayName}：这地得常浇水，不然杂草比庄稼还旺。",
                $"{displayName}：你要是有多余的粮食，村里人都感激。"
            },
            _ => new[]
            {
                $"{displayName}：日子紧，地也荒……你愿意搭把手吗？",
                $"{displayName}：别踩苗。"
            }
        };

        var idx = Mathf.Abs(HashCode.Combine(displayName.GetHashCode(), (int)(mood * 1000f))) % lines.Length;
        return lines[idx];
    }

    /// <summary>主机权威：来自联机 RPC 的赠送/协助（赠送不在主机扣粮仓，由发起端本地扣除）。</summary>
    public void ApplyAuthoritativeOrder(int npcId, NpcInteractionKind kind, float simulatedGameHours, long actingPeerId)
    {
        if (!Authoritative || !_agents.TryGetValue(npcId, out var agent))
        {
            return;
        }

        switch (kind)
        {
            case NpcInteractionKind.GiftCrop:
                EnsureMoodInitialized(agent, actingPeerId);
                agent.MoodByPeer[actingPeerId] = Mathf.Clamp(agent.MoodByPeer[actingPeerId] + 18f, 0f, 100f);
                break;
            case NpcInteractionKind.HelpFarm:
                agent.HelpBoostUntilGameHours = simulatedGameHours + HelpFarmDurationHours;
                break;
        }
    }

    /// <summary>本机尝试赠送（扣粮仓 + 更新主机村民关系）。单机或主机玩家。</summary>
    public bool TryLocalGiftCrop(int npcId, ref float granaryUnits, long actingPeerId, out string message)
    {
        message = string.Empty;
        if (!Authoritative || !_agents.ContainsKey(npcId))
        {
            message = "找不到该村民。";
            return false;
        }

        if (granaryUnits < GiftCropCost)
        {
            message = $"粮仓不足：赠送需要至少 {GiftCropCost:0.#} 单位收成。";
            return false;
        }

        granaryUnits -= GiftCropCost;
        var h = _worldGameHours?.Invoke() ?? 0f;
        ApplyAuthoritativeOrder(npcId, NpcInteractionKind.GiftCrop, h, actingPeerId);
        message = "村民感激地收下了作物。";
        return true;
    }

    /// <summary>本机协助耕种（主机立即生效）。</summary>
    public bool TryLocalHelpFarm(int npcId, out string message)
    {
        message = string.Empty;
        if (!Authoritative || !_agents.TryGetValue(npcId, out var agent))
        {
            message = "找不到该村民。";
            return false;
        }

        var h = _worldGameHours?.Invoke() ?? 0f;
        agent.HelpBoostUntilGameHours = h + HelpFarmDurationHours;
        message = $"{agent.DisplayName} 会与你一同照料田地一阵子。";
        return true;
    }

    public bool TryGetNearestNpc(Vector2 worldPos, float maxRange, long viewerPeerId, out int npcId, out string displayName, out float mood)
    {
        npcId = -1;
        displayName = string.Empty;
        mood = 0f;
        var best = maxRange * maxRange;
        foreach (var agent in _agents.Values)
        {
            var d = agent.Sprite.GlobalPosition.DistanceSquaredTo(worldPos);
            if (d < best)
            {
                best = d;
                npcId = agent.Id;
                displayName = agent.DisplayName;
                mood = GetMoodForViewer(agent, viewerPeerId);
            }
        }

        return npcId >= 0;
    }

    private NpcAgent SpawnNpc(Vector2 worldPos, string name, int? fixedId = null)
    {
        int id;
        if (fixedId is { } fid)
        {
            id = fid;
            _nextId = Math.Max(_nextId, id + 1);
        }
        else
        {
            id = _nextId++;
        }

        var sprite = new Sprite2D();
        var tex = CreateNpcTexture(id);
        sprite.Texture = tex;
        sprite.Centered = true;
        sprite.GlobalPosition = worldPos;
        _gameWorld!.NpcRoot.AddChild(sprite);
        var agent = new NpcAgent
        {
            Id = id,
            Sprite = sprite,
            WanderTarget = worldPos + new Vector2(_rng.RandfRange(-80, 80), _rng.RandfRange(-80, 80)),
            DisplayName = name,
            Phase = NpcAiPhase.Wandering,
            EstablishCountdown = _rng.RandfRange(12f, 38f),
            HostileFarmCooldown = _rng.RandfRange(1.5f, HostileSabotageIntervalSeconds)
        };
        _agents[id] = agent;
        return agent;
    }

    private static ImageTexture CreateNpcTexture(int seed)
    {
        var img = Image.CreateEmpty(14, 18, false, Image.Format.Rgba8);
        var hue = (seed * 47 % 360) / 360f;
        var body = Color.FromHsv(hue, 0.55f, 0.92f);
        var outline = body.Darkened(0.45f);
        for (var y = 0; y < 18; y++)
        {
            for (var x = 0; x < 14; x++)
            {
                var c = Colors.Transparent;
                if (x is >= 4 and <= 9 && y is >= 2 and <= 7)
                {
                    c = body;
                }
                else if (x is >= 3 and <= 10 && y is >= 8 and <= 15)
                {
                    c = body.Lerp(Colors.White, 0.12f);
                }
                else if (x is >= 2 and <= 11 && y is >= 16 and <= 17)
                {
                    c = outline;
                }

                if (c.A > 0.01f)
                {
                    img.SetPixel(x, y, c);
                }
            }
        }

        return ImageTexture.CreateFromImage(img);
    }

    private void ClearAgents()
    {
        foreach (var a in _agents.Values)
        {
            a.Sprite.QueueFree();
        }

        _agents.Clear();
    }

    private Vector2 FindLandWorldPosition()
    {
        if (_world is null || _gameWorld is null || _worldGenerator is null)
        {
            return Vector2.Zero;
        }

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var cell = new Vector2I(_rng.RandiRange(8, _world.Width - 9), _rng.RandiRange(8, _world.Height - 9));
            if (!_world.Contains(cell))
            {
                continue;
            }

            var t = _world.GetTile(cell);
            if (t.IsWater || t.Biome is BiomeType.BareRock)
            {
                continue;
            }

            return _gameWorld.CellToWorld(cell) + new Vector2(_rng.RandfRange(-6, 6), _rng.RandfRange(-6, 6));
        }

        return _gameWorld.CellToWorld(_worldGenerator.FindSpawnCell(_world));
    }

    private static string GetRandomName(int index)
    {
        var family = new[] { "陈", "林", "王", "张", "刘", "赵", "吴", "周" };
        var given = new[] { "禾", "田", "耕", "穗", "禾", "野", "禾", "禾" };
        return $"{family[index % family.Length]}{given[index % given.Length]}";
    }

    private void UpdateNpc(NpcAgent agent, float delta, float gameHours)
    {
        if (_world is null || _gameWorld is null || _ecosystem is null)
        {
            return;
        }

        agent.StateTimer += delta;
        agent.ActionCooldown = Mathf.Max(0f, agent.ActionCooldown - delta);

        var cell = _gameWorld.WorldToCell(agent.Sprite.GlobalPosition);
        if (!_world.Contains(cell) || _world.GetTile(cell).IsWater)
        {
            agent.Sprite.GlobalPosition = FindLandWorldPosition();
            return;
        }

        if (agent.Phase != NpcAiPhase.FollowingPlayer)
        {
            TryHostileSabotageTick(agent, delta);
        }

        if (agent.Phase != NpcAiPhase.FollowingPlayer)
        {
            TryTransitionToFollowingIfEligible(agent);
        }

        switch (agent.Phase)
        {
            case NpcAiPhase.Wandering:
                agent.EstablishCountdown -= delta;
                if (agent.EstablishCountdown <= 0f && agent.VillageCenter is null)
                {
                    TryEstablishVillage(agent);
                    agent.EstablishCountdown = _rng.RandfRange(40f, 90f);
                }

                MoveToward(agent, agent.WanderTarget, delta, 55f);
                if (agent.Sprite.GlobalPosition.DistanceSquaredTo(agent.WanderTarget) < 120f || agent.StateTimer > 9f)
                {
                    agent.StateTimer = 0f;
                    agent.WanderTarget = agent.Sprite.GlobalPosition + new Vector2(_rng.RandfRange(-140, 140), _rng.RandfRange(-140, 140));
                }

                break;
            case NpcAiPhase.VillageLife:
                var vc = agent.VillageCenter!.Value;
                var hub = _gameWorld.CellToWorld(vc);
                if (agent.StateTimer > 8f ||
                    (agent.StateTimer > 0.55f && agent.Sprite.GlobalPosition.DistanceSquaredTo(agent.WanderTarget) < 220f))
                {
                    agent.StateTimer = 0f;
                    PickVillagePatrolTarget(agent, hub);
                }

                MoveToward(agent, agent.WanderTarget, delta, 42f);
                if (agent.ActionCooldown <= 0f && agent.Sprite.GlobalPosition.DistanceSquaredTo(hub) < 6400f)
                {
                    DoVillageTick(agent, gameHours);
                    var boosted = gameHours < agent.HelpBoostUntilGameHours;
                    agent.ActionCooldown = boosted ? 0.55f : 1.35f;
                }

                agent.AbandonVillageTimer += delta;
                if (agent.AbandonVillageTimer >= AbandonVillageMinSeconds && _rng.Randf() < AbandonVillageChanceAfterMin)
                {
                    agent.AbandonVillageTimer = 0f;
                    AbandonVillageAndWander(agent);
                    return;
                }

                break;
            case NpcAiPhase.FollowingPlayer:
                DoFollowingPlayerPhase(agent, delta, gameHours);
                break;
        }
    }

    private List<long> CollectEligibleFollowPeers(NpcAgent agent)
    {
        return agent.MoodByPeer
            .Where(kv => kv.Value >= FollowPlayerMoodThreshold)
            .Select(kv => kv.Key)
            .ToList();
    }

    private bool TryGetPlayerWorldPosition(long peerId, out Vector2 position)
    {
        position = default;
        if (_playerPositionsThisFrame is null)
        {
            return false;
        }

        foreach (var e in _playerPositionsThisFrame)
        {
            if (e.PeerId == peerId)
            {
                position = e.Position;
                return true;
            }
        }

        return false;
    }

    private void TryTransitionToFollowingIfEligible(NpcAgent agent)
    {
        var eligible = CollectEligibleFollowPeers(agent);
        if (eligible.Count == 0)
        {
            return;
        }

        var withPos = eligible.Where(pid => TryGetPlayerWorldPosition(pid, out _)).ToList();
        if (withPos.Count == 0)
        {
            return;
        }

        agent.Phase = NpcAiPhase.FollowingPlayer;
        agent.AssistingPeerId = withPos[_rng.RandiRange(0, withPos.Count - 1)];
        agent.FollowRetargetTimer = 0f;
    }

    private void ExitFollowingPhase(NpcAgent agent)
    {
        agent.AssistingPeerId = null;
        agent.FollowRetargetTimer = 0f;
        agent.AbandonVillageTimer = 0f;
        if (agent.VillageCenter is { } vc && _gameWorld is not null)
        {
            agent.Phase = NpcAiPhase.VillageLife;
            agent.StateTimer = 0f;
            PickVillagePatrolTarget(agent, _gameWorld.CellToWorld(vc));
        }
        else
        {
            agent.Phase = NpcAiPhase.Wandering;
            agent.StateTimer = 0f;
        }
    }

    private void DoFollowingPlayerPhase(NpcAgent agent, float delta, float gameHours)
    {
        if (_world is null || _gameWorld is null || _ecosystem is null)
        {
            return;
        }

        var eligible = CollectEligibleFollowPeers(agent);
        var withPos = eligible.Where(pid => TryGetPlayerWorldPosition(pid, out _)).ToList();
        if (withPos.Count == 0)
        {
            ExitFollowingPhase(agent);
            return;
        }

        agent.FollowRetargetTimer += delta;
        if (agent.AssistingPeerId is null ||
            !withPos.Contains(agent.AssistingPeerId.Value) ||
            agent.FollowRetargetTimer >= FollowRetargetSeconds)
        {
            agent.AssistingPeerId = withPos[_rng.RandiRange(0, withPos.Count - 1)];
            agent.FollowRetargetTimer = 0f;
        }

        var peerId = agent.AssistingPeerId!.Value;
        if (!TryGetPlayerWorldPosition(peerId, out var playerPos))
        {
            ExitFollowingPhase(agent);
            return;
        }

        var orbit = new Vector2(_rng.RandfRange(-28f, 28f), _rng.RandfRange(-32f, 32f));
        MoveToward(agent, playerPos + orbit, delta, 52f);

        var d2 = agent.Sprite.GlobalPosition.DistanceSquaredTo(playerPos);
        if (d2 < 272f * 272f && agent.ActionCooldown <= 0f)
        {
            var pCell = _gameWorld.WorldToCell(playerPos);
            var farmCells = EnumerateDisk(pCell, PlayerAssistFarmlandRadiusTiles)
                .Where(c => _world.Contains(c) && _world.GetTile(c).Biome == BiomeType.Farmland)
                .OrderBy(_ => _rng.Randf())
                .Take(6)
                .ToArray();
            if (farmCells.Length > 0)
            {
                _ = _ecosystem.ApplyFarmBrush(farmCells, FarmBrushTool.Weed, gameHours);
                _flushTileSync?.Invoke();
            }

            agent.ActionCooldown = 0.48f;
        }

        agent.AbandonVillageTimer += delta;
        if (agent.AbandonVillageTimer >= AbandonVillageMinSeconds && _rng.Randf() < AbandonVillageChanceAfterMin * 0.3f)
        {
            agent.AbandonVillageTimer = 0f;
            AbandonVillageAndWander(agent);
            return;
        }
    }

    private void AbandonVillageAndWander(NpcAgent agent)
    {
        agent.VillageCenter = null;
        agent.Phase = NpcAiPhase.Wandering;
        agent.AssistingPeerId = null;
        agent.FollowRetargetTimer = 0f;
        agent.AbandonVillageTimer = 0f;
        agent.EstablishCountdown = _rng.RandfRange(28f, 62f);
        agent.StateTimer = 0f;
        agent.WanderTarget = agent.Sprite.GlobalPosition +
            new Vector2(_rng.RandfRange(-240f, 240f), _rng.RandfRange(-240f, 240f));
    }

    private bool IsCellInAnyNpcVillage(Vector2I cell)
    {
        foreach (var other in _agents.Values)
        {
            if (other.VillageCenter is not { } vc)
            {
                continue;
            }

            var dx = cell.X - vc.X;
            var dy = cell.Y - vc.Y;
            var r = other.VillageRadius + 1;
            if (dx * dx + dy * dy <= r * r)
            {
                return true;
            }
        }

        return false;
    }

    private void TryHostileSabotageTick(NpcAgent agent, float delta)
    {
        if (_world is null || _gameWorld is null || _ecosystem is null || _playerPositionsThisFrame is null)
        {
            return;
        }

        var hostilePeers = agent.MoodByPeer
            .Where(kv => kv.Value < HostilePlayerMoodThreshold)
            .Select(kv => kv.Key)
            .ToList();
        if (hostilePeers.Count == 0)
        {
            return;
        }

        agent.HostileFarmCooldown -= delta;
        if (agent.HostileFarmCooldown > 0f)
        {
            return;
        }

        agent.HostileFarmCooldown = HostileSabotageIntervalSeconds;
        if (_rng.Randf() > HostileSabotageAttemptChance)
        {
            return;
        }

        var peerId = hostilePeers[_rng.RandiRange(0, hostilePeers.Count - 1)];
        if (!TryGetPlayerWorldPosition(peerId, out var ppos))
        {
            return;
        }

        var pCell = _gameWorld.WorldToCell(ppos);
        var candidates = EnumerateDisk(pCell, PlayerSabotageFarmlandRadiusTiles)
            .Where(c =>
                _world.Contains(c) &&
                _world.GetTile(c).Biome == BiomeType.Farmland &&
                !IsCellInAnyNpcVillage(c))
            .OrderBy(_ => _rng.Randf())
            .Take(2)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        _ = _ecosystem.PaintBiome(candidates, BiomeType.Grassland);
        _flushTileSync?.Invoke();
    }

    private void TryEstablishVillage(NpcAgent agent)
    {
        if (_world is null || _gameWorld is null)
        {
            return;
        }

        var center = _gameWorld.WorldToCell(agent.Sprite.GlobalPosition);
        if (!_world.Contains(center))
        {
            return;
        }

        agent.VillageCenter = center;
        agent.Phase = NpcAiPhase.VillageLife;
        agent.StateTimer = 0f;
        PickVillagePatrolTarget(agent, _gameWorld.CellToWorld(center));
        var clear = EnumerateDisk(center, 1).ToArray();
        _ = _ecosystem!.PaintBiome(clear, BiomeType.Grassland);
        _flushTileSync?.Invoke();
    }

    /// <summary>聚落内巡逻点，避免一直走向格心导致 <see cref="MoveToward"/> 在距离&lt;√2 时永久停住。</summary>
    private void PickVillagePatrolTarget(NpcAgent agent, Vector2 hubWorld)
    {
        if (_world is null || _gameWorld is null)
        {
            agent.WanderTarget = hubWorld;
            return;
        }

        var rPx = agent.VillageRadius * Constants.TileSize * 0.9f;
        for (var i = 0; i < 16; i++)
        {
            var offset = new Vector2(_rng.RandfRange(-rPx, rPx), _rng.RandfRange(-rPx, rPx));
            var p = hubWorld + offset;
            var cell = _gameWorld.WorldToCell(p);
            if (_world.Contains(cell) && !_world.GetTile(cell).IsWater)
            {
                agent.WanderTarget = p;
                return;
            }
        }

        agent.WanderTarget = hubWorld + new Vector2(Constants.TileSize, 0);
    }

    private void DoVillageTick(NpcAgent agent, float gameHours)
    {
        if (_world is null || _gameWorld is null || _ecosystem is null || agent.VillageCenter is null)
        {
            return;
        }

        var vc = agent.VillageCenter.Value;
        var roll = _rng.Randf();
        var boosted = gameHours < agent.HelpBoostUntilGameHours;
        if (roll < 0.38f)
        {
            var farmCells = EnumerateDisk(vc, agent.VillageRadius)
                .Where(c => _world.Contains(c) && _world.GetTile(c).Biome == BiomeType.Farmland)
                .Take(4 + (boosted ? 3 : 0))
                .ToArray();
            if (farmCells.Length > 0)
            {
                var tool = _rng.Randf() < 0.45f
                    ? FarmBrushTool.WaterCrops
                    : (_rng.Randf() < 0.5f ? FarmBrushTool.Weed : FarmBrushTool.PlantSeed);
                _ = _ecosystem.ApplyFarmBrush(farmCells, tool, gameHours);
                _flushTileSync?.Invoke();
            }
            else
            {
                var grassCandidates = EnumerateDisk(vc, agent.VillageRadius)
                    .Where(c => _world.Contains(c) &&
                        (_world.GetTile(c).Biome == BiomeType.Grassland || _world.GetTile(c).Biome == BiomeType.Forest))
                    .ToList();
                if (grassCandidates.Count > 0)
                {
                    var grass = grassCandidates[_rng.RandiRange(0, grassCandidates.Count - 1)];
                    _ = _ecosystem.PaintBiome(new[] { grass }, BiomeType.Farmland);
                    _ = _ecosystem.ApplyFarmBrush(new[] { grass }, FarmBrushTool.PlowFarmland, gameHours);
                    _flushTileSync?.Invoke();
                }
            }
        }
        else if (roll < 0.72f)
        {
            var forest = EnumerateDisk(vc, agent.VillageRadius + 2)
                .Where(c => _world.Contains(c) && _world.GetTile(c).Biome is BiomeType.Forest or BiomeType.ConiferForest)
                .Take(1)
                .ToArray();
            if (forest.Length > 0)
            {
                _ = _ecosystem.PaintBiome(forest, BiomeType.Grassland);
                _flushTileSync?.Invoke();
            }
        }
        else if (roll < 0.9f)
        {
            var expand = EnumerateDisk(vc, agent.VillageRadius)
                .Where(c => _world.Contains(c) && _world.GetTile(c).Biome == BiomeType.Grassland)
                .Take(2)
                .ToArray();
            if (expand.Length > 0)
            {
                _ = _ecosystem.PaintBiome(expand, BiomeType.Farmland);
                _ = _ecosystem.ApplyFarmBrush(expand, FarmBrushTool.PlowFarmland, gameHours);
                _flushTileSync?.Invoke();
            }
        }
    }

    private static void MoveToward(NpcAgent agent, Vector2 target, float delta, float speed)
    {
        var pos = agent.Sprite.GlobalPosition;
        var to = target - pos;
        if (to.LengthSquared() < 2f)
        {
            agent.Velocity = Vector2.Zero;
            return;
        }

        agent.Velocity = to.Normalized() * speed;
        pos += agent.Velocity * delta;
        agent.Sprite.GlobalPosition = pos;
    }

    private static IEnumerable<Vector2I> EnumerateDisk(Vector2I center, int radius)
    {
        var r2 = radius * radius;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2)
                {
                    continue;
                }

                yield return center + new Vector2I(dx, dy);
            }
        }
    }

}
