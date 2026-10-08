using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using StarfrontCollab.Wire;
using SensorType = NINA.Core.Enum.SensorType;

namespace StarfrontCollab.Plugin;

/// What N.I.N.A. knows about this telescope right now: its equipment for the
/// hello profile, and where it points for live presence.
internal sealed class NinaRig : IDisposable
{
    private sealed record SavedFrame(string Target, double? RaHours, double? Dec, DateTimeOffset At);

    private static readonly TimeSpan FrameMemory = TimeSpan.FromHours(3);

    private readonly IProfileService profiles;
    private readonly ITelescopeMediator telescope;
    private readonly ICameraMediator camera;
    private readonly IRotatorMediator rotator;
    private readonly IImageSaveMediator images;
    private SavedFrame? lastFrame;

    /// Raised when the mount connects, disconnects, parks, unparks or slews,
    /// so the new state and position reach the server promptly.
    internal event EventHandler? MountChanged;

    internal NinaRig(IProfileService profiles, ITelescopeMediator telescope, ICameraMediator camera, IRotatorMediator rotator, IImageSaveMediator images)
    {
        this.profiles = profiles;
        this.telescope = telescope;
        this.camera = camera;
        this.rotator = rotator;
        this.images = images;
        images.ImageSaved += OnImageSaved;
        telescope.Connected += OnMount;
        telescope.Disconnected += OnMount;
        telescope.Parked += OnMount;
        telescope.Unparked += OnMount;
        telescope.Slewed += OnSlewed;
    }

    internal IProfile Profile => profiles.ActiveProfile;

    internal string TelescopeName(CollabSettings settings) =>
        settings.TelescopeName is { Length: > 0 } name ? name : Profile.Name is { Length: > 0 } profile ? profile : "N.I.N.A. telescope";

    /// The wheel's filter names in slot order: what Target Scheduler templates
    /// must name for N.I.N.A. to find the filter.
    internal IReadOnlyList<string> WheelFilters() =>
        Profile.FilterWheelSettings.FilterWheelFilters?.OrderBy(f => f.Position).Select(f => f.Name?.Trim() ?? "")
            .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    internal RigProfile ReadProfile(CollabSettings settings, IReadOnlyDictionary<string, double> exposures)
    {
        var profile = Profile;
        var info = camera.GetInfo();
        var connected = info.Connected && info.XSize > 0 && info.YSize > 0;
        if (connected)
        {
            var colour = info.SensorType != SensorType.Monochrome;
            if (settings.SensorWidth != info.XSize || settings.SensorHeight != info.YSize || settings.Colour != colour)
            {
                settings.SensorWidth = info.XSize;
                settings.SensorHeight = info.YSize;
                settings.Colour = colour;
            }
        }
        var pixel = connected && info.PixelSize > 0 ? info.PixelSize : profile.CameraSettings.PixelSize;
        var focal = profile.TelescopeSettings.FocalLength;
        return new RigProfile(
            TelescopeName(settings),
            double.IsFinite(focal) && focal > 0 ? focal : null,
            double.IsFinite(pixel) && pixel > 0 ? pixel : null,
            settings.SensorWidth > 0 ? settings.SensorWidth : null,
            settings.SensorHeight > 0 ? settings.SensorHeight : null,
            settings.Binning,
            settings.Colour,
            // A rotator can turn to whatever angle a project asks for.
            rotator.GetInfo().Connected ? null : settings.CameraAngle,
            settings.HoursPerNight > 0 ? settings.HoursPerNight : null,
            [.. WheelFilters().Select(name => (name, Filters.Bandpass(name)))],
            exposures);
    }

    internal Presence ReadPresence(CollabSettings settings, IReadOnlyDictionary<string, string> projects)
    {
        var mount = telescope.GetInfo();
        var exposing = camera.GetInfo() is { Connected: true, IsExposing: true };
        var (ra, dec) = mount.Connected ? J2000(mount) : (null, null);
        var state = CheckIn.State(mount.Connected, mount.Connected && mount.Slewing, mount.Connected && mount.AtPark,
            mount.Connected && mount.TrackingEnabled, exposing);

        // Name what is being shot only while the mount is still on it: the
        // last saved frame's target, if recent and within a degree of here.
        string? target = null;
        if (lastFrame is { } frame && DateTimeOffset.UtcNow - frame.At < FrameMemory && !mount.AtPark
            && (frame.RaHours is null || ra is null || Astro.Separation(frame.RaHours.Value * 15, frame.Dec!.Value, ra.Value * 15, dec!.Value) < 1.0))
            target = frame.Target;
        var project = target is not null && projects.TryGetValue(target, out var id) ? id : null;

        return new Presence(
            settings.SharePosition ? ra : null,
            settings.SharePosition ? dec : null,
            state,
            mount.Connected && mount.Slewing,
            settings.ShareTarget ? target : null,
            project,
            TelescopeName(settings));
    }

    private static (double? Ra, double? Dec) J2000(TelescopeInfo mount)
    {
        try
        {
            if (mount.Coordinates is { } position)
            {
                var j2000 = position.Epoch == Epoch.J2000 ? position : position.Transform(Epoch.J2000);
                if (double.IsFinite(j2000.RA) && double.IsFinite(j2000.Dec)) return (j2000.RA, j2000.Dec);
            }
        }
        catch (Exception error) { Logger.Warning("Starfront TargetScheduler Collab could not convert the mount position to J2000: " + error.Message); }
        return double.IsFinite(mount.RightAscension) && double.IsFinite(mount.Declination) ? (mount.RightAscension, mount.Declination) : (null, null);
    }

    private void OnImageSaved(object? sender, ImageSavedEventArgs args)
    {
        try
        {
            var target = args.MetaData?.Target;
            var name = target?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            double? ra = null, dec = null;
            if (target!.Coordinates is { } position)
            {
                var j2000 = position.Epoch == Epoch.J2000 ? position : position.Transform(Epoch.J2000);
                if (double.IsFinite(j2000.RA) && double.IsFinite(j2000.Dec) && (j2000.RA != 0 || j2000.Dec != 0)) (ra, dec) = (j2000.RA, j2000.Dec);
            }
            lastFrame = new SavedFrame(name, ra, dec, DateTimeOffset.UtcNow);
        }
        catch (Exception error) { Logger.Warning("Starfront TargetScheduler Collab could not read a saved frame's target: " + error.Message); }
    }

    private Task OnMount(object sender, EventArgs args)
    {
        MountChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private Task OnSlewed(object sender, MountSlewedEventArgs args) => OnMount(sender, args);

    public void Dispose()
    {
        images.ImageSaved -= OnImageSaved;
        telescope.Connected -= OnMount;
        telescope.Disconnected -= OnMount;
        telescope.Parked -= OnMount;
        telescope.Unparked -= OnMount;
        telescope.Slewed -= OnSlewed;
    }
}
