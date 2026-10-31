namespace WSGM.Core;

internal sealed record EmulatorBiosSystem(
    string Id,
    string Name,
    string Status,
    string[] Emulators,
    EmulatorBiosFile[] Files,
    string[] Links);
