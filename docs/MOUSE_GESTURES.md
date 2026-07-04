# Mouse gestures

This document describes how mouse gestures work in openlogi-net — which
gestures exist, on which buttons, when they are unavailable, and how the
feature is implemented. It is a Windows-focused port of the gesture design in
the upstream Rust [OpenLogi](https://github.com/AprilNEA/OpenLogi)
(`openlogi-hid`'s `gesture.rs` and `openlogi-agent-core`'s `watchers/gesture.rs`
+ `hook_runtime.rs`).

## What a gesture is

A gesture is a **hold-and-swipe** on a designated *gesture button*. While the
button is held, the pointer's raw movement is captured and classified into one
of five sub-actions:

| Direction | Glyph  | Meaning                              |
|-----------|--------|--------------------------------------|
| Up        | ↑      | Hold, then move the mouse up         |
| Down      | ↓      | Hold, then move the mouse down       |
| Left      | ←      | Hold, then move the mouse left       |
| Right     | →      | Hold, then move the mouse right      |
| Click     | *(none)* | Press and release without moving   |

Each direction maps to its own bound action, so one physical button drives five
actions. A press that never commits a direction (a plain tap) fires the
**Click** action.

Coordinates follow the device convention (**+x = right, +y = down**), so an
upward swipe reports negative `dy` and maps to **Up**.

### Default bindings

The **dedicated gesture button** ships with a full five-direction map
(`Bindings.DefaultGestureBinding` in `OpenLogi.Core/Actions.cs`):

| Direction | Default action        |
|-----------|-----------------------|
| Up        | Task View             |
| Down      | Show Desktop          |
| Left      | Previous Tab          |
| Right     | Next Tab              |
| Click     | Task View             |

Every **other** button defaults to *no* gestures: its editor seeds Click with
the button's current action and the four swipes with **Do Nothing** (the
"Disabled" gesture set), and nothing is diverted until the user actually
configures something.

## On which buttons

The upstream Rust project (and Options+) has exactly **one gesture role per
device**. openlogi-net generalizes this: **each capable button carries its own
gesture map** (`Config.GestureButtons`), several may gesture at once, and a
single **"Gestures Enabled"** master switch (`GestureOwner.Off` when unchecked)
silences them all while keeping every map. Two kinds of control can gesture:

1. **A HID++-captured button**, diverted over **HID++** with **raw-XY
   reporting**: while it is held the device streams raw pointer deltas to the
   app instead of moving the cursor, and the app classifies the swipe. The
   candidates (see `DeviceSession.GestureCandidates`):
   - The **dedicated gesture button** (control ID `0x00c3`, *"App Switch
     Gesture"*) on MX-line mice such as the MX Master series — the thumb button
     under the thumb rest. Gestures by default when present.
   - Any other **divertable raw-XY button** — Middle (`0x0052`), Back
     (`0x0053`), Forward (`0x0056`), or the DPI / wheel-mode button (`0x00c4`
     family) — on mice that have no dedicated gesture button. Repurposing a
     button for gestures gives up its normal function — exactly the trade Logi
     Options+ makes. The swipe mechanism is identical; only the diverted
     control differs. On Windows even Middle/Back/Forward gesture over HID++
     (not the OS hook), since `WH_MOUSE_LL` carries no per-hold move deltas.

2. **An OS-hook button** — Middle, Back, or Forward — repurposed as a gesture
   button. In the upstream Rust app these are captured through the OS input
   hook (CGEventTap on macOS, evdev on Linux): the native click is suppressed,
   the hold accumulates pointer-move deltas, and the swipe is classified the
   same way.

   > **Windows note:** the low-level Windows mouse hook (`WH_MOUSE_LL`) in both
   > the Rust original and this port does not surface pointer-move deltas to the
   > gesture accumulator, so the OS-hook gesture *path* is inert on Windows. The
   > model and swipe math exist (`Bindings.OsHookGesturesFor`,
   > `MouseEvent.Moved`) but nothing feeds them — instead, openlogi-net captures
   > Middle/Back/Forward **over HID++** like every other gesture button, so this
   > costs no functionality. See
   > [Extending to OS-hook gestures](#extending-to-os-hook-gestures).

Buttons without a configured gesture map are never diverted and keep their
normal single-action behavior.

## Swipe detection

Classification is shared by both paths (`OpenLogi.Core/Gestures.cs`, ported from
Rust `binding::detect_swipe` / `SwipeAccumulator`):

- **Hold gate** — travel is ignored until the button has been held for at least
  **160 ms** (`Gestures.HoldForSwipe`). This rejects a quick click that happened
  to drift a few pixels.
- **Commit threshold** — the dominant axis must travel at least **50 raw-XY
  units** (`Gestures.SwipeThreshold`) before a direction commits.
- **Deadzone** — the cross axis must stay within
  `max(40, 35% of the dominant axis)` (`Gestures.SwipeDeadzone`), so a diagonal
  smear does not register as a clean swipe.
- **Mid-swipe commit** — the direction fires **the instant** it crosses the
  threshold during the hold (matching Options+), not on release. It fires **at
  most once per hold**.
- **Click fallback** — if the button is released while a hold was in progress
  and no direction ever committed, that is a **Click**.
- All arithmetic **saturates** — an arbitrarily long diagonal hold can never
  overflow or throw. A crash in the input callback would be a freeze hazard.

The state machine (`SwipeAccumulator`) has three operations: `Begin()` on
press, `Accumulate(dx, dy)` per movement delta (returns the committed direction
once), and `End()` on release (returns `true` when the hold was a plain click).

## Editing gestures in the app

The **Gestures panel** sits to the right of the mouse diagram on the **Buttons**
tab, shown for any mouse that exposes a HID++-capturable gesture control. Its
layout, top to bottom:

1. **Gestures Enabled** checkbox — the device-wide master switch. Unchecking it
   silences every button's gestures at once (every map is kept), releases all
   diverted controls, and clears the Button selection; re-checking brings every
   configured button back to life. An explanatory note under the checkbox is
   always visible.
2. **Button** dropdown — every eligible button (the dedicated gesture button,
   Middle, Back, Forward, and/or the DPI/wheel button). This selects which
   button you're *editing*; selecting a button never creates or clears any
   map, and other configured buttons keep gesturing. Clicking a button's text
   label next to the mouse image selects it here too, and the selected button's
   label + leader line get an accent-blue highlight on the diagram.
3. **Click** row — the plain-tap action for the selected button (always active
   while the button has gestures; set it to the button's native action to keep
   a normal tap).
4. **Gestures** dropdown (below a separator) — a preset per gesture set:
   **Disabled** (the default; all four swipes Do Nothing, only Click acts),
   **Windows & Desktops**, **Media & Volume**, **Arrange Windows**, **Browser
   Tabs**, **Scrolling**, or **Custom**. Picking a preset fills the four swipe
   rows; hand-editing any swipe flips it to *Custom*.
5. **↑ ↓ ← →** rows — one action dropdown per swipe, visible unless the set is
   *Disabled*.

**Ctrl+Z** (while focus is in the panel) undoes Click / preset / swipe edits,
one step per user edit (a preset fill counts as one). The history clears when a
different button is selected.

A button becomes live on its **first actual edit** — that re-arms the HID++
captures and diverts its control. Every gesture-active button shows a third
label line on the diagram — `Gestures: <set name>` when all swipe actions share
a category, else the first action names that fit ~20 characters.

On mice whose artwork has a dedicated gesture-button hotspot, its **diagram
flyout** also offers the five-direction editor directly.

All picker dropdowns group actions by category (Mouse Buttons, Scrolling,
Windows & Desktops, Browser & Tabs, Editing, Media & Volume, DPI & Wheel,
System) with *Do Nothing* as the very last entry.

### Diagnosing a device's controls (CLI)

`OpenLogi.Cli controls` dumps the `0x1b04` control table for each device —
control IDs, capability flags (divertable / raw-xy / virtual), and current
divert/remap state — so you can see whether a mouse has `0x00c3` or which button
to repurpose. `OpenLogi.Cli gestureprobe [cid]` diverts one control (default
`0x00c4`) with raw-XY and prints its events for ~8 s, so a hold-and-swipe can be
confirmed live before wiring it in the GUI.

## When gestures are unavailable

Gestures do nothing — the button behaves natively — in any of these cases:

- **The device exposes no capturable gesture control**, i.e. none of the
  candidate controls (`0x00c3`, Middle `0x0052`, Back `0x0053`, Forward
  `0x0056`, or the `0x00c4`-family DPI/wheel button) is present in its `0x1b04`
  reprogrammable-controls table with the **raw-XY** capability. (Note: some mice,
  e.g. the MX Anywhere 3S, expose a *Virtual Gesture Button* `0x00d7` that
  Logi Options+ uses; openlogi-net does not drive that virtual control directly —
  it repurposes a real button instead.)
- **The "Gestures Enabled" master switch is off** (`Config.DisableGestures` →
  `GestureOwner.Off`). Every gesture control is left undiverted and keeps its
  native function; all maps are preserved for re-enabling.
- **The button has no gesture map** (never configured, i.e. its swipe set is
  effectively *Disabled* with no stored binding). Unconfigured buttons are never
  captured-and-swallowed. A configured button whose set is *Disabled* stays
  diverted — its Click action still fires — but its swipes do nothing.
- **The device is offline / unreachable**, or another process (e.g. Logi
  Options+) owns the receiver. No HID++ channel, no capture.
- **The `0x1b04` feature is absent** on the device.

When a capture session ends for any reason, the diverted control is always
**restored** to its native mapping (best-effort; failures to restore are logged,
not fatal).

## Logitech's gesture spec (for comparison)

Is there an official spec? **Not at the protocol level.** For mice, gestures are
entirely **host-side**: the device only offers the `0x1b04` control divert +
raw-XY delta stream (the mechanism this app uses), and HID++ has no "gesture
commands" feature for mice (feature `0x6501 GESTURE` exists only for
touchpads). Logitech's public support pages name the gesture *sets* but never
publish the per-direction tables.

The de-facto spec is the preset data **Logi Options+ ships on disk**:
`C:\Program Files\LogiOptionsPlus\data\card_presets\card_presets_win.json`,
card `card_global_presets_one_of_gesture_button`. Extracted from an Options+
install (2026-07), these are all eight gesture sets and their per-direction
actions on Windows:

| Set (Options+ name) | Hold + Up | Hold + Down | Hold + Left | Hold + Right | Tap (Click) |
|---|---|---|---|---|---|
| **Virtual desktops** *(default)* | Start menu | Show/hide desktop | Desktop left | Desktop right | Task view |
| **Media controls** | Volume up | Volume down | Previous track | Next track | Play/Pause |
| **Windows management** | Maximize window | Show/hide desktop | Snap left | Snap right | Switch application |
| **App navigation** | Start menu | Show/hide desktop | Switch application ← | Switch application → | Switch application |
| **Zoom / Rotate** | Zoom in | Zoom out | Rotate ↺ | Rotate ↻ | Zoom reset |
| **Pan** | Pan | Pan | Pan | Pan | Middle button |
| **Arrange windows** | Maximize window | Minimize window | Snap left | Snap right | Switch application |
| **Custom** | *any action* | *any action* | *any action* | *any action* | *any action* |

The *Custom* set lets each direction be any Options+ action or keystroke —
the same model as openlogi-net's per-direction editor.

Two structural notes from the same data:

- Some Options+ gestures are **continuous** (`speedControl` / `autoRepeat` in
  `gestureInfo`): volume, pan, and zoom repeat while the mouse keeps moving,
  scaled by speed. openlogi-net (like the upstream Rust project) commits **one
  action per hold** — the Options+ "one-shot" sets (desktops, windows,
  app navigation, media prev/next) map 1:1, the continuous ones don't yet.
- Options+ implements Pan / Zoom / Rotate / Switch-application as private
  in-process behaviors (`CUSTOM_APPLICATION_NAVIGATION`, injected pinch/pan),
  not plain keystrokes.

### Coverage in openlogi-net

| Options+ gesture action | openlogi-net equivalent |
|---|---|
| Task view | `TaskView` ✔ |
| Show/hide desktop | `ShowDesktop` ✔ |
| Desktop left / right | `PreviousDesktop` / `NextDesktop` ✔ |
| Start menu | `StartMenu` ✔ |
| Volume up / down, Prev / Next track, Play/Pause | `VolumeUp/Down`, `PrevTrack/NextTrack`, `PlayPause` ✔ (one-shot, not speed-scaled) |
| Middle button | `MiddleClick` ✔ |
| Any keystroke (Custom) | `CustomShortcut` ✔ |
| Maximize / Minimize window | `MaximizeWindow` / `MinimizeWindow` ✔ (Win+↑ / Win+↓) |
| Snap left / right | `SnapWindowLeft` / `SnapWindowRight` ✔ (Win+← / Win+→) |
| Switch application (held Alt-Tab UI) | ✘ (no one-shot equivalent; `TaskView` is the nearest) |
| Zoom in / out / reset, Rotate | ✘ (continuous manipulation; would need Ctrl+wheel synthesis) |
| Pan (drag-scroll) | ✘ (continuous; would need per-delta scroll synthesis) |

## Implementation in openlogi-net

Gesture buttons are captured **per mouse** (one capture per configured button),
alongside the DPI/ModeShift button capture, so they work whenever the app is
running — even minimized to the tray — independent of which device page is open.

| Concern                     | Location |
|-----------------------------|----------|
| Swipe classification + state machine | `OpenLogi.Core/Gestures.cs` |
| Directions, per-button maps, master switch, defaults | `OpenLogi.Core/Actions.cs`, `OpenLogi.Core/Config.cs` (`GestureButtons`, `GesturesEnabled`) |
| Effective per-button gesture maps | `OpenLogi.Agent/Bindings.cs` (`GestureBindingsFor(config, key, button)`) |
| HID++ `0x1b04` control divert / raw-XY events | `OpenLogi.HidPP/Feature/ReprogControlsFeature.cs` |
| Live gesture capture session (one per button) | `OpenLogi.Hid/DeviceSession.cs` (`StartGestureCaptureAsync`, `GestureCandidates`) |
| Dispatch a committed gesture to its action | `OpenLogi.Agent/AgentRuntime.cs` (`DispatchGesture`) |
| Per-mouse capture wiring + Gestures panel logic | `OpenLogi.App/ViewModels/MainWindowViewModel.cs` (`StartMouseCapturesAsync`, gesture panel handlers, Ctrl+Z undo) |
| Inject the resulting OS input | `OpenLogi.Input/ActionInjector.cs` |

### Capture flow

1. On mouse (re)connect, `ActivateAgentMiceAsync` opens a persistent
   `DeviceSession` and `StartMouseCapturesAsync` starts one gesture capture per
   button in `Config.GestureButtons` (empty when the master switch is off).
   The DPI-button capture is skipped when the DPI/wheel button itself has
   gestures configured.
2. Each `StartGestureCaptureAsync(button, …)` scans the `0x1b04` control table
   for that button's candidate CID (`DeviceSession.GestureCandidates`) with the
   raw-XY flag. If found, it diverts it
   (`SetCidReporting(diverted: true, rawXy: true)`) and starts a pump that reads
   the feature's event stream.
3. Each pump drives its own `SwipeAccumulator`:
   - `DivertedButtons` events containing its CID → `Begin()` on the rising
     edge; on the falling edge, `End()` → emit **Click** if no swipe committed.
   - `DivertedRawMouseXy` events → `Accumulate(dx, dy)`; a returned direction is
     emitted immediately (mid-swipe). Raw-XY events carry no CID, but only an
     accumulator inside a hold consumes them.
4. Each emitted `GestureDirection` is dispatched by
   `AgentRuntime.DispatchGesture(configKey, button, direction)`, which resolves
   that button's per-direction map (`Bindings.GestureBindingsFor`) and runs the
   bound action through `ActionInjector`.
5. Disposing a capture (mouse disconnect, app exit, master switch off,
   capture restart) restores its control to the native mapping.

### Extending to OS-hook gestures

Middle/Back/Forward already gesture on Windows via the HID++ path, so the
OS-hook gesture route is not needed here. It would only matter for a device
whose buttons are *not* divertable over `0x1b04` (none seen so far). If that
case ever arises, the hook would need to (a) emit `MouseEvent.Moved` deltas
from `WM_MOUSEMOVE`, and (b) have `AgentRuntime` suppress the held gesture
button, run a `SwipeAccumulator` over the move deltas, and dispatch via
`Bindings.OsHookGesturesFor` (kept from the upstream macOS/Linux model, unused
on Windows). The pure pieces exist; only the hook plumbing is missing.
