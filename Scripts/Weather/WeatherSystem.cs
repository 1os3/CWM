using Godot;
using CWM.Scripts.World;

namespace CWM.Scripts.Weather;

public enum WeatherState
{
    Clear,
    Cloudy,
    Rain,
    Storm,
    Snow
}

public partial class WeatherSystem : Node
{
    [Export(PropertyHint.Range, "1,12,0.5")]
    public float TransitionIntervalHours { get; set; } = 4.0f;

    public bool Authoritative { get; set; } = true;

    public WeatherState CurrentState { get; private set; } = WeatherState.Clear;

    public float CurrentPrecipitationIntensity { get; private set; }

    public float CurrentWind { get; private set; }

    public float TemperatureModifier { get; private set; }

    private DayNightCycle? _dayNightCycle;
    private SeasonManager? _seasonManager;
    private GameWorld? _gameWorld;
    private readonly RandomNumberGenerator _rng = new();
    private float _hoursUntilTransition = 4.0f;

    public event Action? WeatherChanged;

    public override void _Process(double delta)
    {
        if (!Authoritative || _dayNightCycle is null)
        {
            return;
        }

        _hoursUntilTransition -= (float)delta * _dayNightCycle.GameHoursPerRealSecond;
        if (_hoursUntilTransition <= 0.0f)
        {
            AdvanceState();
        }

        ApplyVisualState();
    }

    public void Initialize(GameWorld gameWorld, DayNightCycle dayNightCycle, SeasonManager seasonManager, int seed)
    {
        _gameWorld = gameWorld;
        _dayNightCycle = dayNightCycle;
        _seasonManager = seasonManager;
        _rng.Seed = (ulong)(Mathf.Abs(seed) + 9071);
        _hoursUntilTransition = TransitionIntervalHours;
        CurrentState = WeatherState.Clear;
        CurrentPrecipitationIntensity = 0.0f;
        CurrentWind = 0.12f;
        TemperatureModifier = 0.02f;
        ApplyVisualState();
    }

    public void SetRemoteState(WeatherState state, float intensity, float wind, float temperatureModifier)
    {
        CurrentState = state;
        CurrentPrecipitationIntensity = intensity;
        CurrentWind = wind;
        TemperatureModifier = temperatureModifier;
        ApplyVisualState();
    }

    public float GetEffectiveTemperature(float baseTemperature)
    {
        var seasonBias = _seasonManager?.GetTemperatureOffset() ?? 0.0f;
        var daylightBias = (_dayNightCycle?.GetDaylightFactor() ?? 0.5f) * 0.08f;
        return Mathf.Clamp(baseTemperature + seasonBias + daylightBias + TemperatureModifier, 0.0f, 1.0f);
    }

    public float GetRainContributionPerHour() => CurrentState switch
    {
        WeatherState.Rain => 0.12f,
        WeatherState.Storm => 0.22f,
        WeatherState.Snow => 0.05f,
        _ => 0.0f
    };

    public float GetEvaporationModifier() => CurrentState switch
    {
        WeatherState.Clear => 1.1f,
        WeatherState.Cloudy => 0.95f,
        WeatherState.Rain => 0.65f,
        WeatherState.Storm => 0.6f,
        WeatherState.Snow => 0.5f,
        _ => 1.0f
    };

    public float GetStormDamageChance() => CurrentState == WeatherState.Storm ? 0.08f : 0.0f;

    private void AdvanceState()
    {
        var currentTemperature = GetEffectiveTemperature(0.45f);
        var roll = _rng.Randf();
        var nextState = CurrentState;

        switch (CurrentState)
        {
            case WeatherState.Clear:
                nextState = currentTemperature < 0.22f
                    ? (roll < 0.35f ? WeatherState.Snow : WeatherState.Cloudy)
                    : (roll < 0.55f ? WeatherState.Cloudy : WeatherState.Clear);
                break;
            case WeatherState.Cloudy:
                if (currentTemperature < 0.2f && roll < 0.35f)
                {
                    nextState = WeatherState.Snow;
                }
                else if (roll < 0.33f)
                {
                    nextState = WeatherState.Clear;
                }
                else if (roll < 0.72f)
                {
                    nextState = WeatherState.Rain;
                }
                break;
            case WeatherState.Rain:
                nextState = roll switch
                {
                    < 0.4f => WeatherState.Cloudy,
                    < 0.52f => WeatherState.Storm,
                    _ => WeatherState.Rain
                };
                break;
            case WeatherState.Storm:
                nextState = roll switch
                {
                    < 0.52f => WeatherState.Rain,
                    < 0.74f => WeatherState.Cloudy,
                    _ => WeatherState.Clear
                };
                break;
            case WeatherState.Snow:
                nextState = currentTemperature > 0.26f
                    ? (roll < 0.55f ? WeatherState.Cloudy : WeatherState.Clear)
                    : (roll < 0.4f ? WeatherState.Snow : WeatherState.Cloudy);
                break;
        }

        CurrentState = nextState;
        CurrentWind = nextState switch
        {
            WeatherState.Clear => _rng.RandfRange(0.04f, 0.12f),
            WeatherState.Cloudy => _rng.RandfRange(0.08f, 0.18f),
            WeatherState.Rain => _rng.RandfRange(0.12f, 0.26f),
            WeatherState.Storm => _rng.RandfRange(0.22f, 0.42f),
            WeatherState.Snow => _rng.RandfRange(0.05f, 0.16f),
            _ => 0.1f
        };
        CurrentPrecipitationIntensity = nextState switch
        {
            WeatherState.Clear => 0.0f,
            WeatherState.Cloudy => 0.0f,
            WeatherState.Rain => _rng.RandfRange(0.55f, 0.85f),
            WeatherState.Storm => _rng.RandfRange(0.9f, 1.0f),
            WeatherState.Snow => _rng.RandfRange(0.45f, 0.75f),
            _ => 0.0f
        };
        TemperatureModifier = nextState switch
        {
            WeatherState.Clear => 0.04f,
            WeatherState.Cloudy => 0.01f,
            WeatherState.Rain => -0.02f,
            WeatherState.Storm => -0.05f,
            WeatherState.Snow => -0.12f,
            _ => 0.0f
        };
        _hoursUntilTransition = TransitionIntervalHours * _rng.RandfRange(0.8f, 1.3f);
        ApplyVisualState();
        WeatherChanged?.Invoke();
    }

    private void ApplyVisualState()
    {
        if (_gameWorld is null)
        {
            return;
        }

        var rain = _gameWorld.RainParticles;
        var snow = _gameWorld.SnowParticles;

        rain.Emitting = CurrentState is WeatherState.Rain or WeatherState.Storm;
        snow.Emitting = CurrentState == WeatherState.Snow;
        rain.AmountRatio = CurrentState is WeatherState.Rain or WeatherState.Storm ? CurrentPrecipitationIntensity : 0.0f;
        snow.AmountRatio = CurrentState == WeatherState.Snow ? CurrentPrecipitationIntensity : 0.0f;

        if (rain.ProcessMaterial is ParticleProcessMaterial rainMaterial)
        {
            rainMaterial.Direction = new Vector3(CurrentWind, 1.0f, 0.0f).Normalized();
            rainMaterial.InitialVelocityMin = Mathf.Lerp(500.0f, 920.0f, CurrentPrecipitationIntensity);
            rainMaterial.InitialVelocityMax = Mathf.Lerp(640.0f, 1020.0f, CurrentPrecipitationIntensity);
        }

        if (snow.ProcessMaterial is ParticleProcessMaterial snowMaterial)
        {
            snowMaterial.Direction = new Vector3(CurrentWind * 0.6f, 1.0f, 0.0f).Normalized();
            snowMaterial.InitialVelocityMin = Mathf.Lerp(50.0f, 110.0f, CurrentPrecipitationIntensity);
            snowMaterial.InitialVelocityMax = Mathf.Lerp(80.0f, 150.0f, CurrentPrecipitationIntensity);
        }
    }
}
