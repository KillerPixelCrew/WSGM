namespace WSGM.AllyXLab;

internal static class WizardPlan
{
    internal static bool IncludesMotion(string name) => new[] { "Gyro", "Gravity:", "Yaw:", "Pitch:", "Roll:" }
        .Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    internal static IReadOnlyList<(string Name, string Instruction)> Captures()
    {
        List<(string Name, string Instruction)> steps = [];
        steps.Add(("Neutral", "Leave every button released and both sticks centered. Keep the device still for the whole capture."));
        foreach (string name in new[] { "A", "B", "X", "Y", "D-pad up", "D-pad down", "D-pad left", "D-pad right", "Left bumper", "Right bumper", "View", "Menu", "Left stick click", "Right stick click", "Command Center", "Armoury Crate", "Rear M1", "Rear M2", "Volume up", "Volume down" })
        {
            steps.Add((name, $"Press {name} three times separately, then hold it for two seconds and release. Leave a second between presses. Do not touch other controls."));
        }

        foreach (string name in new[] { "Left stick", "Right stick", "Left trigger", "Right trigger" })
        {
            steps.Add((name + " travel", $"Move {name} slowly through full travel and back to neutral. For a stick include all four edges and a full circle. For a trigger include half travel."));
        }

        steps.Add(("D-pad diagonals", "Press each diagonal separately, releasing between directions."));
        steps.Add(("Rear M1 + A", "Hold M1, press/release A three times, then release M1. Repeat once in reverse press order."));
        steps.Add(("Rear M2 + A", "Hold M2, press/release A three times, then release M2. Repeat once in reverse press order."));
        steps.Add(("Rollover A + B + LB + RB", "Hold A, add B, LB and RB one at a time, then release in reverse order. Repeat."));
        steps.Add(("Power button / resume (optional)", "Press power once to sleep, then wake the device promptly. A timeout is a valid incomplete result. This observes Windows power events, not raw physical attribution. Do not hold the power button or force shutdown."));
        steps.Add(("Gyro stationary bias", "Put the device flat on a stable surface. Do not touch it. Capture lasts 15 seconds. Repeat after the device warms up; means/stddev are candidates, not firmware calibration."));
        foreach (string pose in new[] { "Screen up", "Screen down", "Left edge down", "Right edge down", "Top edge down", "Bottom edge down" })
        {
            steps.Add(("Gravity: " + pose, $"Hold the device still with {pose.ToLowerInvariant()}. Support it safely without blocking vents. This captures accelerometer offset, scale and axis evidence."));
        }

        foreach (string rotation in new[] { "Yaw: screen up, rotate clockwise on the table", "Pitch: tilt top edge away from you", "Roll: lower right edge" })
        {
            steps.Add((rotation, "Start still. Rotate approximately 90 degrees in the stated direction over two seconds, hold, then return slowly. This establishes gyro sign and axis; do not shake."));
        }

        return steps;
    }
}
