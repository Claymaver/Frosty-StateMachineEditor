using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace StateMachineEditorPlugin
{
    // =========================================================================
    //  Name parsing
    //
    //  SWBF2 node names:  {Prefix}.{Character}.{Action...}.{Type}[ (ActorN)]
    //  e.g. A.Anakin.Attack.SeqFLOW (Actor0)
    //       3P.Rifle.Status.Electrocuted.SeqFLOW (Actor0)
    //       A.Anakin.Dodge.Back.01.SEQ.Player0
    // =========================================================================

    public enum NodeSuffix { SeqFLOW, SEQ, SF, EC, RC, Bool, Branch, Other }

    public struct ParsedName
    {
        public string     Prefix;       // "A" or "3P"
        public string     Character;    // "Anakin", "Rifle", "B1", …
        public string     ActionPath;   // segments between character and suffix
        public NodeSuffix Suffix;
        public int        ActorIndex;   // -1 if absent
        public bool       IsValid;
        public string CharacterKey => $"{Prefix}.{Character}";
    }

    public static class NameParser
    {
        private static readonly (string token, NodeSuffix kind)[] SuffixTable =
        {
            ("SeqFLOW", NodeSuffix.SeqFLOW), ("SEQFlow", NodeSuffix.SeqFLOW),
            ("SEQ",     NodeSuffix.SEQ),      ("Seq",     NodeSuffix.SEQ),
            ("SF",      NodeSuffix.SF),       ("EC",      NodeSuffix.EC),
            ("RC",      NodeSuffix.RC),       ("Bool",    NodeSuffix.Bool),
            ("Branch",  NodeSuffix.Branch),
        };
        private static readonly Regex ActorRx  = new Regex(@"\s*\(Actor(\d+)\)$",    RegexOptions.Compiled);
        private static readonly Regex PlayerRx = new Regex(@"\.(Player|Actor)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ParsedName Parse(string raw)
        {
            var p = new ParsedName { ActorIndex = -1 };
            if (string.IsNullOrWhiteSpace(raw)) return p;
            string work = raw.Trim();

            var m = ActorRx.Match(work);
            if (m.Success) { p.ActorIndex = int.Parse(m.Groups[1].Value); work = work.Substring(0, m.Index); }
            else { m = PlayerRx.Match(work); if (m.Success) { p.ActorIndex = int.Parse(m.Groups[2].Value); work = work.Substring(0, m.Index); } }

            var parts = work.Split('.');
            if (parts.Length < 2) return p;
            p.Prefix = parts[0]; p.Character = parts[1];

            int si = -1; NodeSuffix sk = NodeSuffix.Other;
            for (int i = parts.Length - 1; i >= 2; i--)
                foreach (var (tok, kind) in SuffixTable)
                    if (string.Equals(parts[i], tok, StringComparison.OrdinalIgnoreCase))
                    { si = i; sk = kind; goto done; }
            done:
            p.Suffix     = sk;
            p.ActionPath = si > 2 ? string.Join(".", parts, 2, si - 2)
                         : (parts.Length > 2 ? string.Join(".", parts, 2, parts.Length - 2) : "");
            p.IsValid = (p.Prefix == "A" || p.Prefix == "3P") && !string.IsNullOrEmpty(p.Character);
            return p;
        }
    }

    // =========================================================================
    //  Enums / detail models
    // =========================================================================

    public enum NodeSortMode { CategoryThenName, NameAscending, NameDescending, None }
    public enum GraphDetailKind { Clip, Blend, Condition, SubState, Unknown }

    public class GraphDetail
    {
        public GraphDetailKind   Kind           { get; set; }
        public string            Label          { get; set; }
        public string            SourceProperty { get; set; }
        public object            RawValue       { get; set; }
        public List<GraphDetail> Children       { get; set; } = new List<GraphDetail>();
    }

    // =========================================================================
    //  Document / node models
    // =========================================================================

    public class StateMachineDocument
    {
        public string               Name       { get; set; } = "New State Machine";
        public Guid                 AssetGuid  { get; set; } = Guid.NewGuid();
        public bool                 IsNew      { get; set; }
        public NodeSortMode         SortMode   { get; set; } = NodeSortMode.CategoryThenName;
        public List<CharacterGroup> Characters { get; set; } = new List<CharacterGroup>();
    }

    /// <summary>Groups all controllers under one character key (e.g. "A.Anakin").</summary>
    public class CharacterGroup
    {
        public string                    CharacterKey     { get; set; }  // "A.Anakin"
        public string                    DisplayName      { get; set; }  // "Anakin"
        public string                    Prefix           { get; set; }  // "A" or "3P"
        public List<StateFlowController> SeqFlows         { get; set; } = new List<StateFlowController>();
        public List<StateFlowController> OtherControllers { get; set; } = new List<StateFlowController>();
    }

    public abstract class ControllerNode
    {
        public Guid   Guid { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }

    public class StateFlowController : ControllerNode
    {
        public List<StateNode> Nodes        { get; set; } = new List<StateNode>();
        public string          CharacterKey { get; set; } = "";
        public NodeSuffix      Suffix       { get; set; } = NodeSuffix.Other;
        public string          ActionPath   { get; set; } = "";
        public object          RawObject    { get; set; }   // the EBX controller object
        public StateFlowController() { Type = "StateFlowController"; }
    }

    public class StateNode : ControllerNode
    {
        public List<Transition>  Transitions   { get; set; } = new List<Transition>();
        public string            Category      { get; set; }
        public object            RawObject     { get; set; }
        public List<GraphDetail> Details       { get; set; } = new List<GraphDetail>();
        public ParsedName        Parsed        { get; set; }
        public int               SequenceIndex { get; set; } = -1;
        public StateNode() { Type = "StateNode"; }
    }

    public class Transition
    {
        public StateNode Source    { get; set; }
        public StateNode Target    { get; set; }
        public string    Condition { get; set; }
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
        int  IEqualityComparer<object>.GetHashCode(object obj)    =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // =========================================================================
    //  Editor
    // =========================================================================

    public class NodeFrame
    {
        public string          Label       { get; set; } = "Group";
        public Color           Color       { get; set; } = Color.FromArgb(40, 100, 140, 200);
        public List<StateNode> Nodes       { get; set; } = new List<StateNode>();
        public bool            IsUserFrame { get; set; } = false;
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    [TemplatePart(Name = "PART_TreeView",   Type = typeof(TreeView))]
    [TemplatePart(Name = "PART_Canvas",     Type = typeof(Canvas))]
    [TemplatePart(Name = "PART_EditPanel",  Type = typeof(TabControl))]
    [TemplatePart(Name = "PART_SearchBox",  Type = typeof(TextBox))]
    [TemplatePart(Name = "PART_SortCombo",  Type = typeof(ComboBox))]
    public class StateMachineEditor : FrostyAssetEditor
    {
        private const string PART_TreeView  = "PART_TreeView";
        private const string PART_Canvas    = "PART_Canvas";
        private const string PART_EditPanel = "PART_EditPanel";
        private const string PART_SearchBox = "PART_SearchBox";
        private const string PART_SortCombo = "PART_SortCombo";

        private StateMachineDocument       _doc;
        private TreeView                   _tree;
        private Canvas                     _canvas;
        private TabControl                 _editPanel;
        private TextBox                    _searchBox;
        private ComboBox                   _sortCombo;
        private ScaleTransform             _scale     = new ScaleTransform(1, 1);
        private TranslateTransform         _translate = new TranslateTransform(0, 0);
        private Dictionary<object, UIElement> _nodeVisualsByObject = new Dictionary<object, UIElement>();
        private bool                       _assetLoaded = false;
        private List<StateNode>            _activeNodes = new List<StateNode>();
        private StateNode                  _selectedNode;
        private string                     _searchFilter = "";
        private bool                       _panning;
        private Point                      _panOrigin;
        private StateFlowController        _activeController;
        // Node dragging
        private Canvas                     _dragNode;
        private Point                      _dragOffset;
        private bool                       _isDraggingNode;
        private List<StateNode>            _allNodes = new List<StateNode>();
        private Dictionary<object, StateNode> _nodeByRawObj = new Dictionary<object, StateNode>(ReferenceEqualityComparer.Instance);
        // Live asset reference for in-place editing
        private EbxAsset                   _asset;
        private EbxAssetEntry              _assetEntry;
        // Root CharacterStateOwnerData object — holds AllControllerDatas list
        private object                     _ownerData;
        // Expanded node list for the active SeqFLOW (BFS result, not just ctrl.Nodes)
        private List<StateNode>            _activeChainNodes = new List<StateNode>();
        // Node layout persistence: keyed by "assetName|controllerName|nodeName"
        private readonly Dictionary<string, Point> _savedPositions = new Dictionary<string, Point>();
        private string _savedLayoutKey => _doc?.Name + "|" + (_activeController?.Name ?? "");
        // ── Frosty-consistent color palette ──────────────────────────────────────
        // Backgrounds
        static readonly SolidColorBrush BrushWindowBg     = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)); // #141414
        static readonly SolidColorBrush BrushPanelBg      = new SolidColorBrush(Color.FromRgb(0x29, 0x29, 0x29)); // #292929
        static readonly SolidColorBrush BrushInputBg      = new SolidColorBrush(Color.FromRgb(0x29, 0x29, 0x29)); // #292929
        static readonly SolidColorBrush BrushCardBg       = new SolidColorBrush(Color.FromRgb(0x29, 0x29, 0x29)); // #292929
        static readonly SolidColorBrush BrushGroupHeaderBg= new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)); // #303030
        // Borders
        static readonly SolidColorBrush BrushBorder       = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x3F)); // #3F3F3F
        static readonly SolidColorBrush BrushDivider      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)); // #333333
        // Controls / Buttons
        static readonly SolidColorBrush BrushControl      = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45)); // #454545
        static readonly SolidColorBrush BrushControlHover = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)); // #707070
        static readonly SolidColorBrush BrushControlPress = new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x58)); // #585858
        // Text
        static readonly SolidColorBrush BrushText         = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8)); // #F8F8F8
        static readonly SolidColorBrush BrushTextDim      = new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x86)); // #868686
        static readonly SolidColorBrush BrushTextMuted    = new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA)); // #DADADA
        // Semantic
        static readonly SolidColorBrush BrushPositive     = new SolidColorBrush(Color.FromRgb(0x20, 0x80, 0x20)); // #208020
        static readonly SolidColorBrush BrushNegative     = new SolidColorBrush(Color.FromRgb(0x80, 0x20, 0x20)); // #802020
        static readonly SolidColorBrush BrushInfo         = new SolidColorBrush(Color.FromRgb(0x00, 0x50, 0x80)); // #005080
        // Selection / accent (subdued — no bright blue)
        static readonly SolidColorBrush BrushAccent       = new SolidColorBrush(Color.FromRgb(0x6C, 0x6C, 0x6C)); // #6C6C6C
        static readonly SolidColorBrush BrushSelected     = new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x58)); // #585858

        // View toggle (Graph ↔ EBX Properties)
        private Frosty.Core.Controls.FrostyPropertyGrid _propertyGrid;
        private UIElement                  _graphView;
        private Button                     _viewToggle;
        private bool                       _propertyViewActive;
        // Frames (node groups)
        private readonly List<NodeFrame>   _frames = new List<NodeFrame>();
        private NodeFrame                  _dragFrame;
        private Point                      _dragFrameOffset;
        private Border                     _dragFrameVisual;
        // Box selection
        private bool                       _boxSelecting;
        private Point                      _boxOrigin;
        private Border                     _boxRect;
        private readonly HashSet<StateNode> _selectedNodes = new HashSet<StateNode>();
        // Wire drag (drag from output port to input port)
        private bool                       _wiringActive;
        private StateNode                  _wireFromNode;
        private Path                       _wireDragPath;     // live bezier preview
        private Point                      _wireStartCanvas;  // output port position in canvas space
        private StateNode                  _wireHoverTarget;  // node the mouse is over (for highlight)
        // ── Category order ────────────────────────────────────────────────────
        private static readonly List<string> CategoryOrder = new List<string>
        {
            "Idle", "Locomotion", "Jump", "Movement",
            "Lightsaber", "Blaster", "Melee", "Grenade",
            "Status", "Emote", "Death", "Other"
        };

        // ── Deep-scan tables ──────────────────────────────────────────────────
        private static readonly (string keyword, GraphDetailKind kind)[] DetailKeywords =
        {
            ("clip",GraphDetailKind.Clip),("animation",GraphDetailKind.Clip),("anim",GraphDetailKind.Clip),
            ("blend",GraphDetailKind.Blend),("blendtree",GraphDetailKind.Blend),
            ("condition",GraphDetailKind.Condition),("signal",GraphDetailKind.Condition),
            ("event",GraphDetailKind.Condition),("trigger",GraphDetailKind.Condition),
            ("substate",GraphDetailKind.SubState),("childstate",GraphDetailKind.SubState),
            ("nested",GraphDetailKind.SubState),
        };
        private static readonly string[] ComplexTypeFragments =
        { "Clip","Blend","Condition","State","Transition","Animation","Anim","Signal","Data","Node" };
        private static readonly HashSet<string> SkippedPropertyNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "__Id","__InstanceGuid","__Type","Owner","Name","n" };

        static StateMachineEditor()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(StateMachineEditor),
                new FrameworkPropertyMetadata(typeof(StateMachineEditor)));
        }

        public StateMachineEditor(ILogger inLogger) : base(inLogger) { }

        // ── Template ──────────────────────────────────────────────────────────

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _tree      = GetTemplateChild(PART_TreeView)  as TreeView;
            _canvas    = GetTemplateChild(PART_Canvas)    as Canvas;
            _editPanel = GetTemplateChild(PART_EditPanel) as TabControl;
            _searchBox = GetTemplateChild(PART_SearchBox) as TextBox;
            _sortCombo = GetTemplateChild(PART_SortCombo) as ComboBox;

            if (_canvas != null)
            {
                _canvas.RenderTransform = new TransformGroup { Children = { _scale, _translate } };
                DrawGrid();
                BuildGraphToolbar();

                // Zoom toward cursor
                _canvas.MouseWheel += (s, e) =>
                {
                    double factor = e.Delta > 0 ? 1.12 : 1.0 / 1.12;
                    double newS   = Math.Max(0.1, Math.Min(5.0, _scale.ScaleX * factor));
                    var    parent = _canvas.Parent as UIElement;
                    if (parent != null)
                    {
                        Point  mouse  = e.GetPosition(parent);
                        _translate.X  = mouse.X - newS / _scale.ScaleX * (mouse.X - _translate.X);
                        _translate.Y  = mouse.Y - newS / _scale.ScaleX * (mouse.Y - _translate.Y);
                    }
                    _scale.ScaleX = newS;
                    _scale.ScaleY = newS;
                    e.Handled     = true;
                };

                // Canvas-level mouse handling: node drag + middle-mouse pan
                var panTarget = _canvas.Parent as UIElement ?? (UIElement)_canvas;

                panTarget.MouseLeftButtonDown += (s, e) =>
                {
                    // Only start box-select if clicking empty canvas (no node/frame target)
                    if (_dragNode != null || _dragFrame != null) return;
                    _boxSelecting = true;
                    _boxOrigin    = e.GetPosition(_canvas);
                    // Create rubber-band rectangle
                    _boxRect = new Border
                    {
                        Background      = new SolidColorBrush(Color.FromArgb(30, 0x45, 0x45, 0x45)),
                        BorderBrush     = BrushControl,
                        BorderThickness = new Thickness(1),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(_boxRect, _boxOrigin.X);
                    Canvas.SetTop (_boxRect, _boxOrigin.Y);
                    _canvas.Children.Add(_boxRect);
                    panTarget.CaptureMouse();
                    e.Handled = true;
                };

                panTarget.MouseMove += (s, e) =>
                {
                    // Wire drag — draw live bezier from output port to cursor
                    if (_wiringActive && _wireDragPath != null && e.LeftButton == MouseButtonState.Pressed)
                    {
                        var mp  = e.GetPosition(_canvas);
                        double cx = Math.Abs(mp.X - _wireStartCanvas.X) * 0.55;
                        var seg = new BezierSegment(
                            new Point(_wireStartCanvas.X + cx, _wireStartCanvas.Y),
                            new Point(mp.X - cx, mp.Y),
                            new Point(mp.X, mp.Y), true);
                        var fig = new PathFigure { StartPoint = _wireStartCanvas, IsClosed = false };
                        fig.Segments.Add(seg);
                        var geo = new PathGeometry(); geo.Figures.Add(fig);
                        _wireDragPath.Data = geo;

                        // Hit-test for hover target (port MouseEnter can't fire with capture)
                        _wireHoverTarget = null;
                        foreach (var node in _activeNodes)
                        {
                            if (node == _wireFromNode || node.RawObject == null) continue;
                            if (!_nodeVisualsByObject.TryGetValue(node.RawObject, out var vis)) continue;
                            double nx = Canvas.GetLeft(vis), ny = Canvas.GetTop(vis);
                            // Near input port (left edge)
                            if (mp.X >= nx - 12 && mp.X <= nx + NodeW / 2 &&
                                mp.Y >= ny && mp.Y <= ny + NodeH)
                            { _wireHoverTarget = node; break; }
                        }

                        // Green if over a valid target, yellow if dragging freely
                        _wireDragPath.Stroke = _wireHoverTarget != null
                            ? new SolidColorBrush(Color.FromRgb(80, 200, 80))
                            : new SolidColorBrush(Color.FromRgb(180, 180, 60));
                        _wireDragPath.StrokeDashArray = _wireHoverTarget != null
                            ? null
                            : new DoubleCollection { 6, 3 };
                        e.Handled = true;
                        return;
                    }

                    // Node drag
                    if (_dragNode != null && e.LeftButton == MouseButtonState.Pressed)
                    {
                        var mp  = e.GetPosition(_canvas);
                        // Compute delta from last position
                        double newX = mp.X - _dragOffset.X;
                        double newY = mp.Y - _dragOffset.Y;
                        double dx   = newX - Canvas.GetLeft(_dragNode);
                        double dy   = newY - Canvas.GetTop (_dragNode);
                        // Move primary node
                        Canvas.SetLeft(_dragNode, newX);
                        Canvas.SetTop (_dragNode, newY);
                        // Move all other selected nodes by same delta
                        var primaryNode = _dragNode.Tag as StateNode;
                        foreach (var sn in _selectedNodes)
                        {
                            if (sn == primaryNode || sn.RawObject == null) continue;
                            if (_nodeVisualsByObject.TryGetValue(sn.RawObject, out var sv) && sv is Canvas sc)
                            {
                                Canvas.SetLeft(sc, Canvas.GetLeft(sc) + dx);
                                Canvas.SetTop (sc, Canvas.GetTop (sc) + dy);
                            }
                        }
                        _isDraggingNode = true;
                        DrawAllConnections(_activeController != null &&
                            (_activeController.Suffix == NodeSuffix.SeqFLOW ||
                             _activeController.Suffix == NodeSuffix.SEQ));
                        e.Handled = true;
                        return;
                    }
                    if (e.LeftButton != MouseButtonState.Pressed) _dragNode = null;

                    // Frame drag
                    if (_dragFrame != null && e.LeftButton == MouseButtonState.Pressed && _dragFrameVisual != null)
                    {
                        var mp  = e.GetPosition(_canvas);
                        double dx = mp.X - _dragFrameOffset.X;
                        double dy = mp.Y - _dragFrameOffset.Y;
                        _dragFrameOffset = mp;  // update for next frame
                        _dragFrame.X += dx; _dragFrame.Y += dy;
                        Canvas.SetLeft(_dragFrameVisual, _dragFrame.X);
                        Canvas.SetTop (_dragFrameVisual, _dragFrame.Y);
                        foreach (var node in _dragFrame.Nodes)
                        {
                            if (node.RawObject == null) continue;
                            if (_nodeVisualsByObject.TryGetValue(node.RawObject, out var sv) && sv is Canvas sc)
                            {
                                Canvas.SetLeft(sc, Canvas.GetLeft(sc) + dx);
                                Canvas.SetTop (sc, Canvas.GetTop (sc) + dy);
                            }
                        }
                        DrawAllConnections(_activeController != null &&
                            (_activeController.Suffix == NodeSuffix.SeqFLOW ||
                             _activeController.Suffix == NodeSuffix.SEQ));
                        e.Handled = true;
                        return;
                    }
                    if (e.LeftButton != MouseButtonState.Pressed) { _dragFrame = null; _dragFrameVisual = null; }

                    // Box selection rubber band
                    if (_boxSelecting && e.LeftButton == MouseButtonState.Pressed && _boxRect != null)
                    {
                        var cur = e.GetPosition(_canvas);
                        double x = Math.Min(cur.X, _boxOrigin.X), y = Math.Min(cur.Y, _boxOrigin.Y);
                        double w = Math.Abs(cur.X - _boxOrigin.X), h = Math.Abs(cur.Y - _boxOrigin.Y);
                        Canvas.SetLeft(_boxRect, x); Canvas.SetTop(_boxRect, y);
                        _boxRect.Width = w; _boxRect.Height = h;
                        e.Handled = true;
                        return;
                    }

                    // Middle-mouse pan
                    if (!_panning) return;
                    var panPos    = e.GetPosition(panTarget);
                    _translate.X += panPos.X - _panOrigin.X;
                    _translate.Y += panPos.Y - _panOrigin.Y;
                    _panOrigin    = panPos;
                    e.Handled     = true;
                };

                panTarget.MouseLeftButtonUp += (s, e) =>
                {
                    if (_wiringActive)
                    {
                        // Hit-test: find which node container (if any) is under the cursor
                        // Since canvas has mouse capture, port MouseUp never fires — we do it here
                        if (_wireHoverTarget != null && _wireHoverTarget != _wireFromNode)
                        {
                            CommitWire(_wireFromNode, _wireHoverTarget);
                        }
                        else
                        {
                            // Released on empty canvas — check if cursor is over any input port
                            var mp = e.GetPosition(_canvas);
                            StateNode dropTarget = null;
                            foreach (var node in _activeNodes)
                            {
                                if (node == _wireFromNode || node.RawObject == null) continue;
                                if (!_nodeVisualsByObject.TryGetValue(node.RawObject, out var vis)) continue;
                                double nx = Canvas.GetLeft(vis), ny = Canvas.GetTop(vis);
                                // Input port is at left edge, mid-height — expand hit zone to full left half of node
                                var nodeRect = new Rect(nx - 10, ny, NodeW / 2, NodeH);
                                if (nodeRect.Contains(mp)) { dropTarget = node; break; }
                            }
                            if (dropTarget != null)
                                CommitWire(_wireFromNode, dropTarget);
                            else
                                CancelWire();
                        }
                        e.Handled = true;
                        return;
                    }

                    // Save moved node positions
                    if (_isDraggingNode && _dragNode != null)
                    {
                        var movedNode = _dragNode.Tag as StateNode;
                        if (movedNode?.Name != null)
                            _savedPositions[_savedLayoutKey + "|" + movedNode.Name] = new Point(Canvas.GetLeft(_dragNode), Canvas.GetTop(_dragNode));
                        // Save all multi-selected nodes too
                        foreach (var sn in _selectedNodes)
                        {
                            if (sn.RawObject == null || sn.Name == null) continue;
                            if (_nodeVisualsByObject.TryGetValue(sn.RawObject, out var sv) && sv is Canvas sc)
                                _savedPositions[_savedLayoutKey + "|" + sn.Name] = new Point(Canvas.GetLeft(sc), Canvas.GetTop(sc));
                        }
                    }
                    if (_boxSelecting && _boxRect != null)
                    {
                        // Finalise box selection
                        double bx = Canvas.GetLeft(_boxRect), by = Canvas.GetTop(_boxRect);
                        double bw = _boxRect.Width, bh = _boxRect.Height;
                        var selRect = new Rect(bx, by, bw, bh);
                        _selectedNodes.Clear();
                        foreach (var node in _activeNodes)
                        {
                            if (node.RawObject == null) continue;
                            if (!_nodeVisualsByObject.TryGetValue(node.RawObject, out var vis)) continue;
                            double nx = Canvas.GetLeft(vis), ny = Canvas.GetTop(vis);
                            if (selRect.IntersectsWith(new Rect(nx, ny, NodeW, NodeH)))
                                _selectedNodes.Add(node);
                        }
                        _canvas.Children.Remove(_boxRect);
                        _boxRect = null;
                        _boxSelecting = false;
                        panTarget.ReleaseMouseCapture();
                        // Highlight selected nodes
                        HighlightSelectedNodes();
                        e.Handled = true;
                        return;
                    }
                    _dragNode = null; _isDraggingNode = false;
                    _dragFrame = null; _dragFrameVisual = null;
                };

                panTarget.MouseDown += (s, e) =>
                {
                    if (e.MiddleButton != MouseButtonState.Pressed) return;
                    _panning = true; _panOrigin = e.GetPosition(panTarget);
                    panTarget.CaptureMouse(); e.Handled = true;
                };
                panTarget.MouseUp += (s, e) =>
                {
                    if (_panning && e.MiddleButton == MouseButtonState.Released)
                    { _panning = false; panTarget.ReleaseMouseCapture(); e.Handled = true; }
                };
                panTarget.MouseRightButtonUp += (s, e) =>
                {
                    ShowCanvasContextMenu(e.GetPosition(_canvas));
                    e.Handled = true;
                };
            }

            if (_tree != null)
            {
                _tree.SelectedItemChanged += OnTreeSelectionChanged;
                _tree.MouseRightButtonUp  += OnTreeRightClick;
            }

            // Escape cancels wire drag
            var canvasBorder2 = GetTemplateChild("PART_CanvasBorder") as UIElement;
            if (canvasBorder2 != null)
            {
                canvasBorder2.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Escape && _wiringActive) { CancelWire(); e.Handled = true; }
                };
                canvasBorder2.Focusable = true;
            }

            if (_searchBox != null)
            {
                // Watermark placeholder
                _searchBox.GotFocus  += (s, e) => { if (_searchBox.Tag as string == "placeholder") { _searchBox.Text = ""; _searchBox.Foreground = BrushText; _searchBox.Tag = null; } };
                _searchBox.LostFocus += (s, e) => { if (string.IsNullOrEmpty(_searchBox.Text)) { _searchBox.Text = "Search..."; _searchBox.Foreground = BrushTextDim; _searchBox.Tag = "placeholder"; } };
                _searchBox.Text = "Search...";
                _searchBox.Foreground = BrushTextDim;
                _searchBox.Tag = "placeholder";
                _searchBox.TextChanged += (s, e) =>
                {
                    if (_searchBox.Tag as string == "placeholder") return;
                    _searchFilter = _searchBox.Text?.Trim() ?? "";
                    PopulateTree();
                };
            }

            if (_sortCombo != null)
                _sortCombo.SelectionChanged += (s, e) =>
                {
                    if (_doc == null) return;
                    switch (_sortCombo.SelectedIndex)
                    {
                        case 0: _doc.SortMode = NodeSortMode.CategoryThenName; break;
                        case 1: _doc.SortMode = NodeSortMode.NameAscending;   break;
                        case 2: _doc.SortMode = NodeSortMode.NameDescending;  break;
                        default: _doc.SortMode = NodeSortMode.None;           break;
                    }
                    if (_doc.Characters != null)
                        foreach (var cg in _doc.Characters)
                            foreach (var ctrl in cg.SeqFlows.Concat(cg.OtherControllers))
                                ctrl.Nodes = SortNodes(ctrl.Nodes, _doc.SortMode);
                    PopulateTree();
                    RenderGraph();
                };

            // View toggle (Graph ↔ EBX Properties)
            _propertyGrid = GetTemplateChild("PART_PropertyGrid") as Frosty.Core.Controls.FrostyPropertyGrid;
            _graphView    = GetTemplateChild("PART_GraphView")    as UIElement;
            _viewToggle   = GetTemplateChild("PART_ViewToggle")   as Button;

            if (_viewToggle != null)
            {
                _viewToggle.Click += (s, e) =>
                {
                    if (_propertyViewActive)
                        SwitchToGraphView();
                    else
                        SwitchToPropertyView();
                };
            }

            // Previews browser button
            var previewsBtn = GetTemplateChild("PART_PreviewsBtn") as Button;
            if (previewsBtn != null)
                previewsBtn.Click += (s, e) => ShowAnimationBrowser();

            // Default to property view
            if (_propertyGrid != null && _graphView != null)
                SwitchToPropertyView();

            if (_assetLoaded)
            {
                PopulateTree();
                if (!_propertyViewActive)
                    ShowPlaceholder("Select a controller or character in the explorer.");
            }
        }

        private void SwitchToPropertyView()
        {
            if (_propertyGrid == null || _graphView == null) return;
            _propertyViewActive = true;
            _graphView.Visibility    = Visibility.Collapsed;
            _propertyGrid.Visibility = Visibility.Visible;
            if (_asset != null)
                _propertyGrid.SetClass(_asset.RootObject);
            if (_viewToggle != null)
                _viewToggle.Content = "Graph";
        }

        private void SwitchToGraphView()
        {
            if (_propertyGrid == null || _graphView == null) return;
            _propertyViewActive = false;
            _propertyGrid.Visibility = Visibility.Collapsed;
            _graphView.Visibility    = Visibility.Visible;
            if (_viewToggle != null)
                _viewToggle.Content = "Properties";
            // Build toolbar on first switch if it wasn't created yet (collapsed parent at startup)
            if (_toolbar == null && _canvas != null)
                BuildGraphToolbar();
        }

        // Grid is drawn in Generic.xaml as a DrawingBrush on the parent Border
        private void DrawGrid() { }  // kept for call-site compatibility

        // ── Asset loading ─────────────────────────────────────────────────────

        protected override EbxAsset LoadAsset(EbxAssetEntry entry)
        {
            _doc = new StateMachineDocument { Name = entry.Name };
            _assetEntry = entry;
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            _asset = asset;
            if (asset == null) { App.Logger.LogError("StateMachineEditor: failed to load EBX"); return null; }
            App.Logger.Log($"StateMachineEditor: {asset.Objects.Count()} objects");
            ParseStateMachine(asset);
            _assetLoaded = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                PopulateTree();
                ShowPlaceholder("Select a controller or character in the explorer to load the graph.");
            }));
            return asset;
        }

        // =========================================================================
        //  Parse
        // =========================================================================

        private void ParseStateMachine(EbxAsset asset)
        {
            _doc.Characters = new List<CharacterGroup>();
            var allControllers = new List<StateFlowController>();
            var ctrlByObj      = new Dictionary<object, StateFlowController>(ReferenceEqualityComparer.Instance);
            var nodeByObject   = new Dictionary<object, StateNode>          (ReferenceEqualityComparer.Instance);
            var charGroups     = new Dictionary<string, CharacterGroup>();

            // ── Pass 0: find root CharacterStateOwnerData (holds AllControllerDatas) ──
            _ownerData = null;
            foreach (var obj in asset.Objects)
            {
                if (obj.GetType().Name == "CharacterStateOwnerData")
                {
                    _ownerData = obj;
                    break;
                }
            }
            if (_ownerData != null)
            {
                var acdProp = _ownerData.GetType().GetProperty("AllControllerDatas");
                var acdList = acdProp?.GetValue(_ownerData) as System.Collections.IList;
                App.Logger.Log($"StateMachineEditor: found CharacterStateOwnerData with AllControllerDatas ({acdList?.Count ?? 0} entries)");
            }
            else
                App.Logger.LogWarning("StateMachineEditor: CharacterStateOwnerData not found — new nodes will not be registered in AllControllerDatas");

            // ── Pass 1: controllers - read name + collect Subjects list ───────────────
            // The controller has a Subjects list of PointerRef → CharacterStateStateFlowNodeControllerData
            // We scan all nodes first, build a GUID map, then assign via Subjects
            var allNodeObjs = new List<object>();
            foreach (var obj in asset.Objects)
            {
                if (obj.GetType().Name == "CharacterStateStateFlowControllerData")
                {
                    string rawName = GetRawName(obj);
                    var parsed = NameParser.Parse(rawName);
                    var ctrl = new StateFlowController
                    {
                        CharacterKey = parsed.IsValid ? parsed.CharacterKey : "",
                        Suffix       = parsed.Suffix,
                        ActionPath   = parsed.ActionPath,
                        Name         = parsed.IsValid
                            ? (!string.IsNullOrEmpty(parsed.ActionPath) ? $"{parsed.ActionPath} [{parsed.Suffix}]" : parsed.Suffix.ToString())
                            : rawName,
                    };
                    ctrl.RawObject = obj;
                    allControllers.Add(ctrl);
                    ctrlByObj[obj] = ctrl;
                }
                else if (obj.GetType().Name == "CharacterStateStateFlowNodeControllerData")
                {
                    allNodeObjs.Add(obj);
                }
            }
            App.Logger.Log($"StateMachineEditor: {allControllers.Count} controllers");

            // ── Pass 2: build node objects, keyed by raw object reference ────────────
            var nodeByRawObj = new Dictionary<object, StateNode>(ReferenceEqualityComparer.Instance);
            foreach (var obj in allNodeObjs)
            {
                string rawName = GetRawName(obj);
                var parsed = NameParser.Parse(rawName);
                var node = new StateNode
                {
                    Name     = rawName,
                    RawObject = obj,
                    Parsed   = parsed,
                    Category = DetermineCategory(rawName, parsed)
                };
                nodeByRawObj[obj] = node;
                nodeByObject[obj] = node;
            }

            // ── Pass 3: assign nodes to controllers via Subjects ──────────────────────
            int assignedCount = 0;
            foreach (var kvp in ctrlByObj)
            {
                object ctrlObj = kvp.Key;
                StateFlowController ctrl = kvp.Value;
                try
                {
                    var subjectsProp = ctrlObj.GetType().GetProperty("Subjects");
                    if (subjectsProp == null) continue;
                    var subjects = subjectsProp.GetValue(ctrlObj) as System.Collections.IEnumerable;
                    if (subjects == null) continue;
                    foreach (var subjectRef in subjects)
                    {
                        if (subjectRef == null) continue;
                        try
                        {
                            object nodeObj = ResolvePointerRef(subjectRef);
                            if (nodeObj == null) continue;
                            if (nodeByRawObj.TryGetValue(nodeObj, out StateNode node))
                            {
                                ctrl.Nodes.Add(node);
                                assignedCount++;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            App.Logger.Log($"StateMachineEditor: {nodeByObject.Count} state nodes ({assignedCount} assigned to controllers)");
            App.Logger.Log($"StateMachineEditor: {nodeByObject.Count} state nodes");

            // ── Pass 3: GUID map ───────────────────────────────────────────────────
            var nodeByGuid = new Dictionary<Guid, StateNode>();
            foreach (var kvp in nodeByObject)
            {
                try
                {
                    // __InstanceGuid is AssetClassGuid, use GetInstanceGuid() method
                    try
                    {
                        dynamic d = kvp.Key;
                        var acg = d.GetInstanceGuid();
                        if ((bool)acg.IsExported)
                        {
                            nodeByGuid[(Guid)acg.ExportedGuid] = kvp.Value;
                            continue;
                        }
                    }
                    catch { }
                    // Fallback: try __InstanceGuid property directly
                    var gp = kvp.Key.GetType().GetProperty("__InstanceGuid");
                    var gv = gp?.GetValue(kvp.Key);
                    if (gv == null) continue;
                    if (gv is Guid g) { nodeByGuid[g] = kvp.Value; continue; }
                    // Try as AssetClassGuid struct
                    var isExpProp = gv.GetType().GetProperty("IsExported");
                    var expGuidProp = gv.GetType().GetProperty("ExportedGuid");
                    if (isExpProp != null && expGuidProp != null && (bool)isExpProp.GetValue(gv))
                    {
                        nodeByGuid[(Guid)expGuidProp.GetValue(gv)] = kvp.Value;
                        continue;
                    }
                    if (Guid.TryParse(gv.ToString(), out Guid pg)) nodeByGuid[pg] = kvp.Value;
                }
                catch { }
            }

            // ── Pass 4: transitions ───────────────────────────────────────────────
            int tc = 0;
            foreach (var n in nodeByObject.Values) tc += ScanTransitions(n, nodeByGuid, nodeByRawObj);
            App.Logger.Log($"StateMachineEditor: {tc} transitions");

            // ── Pass 5: deep scan ─────────────────────────────────────────────────
            foreach (var n in nodeByObject.Values) DeepScanNode(n);

            // ── Pass 6: sort nodes within each controller ─────────────────────────
            foreach (var ctrl in allControllers) ctrl.Nodes = SortNodes(ctrl.Nodes, _doc.SortMode);

            // ── Pass 7: group controllers by character ────────────────────────────
            foreach (var ctrl in allControllers)
            {
                if (string.IsNullOrEmpty(ctrl.CharacterKey)) ctrl.CharacterKey = "?.Unknown";
                if (!charGroups.TryGetValue(ctrl.CharacterKey, out var cg))
                {
                    var pts = ctrl.CharacterKey.Split('.');
                    cg = new CharacterGroup
                    {
                        CharacterKey = ctrl.CharacterKey,
                        DisplayName  = pts.Length >= 2 ? pts[1] : ctrl.CharacterKey,
                        Prefix       = pts.Length >= 1 ? pts[0] : "?"
                    };
                    charGroups[ctrl.CharacterKey] = cg;
                    _doc.Characters.Add(cg);
                }
                if (ctrl.Suffix == NodeSuffix.SeqFLOW || ctrl.Suffix == NodeSuffix.SEQ)
                    cg.SeqFlows.Add(ctrl);
                else
                    cg.OtherControllers.Add(ctrl);
            }

            foreach (var cg in _doc.Characters)
            {
                cg.SeqFlows         = cg.SeqFlows.OrderBy(c => c.ActionPath, StringComparer.OrdinalIgnoreCase).ToList();
                cg.OtherControllers = cg.OtherControllers.OrderBy(c => c.Name,       StringComparer.OrdinalIgnoreCase).ToList();
            }
            _doc.Characters = _doc.Characters
                .OrderBy(c => c.Prefix == "A" ? 0 : c.Prefix == "3P" ? 1 : 2)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            App.Logger.Log($"StateMachineEditor: {_doc.Characters.Count} character groups");

            // Keep full node pool and raw-object map for transition-chain expansion
            _allNodes = nodeByObject.Values.ToList();
            _nodeByRawObj = nodeByRawObj;
        }

        /// <summary>
        /// Unwraps a PointerRef (Frosty EBX internal reference) to the actual object.
        /// PointerRef is a struct with Type (int) and Internal (object).
        /// Type == 1 means internal reference. Uses dynamic to avoid reflection issues with value types.
        /// </summary>
        private static object ResolvePointerRef(object val)
        {
            if (val == null) return null;
            try
            {
                var t = val.GetType();
                var typeProp     = t.GetProperty("Type");
                var internalProp = t.GetProperty("Internal");
                if (typeProp == null || internalProp == null) return null;
                // Type is PointerRefType enum - ToString() returns "Internal" (not "1")
                var typeStr = typeProp.GetValue(val)?.ToString();
                if (typeStr == "Internal" || typeStr == "1")
                    return internalProp.GetValue(val);
            }
            catch { }
            return null;
        }

        // =========================================================================
        //  Tree
        // =========================================================================

        private void PopulateTree()
        {
            if (_tree == null) return;
            _tree.Items.Clear();
            if (_doc?.Characters == null) return;

            bool filtering = !string.IsNullOrEmpty(_searchFilter);
            string f = _searchFilter;

            // Match a character group if its name, any controller name, or any node name matches
            bool CharMatches(CharacterGroup cg) =>
                (cg.DisplayName ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                cg.SeqFlows.Any(c => (c.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     (c.ActionPath ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ||
                cg.OtherControllers.Any(c => (c.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var cg in _doc.Characters)
            {
                // When filtering, skip characters that don't match at all
                if (filtering && !CharMatches(cg)) continue;

                var charItem = new TreeViewItem
                {
                    Header = FormatCharHeader(cg), Foreground = CharacterColor(cg.Prefix),
                    FontWeight = FontWeights.Bold, Tag = cg, IsExpanded = filtering
                };

                // SeqFlows sub-section - show all controllers for matching character
                if (cg.SeqFlows.Count > 0)
                {
                    var sfRoot = new TreeViewItem
                    {
                        Header = $"SeqFlows  ({cg.SeqFlows.Count})",
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 220, 255)),
                        FontWeight = FontWeights.SemiBold,
                        Tag = cg.SeqFlows.SelectMany(c => c.Nodes).ToList(),
                        IsExpanded = filtering
                    };
                    foreach (var ctrl in cg.SeqFlows)
                    { var ci = MakeControllerItem(ctrl); if (filtering) ci.IsExpanded = true; sfRoot.Items.Add(ci); }
                    charItem.Items.Add(sfRoot);
                }

                // Other controllers sub-section
                if (cg.OtherControllers.Count > 0)
                {
                    var otRoot = new TreeViewItem
                    {
                        Header = $"Controllers  ({cg.OtherControllers.Count})",
                        Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 180)),
                        Tag = cg.OtherControllers.SelectMany(c => c.Nodes).ToList(),
                        IsExpanded = filtering
                    };
                    foreach (var ctrl in cg.OtherControllers)
                    { var ci = MakeControllerItem(ctrl); if (filtering) ci.IsExpanded = true; otRoot.Items.Add(ci); }
                    charItem.Items.Add(otRoot);
                }

                _tree.Items.Add(charItem);
            }
        }

        private TreeViewItem MakeControllerItem(StateFlowController ctrl, List<StateNode> nodes = null)
        {
            var dn = nodes ?? ctrl.Nodes;
            var ci = new TreeViewItem
            {
                Header = string.IsNullOrEmpty(ctrl.Name) ? "(unnamed)" : ctrl.Name,
                Foreground = SuffixColor(ctrl.Suffix), Tag = ctrl
            };
            foreach (var g in dn.GroupBy(n => n.Category ?? "Other").OrderBy(g => CategoryPriority(g.Key)))
            {
                var cn  = g.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var cat = new TreeViewItem
                {
                    Header = $"[{g.Key}]  ({cn.Count})",
                    Foreground = CategoryColor(g.Key), FontWeight = FontWeights.SemiBold, Tag = cn
                };
                foreach (var n in cn) cat.Items.Add(BuildStateNodeTreeItem(n));
                ci.Items.Add(cat);
            }
            return ci;
        }

        private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!(e.NewValue is TreeViewItem item)) return;
            // Auto-switch to graph view when selecting anything in the tree
            if (_propertyViewActive && (item.Tag is StateFlowController || item.Tag is List<StateNode> || item.Tag is CharacterGroup || item.Tag is StateNode))
                SwitchToGraphView();
            switch (item.Tag)
            {
                case StateFlowController ctrl:
                    _activeController = ctrl;
                    bool isSeq = ctrl.Suffix == NodeSuffix.SeqFLOW || ctrl.Suffix == NodeSuffix.SEQ;
                    ShowToolbar(isSeq);
                    if (isSeq)
                        LoadNodesToGraph(ExpandFlowChain(ctrl.Nodes), ctrl.Name);
                    else
                        LoadNodesToGraph(ctrl.Nodes, ctrl.Name);
                    break;
                case List<StateNode> nodes:
                    _activeController = null;
                    ShowToolbar(false);
                    LoadNodesToGraph(nodes, item.Header?.ToString() ?? ""); break;
                case CharacterGroup cg:
                    _activeController = null;
                    ShowToolbar(false);
                    LoadNodesToGraph(cg.SeqFlows.Concat(cg.OtherControllers).SelectMany(c => c.Nodes).ToList(), cg.DisplayName); break;
                case StateNode node:
                    SelectNode(node); break;
            }
        }

        // ─── Tree right-click → context menu ─────────────────────────────────────
        private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
        {
            if (_tree == null) return;
            // Walk visual tree to find TreeViewItem under cursor
            var hit = e.OriginalSource as DependencyObject;
            TreeViewItem tvi = null;
            while (hit != null)
            {
                if (hit is TreeViewItem t) { tvi = t; break; }
                hit = VisualTreeHelper.GetParent(hit);
            }
            if (tvi == null) return;

            CharacterGroup cg = null;
            if (tvi.Tag is CharacterGroup g)
                cg = g;
            else if (tvi.Tag is List<StateNode>)
            {
                // "SeqFlows (N)" node — parent is character group
                var parent = VisualTreeHelper.GetParent(tvi);
                while (parent != null && !(parent is TreeViewItem)) parent = VisualTreeHelper.GetParent(parent);
                if (parent is TreeViewItem pi && pi.Tag is CharacterGroup pg) cg = pg;
            }

            if (cg == null) return;

            var menu = new ContextMenu();
            var cgCapture = cg;
            var mi = new MenuItem { Header = "Create SeqFLOW..." };
            mi.Click += (s, a) => CreateNewSeqFlow(cgCapture);
            menu.Items.Add(mi);
            var mi2 = new MenuItem { Header = "Kit Setup Info..." };
            mi2.Click += (s, a) => ShowKitSetupInfo(cgCapture);
            menu.Items.Add(mi2);
            menu.IsOpen = true;
            e.Handled = true;
        }

        // ─── Create New SeqFLOW ───────────────────────────────────────────────────
        private void CreateNewSeqFlow(CharacterGroup targetGroup)
        {
            if (_asset == null || _ownerData == null) return;

            // ── Step 1: Template picker ───────────────────────────────────────────
            var allSeqFlows = _doc.Characters
                .SelectMany(cg => cg.SeqFlows)
                .Where(c => c.RawObject != null && c.Nodes.Count > 0)
                .ToList();
            if (allSeqFlows.Count == 0) { App.Logger.LogWarning("StateMachineEditor: no SeqFLOW templates available"); return; }

            StateFlowController templateCtrl = null;
            {
                var win = new Window
                {
                    Title = "Select Template SeqFLOW", Width = 420, Height = 480,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = BrushPanelBg,
                    BorderBrush = BrushBorder,
                    BorderThickness = new Thickness(1), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
                };
                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var hint = new TextBlock { Text = "Pick a SeqFLOW to use as conduit template:", Foreground = BrushTextDim, FontSize = 11, Margin = new Thickness(8, 6, 8, 4) };
                Grid.SetRow(hint, 0); root.Children.Add(hint);

                var listBox = new ListBox
                {
                    Background = BrushPanelBg,
                    BorderThickness = new Thickness(0),
                    Foreground = BrushText,
                    FontSize = 11
                };
                Grid.SetRow(listBox, 1); root.Children.Add(listBox);

                var bottomPanel = new DockPanel { Margin = new Thickness(8, 4, 8, 8) };
                var status = new TextBlock { Foreground = BrushTextDim, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(status, Dock.Left);
                var okBtn = new Button
                {
                    Content = "Select", Width = 70, HorizontalAlignment = HorizontalAlignment.Right,
                    Background = BrushControl,
                    Foreground = Brushes.White, Padding = new Thickness(8, 4, 8, 4), IsEnabled = false
                };
                DockPanel.SetDock(okBtn, Dock.Right);
                bottomPanel.Children.Add(okBtn);
                bottomPanel.Children.Add(status);
                Grid.SetRow(bottomPanel, 2); root.Children.Add(bottomPanel);
                win.Content = root;

                foreach (var grp in allSeqFlows.GroupBy(c => c.CharacterKey).OrderBy(g => g.Key))
                {
                    listBox.Items.Add(new ListBoxItem { Content = grp.Key, IsEnabled = false, Foreground = BrushTextMuted, FontWeight = FontWeights.SemiBold, FontSize = 10, Background = BrushGroupHeaderBg, Padding = new Thickness(8, 3, 8, 3) });
                    foreach (var ctrl in grp.OrderBy(c => c.Name))
                    {
                        var cc = ctrl;
                        var li = new ListBoxItem { Content = $"{ctrl.Name}  ({ctrl.Nodes.Count} nodes)", Tag = cc, Padding = new Thickness(20, 4, 8, 4), Background = Brushes.Transparent, Foreground = BrushText };
                        li.MouseDoubleClick += (s, e) => { templateCtrl = cc; win.DialogResult = true; win.Close(); };
                        listBox.Items.Add(li);
                    }
                }
                status.Text = $"{allSeqFlows.Count} templates";
                listBox.SelectionChanged += (s, e) => { okBtn.IsEnabled = listBox.SelectedItem is ListBoxItem si && si.Tag is StateFlowController; };
                okBtn.Click += (s, e) => { if (listBox.SelectedItem is ListBoxItem si && si.Tag is StateFlowController cc) { templateCtrl = cc; win.DialogResult = true; win.Close(); } };
                listBox.KeyDown += (s, e) => { if (e.Key == Key.Return && listBox.SelectedItem is ListBoxItem li && li.Tag is StateFlowController cc) { templateCtrl = cc; win.DialogResult = true; win.Close(); } };
                try { var owner = Window.GetWindow(_canvas); if (owner != null) win.Owner = owner; } catch { }
                if (win.ShowDialog() != true || templateCtrl == null) return;
            }

            // ── Step 2: Naming dialog ─────────────────────────────────────────────
            string prefix = targetGroup?.Prefix ?? "A";
            string character = targetGroup?.DisplayName ?? "NewHero";
            string actionPath = "";
            {
                var win = new Window
                {
                    Title = "New SeqFLOW Controller", Width = 380, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = BrushPanelBg,
                    BorderBrush = BrushBorder,
                    BorderThickness = new Thickness(1), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
                };
                var sp = new StackPanel { Margin = new Thickness(12) };

                var prefixBox = new ComboBox { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };
                prefixBox.Items.Add("A"); prefixBox.Items.Add("3P");
                prefixBox.SelectedItem = prefix;

                var charBox = new TextBox
                {
                    Text = character, FontSize = 12,
                    Background = BrushInputBg,
                    Foreground = Brushes.White, CaretBrush = Brushes.White,
                    BorderBrush = BrushControl,
                    Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 8)
                };

                var actionBox = new TextBox
                {
                    Text = "Attack", FontSize = 12,
                    Background = BrushInputBg,
                    Foreground = Brushes.White, CaretBrush = Brushes.White,
                    BorderBrush = BrushControl,
                    Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 8)
                };

                var preview = new TextBlock { Foreground = BrushTextMuted, FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };

                void UpdatePreview()
                {
                    string p = prefixBox.SelectedItem?.ToString() ?? "A";
                    string c = charBox.Text.Trim();
                    string a = actionBox.Text.Trim();
                    preview.Text = string.IsNullOrEmpty(a) ? $"{p}.{c}.SeqFLOW" : $"{p}.{c}.{a}.SeqFLOW";
                }
                prefixBox.SelectionChanged += (s, e) => UpdatePreview();
                charBox.TextChanged += (s, e) => UpdatePreview();
                actionBox.TextChanged += (s, e) => UpdatePreview();

                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
                var cancelBtn = new Button
                {
                    Content = "Cancel", Width = 70,
                    Background = BrushBorder,
                    Foreground = Brushes.White, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0)
                };
                cancelBtn.Click += (s, e) => { win.DialogResult = false; win.Close(); };
                var okBtn = new Button
                {
                    Content = "Create", Width = 80, IsDefault = true,
                    Background = BrushControl,
                    Foreground = Brushes.White, Padding = new Thickness(8, 6, 8, 6)
                };
                okBtn.Click += (s, e) => { win.DialogResult = true; win.Close(); };
                btnPanel.Children.Add(cancelBtn);
                btnPanel.Children.Add(okBtn);

                sp.Children.Add(new TextBlock { Text = "Prefix", Foreground = BrushTextDim, FontSize = 10 }); sp.Children.Add(prefixBox);
                sp.Children.Add(new TextBlock { Text = "Character", Foreground = BrushTextDim, FontSize = 10 }); sp.Children.Add(charBox);
                sp.Children.Add(new TextBlock { Text = "Action Path", Foreground = BrushTextDim, FontSize = 10 }); sp.Children.Add(actionBox);
                sp.Children.Add(preview);
                sp.Children.Add(btnPanel);
                win.Content = sp;
                UpdatePreview();
                try { var owner = Window.GetWindow(_canvas); if (owner != null) win.Owner = owner; } catch { }
                charBox.Focus();
                if (win.ShowDialog() != true) return;

                prefix = prefixBox.SelectedItem?.ToString() ?? "A";
                character = charBox.Text.Trim();
                actionPath = actionBox.Text.Trim();
            }

            if (string.IsNullOrEmpty(character)) { App.Logger.LogWarning("StateMachineEditor: character name is required"); return; }
            string fullName = string.IsNullOrEmpty(actionPath) ? $"{prefix}.{character}.SeqFLOW" : $"{prefix}.{character}.{actionPath}.SeqFLOW";

            // Validate no duplicate
            if (_doc.Characters.Any(cg => cg.SeqFlows.Any(c =>
                string.Equals(GetRawName(c.RawObject), fullName, StringComparison.OrdinalIgnoreCase))))
            {
                App.Logger.LogWarning($"StateMachineEditor: a controller named '{fullName}' already exists");
                return;
            }

            // ── Step 3: Deep-copy the template controller ─────────────────────────
            try
            {
                var previousObjects = new HashSet<object>(_asset.Objects, ReferenceEqualityComparer.Instance);

                FrostyClipboard.Current.SetData(templateCtrl.RawObject);
                object newCtrlRaw = FrostyClipboard.Current.GetData(_asset, _assetEntry);
                if (newCtrlRaw == null) { App.Logger.LogWarning("StateMachineEditor: failed to deep-copy template controller"); return; }

                // ── Step 4: Rename the controller ─────────────────────────────────
                SetEbxName(newCtrlRaw, fullName);

                // ── Step 5: Strip animation nodes, keep only conduit ──────────────
                var subsProp = newCtrlRaw.GetType().GetProperty("Subjects");
                var subsList = subsProp?.GetValue(newCtrlRaw) as System.Collections.IList;
                object conduitRaw = null;
                object conduitRef = null; // the PointerRef wrapping the conduit
                if (subsList != null && subsList.Count > 0)
                {
                    // Find the conduit track by name (contains "Conduit")
                    for (int i = 0; i < subsList.Count; i++)
                    {
                        var resolved = ResolvePointerRef(subsList[i]);
                        if (resolved != null)
                        {
                            string rn = GetRawName(resolved);
                            if (rn.IndexOf("Conduit", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                conduitRaw = resolved;
                                conduitRef = subsList[i];
                                App.Logger.Log($"StateMachineEditor: found conduit track at Subjects[{i}]: {rn}");
                                break;
                            }
                        }
                    }
                    // Fallback: if no conduit found by name, use first subject
                    if (conduitRaw == null)
                    {
                        conduitRaw = ResolvePointerRef(subsList[0]);
                        conduitRef = subsList[0];
                        App.Logger.Log("StateMachineEditor: no conduit found by name, using Subjects[0]");
                    }

                    if (conduitRaw != null)
                    {
                        // Clear conduit's transitions and subjects (removes stale template references)
                        foreach (var tname in new[] { "Transitions", "StateTransitions", "OutTransitions", "Subjects" })
                        {
                            var tp = conduitRaw.GetType().GetProperty(tname);
                            var tl = tp?.GetValue(conduitRaw) as System.Collections.IList;
                            if (tl != null) { tl.Clear(); App.Logger.Log($"StateMachineEditor: cleared conduit {tname}"); }
                        }
                        // Rename conduit to "Conduit Track" scoped under the new controller
                        string conduitName = $"{prefix}.{character}.Conduit Track";
                        SetEbxName(conduitRaw, conduitName);
                    }

                    // Rebuild Subjects to contain only the conduit
                    subsList.Clear();
                    if (conduitRef != null)
                        subsList.Add(conduitRef);
                    App.Logger.Log("StateMachineEditor: stripped template nodes, kept conduit track");
                }

                // ── Step 6: Register in AllControllerDatas ────────────────────────
                RegisterNewObjectsInAllControllerDatas(_asset, previousObjects);

                // ── Step 7: Build in-memory model ─────────────────────────────────
                var parsed = NameParser.Parse(fullName);
                var newCtrl = new StateFlowController
                {
                    CharacterKey = parsed.IsValid ? parsed.CharacterKey : $"{prefix}.{character}",
                    Suffix       = NodeSuffix.SeqFLOW,
                    ActionPath   = parsed.ActionPath,
                    Name         = !string.IsNullOrEmpty(parsed.ActionPath)
                        ? $"{parsed.ActionPath} [SeqFLOW]"
                        : "SeqFLOW",
                    RawObject    = newCtrlRaw
                };

                // Build StateNode for the conduit
                if (conduitRaw != null)
                {
                    string condName = GetRawName(conduitRaw);
                    var condParsed = NameParser.Parse(condName);
                    var conduitNode = new StateNode
                    {
                        Name      = condName,
                        RawObject = conduitRaw,
                        Parsed    = condParsed,
                        Category  = DetermineCategory(condName, condParsed)
                    };
                    newCtrl.Nodes.Add(conduitNode);
                    _nodeByRawObj[conduitRaw] = conduitNode;
                    _allNodes.Add(conduitNode);
                }

                // Add to character group (create new group if needed)
                string charKey = newCtrl.CharacterKey;
                var group = _doc.Characters.FirstOrDefault(cg => cg.CharacterKey == charKey);
                if (group == null)
                {
                    var pts = charKey.Split('.');
                    group = new CharacterGroup
                    {
                        CharacterKey = charKey,
                        DisplayName  = pts.Length >= 2 ? pts[1] : charKey,
                        Prefix       = pts.Length >= 1 ? pts[0] : "?"
                    };
                    _doc.Characters.Add(group);
                    _doc.Characters = _doc.Characters
                        .OrderBy(c => c.Prefix == "A" ? 0 : c.Prefix == "3P" ? 1 : 2)
                        .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                }
                group.SeqFlows.Add(newCtrl);
                group.SeqFlows = group.SeqFlows.OrderBy(c => c.ActionPath, StringComparer.OrdinalIgnoreCase).ToList();

                AssetModified = true;
                PopulateTree();

                // Auto-select the new controller and switch to graph view
                if (_propertyViewActive)
                    SwitchToGraphView();
                _activeController = newCtrl;
                ShowToolbar(true);
                LoadNodesToGraph(ExpandFlowChain(newCtrl.Nodes), newCtrl.Name);

                App.Logger.Log($"StateMachineEditor: created new SeqFLOW '{fullName}' (template: {NodeDisplayName(templateCtrl.Nodes.FirstOrDefault())})");
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"StateMachineEditor: create SeqFLOW failed: {ex.Message}");
            }
        }

        // ─── Kit Setup Info ───────────────────────────────────────────────────────
        private void ShowKitSetupInfo(CharacterGroup cg)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Character:  {cg.DisplayName}  (Prefix: {cg.Prefix})");
            sb.AppendLine();

            // List SeqFLOWs
            sb.AppendLine("SeqFLOW Controllers:");
            if (cg.SeqFlows.Count == 0)
                sb.AppendLine("  (none)");
            else
                foreach (var c in cg.SeqFlows)
                    sb.AppendLine($"  {GetRawName(c.RawObject)}  ({c.Nodes.Count} nodes)");
            sb.AppendLine();

            // List other controllers
            if (cg.OtherControllers.Count > 0)
            {
                sb.AppendLine("Other Controllers:");
                foreach (var c in cg.OtherControllers)
                    sb.AppendLine($"  {GetRawName(c.RawObject)}");
                sb.AppendLine();
            }

            sb.AppendLine("─── How Kit Assignment Works ───");
            sb.AppendLine();
            sb.AppendLine("SeqFLOW controllers are linked to characters by naming convention.");
            sb.AppendLine($"Any controller named \"{cg.Prefix}.{cg.DisplayName}.<Action>.SeqFLOW\"");
            sb.AppendLine("will automatically be associated with this character.");
            sb.AppendLine();
            sb.AppendLine("No changes are needed in the state machine file to assign");
            sb.AppendLine("controllers to a character — the name prefix is the link.");
            sb.AppendLine();
            sb.AppendLine("─── Setting Up a New Character Kit ───");
            sb.AppendLine();
            sb.AppendLine("To make a new character playable with these SeqFLOWs:");
            sb.AppendLine();
            sb.AppendLine("1. Duplicate an existing hero kit (e.g. Kit_Hero_Anakin)");
            sb.AppendLine("2. The kit's CharacterStateChannelValues must reference");
            sb.AppendLine("   PublicChannels from the same SoldierStateMachine asset");
            sb.AppendLine("3. The kit's blueprint LocoAnimatable field links to the");
            sb.AppendLine("   state machine asset via GUID");
            sb.AppendLine();
            sb.AppendLine("Kit files are located under:");
            sb.AppendLine("  Gameplay/Kits/Hero/<Character>/Kits/Kit_Hero_<Character>");

            var win = new Window
            {
                Title = $"Kit Setup — {cg.DisplayName}",
                Width = 520, Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = BrushPanelBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var textBox = new TextBox
            {
                Text = sb.ToString(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Background = BrushWindowBg,
                Foreground = BrushText,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(textBox, 0);
            root.Children.Add(textBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            var copyBtn = new Button
            {
                Content = "Copy to Clipboard", Width = 120,
                Background = BrushControl,
                Foreground = Brushes.White, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0)
            };
            copyBtn.Click += (s, e) => { Clipboard.SetText(sb.ToString()); copyBtn.Content = "Copied!"; };
            btnPanel.Children.Add(copyBtn);

            var closeBtn = new Button
            {
                Content = "Close", Width = 70,
                Background = BrushBorder,
                Foreground = Brushes.White, Padding = new Thickness(8, 4, 8, 4)
            };
            closeBtn.Click += (s, e) => win.Close();
            btnPanel.Children.Add(closeBtn);
            Grid.SetRow(btnPanel, 1);
            root.Children.Add(btnPanel);

            win.Content = root;
            try { var owner = Window.GetWindow((DependencyObject)_canvas ?? _tree); if (owner != null) win.Owner = owner; } catch { }
            win.ShowDialog();
        }

        // ─── Animation Preview System ────────────────────────────────────────────

        private string _previewsFolder;

        private string GetPreviewsFolder()
        {
            if (_previewsFolder != null) return _previewsFolder;
            try
            {
                string dllDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string candidate = System.IO.Path.Combine(dllDir, "StateMachineEditor", "Previews");
                if (System.IO.Directory.Exists(candidate)) { _previewsFolder = candidate; return _previewsFolder; }
                // Also check directly next to DLL
                candidate = System.IO.Path.Combine(dllDir, "Previews");
                if (System.IO.Directory.Exists(candidate)) { _previewsFolder = candidate; return _previewsFolder; }
            }
            catch { }
            _previewsFolder = "";
            return _previewsFolder;
        }

        /// <summary>
        /// Tries to find a preview image for a node. Searches:
        ///   Previews/{Character}/{Action}.gif/png/jpg
        ///   Previews/{Character}/{Character}_{Action}.gif/png/jpg
        /// </summary>
        private string FindPreviewPath(StateNode node)
        {
            string folder = GetPreviewsFolder();
            if (string.IsNullOrEmpty(folder) || node == null || !node.Parsed.IsValid) return null;

            string character = node.Parsed.Character;
            string action    = node.Parsed.ActionPath;
            if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(action)) return null;

            string charFolder = System.IO.Path.Combine(folder, character);
            if (!System.IO.Directory.Exists(charFolder)) return null;

            // Normalize action path: "Dodge.Back.01" → "Dodge_Back_01" for filename matching
            string actionFile = action.Replace(".", "_");

            foreach (var ext in new[] { ".gif", ".png", ".jpg", ".jpeg", ".webp" })
            {
                // Try: {Action}.ext
                string path = System.IO.Path.Combine(charFolder, actionFile + ext);
                if (System.IO.File.Exists(path)) return path;

                // Try: {Character}_{Action}.ext
                path = System.IO.Path.Combine(charFolder, $"{character}_{actionFile}{ext}");
                if (System.IO.File.Exists(path)) return path;
            }
            return null;
        }

        private BitmapImage LoadPreviewImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>
        /// For GIF support, uses WPF's MediaElement for animated playback.
        /// Returns an Image for static files, or a MediaElement for GIFs.
        /// </summary>
        private FrameworkElement CreatePreviewElement(string path, double maxWidth = 160, double maxHeight = 120)
        {
            if (string.IsNullOrEmpty(path)) return null;

            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                // WPF doesn't natively animate GIFs via Image — use MediaElement
                var media = new MediaElement
                {
                    Source = new Uri(path, UriKind.Absolute),
                    LoadedBehavior = MediaState.Play,
                    UnloadedBehavior = MediaState.Close,
                    MaxWidth = maxWidth,
                    MaxHeight = maxHeight,
                    Stretch = Stretch.Uniform,
                    IsMuted = true
                };
                // Loop the GIF
                media.MediaEnded += (s, e) => { media.Position = TimeSpan.Zero; media.Play(); };
                return media;
            }

            var img = LoadPreviewImage(path);
            if (img == null) return null;
            return new Image
            {
                Source = img,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private void ShowAnimationBrowser()
        {
            string folder = GetPreviewsFolder();

            var win = new Window
            {
                Title = "Animation Previews",
                Width = 720, Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = BrushWindowBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false
            };

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── Left: character list ──────────────────────────────────────
            var leftPanel = new Grid();
            leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var searchBox = new TextBox
            {
                Background = BrushInputBg,
                Foreground = BrushText,
                CaretBrush = Brushes.White,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 11
            };
            searchBox.GotFocus += (s, e) => { if (searchBox.Tag as string == "ph") { searchBox.Text = ""; searchBox.Foreground = BrushText; searchBox.Tag = null; } };
            searchBox.LostFocus += (s, e) => { if (string.IsNullOrEmpty(searchBox.Text)) { searchBox.Text = "Search..."; searchBox.Foreground = BrushTextDim; searchBox.Tag = "ph"; } };
            searchBox.Text = "Search..."; searchBox.Foreground = BrushTextDim; searchBox.Tag = "ph";
            Grid.SetRow(searchBox, 0);
            leftPanel.Children.Add(searchBox);

            var charList = new ListBox
            {
                Background = BrushPanelBg,
                BorderThickness = new Thickness(0),
                Foreground = BrushText,
                FontSize = 11
            };
            Grid.SetRow(charList, 1);
            leftPanel.Children.Add(charList);
            Grid.SetColumn(leftPanel, 0);
            root.Children.Add(leftPanel);

            // Splitter
            var splitter = new GridSplitter
            {
                Width = 4,
                Background = BrushBorder,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetColumn(splitter, 1);
            root.Children.Add(splitter);

            // ── Right: preview grid ──────────────────────────────────────
            var rightPanel = new Grid();
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var headerText = new TextBlock
            {
                Text = "Select a character",
                Foreground = BrushTextDim,
                FontSize = 11,
                Margin = new Thickness(8, 6, 8, 6)
            };
            Grid.SetRow(headerText, 0);
            rightPanel.Children.Add(headerText);

            var previewScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var previewWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4)
            };
            previewScroll.Content = previewWrap;
            Grid.SetRow(previewScroll, 1);
            rightPanel.Children.Add(previewScroll);
            Grid.SetColumn(rightPanel, 2);
            root.Children.Add(rightPanel);

            // ── Populate ─────────────────────────────────────────────────
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            {
                headerText.Text = "No Previews folder found.\n\nPlace preview images in:\n  <Plugins>/StateMachineEditor/Previews/<Character>/\n  or <Plugins>/Previews/<Character>/";
                headerText.TextWrapping = TextWrapping.Wrap;
                headerText.FontSize = 12;
                headerText.Margin = new Thickness(16);
            }
            else
            {
                var charDirs = System.IO.Directory.GetDirectories(folder)
                    .Select(d => new System.IO.DirectoryInfo(d))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var dir in charDirs)
                {
                    var item = new ListBoxItem
                    {
                        Content = dir.Name,
                        Tag = dir.FullName,
                        Padding = new Thickness(8, 4, 8, 4),
                        Background = Brushes.Transparent,
                        Foreground = BrushText
                    };
                    charList.Items.Add(item);
                }

                charList.SelectionChanged += (s, e) =>
                {
                    if (!(charList.SelectedItem is ListBoxItem sel) || !(sel.Tag is string dirPath)) return;
                    previewWrap.Children.Clear();
                    headerText.Text = sel.Content.ToString();
                    headerText.Foreground = BrushText;
                    headerText.FontWeight = FontWeights.SemiBold;

                    var files = System.IO.Directory.GetFiles(dirPath)
                        .Where(f =>
                        {
                            string ext = System.IO.Path.GetExtension(f).ToLower();
                            return ext == ".gif" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp";
                        })
                        .OrderBy(f => System.IO.Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (files.Length == 0)
                    {
                        previewWrap.Children.Add(new TextBlock
                        {
                            Text = "No preview images in this folder.",
                            Foreground = BrushTextDim,
                            FontStyle = FontStyles.Italic,
                            Margin = new Thickness(8)
                        });
                        return;
                    }

                    foreach (var file in files)
                    {
                        var card = new Border
                        {
                            Background = BrushPanelBg,
                            BorderBrush = BrushBorder,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(3),
                            Margin = new Thickness(4),
                            Padding = new Thickness(4),
                            Width = 160
                        };

                        var stack = new StackPanel();
                        var preview = CreatePreviewElement(file, 150, 110);
                        if (preview != null)
                            stack.Children.Add(preview);
                        else
                            stack.Children.Add(new Border { Height = 80, Background = BrushInputBg });

                        stack.Children.Add(new TextBlock
                        {
                            Text = System.IO.Path.GetFileNameWithoutExtension(file),
                            Foreground = BrushText,
                            FontSize = 10,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 4, 0, 0)
                        });
                        card.Child = stack;
                        previewWrap.Children.Add(card);
                    }
                };

                // Search filter for character list
                searchBox.TextChanged += (s, e) =>
                {
                    if (searchBox.Tag as string == "ph") return;
                    string filter = searchBox.Text.Trim();
                    foreach (ListBoxItem item in charList.Items)
                    {
                        item.Visibility = string.IsNullOrEmpty(filter) ||
                            item.Content.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                };
            }

            win.Content = root;
            try { var owner = Window.GetWindow((DependencyObject)_canvas ?? _tree); if (owner != null) win.Owner = owner; } catch { }
            win.Show(); // Non-modal so users can reference while working
        }

        /// <summary>
        /// BFS from conduit nodes, following transitions to collect all reachable
        /// animation state nodes. Expands a SeqFLOW chain beyond its single Subjects entry.
        /// </summary>
        private List<StateNode> ExpandFlowChain(List<StateNode> roots)
        {
            var visited = new HashSet<StateNode>();
            var queue   = new Queue<StateNode>();
            foreach (var r in roots)
                if (r != null && visited.Add(r)) queue.Enqueue(r);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node.Transitions == null) continue;
                foreach (var t in node.Transitions)
                    if (t?.Target != null && visited.Add(t.Target))
                        queue.Enqueue(t.Target);
            }
            var result = visited.Count > 0 ? visited.ToList() : roots;

            // Read the REAL play order from the conduit node's Subjects list
            // Each root is the conduit — its Subjects are the ordered animation nodes
            AssignSequenceIndices(roots, result);
            return result;
        }

        /// <summary>
        /// Gets the IList of PointerRefs that holds the ordered animation nodes.
        /// The SeqFLOW controller has Subjects→[ConduitTrack], and ConduitTrack.Subjects = the real chain.
        /// </summary>
        // GetChainSubjectsList is no longer used - chain membership is managed via transitions
        private System.Collections.IList GetChainSubjectsList() => null;

        private void DumpSubjectsStructure(object obj, int depth)
        {
            if (obj == null || depth > 2) return;
            App.Logger.Log($"[depth {depth}] {obj.GetType().Name} [{GetRawName(obj)}] properties:");
            foreach (var prop in obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try
                {
                    var val = prop.GetValue(obj);
                    if (val == null) continue;
                    if (val is System.Collections.IList list)
                        App.Logger.Log($"  {prop.Name}: IList Count={list.Count} itemType={( list.Count > 0 ? list[0]?.GetType().Name : "empty")}");
                    else if (val.GetType().Name == "PointerRef")
                    {
                        var res = ResolvePointerRef(val);
                        App.Logger.Log($"  {prop.Name}: PointerRef -> {res?.GetType().Name ?? "null"} [{GetRawName(res)}]");
                    }
                    else if (val.GetType().IsPrimitive || val is string || val.GetType().IsEnum)
                        App.Logger.Log($"  {prop.Name}: {val}");
                }
                catch { }
            }
        }

        // Recursively descend Subjects to find the list that holds the animation nodes.
        // We look for the deepest list whose entries resolve to nodes NOT already in ctrl.Nodes
        // (i.e. not the conduit wrapper node itself).
        private System.Collections.IList GetChainSubjectsListRecursive(object obj, int depth)
        {
            if (obj == null || depth > 5) return null;
            var subProp = obj.GetType().GetProperty("Subjects");
            if (subProp == null) return null;
            var subs = subProp.GetValue(obj) as System.Collections.IList;
            if (subs == null || subs.Count == 0) return null;

            // Build set of conduit raw objects to exclude
            var conduitRaws = new HashSet<object>(
                _activeController.Nodes.Where(n => n.RawObject != null).Select(n => n.RawObject),
                ReferenceEqualityComparer.Instance);

            // Count how many entries resolve to animation nodes that are NOT conduits
            int animNodeCount = 0;
            foreach (var entry in subs)
            {
                var resolved = ResolvePointerRef(entry);
                if (resolved == null) continue;
                if (_nodeByRawObj.ContainsKey(resolved) && !conduitRaws.Contains(resolved))
                    animNodeCount++;
            }

            if (animNodeCount > 0)
                return subs;  // this list contains actual animation nodes

            // Descend into each entry looking for the right level
            foreach (var entry in subs)
            {
                var resolved = ResolvePointerRef(entry);
                if (resolved == null) continue;
                var deeper = GetChainSubjectsListRecursive(resolved, depth + 1);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private void AssignSequenceIndices(List<StateNode> conduits, List<StateNode> allNodes)
        {
            // Sequence order is defined by transitions from the conduit.
            // BFS visit order IS the play order — conduit transitions define what plays first.
            foreach (var n in allNodes) n.SequenceIndex = -1;
            if (conduits.Count == 0) return;
            var conduit = conduits[0];
            if (conduit.Transitions == null || conduit.Transitions.Count == 0) return;
            // Order by the conduit's transition list index
            int idx = 0;
            foreach (var t in conduit.Transitions)
            {
                if (t?.Target != null && t.Target.SequenceIndex < 0)
                    t.Target.SequenceIndex = idx++;
            }
            // Any remaining nodes not directly from conduit get BFS order
            var queue = new Queue<StateNode>(conduit.Transitions.Where(t => t?.Target != null).Select(t => t.Target));
            var visited = new HashSet<StateNode>(queue);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                if (n.Transitions == null) continue;
                foreach (var t in n.Transitions)
                    if (t?.Target != null && visited.Add(t.Target))
                    { t.Target.SequenceIndex = idx++; queue.Enqueue(t.Target); }
            }
        }

        private void LoadNodesToGraph(List<StateNode> nodes, string label)
        {
            _activeNodes  = nodes ?? new List<StateNode>();
            _selectedNode = null;
            bool isSeqFlow = _activeController != null &&
                (_activeController.Suffix == NodeSuffix.SeqFLOW || _activeController.Suffix == NodeSuffix.SEQ);
            if (isSeqFlow)
                _activeChainNodes = new List<StateNode>(_activeNodes);
            else
                _activeChainNodes.Clear();

            // Assign sequence indices based on conduit transitions (for badge numbering)
            if (isSeqFlow && _activeController?.Nodes.Count > 0)
                AssignSequenceIndices(_activeController.Nodes, _activeNodes);

            RenderGraph();
            App.Logger.Log($"StateMachineEditor: loaded {_activeNodes.Count} nodes for \"{label}\"");
        }

        // ── State node tree items ─────────────────────────────────────────────

        private TreeViewItem BuildStateNodeTreeItem(StateNode node)
        {
            var item = new TreeViewItem { Header = NodeDisplayName(node), Foreground = Brushes.LightGray, Tag = node };
            if (node.Transitions?.Count > 0)
            {
                var tr = new TreeViewItem { Header = $"Transitions  ({node.Transitions.Count})", Foreground = new SolidColorBrush(Color.FromRgb(200, 190, 100)) };
                foreach (var t in node.Transitions)
                    tr.Items.Add(new TreeViewItem
                    {
                        Header = string.IsNullOrEmpty(t.Condition) ? $"-> {NodeDisplayName(t.Target)}" : $"-> {NodeDisplayName(t.Target)}  [{t.Condition}]",
                        Foreground = new SolidColorBrush(Color.FromRgb(130, 200, 130))
                    });
                item.Items.Add(tr);
            }
            if (node.Details?.Count > 0)
                foreach (var kg in node.Details.GroupBy(d => d.Kind).OrderBy(g => (int)g.Key))
                {
                    var gi = new TreeViewItem { Header = $"{kg.Key}  ({kg.Count()})", Foreground = DetailKindColor(kg.Key) };
                    foreach (var d in kg) gi.Items.Add(BuildDetailItem(d));
                    item.Items.Add(gi);
                }
            return item;
        }

        private TreeViewItem BuildDetailItem(GraphDetail d)
        {
            var i = new TreeViewItem { Header = d.Label, Foreground = DetailKindColor(d.Kind), FontSize = 11 };
            foreach (var c in d.Children) i.Items.Add(BuildDetailItem(c));
            return i;
        }

        // =========================================================================
        //  Graph rendering
        // =========================================================================

        private const double NodeW        = 200;
        private const double NodeH        = 64;
        private const double ColSpacing   = 260;  // category-column: col width
        private const double RowSpacing   = 100;  // category-column: row height
        private const double FlowColW     = 280;  // flowchart: column width
        private const double FlowRowH     = 96;   // flowchart: row height
        private const double MarginLeft   = 60;
        private const double MarginTop    = 60;

        // ── Graph editing toolbar ────────────────────────────────────────────

        private Border _toolbar;

        private void BuildGraphToolbar()
        {
            var canvasBorder = _canvas?.Parent as Border;
            if (canvasBorder == null) return;

            // Overlay toolbar in top-left of canvas border using an AdornerLayer approach
            // We use a Grid overlay on the canvas border's parent instead
            var parent = canvasBorder.Parent as Grid;
            if (parent == null) return;

            _toolbar = new Border
            {
                Background      = BrushPanelBg,
                BorderBrush     = BrushBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Top,
                Height  = 32,
                Padding = new Thickness(6, 4, 6, 4),
                IsHitTestVisible = true
            };
            // Place in same grid row as canvas border
            int row = Grid.GetRow(canvasBorder);
            Grid.SetRow(_toolbar, row);
            // Put above everything in that row via ZIndex
            Panel.SetZIndex(_toolbar, 10);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            _toolbar.Child = panel;

            // Add Node button
            var addBtn = MakeToolbarButton("+ Add Node", Color.FromRgb(0x00, 0x50, 0x80), "Add a node to this SeqFLOW chain");
            addBtn.Click += (s, e) => AddNodeToChain();
            panel.Children.Add(addBtn);

            // Separator
            panel.Children.Add(new Border { Width = 1, Background = BrushBorder, Margin = new Thickness(6, 2, 6, 2) });

            // Remove Node button
            var removeBtn = MakeToolbarButton("✕ Remove Node", Color.FromRgb(0x80, 0x20, 0x20), "Remove selected node from this chain");
            removeBtn.Click += (s, e) => RemoveSelectedNodeFromChain();
            panel.Children.Add(removeBtn);

            // Commit chain
            panel.Children.Add(new Border { Width = 1, Background = BrushBorder, Margin = new Thickness(6, 2, 6, 2) });
            var commitBtn = MakeToolbarButton("✔ Commit Chain", Color.FromRgb(0x20, 0x80, 0x20), "Rewire all transitions and rebuild Subjects list in sequence order");
            commitBtn.Click += (s, e) => CommitChain();
            panel.Children.Add(commitBtn);

            parent.Children.Add(_toolbar);
            _toolbar.Visibility = Visibility.Collapsed; // hidden until a SeqFLOW is loaded
        }

        private Button MakeToolbarButton(string text, Color accent, string tooltip)
        {
            return new Button
            {
                Content     = text,
                ToolTip     = tooltip,
                Foreground  = BrushText,
                Background  = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                Padding     = new Thickness(8, 2, 8, 2),
                Margin      = new Thickness(0, 0, 4, 0),
                FontSize    = 11,
                Cursor      = Cursors.Hand
            };
        }

        private void ShowToolbar(bool show)
        {
            if (_toolbar != null)
                _toolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─── Add node to chain ────────────────────────────────────────────────

        private void AddNodeToChain()
        {
            if (_activeController == null || _activeController.RawObject == null) return;

            // Build list of ALL CharacterStateStateFlowNodeControllerData not already in this chain
            // Exclude nodes already in the expanded chain (not just conduit)
            var existingRaw = new HashSet<object>(
                _activeChainNodes.Where(n => n.RawObject != null).Select(n => n.RawObject),
                ReferenceEqualityComparer.Instance);

            var candidates = _allNodes
                .Where(n => n.RawObject != null && !existingRaw.Contains(n.RawObject))
                .ToList();

            if (candidates.Count == 0)
            {
                App.Logger.LogWarning("StateMachineEditor: no candidate nodes available to add");
                return;
            }

            // Searchable picker
            var win = new Window
            {
                Title = "Add Node to Chain", Width = 420, Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background    = BrushPanelBg,
                BorderBrush   = BrushBorder,
                BorderThickness = new Thickness(1), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var searchBox = new TextBox
            {
                Background = BrushInputBg,
                Foreground = BrushText,
                BorderBrush = BrushControl,
                BorderThickness = new Thickness(0,0,0,1),
                CaretBrush = Brushes.White, Padding = new Thickness(8,6,8,6), FontSize=12
            };
            Grid.SetRow(searchBox, 0); root.Children.Add(searchBox);

            var listBox = new ListBox
            {
                Background = BrushPanelBg,
                BorderThickness = new Thickness(0),
                Foreground = BrushText,
                FontSize = 11
            };
            Grid.SetRow(listBox, 1); root.Children.Add(listBox);

            var status = new TextBlock { Foreground = BrushTextDim, FontSize=10, Margin=new Thickness(8,4,8,4) };
            Grid.SetRow(status, 2); root.Children.Add(status);
            win.Content = root;

            StateNode picked = null;

            void Rebuild(string f)
            {
                listBox.Items.Clear();
                var filtered = string.IsNullOrWhiteSpace(f)
                    ? candidates
                    : candidates.Where(n =>
                        NodeDisplayName(n).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (n.Parsed.IsValid ? n.Parsed.CharacterKey : "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                foreach (var grp in filtered.GroupBy(n => n.Parsed.IsValid ? n.Parsed.CharacterKey : "Other")
                                            .OrderBy(g => g.Key))
                {
                    listBox.Items.Add(new ListBoxItem { Content=grp.Key, IsEnabled=false, Foreground=BrushTextMuted, FontWeight=FontWeights.SemiBold, FontSize=10, Background=BrushGroupHeaderBg, Padding=new Thickness(8,3,8,3) });
                    foreach (var n in grp.OrderBy(n => NodeDisplayName(n)))
                    {
                        var nn = n;
                        var li = new ListBoxItem { Content=NodeDisplayName(nn), Tag=nn, Padding=new Thickness(20,4,8,4), Background=Brushes.Transparent, Foreground=BrushText };
                        li.MouseDoubleClick += (s,e) => { picked=nn; win.DialogResult=true; win.Close(); };
                        listBox.Items.Add(li);
                    }
                }
                status.Text = $"{filtered.Count} nodes";
            }
            searchBox.TextChanged += (s,e) => Rebuild(searchBox.Text);
            Rebuild("");
            listBox.KeyDown += (s,e) => { if(e.Key==Key.Return && listBox.SelectedItem is ListBoxItem li && li.Tag is StateNode nn){ picked=nn; win.DialogResult=true; win.Close(); } };
            try { var owner=Window.GetWindow(_canvas); if(owner!=null) win.Owner=owner; } catch{}
            searchBox.Focus();
            if (win.ShowDialog() != true || picked == null) return;

            // Deep-copy the node into the current asset using FrostyClipboard.
            // This creates a new EBX object instance (new GUID) registered in this asset,
            // so cross-hero nodes work correctly rather than referencing another hero's context.
            StateNode addedNode = picked;
            bool isCrossAsset = !_allNodes.Any(n => ReferenceEquals(n, picked) &&
                                 picked.RawObject != null &&
                                 _asset != null && _asset.Objects.Contains(picked.RawObject));

            try
            {
                // Snapshot all objects before the copy so we can find newly created ones
                var previousObjects = new HashSet<object>(_asset.Objects, ReferenceEqualityComparer.Instance);

                // Always deep-copy to get a fresh instance properly registered in this asset
                FrostyClipboard.Current.SetData(picked.RawObject);
                object copiedRaw = FrostyClipboard.Current.GetData(_asset, _assetEntry);

                if (copiedRaw != null)
                {
                    // Register all newly created objects in AllControllerDatas with correct AssetIndex
                    RegisterNewObjectsInAllControllerDatas(_asset, previousObjects);

                    // Build a new StateNode wrapper for the copied raw object
                    string rawName = GetRawName(copiedRaw);
                    var parsed = NameParser.Parse(rawName);
                    addedNode = new StateNode
                    {
                        Name      = rawName,
                        RawObject = copiedRaw,
                        Parsed    = parsed,
                        Category  = DetermineCategory(rawName, parsed)
                    };
                    // Register in lookup maps
                    _nodeByRawObj[copiedRaw] = addedNode;
                    _allNodes.Add(addedNode);
                    // Scan transitions and deep details so badge counts (1T, 8X etc.) populate
                    ScanTransitions(addedNode, new Dictionary<Guid, StateNode>(), _nodeByRawObj);
                    DeepScanNode(addedNode);
                    App.Logger.Log($"StateMachineEditor: deep-copied [{NodeDisplayName(picked)}] -> [{rawName}]");
                }
                else
                {
                    App.Logger.LogWarning("StateMachineEditor: deep copy returned null — using original node reference");
                }
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"StateMachineEditor: deep copy failed ({ex.Message}) — using original node reference");
            }

            if (!_activeChainNodes.Contains(addedNode))
            {
                _activeChainNodes.Add(addedNode);
                var display = new List<StateNode>(_activeChainNodes);
                LoadNodesToGraph(display, _activeController.Name);
                App.Logger.Log($"StateMachineEditor: added [{NodeDisplayName(addedNode)}] to viewport — drag output port to connect");
            }
        }

        // ─── Remove node(s) from view ────────────────────────────────────────
        // "Remove" hides nodes from the viewport without touching EBX transitions.
        // Use "Break Connection" to deliberately sever wires in EBX.
        // This is safe — the BFS re-expansion will re-include the node next time
        // the controller is loaded, unless its incoming transitions are broken first.

        private void RemoveSelectedNodeFromChain()
        {
            if (_activeController == null) return;
            if (_selectedNode == null) { App.Logger.LogWarning("StateMachineEditor: select a node first"); return; }
            RemoveNodesFromView(new List<StateNode> { _selectedNode });
        }

        private void RemoveSelectedNodesFromChain()
        {
            if (_activeController == null || _selectedNodes.Count == 0) return;
            RemoveNodesFromView(_selectedNodes.ToList());
        }

        private void RemoveAndBreakNode(StateNode node)
        {
            if (node == null) return;
            // Break all incoming transitions from other chain nodes
            foreach (var chainNode in _activeChainNodes.ToList())
            {
                if (chainNode == node) continue;
                var incoming = chainNode.Transitions?.Where(t => t.Target == node).ToList();
                if (incoming == null) continue;
                foreach (var t in incoming) BreakConnection(chainNode, t);
            }
            // Break all outgoing transitions from this node
            if (node.Transitions?.Count > 0)
                foreach (var t in node.Transitions.ToList())
                    BreakConnection(node, t);
            // Remove from view
            RemoveNodesFromView(new List<StateNode> { node });
        }

        private void RemoveNodesFromView(List<StateNode> nodesToRemove)
        {
            // Remove from display list
            foreach (var n in nodesToRemove)
                _activeChainNodes.Remove(n);

            _selectedNode = null;
            _selectedNodes.Clear();

            // Rebuild display from current _activeChainNodes (no re-expand — avoids re-adding via transitions)
            var display = new List<StateNode>(_activeChainNodes);
            LoadNodesToGraph(display, _activeController.Name);

            App.Logger.Log($"StateMachineEditor: removed {nodesToRemove.Count} node(s) from view. Use Break Connection to sever EBX transitions.");
        }

        // ─── Move node up/down in chain ───────────────────────────────────────

        private void MoveNodeInChain(int delta)
        {
            if (_activeController == null || _activeController.RawObject == null) return;
            if (_selectedNode == null) return;
            var nodes = _activeController.Nodes;
            int idx = nodes.IndexOf(_selectedNode);
            if (idx < 0) return;
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= nodes.Count) return;

            try
            {
                // Swap in EBX Subjects list
                // Move is not meaningful without a Subjects list — skip EBX mutation, just reorder display
                // (sequence order is determined by transitions, not an ordered list)
                App.Logger.Log("StateMachineEditor: move reorders display only — transitions define play order");
                // Swap in model only

                // No Subjects list to reorder — display order only

                // Swap in model
                nodes[idx]    = nodes[newIdx];
                nodes[newIdx] = _selectedNode;

                AssetModified = true;
                LoadNodesToGraph(ExpandFlowChain(_activeController.Nodes), _activeController.Name);
                // Re-select the moved node
                var movedNode = _selectedNode;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                {
                    SelectNode(movedNode);
                }));
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"StateMachineEditor: move node failed: {ex.Message}");
            }
        }

        private void CommitWire(StateNode fromNode, StateNode toNode)
        {
            CancelWire();
            if (fromNode == null || toNode == null || fromNode == toNode) return;

            bool alreadyConnected = fromNode.Transitions != null &&
                                    fromNode.Transitions.Any(t => t.Target == toNode);
            if (alreadyConnected)
            {
                App.Logger.Log($"StateMachineEditor: already connected [{NodeDisplayName(fromNode)}] -> [{NodeDisplayName(toNode)}]");
                return;
            }

            // Find the first non-exit transition on fromNode in EBX
            // Redirect it to toNode (original plugin pattern) rather than creating new
            bool redirected = false;
            foreach (var tname in new[] { "Transitions", "StateTransitions", "OutTransitions" })
            {
                var tp = fromNode.RawObject?.GetType().GetProperty(tname);
                var list = tp?.GetValue(fromNode.RawObject) as System.Collections.IList;
                if (list == null) continue;

                foreach (var entry in list)
                {
                    if (entry == null || IsExitTransition(entry)) continue;
                    var existTarget = ResolvePointerRef(entry.GetType().GetProperty("Target")?.GetValue(entry));
                    // Only redirect free (null) slots first, then overwrite occupied ones
                    if (existTarget != null) continue;
                    SetTransitionTarget(entry, toNode.RawObject);
                    redirected = true;
                    break;
                }

                if (!redirected)
                {
                    // No free slot — redirect first non-exit occupied transition
                    foreach (var entry in list)
                    {
                        if (entry == null || IsExitTransition(entry)) continue;
                        SetTransitionTarget(entry, toNode.RawObject);
                        redirected = true;
                        break;
                    }
                }

                if (!redirected)
                {
                    // No transitions at all — create one
                    var tcType = fromNode.RawObject?.GetType().Assembly.GetTypes()
                                 .FirstOrDefault(t => t.Name == "TransitionCondition");
                    if (tcType != null)
                    {
                        var tc = Activator.CreateInstance(tcType);
                        SetTransitionTarget(tc, toNode.RawObject);
                        list.Add(tc);
                        redirected = true;
                    }
                }
                if (redirected) break;
            }

            // Sync in-memory model
            if (fromNode.Transitions == null) fromNode.Transitions = new List<Transition>();
            // Remove any stale transition that pointed to something else (we just redirected it)
            fromNode.Transitions.RemoveAll(t => t.Target != null && !IsExitTransition(t) && !fromNode.Transitions.Any(t2 => t2 == t && t2.Target == toNode));
            if (!fromNode.Transitions.Any(t => t.Target == toNode))
                fromNode.Transitions.Add(new Transition { Source = fromNode, Target = toNode, Condition = "" });

            AssetModified = true;
            App.Logger.Log($"StateMachineEditor: wired [{NodeDisplayName(fromNode)}] -> [{NodeDisplayName(toNode)}]");
            RenderGraph();
        }

        private void CancelWire()
        {
            if (_wireDragPath != null)
            {
                _canvas?.Children.Remove(_wireDragPath);
                _wireDragPath = null;
            }
            _wiringActive    = false;
            _wireFromNode    = null;
            _wireHoverTarget = null;
            try { _canvas?.ReleaseMouseCapture(); } catch { }
        }

        private void ShowNodeContextMenu(StateNode node)
        {
            var menu = new ContextMenu
            {
                Background = BrushPanelBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1)
            };
            bool isSeq = _activeController != null &&
                         (_activeController.Suffix == NodeSuffix.SeqFLOW || _activeController.Suffix == NodeSuffix.SEQ);

            if (isSeq)
            {
                // ── Break connection to... ────────────────────────────────
                if (node.Transitions?.Count > 0)
                {
                    var breakItem = new MenuItem { Header = "Break connection to...", Foreground = new SolidColorBrush(Color.FromRgb(210,120,120)), Background = Brushes.Transparent };
                    foreach (var t in node.Transitions)
                    {
                        var tc = t;
                        var sub = new MenuItem { Header = $"-> {NodeDisplayName(tc.Target)}", Foreground = BrushText, Background = Brushes.Transparent };
                        sub.Click += (s,e) => BreakConnection(node, tc);
                        breakItem.Items.Add(sub);
                    }
                    menu.Items.Add(breakItem);
                }

                // ── Break incoming connections ────────────────────────────
                var incoming = _activeNodes.Where(n => n.Transitions != null && n.Transitions.Any(t => t.Target == node)).ToList();
                if (incoming.Count > 0)
                {
                    var breakInItem = new MenuItem { Header = "Break connection from...", Foreground = new SolidColorBrush(Color.FromRgb(210,120,120)), Background = Brushes.Transparent };
                    foreach (var src2 in incoming)
                    {
                        var s2 = src2;
                        var tc = s2.Transitions.First(t => t.Target == node);
                        var sub = new MenuItem { Header = $"<- {NodeDisplayName(s2)}", Foreground = BrushText, Background = Brushes.Transparent };
                        sub.Click += (se,ev) => BreakConnection(s2, tc);
                        breakInItem.Items.Add(sub);
                    }
                    menu.Items.Add(breakInItem);
                }
                menu.Items.Add(new Separator());
            }

            // ── Remove from chain ─────────────────────────────────────────
            if (isSeq)
            {
                var removeItem = new MenuItem { Header = "Remove from view", Foreground = new SolidColorBrush(Color.FromRgb(180,140,140)), Background = Brushes.Transparent, ToolTip = "Hide from viewport (EBX transitions kept intact)" };
                removeItem.Click += (s,e) => RemoveSelectedNodeFromChain();
                menu.Items.Add(removeItem);

                var removeBreakItem = new MenuItem { Header = "Remove & break all connections", Foreground = new SolidColorBrush(Color.FromRgb(210,80,80)), Background = Brushes.Transparent, ToolTip = "Break all incoming/outgoing EBX transitions, then remove from view" };
                removeBreakItem.Click += (s,e) => RemoveAndBreakNode(node);
                menu.Items.Add(removeBreakItem);
            }

            if (menu.Items.Count > 0) menu.IsOpen = true;
        }

        private void ShowConnectToDialog(StateNode fromNode)
        {
            // Show all nodes in current chain as connection targets (excluding already-connected ones)
            var existingTargets = new HashSet<StateNode>(fromNode.Transitions?.Select(t => t.Target).Where(t => t != null) ?? Enumerable.Empty<StateNode>());
            var candidates = _activeNodes.Where(n => n != fromNode && !existingTargets.Contains(n)).ToList();
            if (candidates.Count == 0) { App.Logger.LogWarning("StateMachineEditor: no connection targets available"); return; }

            var win = new Window
            {
                Title = $"Connect [{NodeDisplayName(fromNode)}] to...", Width = 380, Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = BrushPanelBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var search = new TextBox { Background=BrushInputBg, Foreground=BrushText, BorderBrush=BrushControl, BorderThickness=new Thickness(0,0,0,1), CaretBrush=Brushes.White, Padding=new Thickness(8,6,8,6), FontSize=12 };
            Grid.SetRow(search, 0); root.Children.Add(search);

            var list = new ListBox { Background=BrushPanelBg, BorderThickness=new Thickness(0), Foreground=BrushText, FontSize=11 };
            Grid.SetRow(list, 1); root.Children.Add(list);
            win.Content = root;

            void Rebuild(string f)
            {
                list.Items.Clear();
                var filtered = string.IsNullOrWhiteSpace(f) ? candidates : candidates.Where(n => NodeDisplayName(n).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (var n in filtered.OrderBy(n => n.SequenceIndex >= 0 ? n.SequenceIndex : 999).ThenBy(n => NodeDisplayName(n)))
                {
                    var nn = n;
                    var li = new ListBoxItem { Padding=new Thickness(8,4,8,4), Background=Brushes.Transparent };
                    var sp = new StackPanel { Orientation=Orientation.Horizontal };
                    if (nn.SequenceIndex >= 0) sp.Children.Add(new TextBlock { Text=$"#{nn.SequenceIndex+1}  ", Foreground=BrushControl, FontWeight=FontWeights.Bold, FontSize=10, VerticalAlignment=VerticalAlignment.Center });
                    sp.Children.Add(new TextBlock { Text=NodeDisplayName(nn), Foreground=BrushText });
                    li.Content = sp; li.Tag = nn;
                    li.MouseDoubleClick += (s,e) => { MakeConnection(fromNode, nn); win.Close(); };
                    list.Items.Add(li);
                }
            }
            search.TextChanged += (s,e) => Rebuild(search.Text);
            list.KeyDown += (s,e) => { if(e.Key==Key.Return && list.SelectedItem is ListBoxItem li && li.Tag is StateNode nn){ MakeConnection(fromNode,nn); win.Close(); }};
            Rebuild("");
            try { var owner=Window.GetWindow(_canvas); if(owner!=null) win.Owner=owner; } catch{}
            search.Focus();
            win.ShowDialog();
        }

        private void ShowConnectFromDialog(StateNode toNode)
        {
            // Which nodes in the view should connect TO this node?
            var existingSources = new HashSet<StateNode>(
                _activeNodes.Where(n => n.Transitions != null && n.Transitions.Any(t => t.Target == toNode)));
            var candidates = _activeNodes.Where(n => n != toNode && !existingSources.Contains(n)).ToList();
            if (candidates.Count == 0) { App.Logger.LogWarning("StateMachineEditor: no source nodes available"); return; }

            var win = new Window { Title=$"Connect ... to [{NodeDisplayName(toNode)}]", Width=380, Height=420, WindowStartupLocation=WindowStartupLocation.CenterOwner, Background=BrushPanelBg, BorderBrush=BrushBorder, BorderThickness=new Thickness(1), ResizeMode=ResizeMode.CanResize, ShowInTaskbar=false };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
            var search = new TextBox { Background=BrushInputBg, Foreground=BrushText, BorderBrush=BrushControl, BorderThickness=new Thickness(0,0,0,1), CaretBrush=Brushes.White, Padding=new Thickness(8,6,8,6), FontSize=12 };
            Grid.SetRow(search,0); root.Children.Add(search);
            var list = new ListBox { Background=BrushPanelBg, BorderThickness=new Thickness(0), Foreground=BrushText, FontSize=11 };
            Grid.SetRow(list,1); root.Children.Add(list);
            win.Content = root;
            void Rebuild(string f)
            {
                list.Items.Clear();
                var filtered = string.IsNullOrWhiteSpace(f) ? candidates : candidates.Where(n => NodeDisplayName(n).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (var n in filtered.OrderBy(n => n.SequenceIndex >= 0 ? n.SequenceIndex : 999).ThenBy(n => NodeDisplayName(n)))
                {
                    var nn = n;
                    var li = new ListBoxItem { Content=NodeDisplayName(nn), Tag=nn, Padding=new Thickness(8,4,8,4), Background=Brushes.Transparent };
                    li.MouseDoubleClick += (s,e) => { MakeConnection(nn, toNode); win.Close(); };
                    list.Items.Add(li);
                }
            }
            search.TextChanged += (s,e) => Rebuild(search.Text);
            list.KeyDown += (s,e) => { if(e.Key==Key.Return && list.SelectedItem is ListBoxItem li && li.Tag is StateNode nn){ MakeConnection(nn,toNode); win.Close(); }};
            Rebuild("");
            try { var owner=Window.GetWindow(_canvas); if(owner!=null) win.Owner=owner; } catch{}
            search.Focus(); win.ShowDialog();
        }

        /// <summary>
        /// Redirects an existing EBX TransitionCondition to point to a new target.
        /// Handles both direct Target and nested ChildConditions.
        /// </summary>
        private bool SetTransitionTarget(object tc, object newTargetRawObj)
        {
            if (tc == null || newTargetRawObj == null) return false;
            bool set = false;
            try
            {
                var tp = tc.GetType().GetProperty("Target");
                if (tp != null) { tp.SetValue(tc, new PointerRef(newTargetRawObj)); set = true; }
            }
            catch { }
            // Also update ChildConditions recursively
            try
            {
                var cp = tc.GetType().GetProperty("ChildConditions");
                if (cp?.GetValue(tc) is System.Collections.IEnumerable children)
                    foreach (var child in children)
                        if (SetTransitionTarget(child, newTargetRawObj)) set = true;
            }
            catch { }
            return set;
        }

        /// <summary>
        /// Determines whether a TransitionCondition is an "exit" transition (should not be redirected).
        /// Exit transitions have ConditionType containing "Int" with IntValue == 1.
        /// </summary>
        private bool IsExitTransition(object tc)
        {
            try
            {
                var ct = tc.GetType().GetProperty("ConditionType")?.GetValue(tc)?.ToString() ?? "";
                if (ct.Contains("Bool")) return false;
                if (ct.Contains("Int"))
                {
                    var iv = tc.GetType().GetProperty("IntValue")?.GetValue(tc);
                    if (iv != null && Convert.ToInt32(iv) == 1) return true;
                }
                // Check ChildConditions
                if (tc.GetType().GetProperty("ChildConditions")?.GetValue(tc) is System.Collections.IEnumerable children)
                    foreach (var child in children)
                        if (IsExitTransition(child)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Redirects all non-exit transitions on fromNode to point to newTarget.
        /// This is how the original attack chain editor rewires nodes — it never deletes transitions,
        /// it just updates their Target pointer.
        /// </summary>
        private void RedirectTransitions(StateNode fromNode, StateNode newTarget)
        {
            if (fromNode?.RawObject == null || newTarget?.RawObject == null) return;
            foreach (var tname in new[] { "Transitions", "StateTransitions", "OutTransitions" })
            {
                var tp = fromNode.RawObject.GetType().GetProperty(tname);
                var list = tp?.GetValue(fromNode.RawObject) as System.Collections.IList;
                if (list == null) continue;
                foreach (var entry in list)
                {
                    if (entry == null || IsExitTransition(entry)) continue;
                    SetTransitionTarget(entry, newTarget.RawObject);
                }
                break;
            }
            // Update in-memory model
            if (fromNode.Transitions != null)
                foreach (var t in fromNode.Transitions)
                    if (t.Target != null && !IsExitTransition(t)) t.Target = newTarget;
        }

        private void MakeConnection(StateNode fromNode, StateNode toNode)
        {
            if (fromNode?.RawObject == null || toNode?.RawObject == null) return;
            try
            {
                foreach (var tname in new[] { "Transitions", "StateTransitions", "OutTransitions" })
                {
                    var transProp = fromNode.RawObject.GetType().GetProperty(tname);
                    var transList = transProp?.GetValue(fromNode.RawObject) as System.Collections.IList;
                    if (transList == null) continue;

                    // Try to redirect an existing non-exit transition first
                    bool redirected = false;
                    foreach (var entry in transList)
                    {
                        if (entry == null || IsExitTransition(entry)) continue;
                        // Check if this transition already has a target
                        var existTarget = ResolvePointerRef(entry.GetType().GetProperty("Target")?.GetValue(entry));
                        if (existTarget != null) continue;  // already wired — don't overwrite
                        SetTransitionTarget(entry, toNode.RawObject);
                        redirected = true;
                        break;
                    }

                    if (!redirected)
                    {
                        // Create a new TransitionCondition
                        var tcType = fromNode.RawObject.GetType().Assembly.GetTypes()
                                     .FirstOrDefault(t => t.Name == "TransitionCondition");
                        if (tcType != null)
                        {
                            var tc = Activator.CreateInstance(tcType);
                            SetTransitionTarget(tc, toNode.RawObject);
                            transList.Add(tc);
                        }
                    }

                    // Sync in-memory model
                    fromNode.Transitions = fromNode.Transitions ?? new List<Transition>();
                    if (!fromNode.Transitions.Any(t => t.Target == toNode))
                        fromNode.Transitions.Add(new Transition { Source = fromNode, Target = toNode, Condition = "" });

                    AssetModified = true;
                    App.Logger.Log($"StateMachineEditor: connected [{NodeDisplayName(fromNode)}] -> [{NodeDisplayName(toNode)}]");
                    RenderGraph();
                    return;
                }
                App.Logger.LogWarning("StateMachineEditor: no Transitions property found on node");
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"StateMachineEditor: connect failed: {ex.Message}");
            }
        }

        private void BreakConnection(StateNode fromNode, Transition trans)
        {
            if (fromNode?.RawObject == null || trans?.Target?.RawObject == null) return;
            try
            {
                foreach (var tname in new[]{ "Transitions","StateTransitions","OutTransitions","TransitionList" })
                {
                    var transProp = fromNode.RawObject.GetType().GetProperty(tname);
                    if (transProp == null) continue;
                    var transList = transProp.GetValue(fromNode.RawObject) as System.Collections.IList;
                    if (transList == null) continue;

                    object entryToRemove = null;
                    foreach (var entry in transList)
                    {
                        // Direct Target check
                        var tp = entry?.GetType().GetProperty("Target");
                        if (tp != null)
                        {
                            var resolved = ResolvePointerRef(tp.GetValue(entry));
                            if (resolved != null && ReferenceEquals(resolved, trans.Target.RawObject))
                            { entryToRemove = entry; break; }
                        }
                        // ChildConditions check
                        var cp = entry?.GetType().GetProperty("ChildConditions");
                        if (cp?.GetValue(entry) is System.Collections.IEnumerable children)
                            foreach (var child in children)
                            {
                                var ct = child?.GetType().GetProperty("Target");
                                var cr = ResolvePointerRef(ct?.GetValue(child));
                                if (cr != null && ReferenceEquals(cr, trans.Target.RawObject))
                                { entryToRemove = entry; break; }
                            }
                        if (entryToRemove != null) break;
                    }

                    if (entryToRemove != null)
                    {
                        transList.Remove(entryToRemove);
                        // Sync in-memory model
                        fromNode.Transitions?.Remove(trans);
                        AssetModified = true;
                        App.Logger.Log($"StateMachineEditor: broke [{NodeDisplayName(fromNode)}] -> [{NodeDisplayName(trans.Target)}]");
                        RenderGraph();
                        return;
                    }
                }
                // EBX entry not found — just sync in-memory model silently
                fromNode.Transitions?.Remove(trans);
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"StateMachineEditor: break connection failed: {ex.Message}");
            }
        }

        // ─── Commit Chain ─────────────────────────────────────────────────────
        // Rewires all transitions in sequence order and rebuilds the Subjects list.
        // Equivalent to the original plugin's Reindex() → RewireChain() + RebuildFlowSubjects().

        private void CommitChain()
        {
            if (_activeController?.RawObject == null || _activeChainNodes.Count == 0) return;
            try
            {
                var conduitRaws = new HashSet<object>(
                    _activeController.Nodes.Where(n => n.RawObject != null).Select(n => n.RawObject),
                    ReferenceEqualityComparer.Instance);

                var ordered = _activeChainNodes
                    .Where(n => n.RawObject != null && !conduitRaws.Contains(n.RawObject))
                    .OrderBy(n => n.SequenceIndex >= 0 ? n.SequenceIndex : 9999)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (ordered.Count == 0) { App.Logger.LogWarning("StateMachineEditor: no chain nodes to commit"); return; }

                // Step 1: RewireChain — redirect each node's non-exit transitions to next (circular)
                for (int i = 0; i < ordered.Count; i++)
                {
                    var curr = ordered[i];
                    var next = ordered[(i + 1) % ordered.Count];
                    RedirectTransitions(curr, next);
                }

                // Step 2: RebuildFlowSubjects — set flowObj.Subjects = ordered node list
                // When Subjects.Count > 1 the game reads play order from this list directly
                var subsProp = _activeController.RawObject.GetType().GetProperty("Subjects");
                var subsList = subsProp?.GetValue(_activeController.RawObject) as System.Collections.IList;
                if (subsList != null)
                {
                    subsList.Clear();
                    foreach (var node in ordered)
                        subsList.Add(new PointerRef(node.RawObject));
                    App.Logger.Log($"StateMachineEditor: rebuilt Subjects with {ordered.Count} nodes");
                }
                else
                    App.Logger.LogWarning("StateMachineEditor: could not access flow Subjects list");

                // Step 2b: Nullify conduit transitions that point to nodes NOT in our chain
                // The conduit may have stale transitions to removed nodes — set those Targets to null
                var orderedRaws = new HashSet<object>(ordered.Select(n => n.RawObject), ReferenceEqualityComparer.Instance);
                var conduitNode = _activeController.Nodes.FirstOrDefault();
                if (conduitNode?.RawObject != null)
                {
                    foreach (var tname in new[]{ "Transitions","StateTransitions","OutTransitions" })
                    {
                        var tp = conduitNode.RawObject.GetType().GetProperty(tname);
                        var tl = tp?.GetValue(conduitNode.RawObject) as System.Collections.IList;
                        if (tl == null) continue;
                        foreach (var entry in tl)
                        {
                            if (entry == null || IsExitTransition(entry)) continue;
                            var targetProp = entry.GetType().GetProperty("Target");
                            if (targetProp == null) continue;
                            var resolved = ResolvePointerRef(targetProp.GetValue(entry));
                            if (resolved != null && !orderedRaws.Contains(resolved))
                                targetProp.SetValue(entry, new PointerRef());  // null out stale target
                        }
                        break;
                    }
                }

                // Step 3: sync in-memory model
                for (int i = 0; i < ordered.Count; i++)
                {
                    ordered[i].SequenceIndex = i;
                    var next = ordered[(i + 1) % ordered.Count];
                    ordered[i].Transitions = ordered[i].Transitions ?? new List<Transition>();
                    ordered[i].Transitions.RemoveAll(t => t != null && !IsExitTransition(t));
                    ordered[i].Transitions.Add(new Transition { Source = ordered[i], Target = next });
                }

                AssetModified = true;
                App.Logger.Log($"StateMachineEditor: committed {ordered.Count}-node chain");
                LoadNodesToGraph(ExpandFlowChain(_activeController.Nodes), _activeController.Name);
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"StateMachineEditor: commit chain failed: {ex.Message}");
            }
        }

        // ─── Register new objects in AllControllerDatas ──────────────────────────
        // After FrostyClipboard deep-copies a node, all newly created objects must be
        // appended to CharacterStateOwnerData.AllControllerDatas with correct AssetIndex
        // values — otherwise the game crashes on load.

        private void RegisterNewObjectsInAllControllerDatas(EbxAsset asset, HashSet<object> previousObjects)
        {
            if (_ownerData == null)
            {
                App.Logger.LogWarning("StateMachineEditor: cannot register new objects — CharacterStateOwnerData not found");
                return;
            }

            var acdProp = _ownerData.GetType().GetProperty("AllControllerDatas");
            if (acdProp == null)
            {
                App.Logger.LogWarning("StateMachineEditor: AllControllerDatas property not found on CharacterStateOwnerData");
                return;
            }
            var acdList = acdProp.GetValue(_ownerData) as System.Collections.IList;
            if (acdList == null)
            {
                App.Logger.LogWarning("StateMachineEditor: AllControllerDatas is null");
                return;
            }

            int registered = 0;
            int indexed = 0;
            foreach (var obj in asset.Objects)
            {
                if (obj == null || previousObjects.Contains(obj)) continue;

                // Only register objects that have an AssetIndex property
                var indexProp = obj.GetType().GetProperty("AssetIndex");
                if (indexProp == null) continue;

                int newIndex = acdList.Count;
                acdList.Add(new PointerRef(obj));
                registered++;

                // Only assign a new AssetIndex to objects that had a non-zero index in the source.
                // Child objects (keyframed channels, clips, etc.) typically have AssetIndex=0
                // in the base game and must stay at 0 — setting them to non-zero causes crashes.
                int copiedIndex = (int)indexProp.GetValue(obj);
                if (copiedIndex != 0)
                {
                    indexProp.SetValue(obj, newIndex);
                    indexed++;
                    App.Logger.Log($"StateMachineEditor: registered [{obj.GetType().Name}] in AllControllerDatas at index {newIndex} (AssetIndex updated)");
                }
            }

            if (registered > 0)
                App.Logger.Log($"StateMachineEditor: registered {registered} new object(s) in AllControllerDatas ({indexed} with AssetIndex, total now {acdList.Count})");
        }

        private void ShowPlaceholder(string msg)
        {
            if (_canvas == null) return;
            _canvas.Children.Clear(); _nodeVisualsByObject.Clear();
            var tb = new TextBlock { Text = msg, Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 100)), FontSize = 14, FontStyle = FontStyles.Italic };
            Canvas.SetLeft(tb, 60); Canvas.SetTop(tb, 60); _canvas.Children.Add(tb);
        }

        private void RenderGraph()
        {
            if (_canvas == null) return;
            _canvas.Children.Clear(); _nodeVisualsByObject.Clear();
            // Keep user-created frames, rebuild auto frames
            _frames.RemoveAll(f => !f.IsUserFrame);
            _selectedNodes.Clear();
            if (_activeNodes == null || _activeNodes.Count == 0) { ShowPlaceholder("No nodes to display."); return; }

            bool isFlow = _activeController != null &&
                          (_activeController.Suffix == NodeSuffix.SeqFLOW ||
                           _activeController.Suffix == NodeSuffix.SEQ);

            if (isFlow) LayoutFlowchart(); else LayoutCategoryColumns();

            // Restore saved node positions before drawing connections/frames
            ApplySavedPositions();

            // Auto-generate frames by category for category-column view
            if (!isFlow) AutoBuildFrames();
            DrawFrames();
            DrawAllConnections(isFlow);
        }

        private void ApplySavedPositions()
        {
            if (_savedPositions.Count == 0) return;
            foreach (var node in _activeNodes)
            {
                if (node.Name == null || !_savedPositions.TryGetValue(_savedLayoutKey + "|" + node.Name, out var pos)) continue;
                if (node.RawObject == null) continue;
                if (_nodeVisualsByObject.TryGetValue(node.RawObject, out var vis) && vis is Canvas c)
                {
                    Canvas.SetLeft(c, pos.X);
                    Canvas.SetTop (c, pos.Y);
                }
            }
        }

        private void ShowCanvasContextMenu(Point canvasPos)
        {
            var menu = new ContextMenu { Background = BrushPanelBg, BorderBrush = BrushBorder };

            // "Create Frame from Selection" - only if nodes selected
            if (_selectedNodes.Count > 0)
            {
                // Remove all selected nodes (SeqFLOW only)
                bool canEdit = _activeController != null &&
                               (_activeController.Suffix == NodeSuffix.SeqFLOW || _activeController.Suffix == NodeSuffix.SEQ);
                if (canEdit && _selectedNodes.Count > 1)
                {
                    var removeAllItem = new MenuItem
                    {
                        Header     = $"Remove {_selectedNodes.Count} selected nodes from chain",
                        Foreground = new SolidColorBrush(Color.FromRgb(210, 80, 80)),
                        Background = Brushes.Transparent
                    };
                    removeAllItem.Click += (s, e) => RemoveSelectedNodesFromChain();
                    menu.Items.Add(removeAllItem);
                    menu.Items.Add(new Separator());
                }

                var createItem = new MenuItem
                {
                    Header     = $"Create Frame from Selection  ({_selectedNodes.Count} nodes)",
                    Foreground = BrushText,
                    Background = Brushes.Transparent
                };
                createItem.Click += (s, e) => CreateFrameFromSelection();
                menu.Items.Add(createItem);
                menu.Items.Add(new Separator());
            }

            // "Clear Saved Layout"
            if (_savedPositions.Count > 0)
            {
                var clearItem = new MenuItem
                {
                    Header     = "Reset Layout",
                    Foreground = new SolidColorBrush(Color.FromRgb(180,100,100)),
                    Background = Brushes.Transparent
                };
                clearItem.Click += (s, e) => { var prefix = _savedLayoutKey + "|"; foreach(var k in _savedPositions.Keys.Where(k=>k.StartsWith(prefix)).ToList()) _savedPositions.Remove(k); RenderGraph(); };
                menu.Items.Add(clearItem);
            }

            if (menu.Items.Count > 0)
                menu.IsOpen = true;
        }

        // Preset colors for frame picker
        private static readonly (byte r, byte g, byte b, string name)[] FrameColorPresets =
        {
            (100, 140, 200, "Blue"),   (80,  180, 120, "Green"),  (180, 140, 80,  "Amber"),
            (200, 80,  80,  "Red"),    (160, 80,  220, "Purple"), (80,  200, 200, "Teal"),
            (220, 160, 60,  "Orange"), (160, 160, 160, "Grey"),   (220, 220, 80,  "Yellow"),
        };

        private void CreateFrameFromSelection()
        {
            if (_selectedNodes.Count == 0) return;
            const double pad = 22;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            var nodes = new List<StateNode>();
            foreach (var node in _selectedNodes)
            {
                if (node.RawObject == null) continue;
                if (!_nodeVisualsByObject.TryGetValue(node.RawObject, out var vis)) continue;
                double lx = Canvas.GetLeft(vis), ty = Canvas.GetTop(vis);
                minX = Math.Min(minX, lx);   minY = Math.Min(minY, ty);
                maxX = Math.Max(maxX, lx + NodeW); maxY = Math.Max(maxY, ty + NodeH);
                nodes.Add(node);
            }
            if (nodes.Count == 0) return;

            var result = PromptFrameProperties("New Frame", FrameColorPresets[0]);
            if (result == null) return;

            var frame = new NodeFrame
            {
                Label       = result.Value.label,
                Color       = Color.FromArgb(45, result.Value.col.r, result.Value.col.g, result.Value.col.b),
                IsUserFrame = true,
                Nodes       = nodes,
                X = minX - pad, Y = minY - pad - 24,
                W = maxX - minX + pad * 2,
                H = maxY - minY + pad * 2 + 24
            };
            _frames.Add(frame);
            DrawSingleFrame(frame, isUserFrame: true);
        }

        private (string label, (byte r, byte g, byte b, string name) col)?
            PromptFrameProperties(string defLabel, (byte r, byte g, byte b, string name) defColor)
        {
            string resultLabel = defLabel;
            var resultColor    = defColor;

            var win = new Window
            {
                Title = "Create Frame", Width = 340, Height = 195,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background    = BrushPanelBg,
                BorderBrush   = BrushBorder,
                BorderThickness = new Thickness(1), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
            };
            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock { Text = "Label:", Foreground = BrushTextDim, FontSize=11, Margin=new Thickness(0,0,0,5) });
            var tb = new TextBox { Text=defLabel, Background=BrushInputBg, Foreground=BrushText, BorderBrush=BrushBorder, CaretBrush=Brushes.White, Padding=new Thickness(6,4,6,4), FontSize=11 };
            root.Children.Add(tb);
            root.Children.Add(new TextBlock { Text="Color:", Foreground=BrushTextDim, FontSize=11, Margin=new Thickness(0,10,0,5) });
            var colorRow = new WrapPanel();
            Border selSwatch = null;
            foreach (var preset in FrameColorPresets)
            {
                var p = preset;
                var sw = new Border { Width=26, Height=26, Margin=new Thickness(0,0,5,0), Background=new SolidColorBrush(Color.FromRgb(p.r,p.g,p.b)), BorderBrush=Brushes.Transparent, BorderThickness=new Thickness(2), CornerRadius=new CornerRadius(4), Cursor=Cursors.Hand, ToolTip=p.name };
                if (p.name == defColor.name) { sw.BorderBrush=Brushes.White; selSwatch=sw; }
                sw.MouseLeftButtonDown += (s,e) => { if(selSwatch!=null) selSwatch.BorderBrush=Brushes.Transparent; sw.BorderBrush=Brushes.White; selSwatch=sw; resultColor=p; e.Handled=true; };
                colorRow.Children.Add(sw);
            }
            if (selSwatch == null && colorRow.Children.Count > 0) { selSwatch = colorRow.Children[0] as Border; if(selSwatch!=null) selSwatch.BorderBrush=Brushes.White; }
            root.Children.Add(colorRow);
            var btnRow = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,14,0,0) };
            var ok  = new Button { Content="Create", Width=75, Margin=new Thickness(0,0,8,0), Background=BrushControl, Foreground=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0,5,0,5) };
            var can = new Button { Content="Cancel", Width=75, Background=BrushBorder, Foreground=BrushText, BorderThickness=new Thickness(0), Padding=new Thickness(0,5,0,5) };
            ok.Click  += (s,e)=>{ resultLabel=tb.Text?.Trim(); win.DialogResult=true;  win.Close(); };
            can.Click += (s,e)=>{ win.DialogResult=false; win.Close(); };
            tb.KeyDown+= (s,e)=>{ if(e.Key==Key.Return){ resultLabel=tb.Text?.Trim(); win.DialogResult=true; win.Close(); } };
            btnRow.Children.Add(ok); btnRow.Children.Add(can);
            root.Children.Add(btnRow);
            win.Content = root;
            try { var owner=Window.GetWindow(_canvas); if(owner!=null) win.Owner=owner; } catch{}
            tb.SelectAll();
            return win.ShowDialog()==true ? ((string,(byte,byte,byte,string))?)(resultLabel, resultColor) : null;
        }

        private string PromptFrameName(string defaultName)
        {
            string result = defaultName;
            var win = new Window { Title="Rename Frame", Width=300, Height=120, WindowStartupLocation=WindowStartupLocation.CenterOwner, Background=BrushPanelBg, BorderBrush=BrushBorder, BorderThickness=new Thickness(1), ResizeMode=ResizeMode.NoResize, ShowInTaskbar=false };
            var sp = new StackPanel { Margin=new Thickness(14) };
            sp.Children.Add(new TextBlock { Text="Frame label:", Foreground=BrushTextDim, FontSize=11, Margin=new Thickness(0,0,0,6) });
            var tb = new TextBox { Text=defaultName, Background=BrushInputBg, Foreground=BrushText, BorderBrush=BrushBorder, Padding=new Thickness(6,4,6,4), CaretBrush=Brushes.White, FontSize=11 };
            sp.Children.Add(tb);
            var btnRow = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,12,0,0) };
            var ok  = new Button { Content="OK",     Width=70, Margin=new Thickness(0,0,8,0), Background=BrushControl, Foreground=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0,5,0,5) };
            var can = new Button { Content="Cancel", Width=70, Background=BrushBorder, Foreground=BrushText, BorderThickness=new Thickness(0), Padding=new Thickness(0,5,0,5) };
            ok.Click  += (s,e)=>{ result=tb.Text?.Trim(); win.DialogResult=true;  win.Close(); };
            can.Click += (s,e)=>{ result=null;            win.DialogResult=false; win.Close(); };
            tb.KeyDown+= (s,e)=>{ if(e.Key==Key.Return){ result=tb.Text?.Trim(); win.DialogResult=true; win.Close(); } };
            btnRow.Children.Add(ok); btnRow.Children.Add(can);
            sp.Children.Add(btnRow); win.Content=sp;
            try { var owner=Window.GetWindow(_canvas); if(owner!=null) win.Owner=owner; } catch{}
            tb.SelectAll();
            return win.ShowDialog()==true ? result : null;
        }
        private void DrawSingleFrame(NodeFrame frame, bool isUserFrame = false)
        {
            var border = BuildFrameBorder(frame, isUserFrame || frame.IsUserFrame);
            // Insert above auto-frames but below nodes
            int insertIdx = _canvas.Children.Count;
            for (int i = 0; i < _canvas.Children.Count; i++)
            {
                if (_canvas.Children[i] is Canvas) { insertIdx = i; break; }
            }
            _canvas.Children.Insert(Math.Max(0, insertIdx), border);
        }

        private Border BuildFrameBorder(NodeFrame frame, bool isUserFrame = false)
        {
            var accentColor = Color.FromArgb(
                Math.Min((byte)200, (byte)(frame.Color.A * 2)),
                frame.Color.R, frame.Color.G, frame.Color.B);

            var border = new Border
            {
                Width           = frame.W,
                Height          = frame.H,
                Background      = new SolidColorBrush(frame.Color),
                BorderBrush     = new SolidColorBrush(accentColor),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Tag             = frame
            };

            // Header bar with label + (for user frames) rename/delete buttons
            var headerGrid = new Grid { VerticalAlignment = VerticalAlignment.Top };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (isUserFrame)
            {
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var label = new TextBlock
            {
                Text       = frame.Label,
                Foreground = new SolidColorBrush(accentColor),
                FontSize   = 11, FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(8, 4, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            headerGrid.Children.Add(label);

            if (isUserFrame)
            {
                // Rename button
                var renameBtn = new TextBlock { Text = "✎", Foreground = new SolidColorBrush(Color.FromRgb(160,160,180)), FontSize = 11, Margin = new Thickness(0,4,4,0), Cursor = Cursors.Hand, ToolTip = "Rename frame" };
                Grid.SetColumn(renameBtn, 1);
                renameBtn.MouseLeftButtonDown += (s, e) =>
                {
                    // Find current color in presets for default selection
                    var cur = FrameColorPresets.FirstOrDefault(p => p.r == frame.Color.R && p.g == frame.Color.G) ;
                    if (cur == default) cur = FrameColorPresets[0];
                    var result = PromptFrameProperties(frame.Label, cur);
                    if (result != null)
                    {
                        frame.Label = result.Value.label;
                        frame.Color = Color.FromArgb(45, result.Value.col.r, result.Value.col.g, result.Value.col.b);
                        label.Text  = result.Value.label;
                        var accent = Color.FromArgb(200, result.Value.col.r, result.Value.col.g, result.Value.col.b);
                        label.Foreground  = new SolidColorBrush(accent);
                        border.Background = new SolidColorBrush(frame.Color);
                        border.BorderBrush= new SolidColorBrush(Color.FromArgb(150, result.Value.col.r, result.Value.col.g, result.Value.col.b));
                    }
                    e.Handled = true;
                };
                headerGrid.Children.Add(renameBtn);

                // Delete button
                var delBtn = new TextBlock { Text = "✕", Foreground = new SolidColorBrush(Color.FromRgb(180,80,80)), FontSize = 11, Margin = new Thickness(0,4,6,0), Cursor = Cursors.Hand, ToolTip = "Remove frame" };
                Grid.SetColumn(delBtn, 2);
                delBtn.MouseLeftButtonDown += (s, e) =>
                {
                    _frames.Remove(frame);
                    _canvas.Children.Remove(border);
                    e.Handled = true;
                };
                headerGrid.Children.Add(delBtn);
            }

            border.Child = headerGrid;
            Canvas.SetLeft(border, frame.X);
            Canvas.SetTop (border, frame.Y);

            // Drag - frame drag moves contained nodes
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (_boxSelecting) return;
                _dragFrame       = frame;
                _dragFrameVisual = border;
                _dragFrameOffset = e.GetPosition(_canvas);
                e.Handled = true;
            };

            return border;
        }

        private void AutoBuildFrames()
        {
            _frames.Clear();
            const double pad = 16;
            var byCat = _activeNodes
                .Where(n => n.RawObject != null && _nodeVisualsByObject.ContainsKey(n.RawObject))
                .GroupBy(n => n.Category ?? "Other");

            foreach (var grp in byCat)
            {
                if (grp.Count() < 2) continue;  // skip single-node categories
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var node in grp)
                {
                    var vis = _nodeVisualsByObject[node.RawObject] as Canvas;
                    if (vis == null) continue;
                    double lx = Canvas.GetLeft(vis), ty = Canvas.GetTop(vis);
                    minX = Math.Min(minX, lx);   minY = Math.Min(minY, ty);
                    maxX = Math.Max(maxX, lx + NodeW); maxY = Math.Max(maxY, ty + NodeH);
                }
                _frames.Add(new NodeFrame
                {
                    Label = grp.Key,
                    Color = CategoryFrameColor(grp.Key),
                    Nodes = grp.ToList(),
                    X = minX - pad, Y = minY - pad - 22,
                    W = maxX - minX + pad * 2,
                    H = maxY - minY + pad * 2 + 22
                });
            }
        }

        private Color CategoryFrameColor(string cat)
        {
            switch (cat)
            {
                case "Lightsaber": return Color.FromArgb(35, 100, 180, 255);
                case "Blaster":    return Color.FromArgb(35, 255, 160,  60);
                case "Melee":      return Color.FromArgb(35, 220,  80,  80);
                case "Status":     return Color.FromArgb(35, 180,  80, 220);
                case "Idle":       return Color.FromArgb(35,  80, 200, 120);
                default:           return Color.FromArgb(35, 120, 120, 140);
            }
        }

        private void DrawFrames()
        {
            // Auto frames deepest (index 0), then user frames above them but below nodes.
            // Insert auto first so they end up at 0; user frames inserted after at nodeIdx.
            foreach (var frame in _frames.Where(f => !f.IsUserFrame))
                _canvas.Children.Insert(0, BuildFrameBorder(frame, false));

            // Find first node Canvas to know where nodes start
            int nodeIdx = _canvas.Children.Count;
            for (int i = 0; i < _canvas.Children.Count; i++)
                if (_canvas.Children[i] is Canvas) { nodeIdx = i; break; }

            // Insert user frames just below nodes (supports nested user frames by order)
            foreach (var frame in _frames.Where(f => f.IsUserFrame))
            {
                _canvas.Children.Insert(nodeIdx, BuildFrameBorder(frame, true));
                nodeIdx++;  // next user frame above previous
            }
        }

        // ── Flowchart: BFS left-to-right by transition depth ─────────────────

        private void LayoutFlowchart()
        {
            var activeSet = new HashSet<StateNode>(_activeNodes);

            // ── Pass 1: BFS depth (column) assignment ─────────────────────
            var inDegree = _activeNodes.ToDictionary(n => n, n => 0);
            foreach (var n in _activeNodes)
                if (n.Transitions != null)
                    foreach (var t in n.Transitions)
                        if (t?.Target != null && activeSet.Contains(t.Target))
                            inDegree[t.Target]++;

            var depth  = new Dictionary<StateNode, int>();
            var queue  = new Queue<StateNode>();
            var queued = new HashSet<StateNode>();
            foreach (var n in _activeNodes.Where(n => inDegree[n] == 0))
            { depth[n] = 0; if (queued.Add(n)) queue.Enqueue(n); }
            if (queue.Count == 0 && _activeNodes.Count > 0)
            { depth[_activeNodes[0]] = 0; queued.Add(_activeNodes[0]); queue.Enqueue(_activeNodes[0]); }
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node.Transitions == null) continue;
                foreach (var t in node.Transitions)
                {
                    if (t?.Target == null || !activeSet.Contains(t.Target)) continue;
                    int d = depth[node] + 1;
                    if (!depth.ContainsKey(t.Target) || depth[t.Target] < d)
                    { depth[t.Target] = d; if (queued.Add(t.Target)) queue.Enqueue(t.Target); }
                }
            }
            int maxD = depth.Values.Count > 0 ? depth.Values.Max() : 0;
            foreach (var n in _activeNodes.Where(n => !depth.ContainsKey(n)))
                depth[n] = ++maxD;

            // ── Pass 2: Build columns ─────────────────────────────────────
            var columns = depth
                .GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                .Select(g => g.Select(kv => kv.Key).ToList()).ToList();

            // ── Pass 3: Row ordering ─────────────────────────────────────
            // Primary: use SequenceIndex from conduit Subjects (real play order).
            // Fallback: minimise crossings via predecessor Y position.
            var nodeRow = new Dictionary<StateNode, int>();
            bool hasSeqIndices = _activeNodes.Any(n => n.SequenceIndex >= 0);

            if (columns.Count > 0)
            {
                // Column 0: conduit always first
                columns[0] = columns[0]
                    .OrderBy(n => n.Name.IndexOf("Conduit", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                    .ThenBy(n => n.SequenceIndex >= 0 ? n.SequenceIndex : 9999)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
                for (int r = 0; r < columns[0].Count; r++) nodeRow[columns[0][r]] = r;

                for (int c = 1; c < columns.Count; c++)
                {
                    if (hasSeqIndices)
                    {
                        // Sort by real sequence index — this IS the play order
                        columns[c] = columns[c]
                            .OrderBy(n => n.SequenceIndex >= 0 ? n.SequenceIndex : 9999)
                            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    else
                    {
                        // Fallback: crossing-minimisation via predecessor avg row
                        var score = columns[c].ToDictionary(n => n, n => -1.0);
                        foreach (var pred in columns[c - 1])
                        {
                            if (pred.Transitions == null || !nodeRow.ContainsKey(pred)) continue;
                            foreach (var t in pred.Transitions)
                            {
                                if (t?.Target == null || !score.ContainsKey(t.Target)) continue;
                                score[t.Target] = score[t.Target] < 0
                                    ? nodeRow[pred]
                                    : (score[t.Target] + nodeRow[pred]) / 2.0;
                            }
                        }
                        double maxScore = score.Values.Where(v => v >= 0).DefaultIfEmpty(0).Max();
                        columns[c] = columns[c]
                            .OrderBy(n => score[n] >= 0 ? score[n] : maxScore + 1)
                            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    for (int r = 0; r < columns[c].Count; r++) nodeRow[columns[c][r]] = r;
                }
            }

            // ── Pass 4: Place nodes ───────────────────────────────────────
            // Centre each column vertically so short columns don't all start at y=0
            int maxRows = columns.Count > 0 ? columns.Max(c => c.Count) : 1;
            for (int c = 0; c < columns.Count; c++)
            {
                int rows = columns[c].Count;
                double colOffset = (maxRows - rows) / 2.0 * FlowRowH;
                for (int r = 0; r < rows; r++)
                {
                    var node = columns[c][r];
                    var container = CreateNodeContainer(node, new Point(
                        MarginLeft + c * FlowColW,
                        MarginTop + colOffset + r * FlowRowH));
                    _canvas.Children.Add(container);
                }
            }
        }

        // ── Category-column layout ────────────────────────────────────────────

        private void LayoutCategoryColumns()
        {
            int col = 0;
            foreach (var group in _activeNodes.GroupBy(n => n.Category ?? "Other").OrderBy(g => CategoryPriority(g.Key)))
            {
                int row = 0;
                foreach (var node in group.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var container = CreateNodeContainer(node, new Point(MarginLeft + col * ColSpacing, MarginTop + row * RowSpacing));
                    _canvas.Children.Add(container);
                    row++;
                }
                col++;
            }
        }

        // ── Connections ───────────────────────────────────────────────────────

        private void DrawAllConnections(bool horizontal)
        {
            // Remove existing connection visuals (Paths and Polygons) keeping node Canvases
            for (int i = _canvas.Children.Count - 1; i >= 0; i--)
            {
                var child = _canvas.Children[i];
                if (child is Path || child is Polygon) _canvas.Children.RemoveAt(i);
            }

            if (_activeNodes == null) return;
            var activeSet = new HashSet<object>(
                _activeNodes.Where(n => n.RawObject != null).Select(n => n.RawObject),
                ReferenceEqualityComparer.Instance);

            foreach (var node in _activeNodes)
            {
                if (node.Transitions == null || node.RawObject == null) continue;
                if (!_nodeVisualsByObject.ContainsKey(node.RawObject)) continue;
                foreach (var trans in node.Transitions)
                {
                    if (trans.Target?.RawObject == null) continue;
                    if (!activeSet.Contains(trans.Target.RawObject)) continue;
                    if (!_nodeVisualsByObject.ContainsKey(trans.Target.RawObject)) continue;
                    DrawConnection(_nodeVisualsByObject[node.RawObject],
                                   _nodeVisualsByObject[trans.Target.RawObject],
                                   trans.Condition, horizontal);
                }
            }
        }

        /// <summary>Updates the name TextBlock on an existing node card without re-rendering.</summary>
        private void RefreshNodeVisual(StateNode node)
        {
            if (node.RawObject == null) return;
            if (!_nodeVisualsByObject.TryGetValue(node.RawObject, out var vis)) return;
            var container = vis as Canvas;
            if (container == null) return;
            var border = container.Children.OfType<Border>().FirstOrDefault();
            var content = border?.Child as Grid;
            if (content == null) return;
            // Find contentRow (second child of content grid, row 1)
            var contentRow = content.Children.OfType<Grid>().FirstOrDefault();
            if (contentRow == null) return;
            // Find the StackPanel (last column of contentRow)
            var stack = contentRow.Children.OfType<StackPanel>().FirstOrDefault();
            if (stack == null) return;
            var blocks = stack.Children.OfType<TextBlock>().ToList();
            if (blocks.Count > 0) blocks[0].Text = NodeDisplayName(node);
            if (blocks.Count > 1) blocks[1].Text = BuildNodeSubtitle(node);
        }

        private Canvas CreateNodeVisual(StateNode node, Point pos)
        {
            bool sel  = node == _selectedNode;
            var catColor    = CategoryBorderBrush(node.Category);
            var selColor    = BrushControl;

            // ── Outer container (Canvas-positioned) ───────────────────────
            var container = new Canvas { Width = NodeW, Height = NodeH, IsHitTestVisible = true };
            Canvas.SetLeft(container, pos.X);
            Canvas.SetTop(container, pos.Y);

            // ── Node card ─────────────────────────────────────────────────
            var border = new Border
            {
                Width           = NodeW,
                Height          = NodeH,
                Background      = BrushPanelBg,
                BorderBrush     = sel ? selColor : catColor,
                BorderThickness = new Thickness(sel ? 0 : 0),  // we use top accent instead
                CornerRadius    = new CornerRadius(5),
                Cursor          = Cursors.Hand,
                Tag             = node,
                ClipToBounds    = true
            };

            // Category accent bar on top
            var accentBar = new Border
            {
                Height          = 4,
                Background      = sel ? selColor : catColor,
                VerticalAlignment = VerticalAlignment.Top
            };

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(accentBar, 0);
            content.Children.Add(accentBar);

            // ── Content row: [badge] [name + subtitle] ───────────────────
            var contentRow = new Grid { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetRow(contentRow, 1);
            contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Sequence badge — only shown when we have real order info
            if (node.SequenceIndex >= 0)
            {
                var badge = new Border
                {
                    Background    = BrushWindowBg,
                    BorderBrush   = sel
                        ? BrushControl
                        : CategoryBorderBrush(node.Category),
                    BorderThickness = new Thickness(1),
                    CornerRadius  = new CornerRadius(3),
                    Margin        = new Thickness(8, 0, 6, 0),
                    Padding       = new Thickness(5, 2, 5, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth      = 26
                };
                badge.Child = new TextBlock
                {
                    Text              = (node.SequenceIndex + 1).ToString(),
                    Foreground        = sel
                        ? BrushControl
                        : CategoryBorderBrush(node.Category),
                    FontSize          = 11,
                    FontWeight        = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip           = $"Play order: #{node.SequenceIndex + 1}"
                };
                Grid.SetColumn(badge, 0);
                contentRow.Children.Add(badge);
            }

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(node.SequenceIndex >= 0 ? 0 : 14, 0, 8, 0)
            };
            Grid.SetColumn(stack, 1);
            stack.Children.Add(new TextBlock
            {
                Text         = NodeDisplayName(node),
                Foreground   = Brushes.White,
                FontWeight   = FontWeights.SemiBold,
                FontSize     = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip      = node.Name
            });
            stack.Children.Add(new TextBlock
            {
                Text       = BuildNodeSubtitle(node),
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 155)),
                FontSize   = 10,
                Margin     = new Thickness(0, 2, 0, 0)
            });
            contentRow.Children.Add(stack);
            content.Children.Add(contentRow);
            border.Child = content;
            // Click to select - no capture, drag is handled at canvas level
            border.MouseLeftButtonDown += (s, e) =>
            {
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (shift)
                    ShiftSelectNode(node);
                else
                    SelectNode(node);
                _dragNode   = container;
                _dragOffset = new Point(
                    e.GetPosition(_canvas).X - Canvas.GetLeft(container),
                    e.GetPosition(_canvas).Y - Canvas.GetTop(container));
                e.Handled   = true;
            };
            border.MouseRightButtonUp += (s, e) =>
            {
                SelectNode(node);
                ShowNodeContextMenu(node);
                e.Handled = true;
            };

            Canvas.SetLeft(border, 0);
            Canvas.SetTop(border, 0);
            container.Children.Add(border);

            // ── Input port (left edge, mid-height) ────────────────────────
            var portIn = new Ellipse
            {
                Width  = 12, Height = 12,
                Fill   = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Stroke = new SolidColorBrush(Color.FromRgb(120, 120, 140)),
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                IsHitTestVisible = true,
                ToolTip = "Input — drop wire here"
            };
            Canvas.SetLeft(portIn, -6);
            Canvas.SetTop(portIn, NodeH / 2 - 6);
            // Hover highlight
            portIn.MouseEnter += (s, e) =>
            {
                portIn.Fill   = BrushControl;
                portIn.Stroke = Brushes.White;
                if (_wiringActive) _wireHoverTarget = node;
            };
            portIn.MouseLeave += (s, e) =>
            {
                portIn.Fill   = new SolidColorBrush(Color.FromRgb(45, 45, 50));
                portIn.Stroke = new SolidColorBrush(Color.FromRgb(120, 120, 140));
                if (_wireHoverTarget == node) _wireHoverTarget = null;
            };
            // Drop wire onto input port
            portIn.MouseLeftButtonUp += (s, e) =>
            {
                if (_wiringActive && _wireFromNode != null && _wireFromNode != node)
                {
                    CommitWire(_wireFromNode, node);
                    e.Handled = true;
                }
            };
            container.Children.Add(portIn);

            // ── Output port (right edge, mid-height) ──────────────────────
            var portOut = new Ellipse
            {
                Width  = 12, Height = 12,
                Fill   = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Stroke = new SolidColorBrush(Color.FromRgb(120, 120, 140)),
                StrokeThickness = 1.5,
                Cursor = Cursors.Cross,
                IsHitTestVisible = true,
                ToolTip = "Output — drag to connect"
            };
            Canvas.SetLeft(portOut, NodeW - 6);
            Canvas.SetTop(portOut, NodeH / 2 - 6);
            portOut.MouseEnter += (s, e) =>
            {
                portOut.Fill   = new SolidColorBrush(Color.FromRgb(80, 180, 80));
                portOut.Stroke = Brushes.White;
            };
            portOut.MouseLeave += (s, e) =>
            {
                portOut.Fill   = new SolidColorBrush(Color.FromRgb(45, 45, 50));
                portOut.Stroke = new SolidColorBrush(Color.FromRgb(120, 120, 140));
            };
            // Start wire drag from output port
            portOut.MouseLeftButtonDown += (s, e) =>
            {
                _wiringActive    = true;
                _wireFromNode    = node;
                _wireHoverTarget = null;
                // Output port centre in canvas space
                var nodePos = new Point(Canvas.GetLeft(container), Canvas.GetTop(container));
                _wireStartCanvas = new Point(nodePos.X + NodeW - 1, nodePos.Y + NodeH / 2);
                // Create the preview path
                _wireDragPath = new Path
                {
                    Stroke          = new SolidColorBrush(Color.FromRgb(80, 200, 80)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 6, 3 },
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(_wireDragPath);
                _canvas.CaptureMouse();
                e.Handled = true;
            };
            container.Children.Add(portOut);

            // Store container (not border) so Canvas.GetLeft/Top works for connection drawing
            // Connection drawing reads position from the stored visual
            if (node.RawObject != null) _nodeVisualsByObject[node.RawObject] = container;
            container.Tag = node;

            return container;
        }

        // Alias so callers use the right name
        private Canvas CreateNodeContainer(StateNode node, Point pos) => CreateNodeVisual(node, pos);

        private void SelectNode(StateNode node)
        {
            _selectedNode = node;
            _selectedNodes.Clear();
            if (node != null) _selectedNodes.Add(node);
            HighlightSelectedNodes();
            PopulateEditPanel(node);
        }

        private void ShiftSelectNode(StateNode node)
        {
            if (node == null) return;
            if (_selectedNodes.Contains(node))
                _selectedNodes.Remove(node);   // deselect if already selected
            else
                _selectedNodes.Add(node);
            // Keep _selectedNode as the last clicked for inspector
            _selectedNode = _selectedNodes.Count > 0
                ? (_selectedNodes.Contains(node) ? node : _selectedNodes.Last())
                : null;
            HighlightSelectedNodes();
            if (_selectedNode != null) PopulateEditPanel(_selectedNode);
        }

        private void HighlightSelectedNodes()
        {
            foreach (var kvp in _nodeVisualsByObject)
            {
                var container = kvp.Value as Canvas;
                if (container == null) continue;
                var stateNode = container.Tag as StateNode;
                bool isSel = _selectedNodes.Contains(stateNode);
                var border = container.Children.OfType<Border>().FirstOrDefault();
                if (border == null) continue;
                border.BorderThickness = new Thickness(isSel ? 2 : 0);
                border.BorderBrush     = isSel
                    ? BrushControl
                    : Brushes.Transparent;
                var content = border.Child as Grid;
                var accent  = content?.Children.OfType<Border>().FirstOrDefault();
                if (accent != null)
                    accent.Background = isSel
                        ? BrushControl
                        : CategoryBorderBrush(stateNode?.Category);
            }
        }

        private void DrawConnection(UIElement from, UIElement to, string condition, bool horizontal)
        {
            double x1, y1, x2, y2;
            double fw = (from as FrameworkElement)?.Width  ?? NodeW;
            double fh = (from as FrameworkElement)?.Height ?? NodeH;
            double tw = (to   as FrameworkElement)?.Width  ?? NodeW;
            double th = (to   as FrameworkElement)?.Height ?? NodeH;
            if (horizontal)
            {
                x1 = Canvas.GetLeft(from) + fw;
                y1 = Canvas.GetTop(from)  + fh / 2;
                x2 = Canvas.GetLeft(to);
                y2 = Canvas.GetTop(to)    + th / 2;
            }
            else
            {
                x1 = Canvas.GetLeft(from) + fw / 2;
                y1 = Canvas.GetTop(from)  + fh;
                x2 = Canvas.GetLeft(to)   + tw / 2;
                y2 = Canvas.GetTop(to);
            }

            bool hasCondition = !string.IsNullOrEmpty(condition);
            var wireColor = hasCondition
                ? Color.FromRgb(180, 120, 255)   // purple  - conditioned
                : Color.FromRgb(100, 160, 220);  // blue    - unconditional
            var brush = new SolidColorBrush(wireColor);

            // ── Bezier curve ──────────────────────────────────────────────
            double cx = horizontal
                ? Math.Abs(x2 - x1) * 0.55
                : Math.Abs(y2 - y1) * 0.55;

            double c1x = horizontal ? x1 + cx : x1;
            double c1y = horizontal ? y1       : y1 + cx;
            double c2x = horizontal ? x2 - cx  : x2;
            double c2y = horizontal ? y2        : y2 - cx;

            var seg  = new BezierSegment(new Point(c1x, c1y), new Point(c2x, c2y), new Point(x2, y2), true);
            var fig  = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
            fig.Segments.Add(seg);
            var geo  = new PathGeometry(); geo.Figures.Add(fig);
            var path = new Path
            {
                Data            = geo,
                Stroke          = brush,
                StrokeThickness = 2,
                ToolTip         = !string.IsNullOrEmpty(condition) ? condition : null,
                IsHitTestVisible = false
            };
            _canvas.Children.Insert(0, path);

            // ── Arrowhead at target end ───────────────────────────────────
            DrawArrowhead(x2, y2, horizontal ? (x2 < x1 ? Math.PI : 0) : Math.PI / 2, brush);
        }

        private void DrawArrowhead(double tipX, double tipY, double angleRad, SolidColorBrush brush)
        {
            const double size = 8;
            const double spread = 0.45; // radians half-angle

            double ax = tipX - size * Math.Cos(angleRad - spread);
            double ay = tipY - size * Math.Sin(angleRad - spread);
            double bx = tipX - size * Math.Cos(angleRad + spread);
            double by = tipY - size * Math.Sin(angleRad + spread);

            var arrow = new Polygon
            {
                Points = new PointCollection { new Point(tipX, tipY), new Point(ax, ay), new Point(bx, by) },
                Fill   = brush,
                IsHitTestVisible = false
            };
            _canvas.Children.Insert(0, arrow);
        }

        // =========================================================================
        //  Edit panel
        // =========================================================================

        private void PopulateEditPanel(StateNode node)
        {
            if (_editPanel == null || node == null) return;
            _editPanel.Items.Clear();
            _editPanel.Background = BrushPanelBg;

            var ov = MakeTab("Overview", out var op);

            // ── Editable Name (__Id) ─────────────────────────────────────
            AddEditableRow(op, "Name", node.Name ?? "", newVal =>
            {
                if (string.IsNullOrWhiteSpace(newVal) || newVal == node.Name) return;
                try
                {
                    SetEbxName(node.RawObject, newVal);
                    node.Name   = newVal;
                    node.Parsed = NameParser.Parse(newVal);
                    node.Category = DetermineCategory(newVal, node.Parsed);
                    AssetModified = true;
                    PopulateTree();
                    RefreshNodeVisual(node);
                    App.Logger.Log($"StateMachineEditor: renamed node to [{newVal}]");
                }
                catch (Exception ex) { App.Logger.LogWarning($"StateMachineEditor: rename failed: {ex.Message}"); }
            });

            AddEditRow(op, "Action",    node.Parsed.ActionPath);
            AddEditRow(op, "Character", node.Parsed.Character ?? "");
            AddEditRow(op, "Suffix",    node.Parsed.Suffix.ToString());
            AddEditRow(op, "Category",  node.Category ?? "");
            AddEditRow(op, "GUID", TryGetGuid(node.RawObject));

            // Preview thumbnail (if available)
            string previewPath = FindPreviewPath(node);
            if (previewPath != null)
            {
                op.Children.Add(new Border { Height = 1, Background = BrushBorder, Margin = new Thickness(0, 6, 0, 4) });
                op.Children.Add(new TextBlock { Text = "PREVIEW", Foreground = BrushTextDim, FontSize = 9, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 2) });
                var previewEl = CreatePreviewElement(previewPath, 200, 140);
                if (previewEl != null)
                {
                    previewEl.HorizontalAlignment = HorizontalAlignment.Left;
                    previewEl.Margin = new Thickness(4, 0, 0, 0);
                    op.Children.Add(previewEl);
                }
            }

            // ── Subject controllers (clip, blend, etc.) ──────────────────
            var subjects = GetNodeSubjects(node);
            for (int si = 0; si < subjects.Count; si++)
            {
                var subObj  = subjects[si];
                var subName = GetRawName(subObj);
                var subType = subObj.GetType().Name.Replace("CharacterState","").Replace("ControllerData","");
                op.Children.Add(new Border { Height=1, Background=BrushBorder, Margin=new Thickness(0,8,0,6) });
                // Subject header row with name + type
                var hdr = new Grid();
                hdr.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
                hdr.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                hdr.Children.Add(new TextBlock { Text=$"{subType.ToUpperInvariant()}", Foreground=BrushTextDim, FontSize=9, FontWeight=FontWeights.SemiBold, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(4,0,0,0) });
                var copyBtn = new Button { Content="Copy from...", FontSize=9, Padding=new Thickness(6,2,6,2), Background=BrushInputBg, Foreground=new SolidColorBrush(Color.FromRgb(180,180,200)), BorderBrush=new SolidColorBrush(Color.FromRgb(80,80,100)), BorderThickness=new Thickness(1), Cursor=Cursors.Hand };
                var capturedSi = si; var capturedSubObj = subObj;
                copyBtn.Click += (s,e) => ShowCopyFromMenu(node, capturedSi, capturedSubObj, copyBtn);
                Grid.SetColumn(copyBtn, 1);
                hdr.Children.Add(copyBtn);
                op.Children.Add(hdr);
                // Name row
                AddEditableRow(op, "Name", subName, newVal =>
                {
                    if (string.IsNullOrWhiteSpace(newVal)) return;
                    try { SetEbxName(subObj, newVal); AssetModified = true; }
                    catch { }
                });
                // All writable primitive properties
                foreach (var (pname, pval, prop, pobj) in GetEditableProps(subObj))
                {
                    var capturedProp = prop; var capturedObj = pobj;
                    AddEditableRow(op, pname, pval, newVal =>
                    {
                        try
                        {
                            object converted = Convert.ChangeType(newVal, capturedProp.PropertyType);
                            capturedProp.SetValue(capturedObj, converted);
                            AssetModified = true;
                        }
                        catch { }
                    });
                }
            }

            _editPanel.Items.Add(ov);

            var tt = MakeTab($"Transitions ({node.Transitions?.Count ?? 0})", out var tp);
            if (node.Transitions?.Count > 0)
                foreach (var t in node.Transitions)
                {
                    var row = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(55,55,68)), BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(4,3,4,3), Margin = new Thickness(0,2,0,0) };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = $"-> {NodeDisplayName(t.Target)}", Foreground = new SolidColorBrush(Color.FromRgb(120,210,120)), FontWeight = FontWeights.SemiBold });
                    if (!string.IsNullOrEmpty(t.Condition)) sp.Children.Add(new TextBlock { Text = t.Condition, Foreground = new SolidColorBrush(Color.FromRgb(190,140,255)), FontSize = 10, Margin = new Thickness(10,0,0,0) });
                    row.Child = sp; tp.Children.Add(row);
                }
            else tp.Children.Add(new TextBlock { Text = "(no transitions)", Foreground = new SolidColorBrush(Color.FromRgb(90,90,105)), FontStyle = FontStyles.Italic, Margin = new Thickness(4) });
            _editPanel.Items.Add(tt);

            if (node.Details?.Count > 0)
            {
                var dt = MakeTab($"Details ({node.Details.Count})", out var dp);
                foreach (var kg in node.Details.GroupBy(d => d.Kind).OrderBy(g => (int)g.Key))
                {
                    dp.Children.Add(new TextBlock { Text = kg.Key.ToString(), Foreground = DetailKindColor(kg.Key), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,6,0,2) });
                    foreach (var d in kg) dp.Children.Add(new TextBlock { Text = $"  {d.Label}", Foreground = new SolidColorBrush(Color.FromRgb(200,200,215)), FontSize = 11 });
                }
                _editPanel.Items.Add(dt);
            }

            var rt = MakeTab("Raw", out var rp);
            if (node.RawObject != null)
                try { foreach (var prop in node.RawObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
                      { object val; try { val = prop.GetValue(node.RawObject); } catch { continue; }
                        if (val == null) continue; var vt = val.GetType();
                        if (vt.IsPrimitive || val is string || val is Guid || vt.IsEnum) AddEditRow(rp, prop.Name, val.ToString()); } }
                catch { }
            _editPanel.Items.Add(rt);
            _editPanel.SelectedIndex = 0;
        }

        private TabItem MakeTab(string header, out StackPanel panel)
        {
            panel = new StackPanel { Margin = new Thickness(6), Background = BrushPanelBg };
            return new TabItem
            {
                Header  = header,
                Content = new ScrollViewer
                {
                    Content = panel,
                    Background = BrushPanelBg,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            };
        }

        private void AddEditRow(StackPanel panel, string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lbl = new TextBlock
            {
                Text = label,
                Foreground = BrushTextDim,  // #858585
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            var val = new TextBox
            {
                Text = value,
                Foreground = BrushText,  // #D4D4D4
                Background = BrushInputBg,     // #3C3C3C
                BorderBrush = BrushBorder,    // #3F3F46
                CaretBrush = BrushText,
                BorderThickness = new Thickness(0, 0, 0, 1),
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2),
                IsReadOnly = true
            };
            Grid.SetColumn(lbl, 0); Grid.SetColumn(val, 1);
            row.Children.Add(lbl); row.Children.Add(val);
            panel.Children.Add(row);
        }

        private void AddEditableRow(StackPanel panel, string label, string value, Action<string> onCommit)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = label,
                Foreground = BrushTextDim,
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            var tb = new TextBox
            {
                Text = value,
                Foreground = BrushText,
                Background = BrushInputBg,
                BorderBrush = BrushControl,
                CaretBrush = BrushText,
                BorderThickness = new Thickness(0, 0, 0, 1),
                FontSize = 11, Padding = new Thickness(4, 2, 4, 2),
                IsReadOnly = false
            };
            // Subtle edit indicator - pencil icon
            var editMark = new TextBlock
            {
                Text = "✎", FontSize = 10,
                Foreground = BrushControl,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                ToolTip = "Press Enter to save"
            };

            string original = value;
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Return) { onCommit(tb.Text); original = tb.Text; tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); }
                if (e.Key == Key.Escape) { tb.Text = original; }
            };
            tb.LostFocus += (s, e) =>
            {
                if (tb.Text != original) { onCommit(tb.Text); original = tb.Text; }
            };

            Grid.SetColumn(lbl, 0); Grid.SetColumn(tb, 1); Grid.SetColumn(editMark, 2);
            row.Children.Add(lbl); row.Children.Add(tb); row.Children.Add(editMark);
            panel.Children.Add(row);
        }

        private void ShowCopyFromMenu(StateNode targetNode, int subjectIndex, object targetSubObj, FrameworkElement anchor)
        {
            string targetTypeName = targetSubObj?.GetType().Name ?? "";

            // Gather ALL compatible nodes across the entire asset (_allNodes)
            var candidates = new List<(StateNode node, object sub, string charKey)>();
            foreach (var srcNode in _allNodes)
            {
                if (srcNode == targetNode) continue;
                var srcSubs = GetNodeSubjects(srcNode);
                if (subjectIndex >= srcSubs.Count) continue;
                var srcSub = srcSubs[subjectIndex];
                if (srcSub.GetType().Name != targetTypeName) continue;
                candidates.Add((srcNode, srcSub, srcNode.Parsed.IsValid ? srcNode.Parsed.CharacterKey : "?.Unknown"));
            }

            // Show searchable popup window
            var win = new Window
            {
                Title = "Copy properties from...", Width = 420, Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background    = BrushPanelBg,
                BorderBrush   = BrushBorder,
                BorderThickness = new Thickness(1), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Search box
            var searchBox = new TextBox
            {
                Background = BrushInputBg,
                Foreground = BrushText,
                BorderBrush = BrushControl,
                BorderThickness = new Thickness(0,0,0,1),
                CaretBrush = Brushes.White, Padding = new Thickness(8,6,8,6), FontSize=12,
                Margin = new Thickness(0)
            };
            // Placeholder
            searchBox.GotFocus  += (s,e) => { if(searchBox.Tag as string == "ph"){ searchBox.Text=""; searchBox.Foreground=BrushText; searchBox.Tag=null; }};
            searchBox.LostFocus += (s,e) => { if(string.IsNullOrEmpty(searchBox.Text)){ searchBox.Text="Search nodes..."; searchBox.Foreground=BrushTextDim; searchBox.Tag="ph"; }};
            searchBox.Text="Search nodes..."; searchBox.Foreground=BrushTextDim; searchBox.Tag="ph";
            Grid.SetRow(searchBox, 0); root.Children.Add(searchBox);

            // Results list
            var listBox = new ListBox
            {
                Background = BrushPanelBg,
                BorderThickness = new Thickness(0),
                Foreground = BrushText,
                FontSize = 11
            };
            Grid.SetRow(listBox, 1); root.Children.Add(listBox);

            // Status bar
            var statusBar = new TextBlock
            {
                Foreground = BrushTextDim,
                FontSize = 10, Margin = new Thickness(8,4,8,4)
            };
            Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);
            win.Content = root;

            void Rebuild(string filter)
            {
                listBox.Items.Clear();
                var filtered = string.IsNullOrWhiteSpace(filter) || filter == "Search nodes..."
                    ? candidates
                    : candidates.Where(c =>
                        NodeDisplayName(c.node).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        c.charKey.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                // Group by character
                foreach (var grp in filtered.GroupBy(c => c.charKey).OrderBy(g => g.Key))
                {
                    // Group header
                    var hdr = new ListBoxItem
                    {
                        Content = grp.Key, IsEnabled = false,
                        Foreground = BrushTextMuted,
                        FontWeight = FontWeights.SemiBold, FontSize = 10,
                        Background = BrushGroupHeaderBg,
                        Padding = new Thickness(8,3,8,3)
                    };
                    listBox.Items.Add(hdr);

                    foreach (var (srcNode, srcSub, charKey) in grp.OrderBy(c => NodeDisplayName(c.node)))
                    {
                        var sn = srcNode; var ss = srcSub;
                        var item = new ListBoxItem
                        {
                            Content = NodeDisplayName(sn),
                            Tag = (sn, ss),
                            Padding = new Thickness(20,4,8,4),
                            Background = Brushes.Transparent,
                            Foreground = BrushText
                        };
                        item.MouseDoubleClick += (s,e) =>
                        {
                            CopySubjectProperties(ss, targetSubObj, targetNode, sn);
                            win.Close();
                        };
                        listBox.Items.Add(item);
                    }
                }
                statusBar.Text = $"{filtered.Count} compatible nodes across {filtered.Select(c=>c.charKey).Distinct().Count()} characters";
            }

            searchBox.TextChanged += (s,e) => { if(searchBox.Tag as string != "ph") Rebuild(searchBox.Text); };
            Rebuild("");

            // Enter key on list = copy
            listBox.KeyDown += (s,e) =>
            {
                if (e.Key != Key.Return) return;
                if (listBox.SelectedItem is ListBoxItem li && li.Tag is ValueTuple<StateNode,object> tup)
                { CopySubjectProperties(tup.Item2, targetSubObj, targetNode, tup.Item1); win.Close(); }
            };

            try { var owner=Window.GetWindow(_canvas); if(owner!=null) win.Owner=owner; } catch{}
            win.ShowDialog();
        }

        private void CopySubjectProperties(object src, object dst, StateNode dstNode, StateNode srcNode)
        {
            if (src == null || dst == null) return;
            var srcType = src.GetType();
            var dstType = dst.GetType();
            if (srcType != dstType) return;

            int count = 0;
            // AssetIndex must never be overwritten — it's the object's position in AllControllerDatas
            var skip = new HashSet<string> { "__Id", "__InstanceGuid", "__Type", "Subjects", "AssetIndex" };
            foreach (var prop in srcType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (skip.Contains(prop.Name) || !prop.CanRead || !prop.CanWrite) continue;
                try
                {
                    var val = prop.GetValue(src);
                    if (val == null) continue;
                    var t = val.GetType();

                    // Copy value types (primitives, enums, strings)
                    if (t.IsPrimitive || t.IsEnum || val is string)
                    {
                        var oldVal = prop.GetValue(dst);
                        prop.SetValue(dst, val);
                        App.Logger.Log($"StateMachineEditor:   copied prop {prop.Name}: [{oldVal}] → [{val}]");
                        count++;
                    }
                    // Deep-copy inline struct lists (e.g. ClipInfos) — these contain
                    // the actual animation references and timing data
                    else if (val is System.Collections.IList srcList && prop.CanWrite)
                    {
                        var dstList = prop.GetValue(dst) as System.Collections.IList;
                        if (dstList == null) continue;

                        // Check if list items are inline structs (not PointerRefs)
                        // PointerRef items have Internal/External fields — skip those
                        bool isInlineList = srcList.Count > 0 &&
                            srcList[0] != null &&
                            !srcList[0].GetType().Name.Contains("PointerRef") &&
                            srcList[0].GetType().GetProperty("Internal") == null;

                        if (isInlineList)
                        {
                            int oldCount = dstList.Count;
                            dstList.Clear();
                            foreach (var item in srcList)
                                dstList.Add(item);
                            App.Logger.Log($"StateMachineEditor:   copied list {prop.Name}: {oldCount} items → {srcList.Count} items");
                            count++;
                        }
                    }
                    // Copy CString and other value-like types via reflection
                    else if (t.Name == "CString")
                    {
                        prop.SetValue(dst, val);
                        App.Logger.Log($"StateMachineEditor:   copied prop {prop.Name} (CString)");
                        count++;
                    }
                    // Copy PointerRef properties (e.g. TransformChannelController, ChannelController)
                    // These must be copied alongside ClipInfos to keep animation data consistent
                    else if (t.Name == "PointerRef")
                    {
                        prop.SetValue(dst, val);
                        App.Logger.Log($"StateMachineEditor:   copied ref {prop.Name}");
                        count++;
                    }
                }
                catch { }
            }

            if (count > 0)
            {
                // Rename the subject object to match source
                string srcSubName = GetRawName(src);
                string oldSubName = GetRawName(dst);
                if (!string.IsNullOrEmpty(srcSubName) && srcSubName != oldSubName)
                {
                    SetEbxName(dst, srcSubName);
                    App.Logger.Log($"StateMachineEditor: renamed subject [{oldSubName}] → [{srcSubName}]");
                }

                // Note: do NOT rename the parent node — its name is the identity within
                // the SeqFLOW chain and must match the character (e.g. Vader_Strike_3).
                // Renaming it to the source character's name breaks the game's lookup.

                AssetModified = true;
                App.Logger.Log($"StateMachineEditor: copied {count} properties from [{NodeDisplayName(srcNode)}] to [{NodeDisplayName(dstNode)}]");
                // Refresh inspector
                PopulateEditPanel(dstNode);
            }
        }

        private string TryGetGuid(object obj) { if (obj == null) return ""; try { return obj.GetType().GetProperty("__InstanceGuid")?.GetValue(obj)?.ToString() ?? ""; } catch { return ""; } }

        /// <summary>Gets all resolved Subject objects on this node.</summary>
        private List<object> GetNodeSubjects(StateNode node)
        {
            var result = new List<object>();
            if (node.RawObject == null) return result;
            try
            {
                var subProp = node.RawObject.GetType().GetProperty("Subjects");
                if (subProp == null) return result;
                var subjects = subProp.GetValue(node.RawObject) as System.Collections.IEnumerable;
                if (subjects == null) return result;
                foreach (var item in subjects)
                {
                    var resolved = ResolvePointerRef(item);
                    if (resolved != null) result.Add(resolved);
                }
            }
            catch { }
            return result;
        }

        private string GetNodeClipName(StateNode node)
        {
            var subs = GetNodeSubjects(node);
            return subs.Count > 0 ? GetRawName(subs[0]) : null;
        }

        private bool SetNodeClipName(StateNode node, string newName)
        {
            if (node.RawObject == null || string.IsNullOrWhiteSpace(newName)) return false;
            try
            {
                var subProp = node.RawObject.GetType().GetProperty("Subjects");
                if (subProp == null) return false;
                var subjects = subProp.GetValue(node.RawObject) as System.Collections.IEnumerable;
                if (subjects == null) return false;
                foreach (var item in subjects)
                {
                    var resolved = ResolvePointerRef(item);
                    if (resolved == null) continue;
                    dynamic d = resolved;
                    d.__Id = newName;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Gets all editable primitive/enum properties from an object as name→value pairs.</summary>
        private List<(string name, string val, PropertyInfo prop, object obj)> GetEditableProps(object obj)
        {
            var result = new List<(string, string, PropertyInfo, object)>();
            if (obj == null) return result;
            try
            {
                var skip = new HashSet<string> { "__Id", "__InstanceGuid", "__Type", "Subjects" };
                foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .OrderBy(p => p.Name))
                {
                    if (skip.Contains(p.Name) || !p.CanWrite) continue;
                    try
                    {
                        var v = p.GetValue(obj);
                        if (v == null) continue;
                        var t = v.GetType();
                        if (t.IsPrimitive || t.IsEnum || v is string || v is Guid)
                            result.Add((p.Name, v.ToString(), p, obj));
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        // =========================================================================
        //  Transitions
        // =========================================================================

        private int ScanTransitions(StateNode node, Dictionary<Guid, StateNode> nodeByGuid, Dictionary<object, StateNode> rawMap = null)
        {
            int count = 0;
            if (node.RawObject == null) return 0;
            var map = rawMap ?? _nodeByRawObj;
            try
            {
                foreach (var cname in new[] { "Transitions", "StateTransitions", "TransitionList", "OutTransitions" })
                {
                    var prop = node.RawObject.GetType().GetProperty(cname);
                    if (prop == null) continue;
                    if (!(prop.GetValue(node.RawObject) is System.Collections.IEnumerable list)) continue;
                    foreach (var t in list)
                        if (t != null) count += TryAddTransition(node, t, nodeByGuid, map);
                }
            }
            catch { }
            return count;
        }

        private int TryAddTransition(StateNode node, object trans, Dictionary<Guid, StateNode> nodeByGuid, Dictionary<object, StateNode> rawMap = null)
        {
            var nodeMap = rawMap ?? _nodeByRawObj;
            // The transition object IS a TransitionCondition with:
            //   Target           - PointerRef to target node (may be null if routing via ChildConditions)
            //   ChildConditions  - list of nested TransitionConditions (each may have their own Target)
            //   ConditionType, BoolChannel1, IntChannel, etc.
            //
            // Strategy: find the first non-null resolved Target in this condition or its children.

            string cond = ExtractCondText(trans);
            object targetObj = FindTransitionTarget(trans);

            if (targetObj == null) return 0;

            StateNode target;
            if (!nodeMap.TryGetValue(targetObj, out target))
            {
                Guid? tg = GetGuidFromObj(targetObj);
                if (tg.HasValue) nodeByGuid.TryGetValue(tg.Value, out target);
            }

            if (target == null) return 0;
            node.Transitions.Add(new Transition { Source = node, Target = target, Condition = cond });
            return 1;
        }

        /// <summary>
        /// Recursively searches a TransitionCondition (and its ChildConditions) for a
        /// non-null resolved Target PointerRef.
        /// </summary>
        private object FindTransitionTarget(object tc, int depth = 0)
        {
            if (tc == null || depth > 4) return null;
            try
            {
                // Try Target directly on this condition
                var targetProp = tc.GetType().GetProperty("Target");
                if (targetProp != null)
                {
                    var targetVal = targetProp.GetValue(tc);
                    var resolved  = ResolvePointerRef(targetVal);
                    if (resolved != null) return resolved;
                }

                // Try ChildConditions
                var ccProp = tc.GetType().GetProperty("ChildConditions");
                if (ccProp != null)
                {
                    var cc = ccProp.GetValue(tc);
                    if (cc is System.Collections.IEnumerable cenum)
                        foreach (var child in cenum)
                        {
                            var r = FindTransitionTarget(child, depth + 1);
                            if (r != null) return r;
                        }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Reads a named property and resolves any PointerRef to the raw object.</summary>
        private object ExtractNodeObj(object obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var n in names)
            {
                try
                {
                    var v = t.GetProperty(n)?.GetValue(obj);
                    if (v == null) continue;
                    var resolved = ResolvePointerRef(v);
                    if (resolved != null) return resolved;
                }
                catch { }
            }
            return null;
        }

        private Guid? GetGuidFromObj(object obj)
        {
            if (obj == null) return null;
            try
            {
                dynamic d = obj;
                var acg = d.GetInstanceGuid();
                if ((bool)acg.IsExported) return (Guid)acg.ExportedGuid;
            }
            catch { }
            try
            {
                var gv = obj.GetType().GetProperty("__InstanceGuid")?.GetValue(obj);
                if (gv is Guid g) return g;
                if (gv != null && Guid.TryParse(gv.ToString(), out Guid pg)) return pg;
            }
            catch { }
            return null;
        }

        private Guid? ExtractGuid(object obj, params string[] names)
        {
            var t = obj.GetType();
            foreach (var n in names)
            {
                try
                {
                    var v = t.GetProperty(n)?.GetValue(obj);
                    if (v == null) continue;
                    // Direct Guid
                    if (v is Guid g) return g;
                    // PointerRef - resolve to internal object then get its __InstanceGuid
                    var resolved = ResolvePointerRef(v);
                    if (resolved != null)
                    {
                        var gp = resolved.GetType().GetProperty("__InstanceGuid");
                        if (gp != null)
                        {
                            var gv = gp.GetValue(resolved);
                            if (gv is Guid gg) return gg;
                            if (gv != null && Guid.TryParse(gv.ToString(), out Guid p3)) return p3;
                        }
                    }
                    // Also try __InstanceGuid on v itself
                    var gp2 = v.GetType().GetProperty("__InstanceGuid");
                    if (gp2 != null)
                    {
                        var gv2 = gp2.GetValue(v);
                        if (gv2 is Guid gg2) return gg2;
                        if (gv2 != null && Guid.TryParse(gv2.ToString(), out Guid p4)) return p4;
                    }
                }
                catch { }
            }
            return null;
        }

        private string ExtractCondText(object obj) { if(obj==null)return""; try { foreach(var p in obj.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance)){var ln=p.Name.ToLower();if(ln.Contains("condition")||ln.Contains("signal")||ln.Contains("event")||ln.Contains("trigger")){var v=p.GetValue(obj);if(v!=null)return$"{p.Name}: {v}";}}} catch{} return""; }

        // =========================================================================
        //  Deep scan
        // =========================================================================

        private void DeepScanNode(StateNode node) { if(node.RawObject==null)return; node.Details=ScanObject(node.RawObject,0,new HashSet<object>(ReferenceEqualityComparer.Instance)); }

        private List<GraphDetail> ScanObject(object obj, int depth, HashSet<object> visited)
        {
            var res=new List<GraphDetail>(); if(obj==null||depth>4||!visited.Add(obj))return res;
            foreach(var prop in obj.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance))
            { if(SkippedPropertyNames.Contains(prop.Name))continue; object val;try{val=prop.GetValue(obj);}catch{continue;} if(val==null)continue; var vt=val.GetType();
              if(val is System.Collections.IEnumerable en&&!(val is string)){int idx=0;foreach(var item in en){if(item==null)continue;var it=item.GetType();if(IsPrim(it))continue;var d=new GraphDetail{Kind=ClassifyProp(prop.Name,it.Name),Label=BuildLabel(prop.Name,idx,item),SourceProperty=prop.Name,RawValue=item};if(depth<3&&IsComplex(it))d.Children=ScanObject(item,depth+1,visited);res.Add(d);idx++;}}
              else if(!IsPrim(vt)&&IsComplex(vt)){var d=new GraphDetail{Kind=ClassifyProp(prop.Name,vt.Name),Label=BuildLabel(prop.Name,-1,val),SourceProperty=prop.Name,RawValue=val};if(depth<3)d.Children=ScanObject(val,depth+1,visited);res.Add(d);} }
            return res;
        }

        private GraphDetailKind ClassifyProp(string pn, string tn) { var s=(pn+" "+tn).ToLower(); foreach(var(kw,k)in DetailKeywords)if(s.Contains(kw))return k; return GraphDetailKind.Unknown; }
        private string BuildLabel(string pn, int idx, object val) { string inner=""; try{foreach(var n in new[]{"Name","Id","n"}){var v=val.GetType().GetProperty(n)?.GetValue(val);if(v!=null){inner=$" \"{v}\"";break;}}}catch{} return$"{pn}{(idx>=0?$"[{idx}]":"")}{inner}"; }
        private bool IsComplex(Type t) => ComplexTypeFragments.Any(f => t.Name.Contains(f));
        private bool IsPrim(Type t)    => t.IsPrimitive||t.IsEnum||t==typeof(string)||t==typeof(Guid)||t==typeof(decimal)||t==typeof(DateTime);

        // =========================================================================
        //  Sorting / helpers
        // =========================================================================

        private List<StateNode> SortNodes(List<StateNode> nodes, NodeSortMode mode)
        {
            switch(mode)
            { case NodeSortMode.NameAscending:    return nodes.OrderBy(n=>n.Name,StringComparer.OrdinalIgnoreCase).ToList();
              case NodeSortMode.NameDescending:   return nodes.OrderByDescending(n=>n.Name,StringComparer.OrdinalIgnoreCase).ToList();
              case NodeSortMode.CategoryThenName: return nodes.OrderBy(n=>CategoryPriority(n.Category)).ThenBy(n=>n.Name,StringComparer.OrdinalIgnoreCase).ToList();
              default: return nodes; }
        }

        private int CategoryPriority(string cat) { int i=CategoryOrder.IndexOf(cat??"Other"); return i<0?CategoryOrder.Count:i; }

        private void SetEbxName(object obj, string name)
        {
            if (obj == null) return;
            // __Id is a plain string — always works
            try { dynamic d = obj; d.__Id = name; } catch { }
            // Name is CString (struct with implicit operator from string)
            var nameProp = obj.GetType().GetProperty("Name");
            if (nameProp != null)
            {
                try
                {
                    var cstringType = nameProp.PropertyType;
                    if (cstringType.Name == "CString")
                    {
                        // CString is a struct — try multiple approaches in order of reliability
                        bool set = false;

                        // 1. Try op_Implicit(string) → CString (most reliable for structs)
                        if (!set)
                        {
                            var implicitOp = cstringType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                                .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == cstringType
                                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                            if (implicitOp != null)
                            {
                                nameProp.SetValue(obj, implicitOp.Invoke(null, new object[] { name }));
                                set = true;
                            }
                        }

                        // 2. Try constructor CString(string)
                        if (!set)
                        {
                            var ctor = cstringType.GetConstructor(new[] { typeof(string) });
                            if (ctor != null)
                            {
                                nameProp.SetValue(obj, ctor.Invoke(new object[] { name }));
                                set = true;
                            }
                        }

                        // 3. Try Activator with constructor
                        if (!set)
                        {
                            var inst = Activator.CreateInstance(cstringType, new object[] { name });
                            nameProp.SetValue(obj, inst);
                            set = true;
                        }
                    }
                    else
                    {
                        nameProp.SetValue(obj, name);
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.LogWarning($"StateMachineEditor: SetEbxName failed for '{name}': {ex.Message}");
                }
            }
        }

        private string GetRawName(object obj)
        {
            if (obj == null) return "";
            // __Id is the primary EBX display name (AttackChainGraphLoader used this)
            try { var v = obj.GetType().GetProperty("__Id")?.GetValue(obj); if (v is string s1 && !string.IsNullOrEmpty(s1)) return s1; } catch { }
            try { dynamic d = obj; string s2 = d.__Id; if (!string.IsNullOrEmpty(s2)) return s2; } catch { }
            // fallback: Name property
            try { var v = obj.GetType().GetProperty("Name")?.GetValue(obj); if (v != null && !string.IsNullOrEmpty(v.ToString())) return v.ToString(); } catch { }
            // fallback: lowercase n tag
            try { var v = obj.GetType().GetProperty("n")?.GetValue(obj); if (v != null && !string.IsNullOrEmpty(v.ToString())) return v.ToString(); } catch { }
            return "";
        }
        private string NodeDisplayName(StateNode node)
        { if(node==null)return"?"; var p=node.Parsed; if(p.IsValid&&!string.IsNullOrEmpty(p.ActionPath)) return p.ActorIndex>=0?$"{p.ActionPath} (A{p.ActorIndex})":p.ActionPath; return node.Name??"(unnamed)"; }

        private string DetermineCategory(string name, ParsedName parsed)
        { var n=(parsed.IsValid?parsed.ActionPath:name).ToLower();
          if(n.Contains("strike")||n.Contains("saber")||n.Contains("attack")||n.Contains("slash"))return"Lightsaber";
          if(n.Contains("pistol")||n.Contains("blaster")||n.Contains("rifle")||n.Contains("shoot"))return"Blaster";
          if(n.Contains("melee")||n.Contains("punch"))return"Melee";
          if(n.Contains("grenade"))return"Grenade";
          if(n.Contains("dodge")||n.Contains("roll"))return"Movement";
          if(n.Contains("jump")||n.Contains("leap"))return"Jump";
          if(n.Contains("idle")||n.Contains("stand"))return"Idle";
          if(n.Contains("run")||n.Contains("walk")||n.Contains("loco")||n.Contains("sprint"))return"Locomotion";
          if(n.Contains("stagger")||n.Contains("electro")||n.Contains("status")||n.Contains("hit"))return"Status";
          if(n.Contains("emote")||n.Contains("taunt"))return"Emote";
          if(n.Contains("death")||n.Contains("die")||n.Contains("dead"))return"Death";
          return"Other"; }

        private string BuildNodeSubtitle(StateNode node)
        { var p=new List<string>(); if(!string.IsNullOrEmpty(node.Category))p.Add(node.Category); if(node.Transitions?.Count>0)p.Add($"{node.Transitions.Count}T");
          int cl=node.Details?.Count(d=>d.Kind==GraphDetailKind.Clip)??0, bl=node.Details?.Count(d=>d.Kind==GraphDetailKind.Blend)??0, co=node.Details?.Count(d=>d.Kind==GraphDetailKind.Condition)??0;
          if(cl>0)p.Add($"{cl}C"); if(bl>0)p.Add($"{bl}B"); if(co>0)p.Add($"{co}X"); return string.Join("  ",p); }

        private string FormatCharHeader(CharacterGroup cg)
        { int t=cg.SeqFlows.Sum(c=>c.Nodes.Count)+cg.OtherControllers.Sum(c=>c.Nodes.Count); return$"{(cg.Prefix=="3P"?"[3P] ":"")}{cg.DisplayName}  ({t})"; }

        // ── Colour helpers ────────────────────────────────────────────────────

        private Brush CharacterColor(string p) =>
            p=="A"  ? new SolidColorBrush(Color.FromRgb(240,210,100)):
            p=="3P" ? new SolidColorBrush(Color.FromRgb(160,200,240)):
                      new SolidColorBrush(Color.FromRgb(160,160,170));

        private Brush SuffixColor(NodeSuffix s) { switch(s){ case NodeSuffix.SeqFLOW:return new SolidColorBrush(Color.FromRgb(120,210,255)); case NodeSuffix.SEQ:return new SolidColorBrush(Color.FromRgb(160,220,180)); case NodeSuffix.SF:return new SolidColorBrush(Color.FromRgb(200,170,255)); case NodeSuffix.EC:return new SolidColorBrush(Color.FromRgb(255,190,100)); case NodeSuffix.RC:return new SolidColorBrush(Color.FromRgb(255,140,140)); default:return new SolidColorBrush(Color.FromRgb(160,160,175)); } }

        private Brush CategoryColor(string cat) { switch(cat){ case"Lightsaber":return new SolidColorBrush(Color.FromRgb(100,200,255)); case"Blaster":return new SolidColorBrush(Color.FromRgb(255,180,80)); case"Melee":return new SolidColorBrush(Color.FromRgb(220,90,90)); case"Grenade":return new SolidColorBrush(Color.FromRgb(200,150,50)); case"Movement":return new SolidColorBrush(Color.FromRgb(130,220,130)); case"Jump":return new SolidColorBrush(Color.FromRgb(150,220,200)); case"Idle":return new SolidColorBrush(Color.FromRgb(180,180,200)); case"Locomotion":return new SolidColorBrush(Color.FromRgb(100,220,170)); case"Status":return new SolidColorBrush(Color.FromRgb(220,180,255)); case"Emote":return new SolidColorBrush(Color.FromRgb(255,220,150)); case"Death":return new SolidColorBrush(Color.FromRgb(180,80,80)); default:return new SolidColorBrush(Color.FromRgb(155,155,165)); } }

        private Brush CategoryBorderBrush(string cat) { switch(cat){ case"Lightsaber":return new SolidColorBrush(Color.FromRgb(55,130,195)); case"Blaster":return new SolidColorBrush(Color.FromRgb(175,115,35)); case"Melee":return new SolidColorBrush(Color.FromRgb(155,55,55)); case"Grenade":return new SolidColorBrush(Color.FromRgb(135,95,25)); case"Movement":return new SolidColorBrush(Color.FromRgb(55,155,75)); case"Jump":return new SolidColorBrush(Color.FromRgb(75,155,135)); case"Idle":return new SolidColorBrush(Color.FromRgb(105,105,125)); case"Locomotion":return new SolidColorBrush(Color.FromRgb(45,155,105)); case"Status":return new SolidColorBrush(Color.FromRgb(145,100,185)); case"Emote":return new SolidColorBrush(Color.FromRgb(185,145,75)); case"Death":return new SolidColorBrush(Color.FromRgb(140,50,50)); default:return new SolidColorBrush(Color.FromRgb(75,75,90)); } }

        private Brush DetailKindColor(GraphDetailKind k) { switch(k){ case GraphDetailKind.Clip:return new SolidColorBrush(Color.FromRgb(130,210,255)); case GraphDetailKind.Blend:return new SolidColorBrush(Color.FromRgb(255,200,100)); case GraphDetailKind.Condition:return new SolidColorBrush(Color.FromRgb(200,150,255)); case GraphDetailKind.SubState:return new SolidColorBrush(Color.FromRgb(150,255,180)); default:return new SolidColorBrush(Color.FromRgb(155,155,165)); } }
    }

    // =========================================================================
    //  Asset definition
    // =========================================================================

    public class StateMachineAssetDefinition : AssetDefinition
    {
        private static ImageSource imageSource =
            new ImageSourceConverter().ConvertFromString(
                "pack://application:,,,/StateMachineEditorPlugin;component/Images/StateMachineFileType.png")
            as ImageSource;
        public override ImageSource GetIcon() => imageSource;
        public override FrostyAssetEditor GetEditor(ILogger logger) => new StateMachineEditor(logger);
    }
}
