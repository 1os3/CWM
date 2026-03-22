using Godot;
using CWM.Scripts.Core;

namespace CWM.Scripts.Weather;

public partial class DayNightCycle : CanvasModulate
{
	[Export(PropertyHint.Range, "60,1800,10")]
	public float DayLengthSeconds { get; set; } = Constants.DefaultDayLengthSeconds;

	public bool Authoritative { get; set; } = true;

	public bool Running { get; set; }

	public float TotalGameHours { get; private set; } = 8.0f;

	public float CurrentHour => Mathf.PosMod(TotalGameHours, 24.0f);

	public float ElapsedDays => TotalGameHours / 24.0f;

	public float GameHoursPerRealSecond => 24.0f / Mathf.Max(DayLengthSeconds, 1.0f);

	public event Action<float, float>? TimeAdvanced;

	public override void _Ready()
	{
		UpdateTint();
	}

	public override void _Process(double delta)
	{
		if (!Running || !Authoritative)
		{
			return;
		}

		AdvanceHours((float)delta * GameHoursPerRealSecond);
	}

	public void ResetToHour(float hour)
	{
		TotalGameHours = hour;
		UpdateTint();
	}

	public void AdvanceHours(float deltaHours)
	{
		TotalGameHours += deltaHours;
		UpdateTint();
		TimeAdvanced?.Invoke(deltaHours, TotalGameHours);
	}

	public void SetRemoteTime(float totalGameHours)
	{
		TotalGameHours = totalGameHours;
		UpdateTint();
	}

	public float GetDaylightFactor()
	{
		var normalized = Mathf.Clamp(Mathf.Cos(((CurrentHour - 12.0f) / 24.0f) * Mathf.Tau) * -0.5f + 0.5f, 0.0f, 1.0f);
		return normalized;
	}

	private void UpdateTint()
	{
		var hour = CurrentHour;
		Color = hour switch
		{
			>= 6.0f and < 8.0f => new Color("f1d7a6").Lerp(Colors.White, (hour - 6.0f) / 2.0f),
			>= 8.0f and < 17.0f => Colors.White,
			>= 17.0f and < 19.0f => Colors.White.Lerp(new Color("e6b17a"), (hour - 17.0f) / 2.0f),
			>= 19.0f and < 21.0f => new Color("e6b17a").Lerp(new Color("506fa4"), (hour - 19.0f) / 2.0f),
			_ => new Color("32456f")
		};
	}
}
