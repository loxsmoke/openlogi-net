# Support Logitech webcams (UVC): live preview, camera controls, profiles

## Summary

Bring the .NET app up to parity with the Rust OpenLogi on Logitech webcams.
Connected Logitech cameras (Brio, C920/C922, C270, C930e, StreamCam, …) should
show up as their own device cards. Each card opens a **Camera** tab with a live
preview, a resolution/frame-rate picker and hardware image controls with
profiles. The CLI gets `camera` and `snapshot` commands.

Reference implementation: [AprilNEA/OpenLogi](https://github.com/AprilNEA/OpenLogi) @ `e581c20` (2026-11-02):
- `crates/openlogi-camera/` — discovery, UVC controls, capture, stream modes, USB link speed
  (Windows: `uvc_windows.rs`, `capture_windows.rs`, `mf_formats.rs`, `usb_windows.rs`)
- `crates/openlogi-device-registry/src/camera.rs` — names for cameras that report no product string
- `crates/openlogi-desktop/src/features/camera/` — preview, mode picker, controls panel, profiles
- `crates/openlogi-desktop/src/state/camera.rs`, `state/devices.rs` (`camera_record`), `runtime.rs` (scan loop)
- `crates/openlogi-cli/src/cmd/camera.rs`, `snapshot.rs`, `list.rs`

## Current situation (.NET)

- `DeviceKind` ([DeviceKind.cs](src/OpenLogi.Core/DeviceInfo/DeviceKind.cs)) has no `Camera` member.
- `MainWindowViewModel.IsConfigurable` / `InputKinds` hide every device that isn't an input device. The comment there says webcams are excluded on purpose.
- Discovery is HID-only (`HidInventory`, `HidDeviceWatcher`). UVC cameras are never enumerated, and `HidDeviceWatcher` deliberately ignores webcam arrivals.
- `DeviceConfig` has no camera fields. The CLI has no camera commands.

## Goal

A user with a Logitech webcam on Windows can see it in OpenLogi, preview it,
change its image settings so that every app sees the change (Meet, Zoom, OBS),
and have those settings restored the next time they open it. Behaviour should
match the Rust Windows build.

## Scope

Windows only (the .NET app targets `net10.0-windows`). The Rust macOS (AVFoundation/IOKit) and Linux (V4L2) backends and the macOS camera-permission page are out of scope.

---

## Requirements

### 1. Discovery

- [ ] Enumerate the DirectShow video-input category (`CLSID_VideoInputDeviceCategory`) and read each device's friendly name and device path.
- [ ] Parse `vid_xxxx&pid_xxxx` and the instance segment out of the device path (`\\?\usb#vid_046d&pid_0893&mi_00#SERIAL#{guid}\global`). Use the instance as the USB serial **only** when it is a real serial, not a parent-generated instance id (contains `&`).
- [ ] Keep only vendor `0x046d`. Detection uses the vendor ID alone, with **no model table**, so any Logitech UVC camera is recognised.
- [ ] Naming: use the OS name. If the camera has no USB product string, look up a small "unnamed cameras" table instead (port `openlogi-device-registry/src/camera.rs`; currently one entry: `046d:081d` → "Logitech HD Webcam C510").
- [ ] Record the maximum resolution and fps when the OS reports them (metadata only: no capture, no LED).
- [ ] Camera model:
  - `Name`
  - `UniqueId` (DirectShow device path; may change when the camera is moved to another port)
  - `SerialNumber?`
  - `VendorId`, `ProductId`
  - `MaxResolution?`, `MaxFps?`

### 2. Identity and persistence key

- [ ] `ConfigKey` keeps settings across USB ports:
  - `camera:{vid:x4}:{pid:x4}:serial:{serial lowercased}` when the camera has a serial
  - otherwise `camera:{vid:x4}:{pid:x4}`
- [ ] Two cameras of the same model without serials **share** one settings key on purpose, but still show as two separate cards, keyed by `UniqueId`.

### 3. Hotplug and gallery

- [ ] Nothing announces UVC arrivals to the app, so re-scan cameras on a background timer every **2 s**, off the UI thread (Rust `CAMERA_SCAN_PERIOD`).
- [ ] An empty scan while cameras are showing gets a **2-scan grace** before cards are removed, so a short blink (e.g. the CLI opening the device) doesn't tear down the open detail page.
- [ ] When a camera reappears with a new `UniqueId` after a port change, don't keep a stale card next to the new one.
- [ ] Add `DeviceKind.Camera`, map registry type `"camera"` in `DeviceKindExtensions`, and let cameras through the `IsConfigurable` filter.
- [ ] The camera card resolves its image from the asset registry by 4-hex product id (Rust `camera_model_info`: `model_ids = [pid, 0, 0]`).
- [ ] Merge cameras into the device list next to HID++ devices; they have no HID++ route.
- [ ] The tray battery icon logic must ignore cameras.

### 4. Detail page: Camera tab

- [ ] A camera's detail page shows the **Camera** tab first, then the generic Device/info tab. No Buttons, Pointer or Lighting tabs.
- [ ] The tab has the live preview, the mode picker under it, then the controls panel.

### 5. Live preview (Media Foundation)

- [ ] Capture with `IMFSourceReader` on a dedicated reader thread that owns the whole MF object graph, converting to RGB32/BGRA. Force alpha to opaque (RGB32 is BGRX). Render into an Avalonia `WriteableBitmap`.
- [ ] **Lifecycle:** the stream runs **only while the Camera tab is visible**. Leaving the tab, going home, selecting another device or minimising to tray drops the stream, so the LED turns off and the stream leaves no CPU or memory use behind.
- [ ] Rebuild the bitmap only when a new frame arrives, and repaint at the camera's delivery rate.
- [ ] Startup counts as successful only once the first **decoded frame** has been stored, not when the first sample request succeeds.
- [ ] Error states:
  - Starting: "Starting preview…"
  - Camera busy or resources unavailable: "Camera resources are unavailable. Another application may be using the camera."
  - Access denied by the Windows privacy toggle (activation error → `AccessDenied`): a message pointing to Windows Settings → Privacy & security → Camera. The Rust string says "System Settings" (macOS wording), so word it for Windows.
  - Any other failure: "The camera preview couldn't start."
- [ ] Keep the last frame on screen while the stream reopens.

### 6. Resolution and frame rate

- [ ] Read the camera's native media types and collapse them into modes (frame size × rate, ignoring pixel format). List only formats the reader can convert to RGB32 (port `mf_formats.rs` and `stream_mode.rs`, both pure logic).
- [ ] Two dropdowns:
  - **Resolution:** every size the camera offers.
  - **Frame rate:** every rate offered at the selected size, shown as "{fps} fps".
  - The dropdowns can't offer a mode the camera would refuse.
- [ ] Default when nothing is saved is about 720p at 30 fps. If the saved mode is no longer offered, fall back to the camera's default.
- [ ] Changing the mode reopens the stream at the new mode. Choosing the already-selected option does nothing.
- [ ] A Windows camera that is still busy when reopened gets retried; `RELEASE_DELAY` is zero on Windows.
- [ ] Note under the dropdowns: "Applies to this preview only. Other apps choose their own resolution and frame rate."
- [ ] Save the mode per camera (`CameraStreamMode`), separate from the image controls. Profiles neither capture nor apply it.
- [ ] Hide the dropdowns when the camera lists no modes.

### 7. USB link notice

- [ ] At enumeration, off the UI thread, find each camera's hub and port: device path → interface device node → parent USB device (port number) → parent hub, via Configuration Manager.
- [ ] Ask the hub with `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX` (current speed) and `_V2` (SuperSpeed capability and whether it runs at SuperSpeed), as in the USBView sample. Nothing is sent to the camera.
- [ ] When the camera supports a faster link than it is running at, show under the preview: "Connected at {speed}, but this camera supports {capable}. On the faster link it may offer more resolutions and frame rates. Plug it straight into a USB 3 port, without a hub, using a USB 3 cable."
- [ ] If any step fails, show no notice. Never report a wrong link speed.

### 8. Camera controls (DirectShow)

The changes land in the camera's own registers, so every app sees them.

- [ ] Map each control to a DirectShow property:

  | Control | Section | DirectShow |
  |---|---|---|
  | Zoom, Pan, Tilt, Focus, Exposure | Lens | `IAMCameraControl` |
  | Brightness, Contrast, Saturation, Sharpness, Gain, Backlight compensation, White balance, Tint (= `Hue`) | Image | `IAMVideoProcAmp` |
  | Anti-flicker (power-line frequency), Low-light compensation | — | **Unsupported on Windows in Rust**: hide the rows. Keep them in the model and the CLI vocabulary. |

- [ ] Auto toggles, using the per-property `Flags_Auto`/`Flags_Manual`:
  - Focus → `focus_auto`
  - Exposure → `exposure_auto`
  - White balance → `white_balance_auto`
- [ ] Read all ranges and auto states in a **single device open**: min, max, default, current, and an optional `value_mask` for discrete controls. Only show the controls the camera supports.
- [ ] If none are supported, show "This camera has no adjustable image controls."
- [ ] Each control row shows: label · slider · live value, plus an **Auto** chip when the control has a paired auto toggle. The slider is disabled while Auto is on.
- [ ] Binary or discrete controls (anti-flicker, low-light) are drawn as chips, not sliders. Only offer values allowed by `value_mask`.
- [ ] Double-click a row to reset that control to its default.
- [ ] Clicking the value opens a "Set {label}" dialog with numeric entry, a "Supported range: {min}–{max}" hint, and clamping.
- [ ] A **Reset to defaults** button.
- [ ] Do all USB reads and writes off the UI thread. If a background read fails, retry.
- [ ] Fail closed when the target camera is ambiguous (`ControlError.Ambiguous`). Never write to a guessed device.

### 9. Persistence and re-apply

- [ ] Save each control value and auto toggle per camera `ConfigKey` (`CameraControls`: name → int, with autos stored as 0/1, using the stable snake_case names listed in §11).
- [ ] The camera only holds its settings until it loses power, so **re-apply the saved settings over USB when the camera is next viewed**, as one batched device open (`apply_settings`: autos first, then values).
- [ ] New `DeviceConfig` fields, all omitted from the file when empty:
  - `CameraControls?`
  - `CameraProfiles` (name → `CameraControls`)
  - `CameraProfile?` (the profile last applied)
  - `CameraStreamMode?`

### 10. Profiles

- [ ] A chip row: three built-ins, then the user's saved profiles, then **Save**.
  - **Default**: every control at the device default, and every auto toggle at its device default.
  - **Streaming**: brightness 0.50, contrast 0.58, saturation 0.62, sharpness 0.60. Values are *fractions of each control's reported range*.
  - **Video call**: brightness 0.55, contrast 0.52, saturation 0.55, sharpness 0.48.
  - For Streaming and Video call: turn every auto toggle on. Keep the current anti-flicker and low-light values. Set every other control to its default.
- [ ] Apply a profile in one batched device open and highlight the active chip.
- [ ] Save prompts for a name and snapshots the current values and autos.
- [ ] Deleting a custom profile also clears the active selection if it was that profile.
- [ ] Changing a control while a *custom* profile is active writes the change back into that profile. Built-in profiles are never edited.

### 11. CLI

- [ ] `list` adds a section `Cameras (N Logitech UVC)`. Each line shows the name, `camera`, vid/pid, max resolution/fps when known, and the id.
- [ ] `camera [get]` prints each control's min/max/default/current and each auto toggle's state.
- [ ] `camera set <name> <value>` sets a value. Names:
  - `zoom`, `pan`, `tilt`, `focus`, `exposure`, `power_line_frequency`, `low_light_compensation`
  - `brightness`, `contrast`, `saturation`, `sharpness`, `gain`, `backlight_compensation`, `white_balance`, `tint`
  - `focus_auto`, `exposure_auto`, `white_balance_auto` (0/1)
- [ ] `snapshot <out.png>` captures one frame to a PNG.
- [ ] Every command takes `--id <unique id>`; the default is the first Logitech camera.

### 12. Localization

Add to `Strings.resx` **and** `Strings.de.resx` / `Strings.zh-Hans.resx`, and run the localization coverage tests (see `AGENTS.md`). Rust strings are in `crates/openlogi-ui/locales/en.toml` under `[camera]`:

`Camera`, `Starting preview…`, the resources-unavailable, start-failed and access-denied messages (Windows wording), `Camera controls`, `Reset to defaults`, `This camera has no adjustable image controls.`, `Lens`, `Image`, the 15 control labels (Zoom…Tint; "Anti-flicker" for power-line frequency), `Auto`, `Set {0}`, `Supported range: {0}–{1}`, `Set {0} value`, `Default`, `Streaming`, `Video call`, `Save`, `Resolution`, `Frame rate`, `{0} fps`, the stream note, and the USB-link notice (`{0}` speed, `{1}` capable).

---

## Out of scope / follow-ups

- Litra "Auto-on with camera" (needs Litra support first; tracked separately).
- macOS and Linux backends; the camera-permission page (macOS only).
- Non-Logitech cameras.

## Implementation notes

- COM: DirectShow (`ICreateDevEnum`, `IAMCameraControl`, `IAMVideoProcAmp`) and Media Foundation (`MFEnumDeviceSources`, `IMFSourceReader`) through source-generated `[GeneratedComInterface]` interop or a small package (DirectShowLib, or the Vortice.MediaFoundation package). Pick one and keep it in a new `OpenLogi.Camera` project, so `Core` stays free of COM.
- Put the hardware layer (enumeration, controls, capture) behind interfaces. Rust tests swap out the UVC transfers and capture resources, not the panel's load lifecycle or persistence (`controls/hardware.rs`, `preview/capture.rs`).
- Pure logic worth porting with its tests:
  - `stream_mode.rs` (mode resolution, which rate survives a change of resolution)
  - `mf_formats.rs` (native types → modes and back)
  - device-path parsing and serial detection in `uvc_windows.rs`
  - `ControlRange.Supports` (`value_mask`)
  - `ConfigKey`
  - built-in profile math
- Cameras don't go through the agent. The GUI drives them directly over USB, as in Rust (no IPC).

## Acceptance criteria

- [ ] Plugging in a Logitech webcam adds a card within about 2 s. Unplugging removes it, after the 2-scan grace.
- [ ] Opening the Camera tab turns the LED on and shows a live preview. Leaving the tab turns the LED off.
- [ ] Changing brightness or zoom in OpenLogi shows up in the Windows Camera app or Teams while both are open.
- [ ] After unplugging and replugging (into a different port, for cameras with a serial), opening the camera restores the saved values.
- [ ] The resolution and frame-rate picks are remembered per camera and don't affect other apps.
- [ ] A USB 3 camera on a USB 2 port shows the link notice; on a USB 3 port it doesn't.
- [ ] Built-in and custom profiles apply, save and delete as described above.
- [ ] `openlogi list`, `camera get`, `camera set brightness 128` and `snapshot out.png` work.
- [ ] Unit tests cover the ported pure logic. The localization coverage tests pass.
- [ ] Hardware-verified on at least one camera with a serial and one without (e.g. C920 + C270), and one camera without lens motors (it shows no lens section).
