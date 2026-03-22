using Godot;

namespace CWM.Scripts.World;

public static class IslandMask
{
    public static float Sample(
        Vector2 centeredUv,
        float baseRadius,
        float falloffPower,
        float coastNoise,
        float coastStrength)
    {
        var distance = centeredUv.Length();
        var radial = Mathf.Clamp(1.0f - Mathf.Pow(distance / Mathf.Max(baseRadius, 0.001f), falloffPower), 0.0f, 1.0f);
        var warped = radial + (coastNoise * coastStrength);
        return Mathf.Clamp(warped, 0.0f, 1.0f);
    }
}
