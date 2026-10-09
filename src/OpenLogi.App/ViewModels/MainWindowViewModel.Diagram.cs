using Avalonia.Media.Imaging;
using OpenLogi.Agent;
using OpenLogi.Assets;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.App.ViewModels;

// The interactive mouse diagram: hotspot annotations, the per-button binding
// pickers, and how a picker edit is persisted.
public partial class MainWindowViewModel
{
    private void RebuildButtonBindings(string configKey)
    {
        var current = BindingMaps.BindingsFor(_config, configKey, null);
        foreach (var button in ButtonIdExtensions.All)
        {
            if (button == ButtonId.GestureButton)
            {
                _bindings[button] = BuildGestureBinding(configKey);
                continue;
            }
            var action = current.TryGetValue(button, out var a) ? a : Bindings.DefaultBinding(button);
            _bindings[button] = new ButtonBindingViewModel(button, action, ButtonBindingViewModel.Catalog,
                (b, act) => Persist(configKey, b, act), RecordShortcut);
        }
        RefreshGestureSummaries(configKey);
    }

    private async Task BuildDiagramAsync(DeviceViewModel device, string configKey)
    {
        ResolvedAsset? resolved = null;
        try { resolved = await _resolver.ResolveAsync(configKey, device.Device.Codename, device.Ext); }
        catch { /* offline / no depot — fall back to the default button set below */ }

        if (!ReferenceEquals(SelectedDevice, device)) // selection moved on while we awaited
            return;

        IReadOnlyList<Hotspot> hotspots = [];
        if (resolved?.ButtonsImagePath is { } buttonsPath && File.Exists(buttonsPath))
        {
            try
            {
                var bitmap = new Bitmap(buttonsPath);
                var pngW = bitmap.PixelSize.Width;
                var pngH = bitmap.PixelSize.Height;
                var displayH = DiagramHeightPx;
                var displayW = pngH > 0 ? DiagramHeightPx * pngW / pngH : DiagramHeightPx;

                hotspots = MouseGeometry.HotspotsForPng(resolved.Metadata, displayW, displayH, pngW, pngH);
                BuildAnnotations(hotspots, displayW, displayH, configKey);

                DiagramImage = bitmap;
            }
            catch { hotspots = []; }
        }

        // Derive the side button list from the device's actual buttons (the mapped
        // hotspots), falling back to a default mouse set when no metadata is available.
        // Ordered top-to-bottom by hotspot centre — the same order LabelYs stacks the
        // diagram's labels in — so the panel's Button dropdown reads like the left column.
        var buttonIds = hotspots.Count > 0
            ? hotspots.OrderBy(h => h.Y + h.Size / 2).Select(h => h.Id).Distinct().ToArray()
            : DefaultMouseButtons;

        Buttons.Clear();
        foreach (var id in buttonIds)
            Buttons.Add(BindingFor(id, configKey));

        // The panel's Button dropdown lists these; keep whatever is selected (or the
        // stored gesture selection) when the list is refilled.
        RebuildGestureOwnerChoices(configKey, SelectedGestureOwner?.Button ?? StoredGestureOwner(configKey));
        // Annotations may be (re)built after the panel chose its button.
        RefreshGestureHighlight();
    }

    // Leader-line layout: a left label column, a gap, then the render. Each
    // hotspot gets a marker over the render and a label connected by a polyline.
    private const double LabelColumnWidth = 168;
    private const double LabelGap = 28;
    private const double LeaderStub = 10;

    private void BuildAnnotations(IReadOnlyList<Hotspot> hotspots, double displayW, double displayH, string configKey)
    {
        var labelYs = MouseGeometry.LabelYs(hotspots, displayH);
        var mouseX = LabelColumnWidth + LabelGap;

        Annotations.Clear();
        for (var i = 0; i < hotspots.Count; i++)
        {
            var h = hotspots[i];
            var markerX = mouseX + h.X;
            var markerY = h.Y;
            var centerX = markerX + h.Size / 2;
            var centerY = markerY + h.Size / 2;
            var anchorY = labelYs[i];

            var center = new Avalonia.Point(centerX, centerY);
            var stub = new Avalonia.Point(mouseX - LeaderStub, centerY);
            var anchor = new Avalonia.Point(LabelColumnWidth, anchorY);

            Annotations.Add(new DiagramAnnotationViewModel(
                BindingFor(h.Id, configKey),
                markerX, markerY, h.Size,
                labelX: 0, labelY: anchorY - 16, labelWidth: LabelColumnWidth - 6,
                center, stub, anchor));
        }

        ImageX = mouseX;
        ImageWidth = displayW;
        DiagramWidth = mouseX + displayW;
        DiagramHeight = displayH;
    }

    private ButtonBindingViewModel BindingFor(ButtonId id, string configKey)
    {
        if (!_bindings.TryGetValue(id, out var binding))
        {
            binding = new ButtonBindingViewModel(id, Bindings.DefaultBinding(id), ButtonBindingViewModel.Catalog,
                (b, act) => Persist(configKey, b, act), RecordShortcut);
            _bindings[id] = binding;
        }
        return binding;
    }

    /// <summary>
    /// Persist a plain-click edit for <paramref name="button"/> — from the diagram's
    /// hotspot picker or the panel's Click row — and mirror it into the other one.
    /// </summary>
    private void Persist(string configKey, ButtonId button, Core.Actions.MouseAction action)
    {
        // A button with a gesture map is diverted at the device while gestures are on, so
        // its plain click is dispatched from the map's Click entry — its single binding is
        // ignored, and writing a Single here would also drop its swipe map (which is kept
        // through a global off, for when gestures come back). Route the edit into the
        // gesture Click instead; PersistGesture mirrors it into the diagram label.
        if (ClickEditRoutesToGesture(_config, configKey, button))
        {
            PersistGesture(configKey, button, GestureDirection.Click, action);
            if (SelectedGestureOwner?.Button == button)
                GestureClick?.SetSelectedSilently(action);
            return;
        }
        _config.SetBinding(configKey, button, new Binding.Single(action));
        try { _config.SaveAtomic(); }
        catch { /* keep editing fluid */ }
        // Keep the diagram label and the panel's Click row in agreement, whichever was edited.
        if (_bindings.TryGetValue(button, out var diagramBinding) && !diagramBinding.IsGesture)
            diagramBinding.SetSelectedSilently(action);
        if (SelectedGestureOwner?.Button == button)
        {
            GestureClick?.SetSelectedSilently(action);
            if (!_suppressGesturePanel)
                PushGestureUndo(); // one click edit = one undo step, like a gesture-map click
        }
    }

    /// <summary>
    /// Whether a plain-click edit for <paramref name="button"/> must be written into its
    /// gesture map's Click entry rather than a single binding. True when the button has a
    /// gesture map: it is diverted at the device while gestures are on, so its click is
    /// dispatched from the map, and a single binding would drop its swipes (also while
    /// gestures are globally off, where the maps are kept for later). Merely being
    /// selected in the panel doesn't count — a button becomes a gesture button on its
    /// first swipe edit, not by having its click changed.
    /// </summary>
    public static bool ClickEditRoutesToGesture(Config config, string configKey, ButtonId button) =>
        config.GestureButtons(configKey).Contains(button) || config.GestureBindingsFor(configKey, button).Count > 0;
}
