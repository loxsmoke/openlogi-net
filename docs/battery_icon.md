# Battery icon

This document describes how openlogi-net shows a device's battery level: the
gauge in the app, the per-device icon in the Windows system tray, where the
level comes from, how often it is refreshed, and how the feature is built.

## Gallery tile and device page

Every device that reports a battery shows a small horizontal gauge on its
gallery tile and on its device page, followed by the percentage and charging
status, for example `72% · Discharging`.

- The fill is proportional to the exact percentage.
- It is **green**, and turns **red at 15% and below**.
- It stays green while the device is charging.

The 15% point follows Logitech's own low-battery LED convention. The rule lives
in one place, `BatteryIcon.IsLow`, which the tray icon shares, so the two can
never disagree.

## Tray icon

Each paired device that reports a battery gets its own icon in the system tray.

### Picture

The icon pairs a device picture on the left with an upright battery on the
right. The two share a wall, with no gap between them.

| Device kind | Picture |
|---|---|
| Keyboard, numpad | A keyboard turned on its side: two columns of keys, with the space bar as the long key at the bottom of the right column |
| Mouse, trackball | The left half of a mouse: a closed "D" shape with the button split line and the scroll wheel on the cut edge |
| Anything else (headset, presenter, …) | No picture; the battery alone, centred |

The device picture is drawn in the taskbar's outline colour: near-white on a
dark taskbar, near-black on a light one. It never changes colour with the
battery.

### Level

The battery fills from the bottom. The level is rounded to the nearest quarter,
because only about four fill heights can be told apart at the tray's 16-pixel
size. The tooltip still gives the exact percentage.

| Reading | Drawn as |
|---|---|
| 88–100% | Full |
| 63–87% | Three quarters |
| 38–62% | Half |
| 13–37% | One quarter |
| 0–12% | Empty |

### Colour

The whole battery (outline, top terminal and fill) is drawn in one colour.

| State | Colour |
|---|---|
| Above 15% | Green `#3FB950` |
| 15% and below | Red `#DA3633` |
| Charging (any level) | Orange `#FF9A1F`, with a lightning bolt |

- Low is decided on the **actual reading**, not the rounded level. A device at
  20% and one at 15% both show a quarter-full battery, but only the 15% one is
  red.
- Below 12.5% the battery draws empty, so the red outline is the whole
  warning.
- While charging, the bolt is cut out of the orange battery in the taskbar's
  background colour, with a thin orange edge so it stays visible over the
  empty part of the battery. A charging device is never shown as low.

### Interaction

- **Tooltip:** device name, percentage and status, for example
  `MX Anywhere 3S · 72% · Discharging`. A wireless device that is asleep adds
  `· Asleep`.
- **Click:** opens the app window.
- **Right-click:** a menu with **Open** and **Quit**. Quit exits without the
  usual confirmation, since choosing it from the tray is already deliberate.

### When the icons appear

- The battery icons stay in the tray whether or not the window is open.
- With battery icons on and no device reporting a battery, the regular app icon
  takes their place, so the tray always holds an OpenLogi icon.
- When an update is offered while the window is hidden, the regular app icon is
  shown with the update message as its tooltip until the window is restored.
- A device that disappears (unplugged, switched off, out of range) loses its
  icon, which comes back when the device returns.

### Setting

**Show device battery icons in the system tray** in Settings turns the icons
on or off. It is on by default and is stored as `show_battery_in_tray` in
`config.toml`. The change takes effect when the Settings window closes.

With the setting off, the app behaves as before this feature: the regular app
icon appears only while the window is minimized to the tray.

## Where the level comes from

| Device | Source |
|---|---|
| HID++ 2.0 devices | Unified Battery feature `0x1004` |
| HID++ 2.0 devices without it (Logitech G keyboards) | Battery Voltage feature `0x1001` |
| HID++ 1.0 devices (Marathon M705 and other legacy models) | Battery registers `0x07` / `0x0D` |

## When the level is refreshed

The level is read during each device scan:

- at startup;
- when a Logitech HID device node appears or disappears, which covers a
  Bluetooth or wired device being plugged in, removed, switched off or
  switched on;
- when a wireless device wakes on its receiver.

While a device's page is open, its level is read when the page opens and then
updated live from the device's battery broadcasts.

A device switched on seconds before a scan can answer the scan only partly,
for example with no name and no battery. The scan marks such a device as
incomplete, and the app scans again after 3, 6 and 12 seconds until it answers
fully. The gallery tile and tray icon then fill in on their own.

## Limitations

- Devices whose page is not open show the level from the most recent scan.
  A device that drains slowly while the app runs is not re-read until the next
  scan.
- The tray icon is redrawn only when its picture changes (quarter, colour,
  charging or taskbar theme). Changes within a quarter only update the tooltip.

## Implementation

| Piece | Where |
|---|---|
| Shared low-battery rule and level colours | `OpenLogi.App/Controls/BatteryIcon.axaml.cs` (`IsLow`, `LevelColor`) |
| Tray icon key, rounding and drawing | `OpenLogi.App/Tray/TrayBatteryIcon.cs` (`TrayBatteryIconKey`, `TrayBatteryIcon`) |
| One tray icon per device, lifecycle and tooltips | `OpenLogi.App/Tray/TrayBatteryIcons.cs` |
| Taskbar light/dark detection | `OpenLogi.App/Tray/TaskbarTheme.cs` |
| App-icon fallback and wiring | `OpenLogi.App/Views/MainWindow.axaml.cs` (`SyncAppTrayIcon`) |
| Setting | `AppSettings.ShowBatteryInTray`, `ConfigCodec`, `SettingsViewModel` |
| Battery read during scans, incomplete-probe flag | `OpenLogi.Hid/HidInventory.cs`, `PairedDevice.ProbeIncomplete` |
| Re-scan schedule | `OpenLogi.App/ViewModels/MainWindowViewModel.Wake.cs` (`ProbeRetryDelays`) |

### Drawing

The icon is drawn with Avalonia's `DrawingContext` into a `RenderTargetBitmap`
and wrapped as a `WindowIcon`. The geometry is authored on a 32-unit grid and
scaled to the tray size, which is 16 pixels times the primary screen's scaling
(16 px at 100%, 24 px at 150%, 32 px at 200%).

| Element | Geometry on the 32-unit grid |
|---|---|
| Battery body | Rounded rectangle at (15.5, 5), 12 × 24, radius 2.5, 1.8 stroke |
| Battery terminal | Filled rectangle at (19, 2.5), 5 × 2.5, radius 1 |
| Fill | x 17.5, width 8, rising from y 27 by up to 20 units |
| Bolt | `M 24.2,7.6 L 17.8,18.2 L 21.4,18.2 L 18.9,26.4 L 25.3,15.6 L 21.7,15.6 Z` |
| Keyboard body | Rounded rectangle at (2.5, 3), 9 × 26, radius 2.5 |
| Mouse body | `M 11,3.2 C 6,3.2 2.5,7 2.5,12 L 2.5,20 C 2.5,25 6,28.8 11,28.8 Z` |

To close the gap, the keyboard shifts right by 3 units and the mouse by 3.5,
so their right edge lands on the battery's left wall. The battery shifts left
by 1 to centre the pair. Without a picture, the battery alone shifts left by
5.5 to centre it.

| Colour | Value |
|---|---|
| Outline on a dark taskbar / light taskbar | `#F2F2F2` / `#1B1B1B` |
| Taskbar background used for the bolt cut-out, dark / light | `#202020` / `#EEEEEE` |

### Caching and identity

- Everything that changes the picture forms the icon key: device picture,
  quarter, charging, low, and taskbar theme. One bitmap is rendered per key and
  reused, so battery chatter within a quarter never re-creates native icons.
- Each tray icon belongs to a device identity made of its receiver, slot, name
  and kind. A rescan creates new device objects, but icons with the same
  identity are kept and re-pointed instead of flickering off and on.
- The taskbar theme is read from `SystemUsesLightTheme` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`, which is
  the system theme the taskbar follows rather than the app theme. Icons are
  re-rendered when Windows reports a general preference change.

### Tests

| Test class | Covers |
|---|---|
| `TrayBatteryIconTests` | Picture per device kind, quarter rounding, low rule shared with the gallery, layout offsets, pixel sizes, tooltip, identity |
| `TrayBatteryIconRenderTests` | Real Win32/Skia renders with pixel checks for fill, shell, bolt and the shared wall; also saves sample PNGs to `%TEMP%\openlogi-tray-icons` |
| `IncompleteProbeInventoryTests` | A device whose feature table or battery read fails is marked incomplete |
| `ProbeRetryTests` | The 3 / 6 / 12 s re-scan schedule and when it stops |
| `ConfigTests.AppSettingsShowBatteryInTrayRoundtrips` | The setting's default and persistence |
