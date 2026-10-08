using System.IO;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using StarfrontCollab.Wire;

namespace StarfrontCollab.Plugin;

/// Plugin settings, stored per N.I.N.A. profile. Never holds a token.
internal sealed class CollabSettings(IPluginOptionsAccessor options)
{

    /// Where Target Scheduler keeps its database: under N.I.N.A.'s own data
    /// folder, which is %LOCALAPPDATA%\NINA unless N.I.N.A. was pointed elsewhere.
    internal static string DefaultDatabasePath => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "SchedulerPlugin", "schedulerdb.sqlite");

    internal const string Off = "Off";
    internal const string Hello = "Hello";
    internal const string Collab = "Collab";

    public string Mode
    {
        get => options.GetValueString(nameof(Mode), Off) is var mode && mode is Hello or Collab ? mode : Off;
        set => options.SetValueString(nameof(Mode), value is Hello or Collab ? value : Off);
    }

    /// Starfront's server unless set otherwise. A former Starfront address
    /// reads as the current one.
    public string ServerUrl
    {
        get => KnownServers.Canonical(options.GetValueString(nameof(ServerUrl), KnownServers.Starfront));
        set => options.SetValueString(nameof(ServerUrl), KnownServers.Canonical(value));
    }

    public bool AllowLoopbackHttp
    {
        get => options.GetValueBoolean(nameof(AllowLoopbackHttp), false);
        set => options.SetValueBoolean(nameof(AllowLoopbackHttp), value);
    }

    public string TelescopeName
    {
        get => options.GetValueString(nameof(TelescopeName), "");
        set => options.SetValueString(nameof(TelescopeName), value.Trim());
    }

    public bool SharePosition
    {
        get => options.GetValueBoolean(nameof(SharePosition), true);
        set => options.SetValueBoolean(nameof(SharePosition), value);
    }

    public bool ShareTarget
    {
        get => options.GetValueBoolean(nameof(ShareTarget), true);
        set => options.SetValueBoolean(nameof(ShareTarget), value);
    }

    /// How often the telescope checks in. In Collab mode each check-in also
    /// fetches tonight's work and reports new frames.
    public int CheckInMinutes
    {
        get => Math.Clamp(options.GetValueInt32(nameof(CheckInMinutes), 5), 1, 60);
        set => options.SetValueInt32(nameof(CheckInMinutes), Math.Clamp(value, 1, 60));
    }

    public bool AutoApply
    {
        get => options.GetValueBoolean(nameof(AutoApply), false);
        set => options.SetValueBoolean(nameof(AutoApply), value);
    }

    public string DatabasePath
    {
        get => options.GetValueString(nameof(DatabasePath), "") is { Length: > 0 } path ? path : DefaultDatabasePath;
        set => options.SetValueString(nameof(DatabasePath), string.Equals(value.Trim(), DefaultDatabasePath, StringComparison.OrdinalIgnoreCase) ? "" : value.Trim());
    }

    public int Binning
    {
        get => Math.Clamp(options.GetValueInt32(nameof(Binning), 1), 1, 4);
        set => options.SetValueInt32(nameof(Binning), Math.Clamp(value, 1, 4));
    }

    /// Zero means "as long as the target is up".
    public double HoursPerNight
    {
        get => Math.Clamp(options.GetValueDouble(nameof(HoursPerNight), 0), 0, 24);
        set => options.SetValueDouble(nameof(HoursPerNight), double.IsFinite(value) ? Math.Clamp(value, 0, 24) : 0);
    }

    /// The camera's position angle when no rotator is connected.
    public double CameraAngle
    {
        get => Astro.Normalize(options.GetValueDouble(nameof(CameraAngle), 0));
        set => options.SetValueDouble(nameof(CameraAngle), double.IsFinite(value) ? Astro.Normalize(value) : 0);
    }

    // The sensor as last seen connected, so the profile stays whole in the
    // afternoon with the camera off. The server tiles cells from it.
    public int SensorWidth
    {
        get => options.GetValueInt32(nameof(SensorWidth), 0);
        set => options.SetValueInt32(nameof(SensorWidth), value);
    }

    public int SensorHeight
    {
        get => options.GetValueInt32(nameof(SensorHeight), 0);
        set => options.SetValueInt32(nameof(SensorHeight), value);
    }

    public bool Colour
    {
        get => options.GetValueBoolean(nameof(Colour), false);
        set => options.SetValueBoolean(nameof(Colour), value);
    }
}
