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
                (b, act) => Persist(configKey, b, act));
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
        var buttonIds = hotspots.Count > 0
            ? hotspots.Select(h => h.Id).Distinct().OrderBy(b => (int)b).ToArray()
            : DefaultMouseButtons;

        Buttons.Clear();
        foreach (var id in buttonIds)
            Buttons.Add(BindingFor(id, configKey));

        // Annotations may be (re)built after the gesture section chose its owner.
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
                (b, act) => Persist(configKey, b, act));
            _bindings[id] = binding;
        }
        return binding;
    }

    private void Persist(string configKey, ButtonId button, Core.Actions.MouseAction action)
    {
        // A button that drives gestures is diverted at the device, so its plain click is
        // dispatched from the gesture map's Click entry — its single binding is ignored,
        // and writing a Single here would also drop its swipe map. Route the diagram
        // picker's edit into the gesture Click instead (preserving the swipes) and mirror
        // it into the Gestures panel's Click row, so picking an action on the diagram
        // actually takes effect. Covers a button already gesturing or one just chosen as
        // the panel's gesture owner.
        if (ClickEditRoutesToGesture(_config, configKey, button, SelectedGestureOwner?.Button))
        {
            PersistGesture(configKey, button, GestureDirection.Click, action);
            if (SelectedGestureOwner?.Button == button)
                GestureClick?.SetSelectedSilently(action);
            return;
        }
        _config.SetBinding(configKey, button, new Binding.Single(action));
        try { _config.SaveAtomic(); }
        catch { /* keep editing fluid */ }
    }

    /// <summary>
    /// Whether a plain-click edit for <paramref name="button"/> (from the diagram picker)
    /// must be written into its gesture map's Click entry rather than a single binding.
    /// True when the button already drives gestures, or is the current gesture owner:
    /// such a button is diverted at the device, so its click is dispatched from the
    /// gesture map — a single binding would be ignored (and would drop the swipe map).
    /// </summary>
    public static bool ClickEditRoutesToGesture(Config config, string configKey, ButtonId button, ButtonId? selectedGestureOwner) =>
        config.GestureButtons(configKey).Contains(button) || selectedGestureOwner == button;
}
