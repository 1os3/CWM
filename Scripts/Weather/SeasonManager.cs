using Godot;

namespace CWM.Scripts.Weather;

public enum Season
{
    Spring,
    Summer,
    Autumn,
    Winter
}

public partial class SeasonManager : Node
{
    [Export(PropertyHint.Range, "0.5,20,0.5")]
    public float DaysPerSeason { get; set; } = 2.5f;

    public Season CurrentSeason { get; private set; } = Season.Spring;

    public float CurrentDayOfYear { get; private set; }

    public event Action<Season>? SeasonChanged;

    public void UpdateFromDays(float elapsedDays)
    {
        CurrentDayOfYear = elapsedDays;
        var cycleLength = Mathf.Max(DaysPerSeason * 4.0f, 0.001f);
        var dayInYear = Mathf.PosMod(elapsedDays, cycleLength);
        var seasonIndex = Mathf.Clamp(Mathf.FloorToInt(dayInYear / DaysPerSeason), 0, 3);
        var nextSeason = (Season)seasonIndex;

        if (nextSeason == CurrentSeason)
        {
            return;
        }

        CurrentSeason = nextSeason;
        SeasonChanged?.Invoke(CurrentSeason);
    }

    public float GetTemperatureOffset() => CurrentSeason switch
    {
        Season.Spring => 0.05f,
        Season.Summer => 0.18f,
        Season.Autumn => -0.02f,
        Season.Winter => -0.22f,
        _ => 0.0f
    };

    public float GetGrowthModifier() => CurrentSeason switch
    {
        Season.Spring => 1.18f,
        Season.Summer => 0.92f,
        Season.Autumn => 0.74f,
        Season.Winter => 0.45f,
        _ => 1.0f
    };

    public string GetDisplayName() => CurrentSeason switch
    {
        Season.Spring => "Spring",
        Season.Summer => "Summer",
        Season.Autumn => "Autumn",
        Season.Winter => "Winter",
        _ => "Unknown"
    };
}
