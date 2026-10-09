using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Win32.Input;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Logging;
using OpenLogi.Input;

namespace OpenLogi.App.Views;

/// <summary>
/// Modal chord recorder for a mouse button's Keyboard Shortcut. Shown via
/// <c>ShowDialog&lt;KeyCombo?&gt;</c>: returns the chord on OK — <see cref="KeyCombo.Empty"/>
/// when nothing is recorded, e.g. after Clear — and <c>null</c> on Cancel (or closing the window).
///
/// While the dialog is active and waiting for a key, a low-level keyboard hook
/// swallows every key before Windows acts on it, so Esc, Tab, Alt+Tab, Win+key and
/// similar can be recorded. Once a key is recorded the hook passes keys through
/// again: Enter is OK, Esc is Cancel, and the chord only changes via Clear or the
/// modifier toggles. Without the hook (install failure) the window's own key events
/// still record whatever reaches it.
/// </summary>
public partial class ShortcutRecorderWindow : Window
{
    private readonly ShortcutRecorderViewModel? _vm;
    private KeyboardHook? _hook;
    // Read on the hook thread, written on the UI thread: swallow keys only while this
    // window is the active one and still waiting for a key.
    private volatile bool _swallow;
    // Hook thread only: keys whose press we swallowed, so their release is swallowed
    // too and the system never sees a release without a press.
    private readonly HashSet<ushort> _swallowedDown = [];

    public ShortcutRecorderWindow()
    {
        InitializeComponent();
        // Tunnel so every key is ours before a focused button or toggle reacts to it;
        // KeyUp is swallowed too, or Space/Enter releases would click whatever has focus.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
        Opened += OnOpened;
        Closed += OnClosed;
        Activated += (_, _) => UpdateSwallow();
        Deactivated += (_, _) => UpdateSwallow();
    }

    public ShortcutRecorderWindow(KeyCombo current) : this()
    {
        _vm = new ShortcutRecorderViewModel(current);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShortcutRecorderViewModel.Capturing)) UpdateSwallow();
        };
        DataContext = _vm;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        try { _hook = KeyboardHook.Start(OnHookEvent); }
        catch (HookException ex) { DiagnosticLog.Info("shortcut", $"keyboard hook unavailable, window keys only: {ex.Message}"); }
        UpdateSwallow();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _swallow = false;
        _hook?.Dispose();
        _hook = null;
    }

    private void UpdateSwallow() => _swallow = IsActive && _vm is { Capturing: true };

    /// <summary>Hook thread. Decide synchronously whether to swallow; feed the view model on the UI thread.</summary>
    private EventDisposition OnHookEvent(KeyboardHookEvent ev)
    {
        if (ev.Pressed)
        {
            if (!_swallow) return EventDisposition.PassThrough;
            _swallowedDown.Add(ev.VirtualKey);
            Dispatcher.UIThread.Post(() => _vm?.KeyDown(ev.VirtualKey));
            return EventDisposition.Suppress;
        }
        // A release: ours only if we took its press (the recorded key's own release, a
        // modifier let go while capturing). Everything else belongs to the system.
        if (!_swallowedDown.Remove(ev.VirtualKey)) return EventDisposition.PassThrough;
        Dispatcher.UIThread.Post(() => _vm?.KeyUp(ev.VirtualKey));
        return EventDisposition.Suppress;
    }

    /// <summary>Keys that reach the window: everything while no hook is installed, else only after a key is recorded.</summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (_vm is null) return;
        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (_vm.Capturing)
        {
            if (vk > 0 && vk <= ushort.MaxValue) _vm.KeyDown((ushort)vk);
            return;
        }
        // Recorded: the chord is frozen, so the keyboard drives the dialog instead.
        var bare = ((int)e.KeyModifiers & 0x0F) == 0;
        if (bare && e.Key == Key.Escape) Close(null);
        else if (bare && e.Key is Key.Enter or Key.Return) Close(_vm.Result);
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (_vm is not { Capturing: true }) return;
        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk > 0 && vk <= ushort.MaxValue) _vm.KeyUp((ushort)vk);
    }

    private void OnClear(object? sender, RoutedEventArgs e) => _vm?.Clear();
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnOk(object? sender, RoutedEventArgs e) => Close(_vm?.Result);
}
