using OpenLogi.Camera;
using OpenLogi.Core.Config;
using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.Tests.Camera;

/// <summary>
/// The camera backend's platform-independent decisions, ported with the Rust
/// <c>openlogi-camera</c> tests: device-path parsing, the USB-link decoding, and
/// the persistence key.
/// </summary>
public class StreamModeTests
{
    private static StreamMode M(uint w, uint h, uint fps) => new(w, h, fps);

    private static StreamModes StreamCam() => new(
    [
        M(640, 360, 30), M(1280, 720, 30), M(1920, 1080, 30),
        M(1920, 1080, 60), M(1280, 720, 60), M(1280, 720, 24),
    ]);

    [Fact]
    public void ModesListTheLargestFrameFirstAndTheFastestRateFirst() =>
        Assert.Equal(
            [M(1920, 1080, 60), M(1920, 1080, 30), M(1280, 720, 60), M(1280, 720, 30), M(1280, 720, 24), M(640, 360, 30)],
            StreamCam().All);

    [Fact]
    public void DuplicatesAndEmptyModesAreDropped()
    {
        var modes = new StreamModes([M(1280, 720, 30), M(1280, 720, 30), M(0, 720, 30), M(1280, 0, 30), M(1280, 720, 0)]);
        Assert.Equal([M(1280, 720, 30)], modes.All);
    }

    [Fact]
    public void FramesOfEqualAreaStayDistinctAndOrdered() =>
        Assert.Equal([(1280u, 720u), (960u, 960u)], new StreamModes([M(960, 960, 30), M(1280, 720, 30)]).Resolutions());

    [Fact]
    public void ResolutionsAreDistinctAndRatesFollowTheirFrame()
    {
        var modes = StreamCam();
        Assert.Equal([(1920u, 1080u), (1280u, 720u), (640u, 360u)], modes.Resolutions());
        Assert.Equal([60u, 30u, 24u], modes.RatesAt((1280, 720)));
        Assert.Equal([30u], modes.RatesAt((640, 360)));
        Assert.Empty(modes.RatesAt((800, 600)));
    }

    [Fact]
    public void TheDefaultIs720pAt30WhenOffered() => Assert.Equal(M(1280, 720, 30), StreamCam().DefaultMode());

    [Fact]
    public void TheDefaultRateIsTheNearestOfferedAndTheSlowerOnATie()
    {
        Assert.Equal(M(1280, 720, 24), new StreamModes([M(1280, 720, 60), M(1280, 720, 24)]).DefaultMode());
        Assert.Equal(M(1280, 720, 20), new StreamModes([M(1280, 720, 40), M(1280, 720, 20)]).DefaultMode());
    }

    [Fact]
    public void TheDefaultFrameIsTheNearestTo720pWhenItIsNotOffered()
    {
        Assert.Equal(M(1024, 768, 30), new StreamModes([M(640, 480, 30), M(1024, 768, 30), M(1920, 1080, 30)]).DefaultMode());
        Assert.Equal(M(3840, 2160, 30), new StreamModes([M(3840, 2160, 30)]).DefaultMode());
    }

    [Fact]
    public void AnEmptyListHasNoDefaultAndResolvesNothing()
    {
        Assert.True(StreamModes.Empty.IsEmpty);
        Assert.Null(StreamModes.Empty.DefaultMode());
        Assert.Null(StreamModes.Empty.Resolve(M(1280, 720, 30)));
        Assert.Null(StreamModes.Empty.Resolve(null));
    }

    [Fact]
    public void AnOfferedRequestIsHonouredAndAnUnofferedOneFallsBackToTheDefault()
    {
        var modes = StreamCam();
        Assert.Equal(M(1920, 1080, 60), modes.Resolve(M(1920, 1080, 60)));
        Assert.Equal(modes.DefaultMode(), modes.Resolve(M(1920, 1080, 24)));
        Assert.Equal(modes.DefaultMode(), modes.Resolve(M(4096, 2160, 30)));
        Assert.Equal(modes.DefaultMode(), modes.Resolve(null));
    }

    [Fact]
    public void ANewResolutionKeepsTheRateOrTakesTheNearest()
    {
        var modes = StreamCam();
        Assert.Equal(M(1920, 1080, 60), modes.WithResolution(M(1280, 720, 60), (1920, 1080)));
        Assert.Equal(M(640, 360, 30), modes.WithResolution(M(1920, 1080, 60), (640, 360)));
        Assert.Equal(M(1920, 1080, 30), modes.WithResolution(M(1280, 720, 24), (1920, 1080)));
        Assert.Null(modes.WithResolution(M(1280, 720, 30), (800, 600)));
    }

    [Fact]
    public void ARateIsTheNearestWholeFps()
    {
        Assert.Equal(30u, StreamModes.WholeFps(30, 1));
        Assert.Equal(30u, StreamModes.WholeFps(10_000_000, 333_333));
        Assert.Equal(30u, StreamModes.WholeFps(30_000, 1001));
        Assert.Equal(8u, StreamModes.WholeFps(15, 2));
        Assert.Null(StreamModes.WholeFps(30, 0));
        Assert.Null(StreamModes.WholeFps(1, 10));
        Assert.Equal(uint.MaxValue, StreamModes.WholeFps(uint.MaxValue, 1));
    }

    [Fact]
    public void NativeTypesCollapseToModesAndTheFirstListedTypeIsChosen()
    {
        NativeFormat[] formats =
        [
            new(0, 1280, 720, 30), // NV12
            new(1, 1280, 720, 30), // YUY2: same mode
            new(2, 1920, 1080, 30),
            new(3, 640, 480, 0),   // names no rate: no mode
        ];
        Assert.Equal([M(1920, 1080, 30), M(1280, 720, 30)], NativeFormat.Offered(formats).All);
        Assert.Equal(0u, NativeFormat.Choose(formats, null)!.Value.Index);
        Assert.Equal(2u, NativeFormat.Choose(formats, M(1920, 1080, 30))!.Value.Index);
        Assert.Equal(0u, NativeFormat.Choose(formats, M(4096, 2160, 30))!.Value.Index);
        Assert.Null(NativeFormat.Choose([], null));
    }
}

public class CameraIdentityTests
{
    private const string WithSerial = @"\\?\usb#vid_046d&pid_0893&mi_00#6B123456#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
    private const string NoSerial = @"\\?\usb#vid_046d&pid_0893&mi_00#9&56d9c30&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";

    [Fact]
    public void DevicePathYieldsTheUsbIds()
    {
        Assert.Equal(((ushort)0x046d, (ushort)0x0893), DevicePath.ParseIds(WithSerial));
        Assert.Null(DevicePath.ParseIds(@"\\?\root#camera#0000#{guid}"));
    }

    [Fact]
    public void ARealUsbSerialIsExtractedAndAParentGeneratedInstanceIsRejected()
    {
        Assert.Equal("6B123456", DevicePath.UsbSerial(WithSerial));
        Assert.Null(DevicePath.UsbSerial(NoSerial));
    }

    [Fact]
    public void DirectShowAndMediaFoundationPathsOfOneCameraMatch()
    {
        const string directShow = @"\\?\usb#vid_046d&pid_0893&mi_00#6B123456#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global";
        Assert.True(DevicePath.SameDevice(WithSerial, directShow));
        Assert.False(DevicePath.SameDevice(WithSerial, NoSerial));
    }

    [Fact]
    public void ConfigKeyPrefersTheSerialAndFallsBackToTheModel()
    {
        Assert.Equal("camera:046d:0893:serial:6b123456",
            new CameraInfo("StreamCam", WithSerial, "6B123456", 0x046d, 0x0893).ConfigKey);
        Assert.Equal("camera:046d:0893", new CameraInfo("StreamCam", NoSerial, null, 0x046d, 0x0893).ConfigKey);
        Assert.Equal("camera:046d:0893", new CameraInfo("StreamCam", NoSerial, "  ", 0x046d, 0x0893).ConfigKey);
    }

    [Fact]
    public void AnUnnamedCameraTakesItsMarketedName()
    {
        var c510 = new CameraInfo(@"USB Camera VID:1133 PID:2077", "x", null, 0x046d, 0x081d).WithRegistryName();
        Assert.Equal("Logitech HD Webcam C510", c510.Name);
        var named = new CameraInfo("Brio 101", "x", null, 0x046d, 0x094d).WithRegistryName();
        Assert.Equal("Brio 101", named.Name);
    }

    [Fact]
    public void UnnamedCameraTableHasNoDuplicatesOrBlanks()
    {
        var all = UnnamedCameras.All;
        Assert.Equal(all.Count, all.Select(c => (c.Vid, c.Pid)).Distinct().Count());
        Assert.All(all, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public void DiscreteRangeRejectsMissingValues()
    {
        var range = new ControlRange(0, 3, 1, 1, (1u << 1) | (1u << 3));
        Assert.True(range.Supports(1));
        Assert.False(range.Supports(2));
        Assert.True(range.Supports(3));
        Assert.False(range.Supports(4));
        Assert.True(new ControlRange(-11, -2, -5, -5).Supports(-11));
    }

    [Fact]
    public void ControlNamesRoundTrip()
    {
        foreach (var c in CameraControlNames.AllControls)
            Assert.Equal(c, CameraControlNames.ParseControl(c.Name()));
        foreach (var t in CameraControlNames.AllToggles)
            Assert.Equal(t, CameraControlNames.ParseToggle(t.Name()));
        Assert.Equal(AutoToggle.WhiteBalance, CameraControl.WhiteBalance.AutoToggleFor());
        Assert.Null(CameraControl.Brightness.AutoToggleFor());
    }

    [Fact]
    public void RegistryTypeCameraMapsToTheCameraKind() =>
        Assert.Equal(DeviceKind.Camera, DeviceKindExtensions.FromRegistryType("CAMERA"));
}

public class UsbLinkTests
{
    [Fact]
    public void AHighSpeedLinkOfASuperSpeedCameraOffersMore()
    {
        // Running at high speed, capable of SuperSpeed (V2 flag bit 1).
        var link = UsbLinks.LinkFrom(2, 1u << 1)!.Value;
        Assert.Equal(UsbSpeed.High, link.Speed);
        Assert.Equal(UsbSpeed.Super, link.FasterAvailable);
    }

    [Fact]
    public void TheV2FlagsWinOverTheFirstAnswersSpeed()
    {
        // A SuperSpeed device reports SPEED_HIGH in the first answer.
        var link = UsbLinks.LinkFrom(2, (1u << 0) | (1u << 1))!.Value;
        Assert.Equal(UsbSpeed.Super, link.Speed);
        Assert.Null(link.FasterAvailable);
        Assert.Equal(UsbSpeed.SuperPlus, UsbLinks.LinkFrom(2, (1u << 2) | (1u << 3))!.Value.Speed);
    }

    [Fact]
    public void WithoutTheV2AnswerTheCameraDeclaresNothing()
    {
        var link = UsbLinks.LinkFrom(2, null)!.Value;
        Assert.Equal(UsbSpeed.High, link.Speed);
        Assert.Null(link.Capable);
        Assert.Null(UsbLinks.LinkFrom(9, null));
    }

    [Fact]
    public void AnInterfacePathMapsToItsInstanceId()
    {
        Assert.Equal(@"usb\vid_046d&pid_0893&mi_00\9&56d9c30&0&0000",
            UsbLinks.InstanceId(@"\\?\usb#vid_046d&pid_0893&mi_00#9&56d9c30&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global"));
        Assert.Null(UsbLinks.InstanceId(@"\\?\root#camera#0000#{guid}\global"));
        Assert.Null(UsbLinks.InstanceId("usb#vid_046d"));
        Assert.True(UsbLinks.IsFunction(@"USB\VID_046D&PID_094D&MI_00\B&1E0BAABA&0&0000"));
        Assert.False(UsbLinks.IsFunction(@"USB\VID_046D&PID_094D\2620AP707LT8"));
    }
}

public class CameraConfigTests
{
    [Fact]
    public void CameraSettingsRoundTripThroughToml()
    {
        var config = new Config();
        const string key = "camera:046d:0893:serial:6b123456";
        config.SetCameraControl(key, "brightness", 128);
        config.SetCameraControl(key, "exposure", -6);
        config.SetCameraControl(key, "focus_auto", 1);
        config.SaveCameraProfile(key, "Custom 1", new SortedDictionary<string, int> { ["contrast"] = 5 });
        config.SetCameraActiveProfile(key, "Custom 1");
        config.SetCameraStreamMode(key, new CameraStreamMode(1920, 1080, 30));

        var text = ConfigCodec.Serialize(config);
        var back = ConfigCodec.Parse(text, "test");

        Assert.Equal(128, back.CameraControls(key)!["brightness"]);
        Assert.Equal(-6, back.CameraControls(key)!["exposure"]);
        Assert.Equal(1, back.CameraControls(key)!["focus_auto"]);
        Assert.Equal(5, back.CameraProfiles(key)["Custom 1"]["contrast"]);
        Assert.Equal("Custom 1", back.CameraActiveProfile(key));
        Assert.Equal(new CameraStreamMode(1920, 1080, 30), back.CameraStreamMode(key));
    }

    [Fact]
    public void UntouchedCamerasStayOutOfTheFile() =>
        Assert.DoesNotContain("camera_", ConfigCodec.Serialize(new Config()));

    [Fact]
    public void DeletingTheActiveProfileClearsTheSelection()
    {
        var config = new Config();
        config.SaveCameraProfile("k", "Custom 1", new SortedDictionary<string, int>());
        config.SetCameraActiveProfile("k", "Custom 1");
        config.DeleteCameraProfile("k", "Custom 1");
        Assert.Empty(config.CameraProfiles("k"));
        Assert.Null(config.CameraActiveProfile("k"));
    }

    [Fact]
    public void AZeroStreamModeRefusesToLoad()
    {
        const string text = "schema_version = 3\n[devices.\"camera:046d:0893\".camera_stream_mode]\nwidth = 0\nheight = 720\nfps = 30\n";
        Assert.Throws<ConfigException>(() => ConfigCodec.Parse(text, "test"));
    }
}

public class CameraAssetTests
{
    [Fact]
    public void AVariantOnlyCameraDepotStillYieldsItsHeroRender()
    {
        // The Brio 101 depot lists only its _ext7 variant, keyed device_camera_image.
        const string json = """
            {"devices":[{"modelId":"094d_ext7","resources":[
              {"key":"device_camera_image","src":"front_ext_7.png"},
              {"key":"image_metadata","src":"metadata.json"}]}]}
            """;
        var manifest = OpenLogi.Assets.AssetClient.ParseManifest(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Null(manifest.ResourceForVariant("094d", 0, "device_camera_image"));
        Assert.Equal("front_ext_7.png", manifest.AnyResource("device_camera_image"));
        Assert.Null(manifest.AnyResource("device_image"));
    }
}

public class PngEncoderTests
{
    [Fact]
    public void WritesAValidPngHeaderAndChunks()
    {
        var frame = new Frame(2, 1, [0, 0, 255, 255, 255, 0, 0, 255]); // red, blue (BGRA)
        using var ms = new MemoryStream();
        PngEncoder.Write(frame, ms);
        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));
        // IEND's CRC is fixed: AE 42 60 82.
        Assert.Equal(new byte[] { 0xAE, 0x42, 0x60, 0x82 }, bytes[^4..]);
    }
}
