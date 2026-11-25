namespace EchoDiar.App;

/// <summary>
/// Build-time feature flags, set by MSBuild properties in EchoDiar.App.csproj.
/// <para>
/// Each flag is <c>static readonly</c>, not <c>const</c>: a <c>const</c> false would make the code it guards
/// unreachable, and CS0162 fails the build under TreatWarningsAsErrors.
/// </para>
/// </summary>
internal static class Features
{
    /// <summary>
    /// Windows screen recording (<c>-p:EchoDiarScreenRecording</c>, default true, which defines
    /// <c>SCREEN_RECORDING</c>). False hides only the capture entry points; playback, export, delete, the badge,
    /// the size tint, the live size and the tray info stay, so recorded videos keep working.
    /// </summary>
#if SCREEN_RECORDING
    public static readonly bool ScreenRecording = true;
#else
    public static readonly bool ScreenRecording = false;
#endif
}
