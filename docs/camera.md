# Webcams

This document describes how openlogi-net supports Logitech webcams: how cameras
are found, what the Camera tab shows, how the image controls and profiles work,
what is saved (and where), and what deliberately is not. It ports the Rust
OpenLogi camera support (`crates/openlogi-camera`, `features/camera`). The main
difference is that the preview shares the camera by default and takes it
exclusively only on request (see
[Shared and exclusive access](#shared-and-exclusive-access)); all differences
are listed under [Differences from the Rust app](#differences-from-the-rust-app).

Webcams are not HID++ devices. Every Logitech camera (Brio, C920, C270,
StreamCam, …) is a standard USB Video Class (UVC) device, so cameras have their
own discovery path, their own library (`OpenLogi.Camera`) and their own tab.

## Discovery

- **What counts as a camera.** Every video-capture device Media Foundation
  lists whose USB vendor id is `0x046d`. There is no model table: any Logitech
  UVC camera is recognised. Built-in, virtual and other vendors' cameras are
  ignored.
- **Name.** The product string the camera reports. A few older models report
  none, so Windows invents a placeholder; those are named from a small table
  (`UnnamedCameras`, currently only `046d:081d` → "Logitech HD Webcam C510").
- **Rescan.** Nothing tells the app when a webcam is plugged in, so cameras are
  re-enumerated every **2 seconds** on a background thread, beside the HID
  device watchers.
- **Unplug grace.** An empty scan while camera tiles are showing is tolerated
  **twice** before the tiles go, so a momentary blink (a control write, another
  process opening the camera) never tears down the page being looked at. A
  camera moved to another USB port reappears under a new capture id; its old
  tile is removed rather than kept beside the new one.
- **USB link.** Each scan also asks the USB hub the camera hangs off how fast
  the link runs and how fast the camera could run (no traffic is sent to the
  camera). Any failure means "unknown", never a wrong answer.
- **Who else is using it.** The Windows sensor activity monitor
  (`MFCreateSensorActivityMonitor`) reports, for each camera, which processes
  are streaming from it and whether each one controls the camera or shares it.
  It reports changes within about a second. OpenLogi's own process is left out.
  The monitor starts with the app, alongside the camera scan, and the Camera
  tab uses it to offer or withhold exclusive access.

## Identity

Each camera has two identities:

| Identity | Value | Used for |
|---|---|---|
| Capture id | The Media Foundation device path, e.g. `\\?\usb#vid_046d&pid_094d&mi_00#…#{…}\global` | Opening the preview and the controls; telling two cards apart. Changes when the camera moves to another USB port. |
| Settings key | `camera:{vid}:{pid}:serial:{serial}` when the device path carries a USB serial, otherwise `camera:{vid}:{pid}` | Everything saved in `config.toml`. Stable across USB ports. |

A device path only carries a serial when Windows did not have to invent the
instance id (an invented one contains `&` and is port-dependent, so it is
rejected). Many cameras, the Brio 101 included, therefore get the model-level
key, `camera:046d:094d`. Two such cameras of the same model share one set of
saved settings, though each still gets its own gallery card.

## Gallery tile

A camera appears in the device gallery like any other device:

- Its render comes from the OpenLogi asset registry, matched on the 4-digit USB
  product id. Camera depots name their render `device_camera_image`, and some
  (the Brio 101) only list it for a colour variant; a camera reports no colour
  variant, so any variant's render is used.
- The corner badge is the USB cable plug.
- There is no battery gauge and no battery placeholder.
- No tray battery icon is created for a camera.

## Camera page

Opening a camera's tile shows two tabs: **Camera** (selected first) and
**Device** (name, type, status, connection). There are no Buttons, Pointer or
Lighting tabs.

The Camera tab has the live preview on the left and the camera controls on the
right.

### Preview

- **When it runs.** Only while the Camera tab is the tab on screen *and* the
  window is visible. Switching to the Device tab, going back to the gallery,
  opening another device, minimising the window or hiding it to the tray stops
  the stream: the camera LED goes off and the camera is released (Windows'
  "camera in use" indicator clears for OpenLogi).
- **Starting.** "Starting preview…" shows until the first decoded frame
  arrives. Opening waits up to 5 seconds; a camera still releasing a previous
  stream is retried twice.
- **Errors.**

  | Situation | Message |
  |---|---|
  | Windows privacy settings block desktop apps from the camera | Windows is blocking camera access … Settings → Privacy & security → Camera |
  | The camera's hardware resources are unavailable | Camera resources are unavailable. Another application may be using the camera. |
  | Anything else, or the stream ending (unplug) | The camera preview couldn't start. |

- **Picture.** Frames are converted to RGB32 by Media Foundation and drawn
  scaled to fit. Nothing is recorded or stored.

### Shared and exclusive access

Windows allows one *controlling* client per camera and any number of *sharing*
clients. Only the controlling client may choose the camera's resolution and
frame rate; a sharing client streams whatever mode the camera is in.

**Shared (the default).** The preview opens the camera in Windows' sharing mode
(`MF_DEVSOURCE_ATTRIBUTE_FRAMESERVER_SHARE_MODE`, Windows 11 build 26100 and
later), so it never locks another app (Camera, Teams, Zoom, OBS, …) out:

- An app opened while OpenLogi's preview runs gets the camera normally.
- OpenLogi's preview opened while another app is streaming joins it.
- The preview runs at the camera's default mode (640×480 on the Brio 101) when
  no other app is streaming, or at the controlling app's mode when one is. If
  that app switches mode mid-stream, the preview follows it.
- Straight after exclusive access ends, the shared preview can keep the mode the
  exclusive preview used (the camera is still running it while the exclusive
  stream lets go). It returns to the camera's default the next time the preview
  opens.

**Exclusive.** The **Use the camera exclusively to preview at different
resolutions and frame rates** checkbox under the preview makes OpenLogi's
preview the controlling client:

- The **Resolution** and **Frame rate** dropdowns appear. They list every mode
  the camera offers as NV12, YUY2, RGB24 or RGB32, the formats Media Foundation
  can convert for the preview. Modes a camera delivers only compressed (MJPEG,
  common for its largest sizes) are not listed. Resolutions are sorted largest
  first, rates fastest first. Picking a resolution keeps the frame rate when the
  new size offers it, otherwise the nearest one (the slower on a tie). Picking
  reopens the preview at the new mode.
- It opens at the saved mode (see [What is saved](#what-is-saved)), or, with
  none saved, at the mode the shared preview was already showing, so ticking
  the box doesn't jump the picture. A saved mode the camera doesn't offer falls
  back to the mode nearest 1280×720 at the rate nearest 30 fps.
- A note says the choice applies to this preview only and that other apps
  can't use the camera meanwhile. An app that tries to open the camera while
  OpenLogi holds it exclusively gets the camera-in-use error it would get from
  any other controlling app.
- Exclusive access lasts until the box is unticked or the page is left
  (another tab, the gallery, another device, minimising or hiding the window).
  Every visit to the page starts shared again.

**When another app controls the camera** the checkbox is disabled, with the
message "Another app is using the camera, so its resolution and frame rate
can't be changed here." The checkbox enables itself again within about a second
of that app letting go. If another app takes control in the moment between the
box being ticked and the preview reopening, the exclusive open is refused, and
the preview unticks the box, reopens shared and shows the same message.

**Current mode.** A line under the preview, e.g. "1280 × 720, 30 fps", always
shows the mode the camera is actually running, shared or exclusive, and follows
a controlling app's changes. It is read from the camera's current native media
type, not from what was asked for.

The layout under the preview, top to bottom: the current mode; the checkbox; the
"another app" message (only while one controls the camera); the Resolution and
Frame rate dropdowns and their note (only while exclusive); the USB link notice
(only on a slow link).

On Windows older than build 26100 the sharing attribute is ignored and every
preview opens in controlling mode, as the Rust app's does: other apps cannot use
the camera while the Camera tab is showing.

### USB link notice

When the camera supports a faster USB link than it is running on (a USB 3
camera on a USB 2 port, hub or cable), a notice under the preview says so:
"Connected at USB 2.0 (480 Mbps), but this camera supports USB 3.0 (5 Gbps) …".
A camera on a slow link offers fewer resolutions and frame rates to every app.
USB 2-only cameras (such as the Brio 101) never show it.

## Camera controls

The controls are the camera's own UVC settings. A change is written to the
camera itself, so **every app that uses the camera sees it**, not only
OpenLogi's preview.

### Which controls

Only controls the camera reports are shown, grouped as **Lens** and **Image**
(the headings appear only when both groups have rows). If the camera reports
none: "This camera has no adjustable image controls."

| Control | Group | Windows interface | Auto mode |
|---|---|---|---|
| Zoom, Pan, Tilt | Lens | `IAMCameraControl` | — |
| Focus | Lens | `IAMCameraControl` | Auto focus |
| Exposure | Lens | `IAMCameraControl` | Auto exposure |
| Brightness, Contrast, Saturation, Sharpness, Gain, Backlight compensation | Image | `IAMVideoProcAmp` | — |
| White balance | Image | `IAMVideoProcAmp` | Auto white balance |
| Tint | Image | `IAMVideoProcAmp` (hue) | — |
| Anti-flicker (power-line frequency), Low-light compensation | — | Not reachable through these interfaces on Windows | — |

The Brio 101, for example, reports exposure, brightness, contrast, saturation,
sharpness, gain, backlight compensation and white balance, plus auto exposure
and auto white balance.

Control writes also use sharing mode. Windows lets sharing clients change these
legacy camera controls, so the controls work while another app is streaming.

### Editing a control

Each row is: label · slider · value · **Auto** chip (only for controls with an
auto mode).

- **Slider.** The value label follows the thumb. The camera is written once the
  slider has been still for 250 ms, not on every intermediate value.
- **Auto.** While Auto is on, the row is dimmed and the camera drives the value.
  The slider still works: moving it is the take-over gesture, switching Auto off
  and writing the value in one go. Clicking the chip toggles Auto; turning it off
  re-asserts the slider's value.
- **Exact value.** Clicking the value opens a "Set {control}" dialog with the
  supported range. A value outside the camera's range keeps the dialog open
  rather than being rounded.
- **Reset one control.** Double-click the row: the value and its Auto mode go
  back to the camera's defaults.
- **Reset to defaults.** Every control and Auto mode back to the camera's
  defaults, in one batch.

Windows does not report a factory default for Auto modes. As in the Rust app,
the default is taken to be **on**, which is how Logitech cameras ship.

Batches write the values first and the Auto modes last. A value write carries
the "manual" flag, so the other order would switch an Auto mode straight back
off.

If a write fails (the camera was unplugged or refused it), the panel re-reads
the camera's live state and clears the active profile, so it never shows, or
later saves, a value the camera did not take. If reading the controls fails
when the page opens, the read is retried every 2 seconds.

### Profiles

A row of chips above the controls:

| Chip | Effect |
|---|---|
| **Default** | Every control and Auto mode to the camera's defaults. |
| **Streaming** | Brightness 50%, contrast 58%, saturation 62%, sharpness 60% of each control's range; every Auto mode on; every other control to its default. |
| **Video call** | Brightness 55%, contrast 52%, saturation 55%, sharpness 48%; every Auto mode on; every other control to its default. |
| Saved profiles (**Custom 1**, **Custom 2**, …) | The values and Auto modes captured when saved. The ✕ deletes it; the camera keeps its current settings. |
| **+ New** | Saves the current values and Auto modes as the next free "Custom N" and selects it. |

Percentages are fractions of the range the camera reports, so the built-ins
suit any camera. Anti-flicker and low-light compensation keep their current
values under Streaming and Video call (they are not reachable on Windows
anyway).

The applied profile is highlighted. While a **saved** profile is selected, any
edit to a control is written back into that profile, so it always matches what
was last seen while it was selected. Built-in profiles are never edited.

## What is saved

### On the camera

Every control value and Auto mode set through OpenLogi lives in the camera's
own registers. Every app sees it immediately. The camera keeps it **only until
it loses power** (unplugged, or the PC sleeps or shuts down).

### In `config.toml`

`%APPDATA%\OpenLogi\config.toml`, under the camera's settings key:

```toml
[devices."camera:046d:094d"]
camera_profile = "video_call"          # the profile last applied (highlighted)

[devices."camera:046d:094d".camera_stream_mode]
width = 1920                           # the exclusive preview's resolution
height = 1080
fps = 30                               # and frame rate

[devices."camera:046d:094d".camera_controls]
brightness = 140                       # every control value set
contrast = 133
exposure = -6
exposure_auto = 1                      # Auto modes: 1 = on, 0 = off
white_balance_auto = 1

[devices."camera:046d:094d".camera_profiles."Custom 1"]
brightness = 160                       # a saved profile: values + Auto modes
exposure_auto = 0
```

| Key | Written when | Contents |
|---|---|---|
| `camera_controls` | A control or Auto mode is changed, reset, or set by a profile | One entry per control (`zoom`, `pan`, `tilt`, `focus`, `exposure`, `power_line_frequency`, `low_light_compensation`, `brightness`, `contrast`, `saturation`, `sharpness`, `gain`, `backlight_compensation`, `white_balance`, `tint`) and Auto mode (`focus_auto`, `exposure_auto`, `white_balance_auto`) that has been set |
| `camera_profiles` | **+ New**, an edit while a saved profile is selected, or ✕ | Name → snapshot of every shown control and Auto mode |
| `camera_profile` | A profile is applied or saved; cleared when that profile is deleted or a write fails | The selected profile: `default`, `streaming`, `video_call` or a saved profile's name |
| `camera_stream_mode` | A resolution or frame rate is picked while the preview has exclusive access | `width`, `height`, `fps` the exclusive preview opens at. Used only by OpenLogi's preview; nothing is sent to the camera, and other apps choose their own. A saved mode the camera no longer offers falls back to the camera's default. |

Nothing camera-related is written until the user changes something, so an
untouched camera adds nothing to the file. The names and layout match the Rust
app, so one `config.toml` works with both.

### Restoring after power loss

Because the camera forgets its settings when it loses power, **opening the
camera's page re-applies the saved settings**:

- Each saved Auto mode that differs from the camera's is set.
- Each saved value that differs from the camera's is written, except for
  controls whose Auto mode is on (the camera drives those).

Settings are restored when the page opens, not when the camera is plugged in.
Until the page is opened, a camera that lost power runs on its own defaults in
every app. This matches the Rust app.

## What is not saved

| Not saved | Why |
|---|---|
| Resolution and frame rate on the camera | Not settings a camera holds: every app agrees its own mode each time it opens the camera. OpenLogi saves only its own exclusive preview's choice (`camera_stream_mode`). |
| The exclusive-access checkbox | Deliberately per visit: a saved "exclusive" would silently lock other apps out of the camera every time the page opened. |
| Who else is using the camera | Live from the sensor activity monitor. |
| Preview frames | The preview is live only; nothing is recorded. (The CLI `snapshot` command writes a PNG only where asked.) |
| Capture ids and the camera list | Rediscovered every 2 seconds; ids change with the USB port. |
| USB link speed | Re-read on every scan. |
| Auto-mode defaults | Not reported by Windows; assumed on. |
| Anything for an untouched camera | Settings appear in the file only after the first change. |

## Diagnostic log

Camera events go to the app's log, `%LOCALAPPDATA%\OpenLogi\logs\log.txt`,
under the `camera` area, so a bug report shows what happened:

| Line | When |
|---|---|
| `preview open: shared at 640x480 @ 30 fps` / `exclusive at …` | Every time the preview stream opens |
| `exclusive access on/off …, other app controls: …` | The checkbox changes; `(automatic)` when the app unticks it after a refused exclusive open |
| `exclusive preview mode … (was …)` | A resolution or frame rate is picked |
| `preview start failed: …` / `preview stream ended: …` | The preview could not open, or stopped on its own |
| `controls read failed`, `control write failed`, `auto write failed`, `control reset failed`, `camera reset failed`, `camera profile apply failed`, `saved camera state reapply failed` | A control operation failed (the panel then resyncs from the camera) |
| `camera scan failed` | An enumeration threw |

## Cost when idle

Nothing streams unless a Camera tab is on screen. The rest is cheap, measured on
a 16-core PC with one camera:

- The 2-second scan takes about 1.2 ms (0.8 ms of CPU), roughly 0.04% of one
  core.
- The sensor activity monitor is driven by Windows and reports only changes.

## Command line

`OpenLogi.Cli` uses the same library:

| Command | Effect |
|---|---|
| `list` | Also lists cameras: name, vid/pid, USB link speed, capture id. |
| `camera [get]` | Each control's current, minimum, maximum and default, and each Auto mode. |
| `camera set <name> <value>` | Writes a control (names as in `camera_controls`) or an Auto mode (`0`/`1`) to the camera. Does **not** update `config.toml`. |
| `snapshot <file.png>` | Captures one frame to a PNG (shared, at the camera's current mode). It is the first frame, so it can be dark before auto exposure settles. |

The CLI ignores the exclusive-access setting and `camera_stream_mode`.

Each takes `--id <capture id>`; the default is the first Logitech camera.

## Differences from the Rust app

| Area | Rust app | This app |
|---|---|---|
| Camera access | The preview always controls the camera, locking other apps out while it shows | Shared by default; exclusive (controlling) only while the checkbox is ticked, and offered only when no other app controls the camera |
| Resolution / frame rate picker | Always shown | Shown only with exclusive access; a shared preview cannot choose |
| Current mode line | — | Shown under the preview |
| Exclusive-access setting | — | Per visit, never saved |
| Batch write order | Auto modes, then values (a value write then switches Auto back off on Windows) | Values, then Auto modes |
| Slider writes | On mouse release | 250 ms after the slider settles (also covers track clicks and the keyboard) |
| Camera render | `device_camera_image` for the base model or the exact colour variant | Also any variant's, for depots (Brio 101) that list only a variant |
| Diagnostic log | — | The lines above |

The same in both: discovery by vendor id, settings keys, the control set
(anti-flicker and low-light compensation are unavailable on Windows in both),
profiles and their values, `config.toml` keys, and restoring settings when the
page opens.

## Code map

| Part | Where |
|---|---|
| Discovery, identity, controls, USB link | `src/OpenLogi.Camera/Cameras.cs`, `CameraInfo.cs`, `CameraControls.cs`, `UsbLink.cs`, `DevicePath.cs`, `CameraWorker.cs` |
| Live stream (shared / exclusive), modes | `src/OpenLogi.Camera/CameraStream.cs`, `StreamMode.cs` |
| Other apps using a camera | `src/OpenLogi.Camera/CameraUsage.cs`, `Interop/SensorActivity.cs` |
| Media Foundation / DirectShow / hub interop | `src/OpenLogi.Camera/Interop/` |
| Gallery tiles and the 2-second scan | `src/OpenLogi.App/ViewModels/MainWindowViewModel.Camera.cs` |
| Camera tab | `src/OpenLogi.App/ViewModels/CameraViewModel.cs`, `CameraControlRowViewModel.cs`, `Views/MainWindow.axaml`, `Views/CameraValueWindow.axaml` |
| Saved settings | `src/OpenLogi.Core/Config/DeviceConfig.cs`, `Config.cs`, `ConfigCodec.cs` |
| Device kind | `DeviceKind.Camera` in `src/OpenLogi.Core/DeviceInfo/DeviceKind.cs` |
| Tile render lookup | `src/OpenLogi.Assets/AssetResolver.cs`, `AssetRegistry.cs` (`device_camera_image` fallback) |
| Strings | `Camera_*` and `Main_Camera` in `src/OpenLogi.Core/Localization/Strings*.resx` |
| CLI | `src/OpenLogi.Cli/Commands.Camera.cs` |
| Tests | `tests/OpenLogi.Tests/Camera/CameraLogicTests.cs` |

All camera I/O except the preview runs on one dedicated background thread,
which keeps a control write from racing a scan and keeps COM calls off the UI
thread. Each preview stream has its own reader thread.
