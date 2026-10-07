using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace FPE_Legacy.Sandbox
{
    internal sealed class FunSandboxScroll
    {
        private sealed class Pane
        {
            internal RectTransform Viewport, Content;
            internal ScrollRect Scroll;
            internal int Geometry, Children;
            internal bool Measured;
        }
        private readonly Pane[] _panes = { new(), new() };
        private readonly Il2CppStructArray<Vector3> _corners = new(4);
        private readonly List<(RectTransform child, Vector3 position)> _positions = new();
        private static readonly string[] Viewports =
        {
            "Canvas/Console/Catagories_UI", "Canvas/Console/Options_UI"
        };
        private static readonly string[] Contents = { "Catagories_Root", "Options_Root" };

        internal void Refresh(Volume console, bool initial)
        {
            for (int i = 0; i < _panes.Length; i++)
            {
                var pane = _panes[i];
                if (pane.Viewport == null || pane.Content == null || pane.Scroll == null)
                {
                    var viewport = console.transform.Find(Viewports[i]);
                    if (viewport == null) continue;
                    var content = viewport.Find(Contents[i]);
                    if (content == null) continue;
                    pane.Viewport = viewport.GetComponent<RectTransform>();
                    pane.Content = content.GetComponent<RectTransform>();
                    if (pane.Viewport == null || pane.Content == null) continue;
                    pane.Scroll = viewport.GetComponent<ScrollRect>() ?? viewport.gameObject.AddComponent<ScrollRect>();
                    if (viewport.GetComponent<RectMask2D>() == null) viewport.gameObject.AddComponent<RectMask2D>();
                    var graphic = viewport.GetComponent<Graphic>();
                    if (graphic == null)
                    {
                        var image = viewport.gameObject.AddComponent<Image>();
                        image.color = new Color(0, 0, 0, .001f);
                        graphic = image;
                    }
                    graphic.raycastTarget = true;
                    var scroll = pane.Scroll;
                    scroll.content = pane.Content;
                    scroll.viewport = pane.Viewport;
                    scroll.horizontal = false;
                    scroll.vertical = true;
                    scroll.movementType = ScrollRect.MovementType.Clamped;
                    scroll.inertia = true;
                    scroll.decelerationRate = .135f;
                    scroll.scrollSensitivity = 60f;
                    scroll.horizontalScrollbar = null;
                    scroll.verticalScrollbar = null;
                    if (i == 1) pane.Viewport.anchorMax = new Vector2(1, .91f);
                    pane.Measured = false;
                }
                if (!initial && !pane.Viewport.gameObject.activeInHierarchy) continue;
                var stamp = Stamp(pane);
                if (pane.Measured && pane.Geometry == stamp.geometry) continue;
                Canvas.ForceUpdateCanvases();
                Measure(pane, !pane.Measured || pane.Children != stamp.children);
                stamp = Stamp(pane); // Measure can change child rects, so refresh the stamp.
                pane.Geometry = stamp.geometry;
                pane.Children = stamp.children;
                pane.Measured = true;
            }
        }

        private static (int geometry, int children) Stamp(Pane pane)
        {
            unchecked
            {
                int children = 17, geometry = pane.Viewport.rect.height.GetHashCode();
                for (int i = 0; i < pane.Content.childCount; i++)
                {
                    // Use TryCast: IL2CPP returns a Transform wrapper even for RectTransforms.
                    var child = pane.Content.GetChild(i).TryCast<RectTransform>();
                    if (child == null || !child.gameObject.activeSelf) continue;
                    children = children * 31 + child.GetInstanceID();
                    geometry = geometry * 31 + child.localPosition.GetHashCode();
                    geometry = geometry * 31 + child.localRotation.GetHashCode();
                    geometry = geometry * 31 + child.localScale.GetHashCode();
                    geometry = geometry * 31 + child.rect.GetHashCode();
                }
                return (geometry * 31 + children, children);
            }
        }

        private void Measure(Pane pane, bool resetPosition)
        {
            var content = pane.Content;
            float lowest = float.MaxValue;
            bool found = false;
            _positions.Clear();
            for (int i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i).TryCast<RectTransform>();
                if (child == null) continue;
                _positions.Add((child, child.position));
                if (!child.gameObject.activeSelf) continue;
                // GetWorldCorners needs a native array; writes to a temporary copy of Vector3[] would be lost.
                child.GetWorldCorners(_corners);
                for (int p = 0; p < 4; p++)
                {
                    float y = content.InverseTransformPoint(_corners[p]).y;
                    lowest = Mathf.Min(lowest, y);
                }
                found = true;
            }
            float height = pane.Viewport.rect.height;
            if (found) height = Mathf.Max(height, content.rect.yMax - lowest + 20f);
            float oldTop = content.anchoredPosition.y + content.rect.height * (1f - content.pivot.y);
            content.anchorMin = new Vector2(content.anchorMin.x, 1);
            content.anchorMax = new Vector2(content.anchorMax.x, 1);
            content.pivot = new Vector2(content.pivot.x, 1);
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            var position = content.anchoredPosition;
            position.y = oldTop;
            content.anchoredPosition = position;
            // Restore button positions after changing the content pivot and height.
            foreach (var saved in _positions)
                if (saved.child != null) saved.child.position = saved.position;
            _positions.Clear();
            if (resetPosition)
            {
                pane.Scroll.StopMovement();
                pane.Scroll.verticalNormalizedPosition = 1f;
            }
        }
    }
}
