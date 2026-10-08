using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace StarfrontCollab.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class CollabPlugin : PluginBase
{
    private static readonly Guid PluginId = new("6687bd68-7bea-4d5e-b8c4-91f79cad8d37");
    private readonly IProfileService profiles;
    private readonly NinaRig rig;
    private readonly CollabController controller;

    [ImportingConstructor]
    public CollabPlugin(IProfileService profileService, ITelescopeMediator telescope, ICameraMediator camera,
        IRotatorMediator rotator, IImageSaveMediator images)
    {
        profiles = profileService;
        rig = new NinaRig(profileService, telescope, camera, rotator, images);
        controller = new CollabController(rig, new CollabSettings(new PluginOptionsAccessor(profileService, PluginId)));
    }

    /// The options page binds here.
    public object Controller => controller;

    public override async Task Initialize()
    {
        await base.Initialize();
        profiles.ProfileChanged += ProfileChanged;
        controller.Start();
    }

    public override async Task Teardown()
    {
        profiles.ProfileChanged -= ProfileChanged;
        try { await controller.StopAsync(sayGoodbye: true); }
        catch (Exception error) { Logger.Error(error); }
        rig.Dispose();
        await base.Teardown();
    }

    private void ProfileChanged(object? sender, EventArgs args) => controller.ProfileChanged();
}
