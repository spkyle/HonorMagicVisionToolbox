using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Enhances Honor's DockPreviewWindow (multi-window stacked preview) with:
    /// 1. Keeping remaining preview cards open after middle-click / X close (dock_preview_keep_open_on_close)
    /// 2. Live full-window desktop Aero Peek on thumbnail hover with clean restore on leave (dock_preview_aero_peek)
    /// </summary>
    internal sealed class DockPreviewEnhancer : IDisposable
    {
        private static readonly TimeSpan InitialPeekDelay = TimeSpan.FromMilliseconds(160);
        private static readonly TimeSpan LeaveHideDelay = TimeSpan.FromMilliseconds(60);

        private readonly Window _previewWindow;
        private readonly FieldInfo? _cardsField;
        private readonly FieldInfo? _presentedItemsField;
        private readonly FieldInfo? _previewCloseRequestedField;
        private readonly FieldInfo? _previewInteractionCompletedField;
        private readonly MethodInfo? _applyPreviewMethod;
        private readonly MethodInfo? _positionAboveScreenPixelsMethod;
        private readonly MethodInfo? _syncBackdropMethod;
        private readonly PropertyInfo? _hasOpenContextMenuProp;

        // MainWindow (previewWindow.Owner) helpers
        private readonly FieldInfo? _shownAnchorPixelField;
        private readonly FieldInfo? _shownPreviewItemIdField;
        private readonly FieldInfo? _dockItemCustomDragActiveField;
        private readonly FieldInfo? _compositionNativeWindowField;
        private readonly MethodInfo? _getDockPopupPlateRectMethod;
        private readonly MethodInfo? _scheduleRefreshBurstMethod;
        private readonly MethodInfo? _cancelQueuedPreviewMethod;
        private readonly MethodInfo? _screenRectPixelToDipMethod;

        // NativeCompositionDockWindow hover hook fields discovered dynamically
        private object? _hookedCompositionWindow;
        private FieldInfo? _hoveredItemChangedField;
        private FieldInfo? _hoveredItemIdField;
        private Delegate? _origHoveredItemChanged;
        private Delegate? _wrappedHoveredItemChanged;

        // Card & cache fields discovered dynamically
        private Type? _cardType;
        private PropertyInfo? _cardRootProp;
        private PropertyInfo? _cardIsContextMenuOpenProp;
        private FieldInfo? _cardActivateHwndField;
        private FieldInfo? _cardStaticImageField;
        private MethodInfo? _cacheTryGetMethod;

        private readonly HashSet<object> _hookedCards = new();
        private readonly Dictionary<object, (MouseEventHandler Enter, MouseEventHandler Leave, MouseEventHandler Move, MouseButtonEventHandler LeftDown, MouseButtonEventHandler RightUp)> _cardHandlers = new();

        private readonly DispatcherTimer _peekDelayTimer;
        private readonly DispatcherTimer _peekLeaveTimer;
        private readonly DispatcherTimer _postCloseRetentionTimer;
        private readonly DispatcherTimer _dockDragGuardTimer;
        private readonly DispatcherTimer _transferCorridorTimer;
        private AeroPeekOverlayWindow? _peekOverlay;

        private Delegate? _origCloseRequested;
        private Delegate? _origInteractionCompleted;
        private Delegate? _wrappedCloseRequested;
        private Delegate? _wrappedInteractionCompleted;

        private IntPtr _lastCloseRequestedHwnd;
        private long _lastCloseRequestedTick;
        private IntPtr _pendingPeekHwnd;
        private IntPtr _pendingPeekSourceHwnd;
        private ImageSource? _pendingPeekFallbackBitmap;
        private IntPtr _activePeekHwnd;
        private IntPtr _tempRestoredHwnd;
        private Rect _postCloseRetentionBounds;
        private long _postCloseRetentionUntilTick;
        private bool _postCloseRetentionActive;
        private bool _dockDragGuardActive;
        private bool _transferCorridorHoldActive;
        private long _transferCorridorHoldStartTick;
        private Point _lastCorridorCursorDip;
        private string? _deferredHoverItemId;
        private long _deferredHoverStartTick;
        private Point _deferredHoverStartCursorDip;
        private bool _disposed;

        public DockPreviewEnhancer(Window previewWindow)
        {
            _previewWindow = previewWindow ?? throw new ArgumentNullException(nameof(previewWindow));

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type dpwType = previewWindow.GetType();
            _cardsField = dpwType.GetField("_cards", flags);
            _presentedItemsField = dpwType.GetField("_presentedItems", flags);
            _previewCloseRequestedField = dpwType.GetField("PreviewCloseRequested", flags);
            _previewInteractionCompletedField = dpwType.GetField("PreviewInteractionCompleted", flags);
            _applyPreviewMethod = dpwType.GetMethod("ApplyPreview", flags);
            _positionAboveScreenPixelsMethod = dpwType.GetMethod("PositionAboveScreenPixels", flags);
            _syncBackdropMethod = dpwType.GetMethod("SyncBackdrop", flags);
            _hasOpenContextMenuProp = dpwType.GetProperty("HasOpenContextMenu", flags);

            Window? owner = previewWindow.Owner;
            if (owner != null)
            {
                Type mwType = owner.GetType();
                _shownAnchorPixelField = mwType.GetField("_shownPreviewAnchorScreenRectPixel", flags);
                _shownPreviewItemIdField = mwType.GetField("_shownPreviewItemId", flags);
                _dockItemCustomDragActiveField = mwType.GetField("_dockItemCustomDragActive", flags);
                _compositionNativeWindowField = mwType.GetField("_compositionNativeWindow", flags);
                _getDockPopupPlateRectMethod = mwType.GetMethod("GetDockPopupPlateScreenRectPixel", flags);
                _scheduleRefreshBurstMethod = mwType.GetMethod("SchedulePreviewInteractionRefreshBurst", flags);
                _cancelQueuedPreviewMethod = mwType.GetMethod("CancelQueuedPreview", flags);
                _screenRectPixelToDipMethod = mwType.GetMethod("ScreenRectPixelToDip", flags);
            }

            _peekDelayTimer = new DispatcherTimer(DispatcherPriority.Normal, _previewWindow.Dispatcher)
            {
                Interval = InitialPeekDelay
            };
            _peekDelayTimer.Tick += OnPeekDelayTimerTick;

            _peekLeaveTimer = new DispatcherTimer(DispatcherPriority.Normal, _previewWindow.Dispatcher)
            {
                Interval = LeaveHideDelay
            };
            _peekLeaveTimer.Tick += OnPeekLeaveTimerTick;

            _postCloseRetentionTimer = new DispatcherTimer(DispatcherPriority.Input, _previewWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _postCloseRetentionTimer.Tick += OnPostCloseRetentionTimerTick;

            _dockDragGuardTimer = new DispatcherTimer(DispatcherPriority.Send, _previewWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _dockDragGuardTimer.Tick += OnDockDragGuardTimerTick;

            _transferCorridorTimer = new DispatcherTimer(DispatcherPriority.Input, _previewWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            _transferCorridorTimer.Tick += OnTransferCorridorTimerTick;

            _previewWindow.IsVisibleChanged += OnPreviewWindowIsVisibleChanged;
            _previewWindow.LayoutUpdated += OnPreviewWindowLayoutUpdated;
            _previewWindow.PreviewMouseMove += OnPreviewWindowMouseMove;
            _previewWindow.Closed += OnPreviewWindowClosed;
            AdaptiveSettingsStore.DockPreviewAeroPeekChanged += OnAeroPeekSettingChanged;
            AdaptiveSettingsStore.DockPreviewTransferCorridorChanged += OnTransferCorridorSettingChanged;

            WrapCloseDelegates();
            EnsureCardsHooked();
            TryPeekCurrentlyHoveredCard();

            DiagnosticLog.Info(
                $"DockPreviewEnhancer attached (cards={_cardsField != null}, items={_presentedItemsField != null}, " +
                $"apply={_applyPreviewMethod != null}, owner={owner != null}).");
        }

        private void OnAeroPeekSettingChanged(bool enabled)
        {
            if (_disposed || _previewWindow.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (!enabled)
            {
                _previewWindow.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(HidePeekImmediate));
            }
        }

        private void OnTransferCorridorSettingChanged(bool enabled)
        {
            if (_disposed || _previewWindow.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (!enabled)
            {
                _previewWindow.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    if (!_disposed)
                    {
                        EndTransferCorridorHold(updateContextMenuFlag: true);
                    }
                }));
            }
        }

        private void OnPreviewWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (e.NewValue is true)
            {
                WrapCloseDelegates();
                EnsureCardsHooked();
                TryPeekCurrentlyHoveredCard();
            }
            else
            {
                EndTransferCorridorHold(updateContextMenuFlag: true);
                EndPostCloseRetention();
                EndDockDragGuard();
                HidePeekImmediate();
            }
        }

        private void OnPreviewWindowLayoutUpdated(object? sender, EventArgs e)
        {
            if (_disposed || !_previewWindow.IsVisible)
            {
                return;
            }

            if (_cardsField?.GetValue(_previewWindow) is IList cards && cards.Count != _hookedCards.Count)
            {
                EnsureCardsHooked();
                TryPeekCurrentlyHoveredCard();
            }
        }

        private void OnPreviewWindowMouseMove(object sender, MouseEventArgs e)
        {
            if (_disposed || !_previewWindow.IsVisible)
            {
                return;
            }

            if (_transferCorridorHoldActive)
            {
                EndTransferCorridorHold(updateContextMenuFlag: true);
            }

            if (!AdaptiveSettingsStore.IsDockPreviewAeroPeekEnabled)
            {
                return;
            }

            EnsureCardsHooked();
            TryPeekCurrentlyHoveredCard();
        }

        private void WrapCloseDelegates()
        {
            if (_previewCloseRequestedField != null)
            {
                if (_previewCloseRequestedField.GetValue(_previewWindow) is Delegate curClose &&
                    !ReferenceEquals(curClose.Target, this))
                {
                    _origCloseRequested = curClose;
                    _wrappedCloseRequested ??= (Action<IntPtr>)OnCloseRequestedIntercepted;
                    if (_previewCloseRequestedField.FieldType == typeof(Action<IntPtr>))
                    {
                        _previewCloseRequestedField.SetValue(_previewWindow, _wrappedCloseRequested);
                    }
                }
            }

            if (_previewInteractionCompletedField != null)
            {
                if (_previewInteractionCompletedField.GetValue(_previewWindow) is Delegate curDone &&
                    !ReferenceEquals(curDone.Target, this))
                {
                    _origInteractionCompleted = curDone;
                    _wrappedInteractionCompleted ??= (Action)OnInteractionCompletedIntercepted;
                    if (_previewInteractionCompletedField.FieldType == typeof(Action))
                    {
                        _previewInteractionCompletedField.SetValue(_previewWindow, _wrappedInteractionCompleted);
                    }
                }
            }

            WrapCompositionHoverDelegate();
        }

        private void WrapCompositionHoverDelegate()
        {
            Window? owner = _previewWindow.Owner;
            if (owner == null || _compositionNativeWindowField == null)
            {
                return;
            }

            object? ncdw = _compositionNativeWindowField.GetValue(owner);
            if (ncdw == null)
            {
                return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type ncdwType = ncdw.GetType();
            _hoveredItemChangedField ??= ncdwType.GetField("HoveredItemChanged", flags);
            _hoveredItemIdField ??= ncdwType.GetField("_hoveredItemId", flags);

            if (_hoveredItemChangedField != null &&
                _hoveredItemChangedField.FieldType == typeof(Action<string>) &&
                _hoveredItemChangedField.GetValue(ncdw) is Delegate curHover &&
                !ReferenceEquals(curHover.Target, this))
            {
                _hookedCompositionWindow = ncdw;
                _origHoveredItemChanged = curHover;
                _wrappedHoveredItemChanged ??= (Action<string>)OnCompositionHoveredItemChanged;
                _hoveredItemChangedField.SetValue(ncdw, _wrappedHoveredItemChanged);
            }
        }

        private void UnwrapCompositionHoverDelegate()
        {
            if (_hookedCompositionWindow != null &&
                _hoveredItemChangedField != null &&
                _origHoveredItemChanged != null)
            {
                try
                {
                    if (ReferenceEquals((_hoveredItemChangedField.GetValue(_hookedCompositionWindow) as Delegate)?.Target, this))
                    {
                        _hoveredItemChangedField.SetValue(_hookedCompositionWindow, _origHoveredItemChanged);
                    }
                }
                catch
                {
                    // Ignore shutdown unwrap error.
                }
            }

            _hookedCompositionWindow = null;
        }

        private void OnCompositionHoveredItemChanged(string itemId)
        {
            if (_disposed ||
                !_previewWindow.IsVisible ||
                !AdaptiveSettingsStore.IsDockPreviewTransferCorridorEnabled)
            {
                EndTransferCorridorHold(updateContextMenuFlag: true);
                (_origHoveredItemChanged as Action<string>)?.Invoke(itemId);
                return;
            }

            Window? owner = _previewWindow.Owner;
            string? shownItemId = owner != null ? _shownPreviewItemIdField?.GetValue(owner) as string : null;
            if (string.IsNullOrWhiteSpace(shownItemId))
            {
                EndTransferCorridorHold(updateContextMenuFlag: true);
                (_origHoveredItemChanged as Action<string>)?.Invoke(itemId);
                return;
            }

            // Returning to the currently previewed Dock icon cancels any deferred neighbor switch.
            if (!string.IsNullOrWhiteSpace(itemId) &&
                string.Equals(itemId, shownItemId, StringComparison.Ordinal))
            {
                _deferredHoverItemId = null;
                (_origHoveredItemChanged as Action<string>)?.Invoke(itemId);
                return;
            }

            if (!TryGetTransferCorridorGeometries(out Rect corridorRectDip, out Rect previewRectDip, out _) ||
                !TryGetCursorDip(_previewWindow, out Point cursorDip))
            {
                EndTransferCorridorHold(updateContextMenuFlag: true);
                (_origHoveredItemChanged as Action<string>)?.Invoke(itemId);
                return;
            }

            // Case 1: Empty itemId (crossing the '|' divider line between Edge and Antigravity,
            // or gaps between Dock icons, or the upper edge of the Dock plate).
            // Honor's GetPreviewTransferZoneRectDip only covers down to anchorRect.Top + 10 DIP,
            // causing crossing the '|' divider below the top 10px of the icon to call HidePreview().
            if (string.IsNullOrWhiteSpace(itemId))
            {
                if (corridorRectDip.Contains(cursorDip))
                {
                    _deferredHoverItemId = null;
                    CancelOwnerQueuedPreview("transfer-corridor-gap");
                    BeginTransferCorridorHold(cursorDip);
                    return;
                }

                EndTransferCorridorHold(updateContextMenuFlag: true);
                (_origHoveredItemChanged as Action<string>)?.Invoke(itemId);
                return;
            }

            // Case 2: Brushing an adjacent Dock icon while sliding diagonally toward a wide/multi-card
            // DockPreviewWindow (or across the '|' divider onto the neighboring icon underneath the preview).
            bool withinPreviewHorizontalCorridor =
                corridorRectDip.Contains(cursorDip) &&
                cursorDip.X >= previewRectDip.Left - 26.0 &&
                cursorDip.X <= previewRectDip.Right + 26.0;

            if (withinPreviewHorizontalCorridor)
            {
                _deferredHoverItemId = itemId;
                _deferredHoverStartTick = Environment.TickCount64;
                _deferredHoverStartCursorDip = cursorDip;
                CancelOwnerQueuedPreview("transfer-corridor-aim");
                BeginTransferCorridorHold(cursorDip);
                return;
            }

            EndTransferCorridorHold(updateContextMenuFlag: true);
            (_origHoveredItemChanged as Action<string>)?.Invoke(itemId);
        }

        private void CancelOwnerQueuedPreview(string reason)
        {
            Window? owner = _previewWindow.Owner;
            if (owner == null || _cancelQueuedPreviewMethod == null)
            {
                return;
            }

            try
            {
                _cancelQueuedPreviewMethod.Invoke(owner, new object[] { reason });
            }
            catch
            {
                // Ignore signature mismatch.
            }
        }

        private void BeginTransferCorridorHold(Point cursorDip)
        {
            if (!_transferCorridorHoldActive)
            {
                _transferCorridorHoldActive = true;
                _transferCorridorHoldStartTick = Environment.TickCount64;
                _lastCorridorCursorDip = cursorDip;
            }

            try
            {
                if (_hasOpenContextMenuProp != null && _hasOpenContextMenuProp.CanWrite)
                {
                    _hasOpenContextMenuProp.SetValue(_previewWindow, true);
                }
            }
            catch
            {
                // Ignore if read-only.
            }

            if (!_transferCorridorTimer.IsEnabled)
            {
                _transferCorridorTimer.Start();
            }
        }

        private void OnTransferCorridorTimerTick(object? sender, EventArgs e)
        {
            if (_disposed || !_previewWindow.IsVisible || !_transferCorridorHoldActive)
            {
                EndTransferCorridorHold(updateContextMenuFlag: true);
                return;
            }

            if (!TryGetTransferCorridorGeometries(out Rect corridorRectDip, out Rect previewRectDip, out Rect anchorRectDip) ||
                !TryGetCursorDip(_previewWindow, out Point cursorDip))
            {
                EndTransferCorridorHold(updateContextMenuFlag: true);
                return;
            }

            // Reached the preview popup itself: native IsPointerWithinBounds takes over.
            if (previewRectDip.Contains(cursorDip))
            {
                _deferredHoverItemId = null;
                EndTransferCorridorHold(updateContextMenuFlag: true);
                return;
            }

            string? currentHoveredId = _hookedCompositionWindow != null
                ? _hoveredItemIdField?.GetValue(_hookedCompositionWindow) as string
                : null;

            // Left the full transfer corridor completely without entering the preview popup.
            if (!corridorRectDip.Contains(cursorDip) ||
                Environment.TickCount64 - _transferCorridorHoldStartTick > 4000)
            {
                string? deferredId = _deferredHoverItemId;
                EndTransferCorridorHold(updateContextMenuFlag: true);

                if (!string.IsNullOrWhiteSpace(currentHoveredId))
                {
                    (_origHoveredItemChanged as Action<string>)?.Invoke(currentHoveredId);
                }
                else
                {
                    (_origHoveredItemChanged as Action<string>)?.Invoke(deferredId ?? string.Empty);
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(_deferredHoverItemId))
            {
                // If cursor moved off the neighbor icon into the divider/gap or rose above the icon tops toward the preview,
                // discard the accidental neighbor brush and keep the current preview open.
                if (string.IsNullOrWhiteSpace(currentHoveredId) ||
                    cursorDip.Y <= anchorRectDip.Top + 4.0)
                {
                    _deferredHoverItemId = null;
                }
                else
                {
                    long elapsedMs = Environment.TickCount64 - _deferredHoverStartTick;
                    double recentDy = cursorDip.Y - _lastCorridorCursorDip.Y;
                    double totalDy = cursorDip.Y - _deferredHoverStartCursorDip.Y;
                    bool movingUpward = recentDy < -0.6 || totalDy < -5.0;
                    long requiredDwellMs = movingUpward ? 460 : 180;

                    if (elapsedMs >= requiredDwellMs)
                    {
                        string toCommit = currentHoveredId;
                        EndTransferCorridorHold(updateContextMenuFlag: true);
                        (_origHoveredItemChanged as Action<string>)?.Invoke(toCommit);
                        return;
                    }
                }
            }

            _lastCorridorCursorDip = cursorDip;
        }

        private void EndTransferCorridorHold(bool updateContextMenuFlag)
        {
            _transferCorridorTimer.Stop();
            _deferredHoverItemId = null;
            if (!_transferCorridorHoldActive)
            {
                return;
            }

            _transferCorridorHoldActive = false;
            if (updateContextMenuFlag && !_postCloseRetentionActive)
            {
                try
                {
                    if (!HasAnyRealCardContextMenuOpen() &&
                        _hasOpenContextMenuProp != null && _hasOpenContextMenuProp.CanWrite)
                    {
                        _hasOpenContextMenuProp.SetValue(_previewWindow, false);
                    }
                }
                catch
                {
                    // Ignore.
                }
            }
        }

        private bool TryGetTransferCorridorGeometries(out Rect corridorRectDip, out Rect previewRectDip, out Rect anchorRectDip)
        {
            corridorRectDip = Rect.Empty;
            previewRectDip = Rect.Empty;
            anchorRectDip = Rect.Empty;

            double previewW = GetWindowDipWidth(_previewWindow);
            double previewH = GetWindowDipHeight(_previewWindow);
            if (previewW <= 1.0 || previewH <= 1.0 ||
                double.IsNaN(_previewWindow.Left) || double.IsNaN(_previewWindow.Top))
            {
                return false;
            }

            previewRectDip = new Rect(_previewWindow.Left, _previewWindow.Top, previewW, previewH);

            Window? owner = _previewWindow.Owner;
            if (owner != null &&
                _shownAnchorPixelField?.GetValue(owner) is Rect anchorPx &&
                !anchorPx.IsEmpty && anchorPx.Width > 0 && anchorPx.Height > 0)
            {
                if (_screenRectPixelToDipMethod != null)
                {
                    try
                    {
                        if (_screenRectPixelToDipMethod.Invoke(owner, new object[] { anchorPx }) is Rect converted &&
                            !converted.IsEmpty)
                        {
                            anchorRectDip = converted;
                        }
                    }
                    catch
                    {
                        // Fallback below.
                    }
                }

                if (anchorRectDip.IsEmpty &&
                    PresentationSource.FromVisual(_previewWindow) is HwndSource hs &&
                    hs.CompositionTarget != null)
                {
                    Matrix m = hs.CompositionTarget.TransformFromDevice;
                    Point tl = m.Transform(new Point(anchorPx.Left, anchorPx.Top));
                    Point br = m.Transform(new Point(anchorPx.Right, anchorPx.Bottom));
                    anchorRectDip = new Rect(tl, br);
                }
            }

            if (anchorRectDip.IsEmpty)
            {
                double centerX = previewRectDip.Left + previewRectDip.Width * 0.5;
                anchorRectDip = new Rect(centerX - 28.0, previewRectDip.Bottom + 8.0, 56.0, 64.0);
            }

            double left = Math.Min(anchorRectDip.Left, previewRectDip.Left) - 26.0;
            double right = Math.Max(anchorRectDip.Right, previewRectDip.Right) + 26.0;
            double top = Math.Min(anchorRectDip.Top, previewRectDip.Top) - 14.0;
            double bottom = Math.Max(anchorRectDip.Bottom, previewRectDip.Bottom) + 12.0;
            corridorRectDip = new Rect(left, top, Math.Max(1.0, right - left), Math.Max(1.0, bottom - top));
            return true;
        }

        private void OnCloseRequestedIntercepted(IntPtr hwnd)
        {
            if (_activePeekHwnd == hwnd || _pendingPeekHwnd == hwnd || _tempRestoredHwnd == hwnd)
            {
                // Window is about to close; do not re-minimize it.
                if (_tempRestoredHwnd == hwnd)
                {
                    _tempRestoredHwnd = IntPtr.Zero;
                }
                HidePeekImmediate(restoreMinimized: true);
            }

            if (AdaptiveSettingsStore.IsDockPreviewKeepOpenOnCloseEnabled)
            {
                BeginDockDragGuard();
            }

            _lastCloseRequestedHwnd = hwnd;
            _lastCloseRequestedTick = Environment.TickCount64;
            (_origCloseRequested as Action<IntPtr>)?.Invoke(hwnd);
        }

        private void OnInteractionCompletedIntercepted()
        {
            IntPtr closedHwnd = _lastCloseRequestedHwnd;
            bool isRecentClose = closedHwnd != IntPtr.Zero &&
                                 (Environment.TickCount64 - _lastCloseRequestedTick) < 500;
            _lastCloseRequestedHwnd = IntPtr.Zero;

            if (isRecentClose &&
                AdaptiveSettingsStore.IsDockPreviewKeepOpenOnCloseEnabled &&
                TryRefreshRemainingCardsAfterClose(closedHwnd))
            {
                return;
            }

            EndTransferCorridorHold(updateContextMenuFlag: true);
            EndPostCloseRetention();
            HidePeekImmediate(restoreMinimized: false);
            (_origInteractionCompleted as Action)?.Invoke();
        }

        private bool TryRefreshRemainingCardsAfterClose(IntPtr closedHwnd)
        {
            if (_presentedItemsField == null || _applyPreviewMethod == null)
            {
                return false;
            }

            if (_presentedItemsField.GetValue(_previewWindow) is not Array presented || presented.Length <= 1)
            {
                return false;
            }

            Type? elementType = presented.GetType().GetElementType();
            if (elementType == null)
            {
                return false;
            }

            PropertyInfo? activateHwndProp = elementType.GetProperty("ActivateHwnd", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (activateHwndProp == null)
            {
                return false;
            }

            var remaining = new List<object>(presented.Length - 1);
            bool removedAny = false;
            for (int i = 0; i < presented.Length; i++)
            {
                object? item = presented.GetValue(i);
                if (item == null)
                {
                    continue;
                }

                IntPtr itemHwnd = activateHwndProp.GetValue(item) is IntPtr h ? h : IntPtr.Zero;
                if (!removedAny && itemHwnd == closedHwnd)
                {
                    removedAny = true;
                    continue;
                }
                remaining.Add(item);
            }

            if (remaining.Count == 0)
            {
                return false;
            }

            bool cursorInsideBefore = TryGetCursorDip(_previewWindow, out Point cursorDipBefore) &&
                cursorDipBefore.X >= _previewWindow.Left - 8.0 &&
                cursorDipBefore.X <= _previewWindow.Left + GetWindowDipWidth(_previewWindow) + 8.0 &&
                cursorDipBefore.Y >= _previewWindow.Top - 8.0 &&
                cursorDipBefore.Y <= _previewWindow.Top + GetWindowDipHeight(_previewWindow) + 8.0;

            Rect boundsBefore = new(
                _previewWindow.Left,
                _previewWindow.Top,
                Math.Max(1.0, GetWindowDipWidth(_previewWindow)),
                Math.Max(1.0, GetWindowDipHeight(_previewWindow)));

            Array newArray = Array.CreateInstance(elementType, remaining.Count);
            for (int i = 0; i < remaining.Count; i++)
            {
                newArray.SetValue(remaining[i], i);
            }

            _presentedItemsField.SetValue(_previewWindow, newArray);
            _applyPreviewMethod.Invoke(_previewWindow, new object[] { newArray });
            EnsureCardsHooked();

            RepositionAfterCardRemoved(cursorInsideBefore, cursorDipBefore, boundsBefore);

            try
            {
                if (_previewWindow.Owner != null)
                {
                    _scheduleRefreshBurstMethod?.Invoke(_previewWindow.Owner, null);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"SchedulePreviewInteractionRefreshBurst failed: {ex.Message}");
            }

            DiagnosticLog.Info($"Kept DockPreviewWindow open after closing hwnd=0x{closedHwnd.ToInt64():X}, remaining={remaining.Count}.");
            return true;
        }

        private static double GetWindowDipWidth(Window win) =>
            win.ActualWidth > 1.0 ? win.ActualWidth : (!double.IsNaN(win.Width) ? win.Width : 0.0);

        private static double GetWindowDipHeight(Window win) =>
            win.ActualHeight > 1.0 ? win.ActualHeight : (!double.IsNaN(win.Height) ? win.Height : 0.0);

        private void RepositionAfterCardRemoved(bool cursorInsideBefore, Point cursorDipBefore, Rect boundsBefore)
        {
            Window? owner = _previewWindow.Owner;
            if (owner != null && _positionAboveScreenPixelsMethod != null &&
                _shownAnchorPixelField?.GetValue(owner) is Rect anchorPx &&
                !anchorPx.IsEmpty && anchorPx.Width > 0)
            {
                try
                {
                    Rect dockBoundsPx = _getDockPopupPlateRectMethod?.Invoke(owner, null) is Rect r ? r : Rect.Empty;
                    _positionAboveScreenPixelsMethod.Invoke(
                        _previewWindow,
                        new object[] { owner, anchorPx.Left, anchorPx.Top, anchorPx.Width, dockBoundsPx });
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn($"PositionAboveScreenPixels after close failed: {ex.Message}");
                }
            }

            try
            {
                _previewWindow.UpdateLayout();
            }
            catch
            {
                // Non-fatal layout sync.
            }

            double curWidth = GetWindowDipWidth(_previewWindow);
            double curHeight = GetWindowDipHeight(_previewWindow);

            // If closing an outer card (or shrinking list rows) moved the popup edge past the cursor,
            // align smoothly so the replacement card remains under the cursor without closing the popup.
            if (cursorInsideBefore && curWidth > 20.0 && curHeight > 20.0)
            {
                double curLeft = _previewWindow.Left;
                double minVirtualLeft = SystemParameters.VirtualScreenLeft + 4.0;
                double maxVirtualLeft = minVirtualLeft + Math.Max(0.0, SystemParameters.VirtualScreenWidth - curWidth - 8.0);
                if (cursorDipBefore.X > curLeft + curWidth - 8.0)
                {
                    double shiftedLeft = Math.Clamp(cursorDipBefore.X - curWidth + 24.0, minVirtualLeft, Math.Max(minVirtualLeft, maxVirtualLeft));
                    if (Math.Abs(shiftedLeft - curLeft) > 1.0)
                    {
                        _previewWindow.Left = shiftedLeft;
                    }
                }
                else if (cursorDipBefore.X < curLeft + 8.0)
                {
                    double shiftedLeft = Math.Clamp(cursorDipBefore.X - 24.0, minVirtualLeft, Math.Max(minVirtualLeft, maxVirtualLeft));
                    if (Math.Abs(shiftedLeft - curLeft) > 1.0)
                    {
                        _previewWindow.Left = shiftedLeft;
                    }
                }

                // When closing the top row of a vertical list shrinks Top downward, lift Top gently if safe.
                double curTop = _previewWindow.Top;
                if (cursorDipBefore.Y < curTop + 6.0 && boundsBefore.Height > curHeight + 4.0)
                {
                    double minVirtualTop = SystemParameters.VirtualScreenTop + 36.0;
                    double targetTop = Math.Clamp(cursorDipBefore.Y - 18.0, minVirtualTop, Math.Max(minVirtualTop, curTop));
                    if (curTop - targetTop > 1.0)
                    {
                        _previewWindow.Top = targetTop;
                    }
                }

                try
                {
                    _previewWindow.UpdateLayout();
                }
                catch
                {
                    // Non-fatal.
                }
                _syncBackdropMethod?.Invoke(_previewWindow, null);
            }

            Rect boundsAfter = new(
                _previewWindow.Left,
                _previewWindow.Top,
                Math.Max(1.0, GetWindowDipWidth(_previewWindow)),
                Math.Max(1.0, GetWindowDipHeight(_previewWindow)));
            Rect retentionZone = Rect.Union(boundsBefore, boundsAfter);
            retentionZone.Inflate(28.0, 28.0);
            BeginPostCloseRetention(retentionZone);
        }

        private void BeginPostCloseRetention(Rect retentionZone)
        {
            _postCloseRetentionBounds = retentionZone;
            _postCloseRetentionUntilTick = Environment.TickCount64 + 900;
            _postCloseRetentionActive = true;

            try
            {
                if (_hasOpenContextMenuProp != null && _hasOpenContextMenuProp.CanWrite)
                {
                    _hasOpenContextMenuProp.SetValue(_previewWindow, true);
                }
            }
            catch
            {
                // Ignore if property is read-only.
            }

            _postCloseRetentionTimer.Stop();
            _postCloseRetentionTimer.Start();
        }

        private void OnPostCloseRetentionTimerTick(object? sender, EventArgs e)
        {
            if (_disposed || !_previewWindow.IsVisible || !_postCloseRetentionActive)
            {
                EndPostCloseRetention();
                return;
            }

            bool timedOut = Environment.TickCount64 >= _postCloseRetentionUntilTick;
            bool insideZone = TryGetCursorDip(_previewWindow, out Point pt) &&
                              _postCloseRetentionBounds.Contains(pt);

            if (timedOut || !insideZone)
            {
                EndPostCloseRetention();
            }
        }

        private void EndPostCloseRetention()
        {
            _postCloseRetentionTimer.Stop();
            if (!_postCloseRetentionActive)
            {
                return;
            }

            _postCloseRetentionActive = false;
            if (_transferCorridorHoldActive)
            {
                return;
            }

            try
            {
                if (!HasAnyRealCardContextMenuOpen() &&
                    _hasOpenContextMenuProp != null && _hasOpenContextMenuProp.CanWrite)
                {
                    _hasOpenContextMenuProp.SetValue(_previewWindow, false);
                }
            }
            catch
            {
                // Ignore.
            }
        }

        private void BeginDockDragGuard()
        {
            if (_dockItemCustomDragActiveField == null || _previewWindow.Owner == null)
            {
                return;
            }

            try
            {
                _dockItemCustomDragActiveField.SetValue(_previewWindow.Owner, true);
                _dockDragGuardActive = true;
                _dockDragGuardTimer.Stop();
                _dockDragGuardTimer.Start();
            }
            catch
            {
                // Ignore.
            }
        }

        private void OnDockDragGuardTimerTick(object? sender, EventArgs e)
        {
            EndDockDragGuard();
        }

        private void EndDockDragGuard()
        {
            _dockDragGuardTimer.Stop();
            if (!_dockDragGuardActive || _dockItemCustomDragActiveField == null || _previewWindow.Owner == null)
            {
                _dockDragGuardActive = false;
                return;
            }

            _dockDragGuardActive = false;
            try
            {
                _dockItemCustomDragActiveField.SetValue(_previewWindow.Owner, false);
            }
            catch
            {
                // Ignore.
            }
        }

        internal void EnsureCardsHooked()
        {
            if (_cardsField?.GetValue(_previewWindow) is not IList cards)
            {
                return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int i = 0; i < cards.Count; i++)
            {
                object? card = cards[i];
                if (card == null || _hookedCards.Contains(card))
                {
                    continue;
                }

                _cardType ??= card.GetType();
                _cardRootProp ??= _cardType.GetProperty("Root", flags);
                _cardIsContextMenuOpenProp ??= _cardType.GetProperty("IsContextMenuOpen", flags);
                _cardActivateHwndField ??= _cardType.GetField("_activateHwnd", flags);
                _cardStaticImageField ??= _cardType.GetField("_staticPreviewImage", flags);
                if (_cacheTryGetMethod == null)
                {
                    Type? cacheType = _previewWindow.GetType().Assembly.GetType("MagicTaskbarWpf.WindowPreviewCache");
                    _cacheTryGetMethod = cacheType?.GetMethod("TryGet", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_cardRootProp?.GetValue(card) is not Border root)
                {
                    continue;
                }

                MouseEventHandler enterHandler = (_, _) => OnCardMouseEnter(card);
                MouseEventHandler leaveHandler = (_, _) => OnCardMouseLeave(card);
                MouseEventHandler moveHandler = (_, _) => OnCardMouseMove(card);
                MouseButtonEventHandler leftDownHandler = (_, _) => HidePeekImmediate(restoreMinimized: false);
                MouseButtonEventHandler rightUpHandler = (_, _) => HidePeekImmediate(restoreMinimized: true);

                root.MouseEnter += enterHandler;
                root.MouseLeave += leaveHandler;
                root.PreviewMouseMove += moveHandler;
                root.PreviewMouseLeftButtonDown += leftDownHandler;
                root.PreviewMouseRightButtonUp += rightUpHandler;

                _hookedCards.Add(card);
                _cardHandlers[card] = (enterHandler, leaveHandler, moveHandler, leftDownHandler, rightUpHandler);
            }
        }

        private void OnCardMouseMove(object card)
        {
            if (_disposed || !AdaptiveSettingsStore.IsDockPreviewAeroPeekEnabled || !_previewWindow.IsVisible)
            {
                return;
            }

            IntPtr hwnd = _cardActivateHwndField?.GetValue(card) is IntPtr h ? h : IntPtr.Zero;
            if (hwnd != IntPtr.Zero && hwnd != _pendingPeekHwnd && hwnd != _activePeekHwnd)
            {
                OnCardMouseEnter(card);
            }
        }

        private void TryPeekCurrentlyHoveredCard()
        {
            if (_disposed || !AdaptiveSettingsStore.IsDockPreviewAeroPeekEnabled || !_previewWindow.IsVisible)
            {
                return;
            }

            if (_cardsField?.GetValue(_previewWindow) is not IList cards)
            {
                return;
            }

            for (int i = 0; i < cards.Count; i++)
            {
                object? card = cards[i];
                if (card != null &&
                    _cardRootProp?.GetValue(card) is Border root &&
                    root.IsVisible &&
                    root.IsMouseOver)
                {
                    IntPtr hwnd = _cardActivateHwndField?.GetValue(card) is IntPtr h ? h : IntPtr.Zero;
                    if (hwnd != IntPtr.Zero && hwnd != _pendingPeekHwnd && hwnd != _activePeekHwnd)
                    {
                        OnCardMouseEnter(card);
                    }
                    break;
                }
            }
        }

        private bool HasAnyRealCardContextMenuOpen()
        {
            if (_cardIsContextMenuOpenProp == null || _cardsField?.GetValue(_previewWindow) is not IList cards)
            {
                return false;
            }

            for (int i = 0; i < cards.Count; i++)
            {
                object? card = cards[i];
                if (card != null && _cardIsContextMenuOpenProp.GetValue(card) is true)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnCardMouseEnter(object card)
        {
            if (_disposed || !AdaptiveSettingsStore.IsDockPreviewAeroPeekEnabled || !_previewWindow.IsVisible)
            {
                return;
            }

            if (HasAnyRealCardContextMenuOpen())
            {
                return;
            }

            IntPtr hwnd = _cardActivateHwndField?.GetValue(card) is IntPtr h ? h : IntPtr.Zero;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }

            _peekLeaveTimer.Stop();
            var (previewSourceHwnd, fallbackBitmap) = ResolveCardPeekSources(card, hwnd);
            _pendingPeekHwnd = hwnd;
            _pendingPeekSourceHwnd = previewSourceHwnd;
            _pendingPeekFallbackBitmap = fallbackBitmap;

            if ((_peekOverlay != null && _peekOverlay.IsVisible) || _tempRestoredHwnd != IntPtr.Zero)
            {
                _peekDelayTimer.Stop();
                ShowPeekForWindow(hwnd, _pendingPeekFallbackBitmap, _pendingPeekSourceHwnd);
                return;
            }

            _peekDelayTimer.Stop();
            _peekDelayTimer.Start();
        }

        private void OnCardMouseLeave(object card)
        {
            IntPtr hwnd = _cardActivateHwndField?.GetValue(card) is IntPtr h ? h : IntPtr.Zero;
            if (hwnd == _pendingPeekHwnd)
            {
                _peekDelayTimer.Stop();
            }

            if ((_peekOverlay != null && _peekOverlay.IsVisible) || _tempRestoredHwnd != IntPtr.Zero)
            {
                _peekLeaveTimer.Stop();
                _peekLeaveTimer.Start();
            }
        }

        private void OnPeekDelayTimerTick(object? sender, EventArgs e)
        {
            _peekDelayTimer.Stop();
            if (_disposed || !AdaptiveSettingsStore.IsDockPreviewAeroPeekEnabled || !_previewWindow.IsVisible)
            {
                return;
            }

            if (_pendingPeekHwnd != IntPtr.Zero && IsWindow(_pendingPeekHwnd))
            {
                ShowPeekForWindow(_pendingPeekHwnd, _pendingPeekFallbackBitmap, _pendingPeekSourceHwnd);
            }
        }

        private void OnPeekLeaveTimerTick(object? sender, EventArgs e)
        {
            _peekLeaveTimer.Stop();
            HidePeekImmediate(restoreMinimized: true);
        }

        internal void ShowPeekForWindow(IntPtr targetHwnd, ImageSource? fallbackBitmap = null, IntPtr previewSourceHwnd = default)
        {
            if (_disposed || targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd))
            {
                return;
            }

            if (_tempRestoredHwnd != IntPtr.Zero && _tempRestoredHwnd != targetHwnd)
            {
                RestoreTempMinimizedWindow();
            }

            _peekOverlay ??= new AeroPeekOverlayWindow();
            IntPtr previewHwnd = new WindowInteropHelper(_previewWindow).Handle;
            IntPtr dockHwnd = _previewWindow.Owner != null
                ? new WindowInteropHelper(_previewWindow.Owner).Handle
                : IntPtr.Zero;

            if (_peekOverlay.ShowPeek(targetHwnd, previewSourceHwnd, fallbackBitmap, previewHwnd, dockHwnd))
            {
                _activePeekHwnd = targetHwnd;
                return;
            }

            // Fallback for minimized windows (such as classic conhost/cmd) whose DWM texture was evicted
            // and no cached bitmap exists: show without activation below DockPreviewWindow with dimmed scrim, then re-minimize on leave.
            if (IsIconic(targetHwnd))
            {
                if (TryTempUnminimizePeek(targetHwnd, previewHwnd))
                {
                    _peekOverlay.ShowScrimOnly(targetHwnd, previewHwnd, dockHwnd);
                    DiagnosticLog.Info($"AeroPeek temporarily restored minimized window without activation hwnd=0x{targetHwnd.ToInt64():X}.");
                }
                else
                {
                    _peekOverlay.HidePeek();
                    DiagnosticLog.Info($"AeroPeek could not display minimized hwnd=0x{targetHwnd.ToInt64():X} (live=False, fallback=False).");
                }
            }
            else
            {
                DiagnosticLog.Info($"AeroPeek could not display hwnd=0x{targetHwnd.ToInt64():X} (live=False, fallback={(fallbackBitmap != null)}).");
            }
        }

        internal bool TryTempUnminimizePeek(IntPtr targetHwnd, IntPtr previewHwnd)
        {
            if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd) || !IsIconic(targetHwnd))
            {
                return false;
            }

            if (ShowWindow(targetHwnd, SwShowNoActivate) && !IsIconic(targetHwnd))
            {
                SetWindowPos(targetHwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                if (previewHwnd != IntPtr.Zero)
                {
                    SetWindowPos(previewHwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                }
                _tempRestoredHwnd = targetHwnd;
                _activePeekHwnd = targetHwnd;
                return true;
            }

            return false;
        }

        internal void HidePeekImmediate()
        {
            HidePeekImmediate(restoreMinimized: true);
        }

        internal void HidePeekImmediate(bool restoreMinimized)
        {
            _peekDelayTimer.Stop();
            _peekLeaveTimer.Stop();
            _pendingPeekHwnd = IntPtr.Zero;
            _pendingPeekSourceHwnd = IntPtr.Zero;
            _activePeekHwnd = IntPtr.Zero;
            _peekOverlay?.HidePeek();

            if (restoreMinimized)
            {
                RestoreTempMinimizedWindow();
            }
            else
            {
                _tempRestoredHwnd = IntPtr.Zero;
            }
        }

        private void RestoreTempMinimizedWindow()
        {
            IntPtr h = _tempRestoredHwnd;
            _tempRestoredHwnd = IntPtr.Zero;
            if (h != IntPtr.Zero && IsWindow(h) && !IsIconic(h))
            {
                ShowWindow(h, SwShowMinNoActive);
            }
        }

        private (IntPtr PreviewSourceHwnd, ImageSource? FallbackBitmap) ResolveCardPeekSources(object card, IntPtr activateHwnd)
        {
            ImageSource? fallback = null;
            if (_cardStaticImageField?.GetValue(card) is Image staticImg && staticImg.Source != null)
            {
                fallback = staticImg.Source;
            }

            IntPtr previewSourceHwnd = IntPtr.Zero;
            if (_presentedItemsField?.GetValue(_previewWindow) is Array items)
            {
                Type? elemType = items.GetType().GetElementType();
                PropertyInfo? actProp = elemType?.GetProperty("ActivateHwnd", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo? prevProp = elemType?.GetProperty("PreviewHwnd", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo? bmpProp = elemType?.GetProperty("PreviewBitmap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (actProp != null)
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        object? item = items.GetValue(i);
                        if (item != null && actProp.GetValue(item) is IntPtr h && h == activateHwnd)
                        {
                            if (prevProp?.GetValue(item) is IntPtr ph && ph != IntPtr.Zero)
                            {
                                previewSourceHwnd = ph;
                            }
                            fallback ??= bmpProp?.GetValue(item) as ImageSource;
                            break;
                        }
                    }
                }
            }

            fallback ??= TryGetCachedPreviewBitmap(previewSourceHwnd);
            fallback ??= TryGetCachedPreviewBitmap(activateHwnd);
            return (previewSourceHwnd, fallback);
        }

        private ImageSource? TryGetCachedPreviewBitmap(IntPtr hwnd)
        {
            if (_cacheTryGetMethod == null || hwnd == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                object?[] args = { hwnd, null, null };
                if (_cacheTryGetMethod.Invoke(null, args) is true)
                {
                    return args[1] as ImageSource;
                }
            }
            catch
            {
                // Ignore signature mismatch.
            }
            return null;
        }

        internal static Point? TestCursorDipOverride = null;

        private static bool TryGetCursorDip(Window referenceWindow, out Point cursorDip)
        {
            if (TestCursorDipOverride.HasValue)
            {
                cursorDip = TestCursorDipOverride.Value;
                return true;
            }

            if (GetCursorPos(out POINT pt) &&
                PresentationSource.FromVisual(referenceWindow) is HwndSource hs &&
                hs.CompositionTarget != null)
            {
                cursorDip = hs.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y));
                return true;
            }

            cursorDip = default;
            return false;
        }

        private void OnPreviewWindowClosed(object? sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            EndTransferCorridorHold(updateContextMenuFlag: true);
            EndPostCloseRetention();
            EndDockDragGuard();
            UnwrapCompositionHoverDelegate();
            AdaptiveSettingsStore.DockPreviewAeroPeekChanged -= OnAeroPeekSettingChanged;
            AdaptiveSettingsStore.DockPreviewTransferCorridorChanged -= OnTransferCorridorSettingChanged;
            _previewWindow.IsVisibleChanged -= OnPreviewWindowIsVisibleChanged;
            _previewWindow.LayoutUpdated -= OnPreviewWindowLayoutUpdated;
            _previewWindow.PreviewMouseMove -= OnPreviewWindowMouseMove;
            _previewWindow.Closed -= OnPreviewWindowClosed;

            HidePeekImmediate();
            if (_peekOverlay != null)
            {
                try
                {
                    _peekOverlay.Close();
                }
                catch
                {
                    // Ignore close errors during shutdown.
                }
                _peekOverlay = null;
            }

            if (_previewCloseRequestedField != null && _origCloseRequested != null &&
                ReferenceEquals((_previewCloseRequestedField.GetValue(_previewWindow) as Delegate)?.Target, this))
            {
                _previewCloseRequestedField.SetValue(_previewWindow, _origCloseRequested);
            }

            if (_previewInteractionCompletedField != null && _origInteractionCompleted != null &&
                ReferenceEquals((_previewInteractionCompletedField.GetValue(_previewWindow) as Delegate)?.Target, this))
            {
                _previewInteractionCompletedField.SetValue(_previewWindow, _origInteractionCompleted);
            }

            foreach (var kv in _cardHandlers)
            {
                if (_cardRootProp?.GetValue(kv.Key) is Border root)
                {
                    root.MouseEnter -= kv.Value.Enter;
                    root.MouseLeave -= kv.Value.Leave;
                    root.PreviewMouseMove -= kv.Value.Move;
                    root.PreviewMouseLeftButtonDown -= kv.Value.LeftDown;
                    root.PreviewMouseRightButtonUp -= kv.Value.RightUp;
                }
            }
            _cardHandlers.Clear();
            _hookedCards.Clear();
        }

        private const int SwShowNoActivate = 4;
        private const int SwShowMinNoActive = 7;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private static readonly IntPtr HwndTop = IntPtr.Zero;
        private static readonly IntPtr HwndTopmost = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    }

    /// <summary>
    /// Transparent, click-through DWM live thumbnail overlay window placed immediately beneath
    /// DockPreviewWindow and Dock MainWindow so Aero Peek never steals focus or mutates user window state.
    /// </summary>
    internal sealed class AeroPeekOverlayWindow : Window
    {
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsMaximize = 0x01000000L;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExTopmost = 0x00000008L;
        private const long WsExNoActivate = 0x08000000L;
        private const uint WpfRestoreToMaximized = 0x0002;
        private const uint MonitorDefaultToNearest = 0x00000002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint DwmTnpRectDestination = 0x00000001;
        private const uint DwmTnpOpacity = 0x00000004;
        private const uint DwmTnpVisible = 0x00000008;
        private const uint DwmTnpSourceClientAreaOnly = 0x00000010;

        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly Canvas _canvas;
        private readonly Border _scrim;
        private readonly Image _fallbackImage;

        private IntPtr _overlayHwnd;
        private IntPtr _thumbnailHandle;
        private IntPtr _registeredSourceHwnd;

        public AeroPeekOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

            _scrim = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xA6, 0x00, 0x00, 0x00))
            };
            _fallbackImage = new Image
            {
                Stretch = Stretch.Fill,
                Visibility = Visibility.Collapsed
            };
            RenderOptions.SetBitmapScalingMode(_fallbackImage, BitmapScalingMode.HighQuality);

            _canvas = new Canvas();
            _canvas.Children.Add(_scrim);
            _canvas.Children.Add(_fallbackImage);
            Content = _canvas;

            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (PresentationSource.FromVisual(this) is HwndSource hs)
            {
                _overlayHwnd = hs.Handle;
                if (hs.CompositionTarget != null)
                {
                    hs.CompositionTarget.BackgroundColor = Colors.Transparent;
                }

                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(_overlayHwnd, ref margins);

                long exStyle = GetWindowLongPtr(_overlayHwnd, GwlExStyle).ToInt64();
                exStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
                SetWindowLongPtr(_overlayHwnd, GwlExStyle, new IntPtr(exStyle));
            }
        }

        private bool TryPrepareScrimCanvas(IntPtr targetHwnd, IntPtr previewWinHwnd, out RECT work, out Matrix fromDevice)
        {
            work = default;
            fromDevice = Matrix.Identity;

            IntPtr hMon = MonitorFromWindow(targetHwnd, MonitorDefaultToNearest);
            if (hMon == IntPtr.Zero && previewWinHwnd != IntPtr.Zero)
            {
                hMon = MonitorFromWindow(previewWinHwnd, MonitorDefaultToNearest);
            }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hMon == IntPtr.Zero || !GetMonitorInfo(hMon, ref mi))
            {
                HidePeek();
                return false;
            }

            work = (mi.rcWork.Right > mi.rcWork.Left && mi.rcWork.Bottom > mi.rcWork.Top)
                ? mi.rcWork
                : mi.rcMonitor;

            int workW = Math.Max(1, work.Right - work.Left);
            int workH = Math.Max(1, work.Bottom - work.Top);

            if (!IsVisible)
            {
                Left = -32000;
                Top = -32000;
                Width = 1;
                Height = 1;
                Show();
            }

            if (_overlayHwnd == IntPtr.Zero)
            {
                _overlayHwnd = new WindowInteropHelper(this).Handle;
            }

            if (PresentationSource.FromVisual(this) is HwndSource hs && hs.CompositionTarget != null)
            {
                fromDevice = hs.CompositionTarget.TransformFromDevice;
            }

            Size workSizeDip = new(workW * fromDevice.M11, workH * fromDevice.M22);
            _scrim.Width = Math.Max(1.0, workSizeDip.Width);
            _scrim.Height = Math.Max(1.0, workSizeDip.Height);
            Canvas.SetLeft(_scrim, 0);
            Canvas.SetTop(_scrim, 0);
            return true;
        }

        public bool ShowPeek(IntPtr targetHwnd, IntPtr previewSourceHwnd, ImageSource? fallbackBitmap, IntPtr previewWinHwnd, IntPtr dockWinHwnd)
        {
            if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd))
            {
                HidePeek();
                return false;
            }

            IntPtr hMon = MonitorFromWindow(targetHwnd, MonitorDefaultToNearest);
            if (hMon == IntPtr.Zero && previewWinHwnd != IntPtr.Zero)
            {
                hMon = MonitorFromWindow(previewWinHwnd, MonitorDefaultToNearest);
            }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hMon == IntPtr.Zero || !GetMonitorInfo(hMon, ref mi))
            {
                HidePeek();
                return false;
            }

            if (!TryPrepareScrimCanvas(targetHwnd, previewWinHwnd, out RECT work, out Matrix fromDevice))
            {
                return false;
            }

            RECT destScreen = ResolveTargetPeekScreenRect(targetHwnd, mi, work);
            RECT destClient = new()
            {
                Left = destScreen.Left - work.Left,
                Top = destScreen.Top - work.Top,
                Right = destScreen.Right - work.Left,
                Bottom = destScreen.Bottom - work.Top
            };

            EnsureZOrderBelowDockAndPreview(_overlayHwnd, work, previewWinHwnd, dockWinHwnd);

            bool liveOk = (previewSourceHwnd != IntPtr.Zero && previewSourceHwnd != targetHwnd && IsWindow(previewSourceHwnd) &&
                           TryUpdateDwmThumbnail(previewSourceHwnd, destClient))
                          || TryUpdateDwmThumbnail(targetHwnd, destClient);

            if (!liveOk && fallbackBitmap != null)
            {
                _fallbackImage.Source = fallbackBitmap;
                _fallbackImage.Width = Math.Max(1.0, (destClient.Right - destClient.Left) * fromDevice.M11);
                _fallbackImage.Height = Math.Max(1.0, (destClient.Bottom - destClient.Top) * fromDevice.M22);
                Canvas.SetLeft(_fallbackImage, destClient.Left * fromDevice.M11);
                Canvas.SetTop(_fallbackImage, destClient.Top * fromDevice.M22);
                _fallbackImage.Visibility = Visibility.Visible;
            }
            else
            {
                _fallbackImage.Visibility = Visibility.Collapsed;
                _fallbackImage.Source = null;
            }

            if (!liveOk && fallbackBitmap == null)
            {
                HidePeek();
                return false;
            }

            return true;
        }

        public bool ShowScrimOnly(IntPtr targetHwnd, IntPtr previewWinHwnd, IntPtr dockWinHwnd)
        {
            if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd))
            {
                HidePeek();
                return false;
            }

            UnregisterThumbnail();
            _fallbackImage.Visibility = Visibility.Collapsed;
            _fallbackImage.Source = null;

            if (!TryPrepareScrimCanvas(targetHwnd, previewWinHwnd, out RECT work, out _))
            {
                return false;
            }

            int workW = Math.Max(1, work.Right - work.Left);
            int workH = Math.Max(1, work.Bottom - work.Top);

            SetWindowPos(_overlayHwnd, targetHwnd, work.Left, work.Top, workW, workH, SwpNoActivate | SwpShowWindow);
            if (previewWinHwnd != IntPtr.Zero && IsWindow(previewWinHwnd))
            {
                SetWindowPos(previewWinHwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            if (dockWinHwnd != IntPtr.Zero && IsWindow(dockWinHwnd) &&
                (GetWindowLongPtr(dockWinHwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0)
            {
                SetWindowPos(dockWinHwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }

            return true;
        }

        public void HidePeek()
        {
            UnregisterThumbnail();
            _fallbackImage.Visibility = Visibility.Collapsed;
            _fallbackImage.Source = null;
            if (IsVisible)
            {
                Hide();
            }
        }

        private bool TryUpdateDwmThumbnail(IntPtr targetHwnd, RECT destClient)
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return false;
            }

            if (_thumbnailHandle == IntPtr.Zero || _registeredSourceHwnd != targetHwnd)
            {
                UnregisterThumbnail();
                int hr = DwmRegisterThumbnail(_overlayHwnd, targetHwnd, out _thumbnailHandle);
                if (hr != 0 || _thumbnailHandle == IntPtr.Zero)
                {
                    _thumbnailHandle = IntPtr.Zero;
                    _registeredSourceHwnd = IntPtr.Zero;
                    return false;
                }
                _registeredSourceHwnd = targetHwnd;
            }

            if (DwmQueryThumbnailSourceSize(_thumbnailHandle, out SIZE srcSize) != 0 ||
                srcSize.cx <= 8 || srcSize.cy <= 8)
            {
                UnregisterThumbnail();
                return false;
            }

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly,
                rcDestination = destClient,
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false
            };

            if (DwmUpdateThumbnailProperties(_thumbnailHandle, ref props) != 0)
            {
                UnregisterThumbnail();
                return false;
            }

            return true;
        }

        private void UnregisterThumbnail()
        {
            if (_thumbnailHandle != IntPtr.Zero)
            {
                DwmUnregisterThumbnail(_thumbnailHandle);
                _thumbnailHandle = IntPtr.Zero;
            }
            _registeredSourceHwnd = IntPtr.Zero;
        }

        private static RECT ResolveTargetPeekScreenRect(IntPtr targetHwnd, in MONITORINFO mi, in RECT work)
        {
            if (!IsIconic(targetHwnd) && GetWindowRect(targetHwnd, out RECT wr) &&
                wr.Right > wr.Left && wr.Bottom > wr.Top)
            {
                return wr;
            }

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(targetHwnd, ref wp))
            {
                bool restoreMax = (wp.flags & WpfRestoreToMaximized) != 0 ||
                                  (GetWindowLongPtr(targetHwnd, GwlStyle).ToInt64() & WsMaximize) != 0;
                if (restoreMax)
                {
                    return work;
                }

                // rcNormalPosition is in workspace coordinates; convert to screen pixels clamped inside work area.
                int offsetX = work.Left - mi.rcMonitor.Left;
                int offsetY = work.Top - mi.rcMonitor.Top;
                int left = wp.rcNormalPosition.Left + offsetX;
                int top = wp.rcNormalPosition.Top + offsetY;
                int w = Math.Max(200, wp.rcNormalPosition.Right - wp.rcNormalPosition.Left);
                int h = Math.Max(150, wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top);

                left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w));
                top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));
                return new RECT
                {
                    Left = left,
                    Top = top,
                    Right = Math.Min(work.Right, left + w),
                    Bottom = Math.Min(work.Bottom, top + h)
                };
            }

            return work;
        }

        private static void EnsureZOrderBelowDockAndPreview(IntPtr overlayHwnd, in RECT work, IntPtr previewHwnd, IntPtr dockHwnd)
        {
            int w = Math.Max(1, work.Right - work.Left);
            int h = Math.Max(1, work.Bottom - work.Top);

            SetWindowPos(
                overlayHwnd,
                HwndTopmost,
                work.Left,
                work.Top,
                w,
                h,
                SwpNoActivate | SwpShowWindow);

            if (previewHwnd != IntPtr.Zero && IsWindow(previewHwnd))
            {
                SetWindowPos(previewHwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }

            if (dockHwnd != IntPtr.Zero && IsWindow(dockHwnd) &&
                (GetWindowLongPtr(dockHwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0)
            {
                SetWindowPos(overlayHwnd, dockHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            else if (previewHwnd != IntPtr.Zero && IsWindow(previewHwnd))
            {
                SetWindowPos(overlayHwnd, previewHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterThumbnail();
            base.OnClosed(e);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left, Right, Top, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx, cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public uint flags;
            public uint showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out SIZE pSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    }
}
