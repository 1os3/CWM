using Godot;
using CWM.Scripts.Core;
using CWM.Scripts.World;
using WorldTileData = CWM.Scripts.World.TileData;

namespace CWM.Scripts.Ecology;

public enum AnimalKind
{
    Herbivore,
    Predator,
    Bird
}

public partial class FaunaManager : Node
{
    private sealed class AnimalAgent
    {
        public required int Id { get; init; }
        public required AnimalKind Kind { get; init; }
        public required Sprite2D Sprite { get; init; }
        public Vector2 Velocity { get; set; }
        public Vector2 WanderTarget { get; set; }
        public float Energy { get; set; }
        public float ReproductionCooldown { get; set; }
    }

    private WorldData? _world;
    private GameWorld? _gameWorld;
    private readonly RandomNumberGenerator _rng = new();
    private readonly Dictionary<int, AnimalAgent> _agents = [];
    private readonly List<Vector2> _focusPositions = [];
    private int _nextId = 1;

    public bool Authoritative { get; set; } = true;

    public void Initialize(WorldData world, GameWorld gameWorld, int seed)
    {
        _world = world;
        _gameWorld = gameWorld;
        _rng.Seed = (ulong)(Mathf.Abs(seed) + 58291);
        ClearAgents();

        if (!Authoritative)
        {
            return;
        }

        for (var i = 0; i < 40; i++)
        {
            SpawnAgent(AnimalKind.Herbivore, FindSpawnPosition(tile => !tile.IsWater && tile.Flora != FloraType.None));
        }

        for (var i = 0; i < 14; i++)
        {
            SpawnAgent(AnimalKind.Predator, FindSpawnPosition(tile => tile.Biome is BiomeType.Forest or BiomeType.ConiferForest or BiomeType.Grassland));
        }

        for (var i = 0; i < 6; i++)
        {
            SpawnAgent(AnimalKind.Bird, FindSpawnPosition(tile => !tile.IsWater));
        }
    }

    public override void _Process(double delta)
    {
        if (!Authoritative || _world is null || _gameWorld is null)
        {
            return;
        }

        var snapshot = _agents.Values.ToArray();
        foreach (var agent in snapshot)
        {
            UpdateAgent(agent, (float)delta, snapshot);
        }
    }

    public void SetFocusPositions(IEnumerable<Vector2> focusPositions)
    {
        _focusPositions.Clear();
        _focusPositions.AddRange(focusPositions);
    }

    public Godot.Collections.Array<Godot.Collections.Dictionary> BuildSnapshots()
    {
        var snapshots = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var agent in _agents.Values)
        {
            snapshots.Add(new Godot.Collections.Dictionary
            {
                ["id"] = agent.Id,
                ["kind"] = (int)agent.Kind,
                ["x"] = agent.Sprite.GlobalPosition.X,
                ["y"] = agent.Sprite.GlobalPosition.Y,
                ["vx"] = agent.Velocity.X,
                ["vy"] = agent.Velocity.Y
            });
        }

        return snapshots;
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
                agent = SpawnAgent((AnimalKind)(int)snapshot["kind"], new Vector2((float)snapshot["x"], (float)snapshot["y"]), id);
            }

            agent.Sprite.GlobalPosition = new Vector2((float)snapshot["x"], (float)snapshot["y"]);
            agent.Velocity = new Vector2((float)snapshot["vx"], (float)snapshot["vy"]);
        }

        foreach (var staleId in _agents.Keys.Where(id => !received.Contains(id)).ToArray())
        {
            _agents[staleId].Sprite.QueueFree();
            _agents.Remove(staleId);
        }
    }

    private void UpdateAgent(AnimalAgent agent, float delta, IReadOnlyList<AnimalAgent> population)
    {
        if (_world is null || _gameWorld is null)
        {
            return;
        }

        agent.Energy -= delta * (agent.Kind == AnimalKind.Predator ? 0.04f : 0.02f);
        agent.ReproductionCooldown = Mathf.Max(0.0f, agent.ReproductionCooldown - delta);

        var currentCell = _gameWorld.WorldToCell(agent.Sprite.GlobalPosition);
        if (!_world.Contains(currentCell) || _world.GetTile(currentCell).IsWater)
        {
            agent.Sprite.GlobalPosition = FindSpawnPosition(tile => !tile.IsWater);
            currentCell = _gameWorld.WorldToCell(agent.Sprite.GlobalPosition);
        }

        switch (agent.Kind)
        {
            case AnimalKind.Herbivore:
                UpdateHerbivore(agent, currentCell, population);
                break;
            case AnimalKind.Predator:
                UpdatePredator(agent, population);
                break;
            case AnimalKind.Bird:
                UpdateBird(agent);
                break;
        }

        agent.Sprite.GlobalPosition += agent.Velocity * delta;

        if (agent.Energy <= 0.0f)
        {
            agent.Sprite.GlobalPosition = FindSpawnPosition(tile => !tile.IsWater);
            agent.Energy = 0.9f;
        }

        if (agent.Energy > 1.7f && agent.ReproductionCooldown <= 0.0f && _agents.Count < Constants.MaxAnimals)
        {
            var child = SpawnAgent(agent.Kind, agent.Sprite.GlobalPosition + new Vector2(_rng.RandfRange(-16.0f, 16.0f), _rng.RandfRange(-16.0f, 16.0f)));
            child.Energy = 0.8f;
            child.ReproductionCooldown = 8.0f;
            agent.Energy *= 0.6f;
            agent.ReproductionCooldown = 12.0f;
        }
    }

    private void UpdateHerbivore(AnimalAgent agent, Vector2I currentCell, IReadOnlyList<AnimalAgent> population)
    {
        var predator = population
            .Where(other => other.Kind == AnimalKind.Predator)
            .OrderBy(other => other.Sprite.GlobalPosition.DistanceSquaredTo(agent.Sprite.GlobalPosition))
            .FirstOrDefault();

        if (predator is not null && predator.Sprite.GlobalPosition.DistanceTo(agent.Sprite.GlobalPosition) < 96.0f)
        {
            var fleeDirection = (agent.Sprite.GlobalPosition - predator.Sprite.GlobalPosition).Normalized();
            agent.Velocity = fleeDirection * 70.0f;
            return;
        }

        if (_world is null)
        {
            return;
        }

        var tile = _world.GetTile(currentCell);
        if (tile.FloraGrowth > 0.45f && tile.Flora != FloraType.None)
        {
            agent.Energy = Mathf.Min(agent.Energy + 0.08f, 2.0f);
            agent.Velocity *= 0.8f;
            return;
        }

        Wander(agent, 44.0f);
    }

    private void UpdatePredator(AnimalAgent agent, IReadOnlyList<AnimalAgent> population)
    {
        var prey = population
            .Where(other => other.Kind == AnimalKind.Herbivore)
            .OrderBy(other => other.Sprite.GlobalPosition.DistanceSquaredTo(agent.Sprite.GlobalPosition))
            .FirstOrDefault();

        if (prey is null)
        {
            Wander(agent, 50.0f);
            return;
        }

        var distance = prey.Sprite.GlobalPosition.DistanceTo(agent.Sprite.GlobalPosition);
        if (distance < 10.0f)
        {
            prey.Energy -= 0.5f;
            agent.Energy = Mathf.Min(agent.Energy + 0.18f, 2.0f);
        }

        var pursuit = (prey.Sprite.GlobalPosition - agent.Sprite.GlobalPosition).Normalized();
        agent.Velocity = pursuit * 60.0f;
    }

    private void UpdateBird(AnimalAgent agent)
    {
        if (agent.WanderTarget == Vector2.Zero || agent.Sprite.GlobalPosition.DistanceTo(agent.WanderTarget) < 12.0f)
        {
            agent.WanderTarget = agent.Sprite.GlobalPosition + new Vector2(_rng.RandfRange(-80.0f, 80.0f), _rng.RandfRange(-50.0f, 30.0f));
        }

        agent.Velocity = (agent.WanderTarget - agent.Sprite.GlobalPosition).Normalized() * 55.0f;
    }

    private void Wander(AnimalAgent agent, float speed)
    {
        if (agent.WanderTarget == Vector2.Zero || agent.Sprite.GlobalPosition.DistanceTo(agent.WanderTarget) < 10.0f)
        {
            var baseFocus = _focusPositions.Count > 0
                ? _focusPositions[_rng.RandiRange(0, _focusPositions.Count - 1)]
                : agent.Sprite.GlobalPosition;
            agent.WanderTarget = baseFocus + new Vector2(_rng.RandfRange(-120.0f, 120.0f), _rng.RandfRange(-120.0f, 120.0f));
        }

        agent.Velocity = (agent.WanderTarget - agent.Sprite.GlobalPosition).Normalized() * speed;
    }

    private AnimalAgent SpawnAgent(AnimalKind kind, Vector2 position, int? forcedId = null)
    {
        if (_gameWorld is null)
        {
            throw new InvalidOperationException("Game world must be initialized before fauna.");
        }

        var id = forcedId ?? _nextId;
        _nextId = Mathf.Max(_nextId, id + 1);

        var sprite = new Sprite2D
        {
            Name = $"{kind}_{id}",
            Texture = CreateAnimalTexture(kind),
            Centered = true,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest
        };
        sprite.GlobalPosition = position;
        _gameWorld.FaunaRoot.AddChild(sprite);

        var agent = new AnimalAgent
        {
            Id = id,
            Kind = kind,
            Sprite = sprite,
            Velocity = Vector2.Right.Rotated(_rng.Randf() * Mathf.Tau) * 18.0f,
            WanderTarget = position,
            Energy = 1.0f,
            ReproductionCooldown = 6.0f
        };
        _agents[agent.Id] = agent;
        return agent;
    }

    private Vector2 FindSpawnPosition(Func<WorldTileData, bool> predicate)
    {
        if (_world is null || _gameWorld is null)
        {
            return Vector2.Zero;
        }

        for (var attempt = 0; attempt < 512; attempt++)
        {
            var x = _rng.RandiRange(0, _world.Width - 1);
            var y = _rng.RandiRange(0, _world.Height - 1);
            var tile = _world.GetTile(x, y);
            if (predicate(tile))
            {
                return _gameWorld.CellToWorld(new Vector2I(x, y));
            }
        }

        return _gameWorld.CellToWorld(new Vector2I(_world.Width / 2, _world.Height / 2));
    }

    private void ClearAgents()
    {
        foreach (var agent in _agents.Values)
        {
            agent.Sprite.QueueFree();
        }

        _agents.Clear();
        _nextId = 1;
    }

    private static Texture2D CreateAnimalTexture(AnimalKind kind)
    {
        var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));
        var color = kind switch
        {
            AnimalKind.Herbivore => new Color("d4b483"),
            AnimalKind.Predator => new Color("b85e4a"),
            AnimalKind.Bird => new Color("d7edf9"),
            _ => Colors.White
        };

        for (var y = 2; y < 6; y++)
        {
            for (var x = 1; x < 7; x++)
            {
                image.SetPixel(x, y, color);
            }
        }

        image.SetPixel(6, 3, Colors.Black);
        return ImageTexture.CreateFromImage(image);
    }
}
