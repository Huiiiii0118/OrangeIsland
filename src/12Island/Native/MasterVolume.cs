using NAudio.CoreAudioApi;

namespace TwelveIsland.Native;

// Reads and sets the system's master volume, the one the keyboard's volume keys and the panel wheel step. The music
// page's volume bar shows this level and drags it; a slider cannot press keys, so it writes the endpoint directly,
// which sets the level silently — without Windows' volume flyout the keys would show.
internal static class MasterVolume
{
    // The default output device's level (0..1) and its mute state; null when audio services answer nothing.
    public static (float Level, bool Mute)? Read()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var volume = device.AudioEndpointVolume;
            return (volume.MasterVolumeLevelScalar, volume.Mute);
        }
        catch { return null; }
    }

    public static bool Set(float level)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            return true;
        }
        catch { return false; }
    }

    public static bool SetMute(bool mute)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            device.AudioEndpointVolume.Mute = mute;
            return true;
        }
        catch { return false; }
    }
}
