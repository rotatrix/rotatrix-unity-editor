using System;
using System.Collections.Generic;
using OpenAxis.Client;
using OpenAxis.Geometry;
using OpenAxis.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

namespace Rotatrix.Unity.Editor
{
    internal sealed class SceneViewAdapter : INavigationAdapter
    {
        // Unity SceneViewMotion.GetMovementDirection uses k_FlySpeed=9 world
        // units/second at cameraSettings.speed=1, before acceleration/Shift.
        // Trial default: a quarter ball turn at unit gain covers two seconds.
        internal const double NativeFlySpeed = 9;
        internal const double TravelSecondsPerQuarterTurn = 2;
        internal SceneView? ActiveView;
        internal bool Focused;
        private SceneView? cursorView;
        private Vector2? cursor;
        private SceneView? pivotView;
        private Vector3? pivotPoint;

        internal sealed class Context
        {
            internal readonly SceneView View;
            internal readonly StageHandle Stage;
            internal readonly string Document;
            internal Context(SceneView view)
            { View = view; Stage = StageUtility.GetCurrentStageHandle(); Document = DocumentId(); }
        }

        internal static string DocumentId()
        {
            var scenes = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i).handle.ToString());
            return StageUtility.GetCurrentStageHandle().GetHashCode() + ":" +
                SceneManager.GetActiveScene().handle + ":" + string.Join(",", scenes);
        }

        internal static bool Supported(SceneView? view) => view != null && view.camera != null
            && !view.in2DMode && !view.isRotationLocked && !EditorApplication.isPlayingOrWillChangePlaymode
            && !EditorApplication.isCompiling && !EditorApplication.isUpdating;

        public object? CaptureContext() => Focused && Supported(ActiveView) ? new Context(ActiveView!) : null;
        public bool IsCurrent(object context)
        {
            var bound = (Context)context;
            return Focused && Supported(bound.View) && bound.View == ActiveView
                && bound.Stage == StageUtility.GetCurrentStageHandle() && bound.Document == DocumentId();
        }

        internal static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);
        internal static Vector3 V(Vec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
        internal static double[] Values(Vec3 v) => new[] { v.X, v.Y, v.Z };
        internal static SceneCameraState ReadState(SceneView view)
        {
            var q = view.rotation;
            return new SceneCameraState(V(view.pivot), new Quat(q.w, q.x, q.y, q.z),
                Math.Abs(view.size), view.cameraSettings.fieldOfView, view.camera.aspect, view.orthographic);
        }
        internal CameraPoseValue? Read(object context) => ReadState(((Context)context).View).ToPose();
        public INavigationCapture BeginQuery(object context) => new Capture((Context)context,
            cursorView == ((Context)context).View && EditorWindow.mouseOverWindow == cursorView ? cursor : null);

        public NavigationWriteResult ApplyCamera(object context, CameraPoseValue desired, NavigationState? state, Vec3? pivot)
        {
            if (!IsCurrent(context)) return new NavigationWriteResult(false);
            var view = ((Context)context).View;
            var next = ReadState(view).Apply(desired, pivot);
            var settings = view.cameraSettings;
            settings.fieldOfView = (float)next.FieldOfView;
            view.cameraSettings = settings;
            var q = next.Rotation;
            view.LookAt(V(next.Pivot), new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W),
                (float)next.Size, next.Orthographic, true);
            view.Repaint();
            // Read the authoritative SceneView state; its render Camera updates
            // later during repaint and would report a stale transform here.
            return new NavigationWriteResult(true, Read(context));
        }

        public void ShowPivot(object context, Vec3? point)
        {
            pivotView = ((Context)context).View;
            pivotPoint = point.HasValue ? V(point.Value) : (Vector3?)null;
            if (pivotView != null) pivotView.Repaint();
        }
        internal void ClearPivot()
        {
            pivotPoint = null;
            if (pivotView != null) pivotView.Repaint();
            pivotView = null;
        }
        internal void TrackCursor(SceneView view)
        {
            var evt = Event.current;
            if (evt == null) return;
            if (evt.rawType == EventType.MouseLeaveWindow && cursorView == view)
            { cursor = null; cursorView = null; return; }
            if (EditorWindow.mouseOverWindow != view) return;
            // Ignore layout/key events: their mouse position can be synthetic.
            // rawType also preserves pointer events consumed by another editor tool.
            if (evt.rawType == EventType.MouseMove || evt.rawType == EventType.MouseDown
                || evt.rawType == EventType.MouseUp || evt.rawType == EventType.MouseDrag
                || evt.rawType == EventType.ScrollWheel || evt.rawType == EventType.MouseEnterWindow
                || (cursorView != view && evt.rawType == EventType.Repaint))
            {
                // beforeSceneGui runs inside Unity's camera viewport GUI group.
                // Its mouse position is already viewport-local: un-clipping it
                // would reintroduce toolbar offsets. No Camera.current fallback.
                var pixel = EditorGUIUtility.PointsToPixels(evt.mousePosition);
                pixel.y = view.camera.pixelHeight - pixel.y;
                cursorView = view;
                cursor = CursorNdc(pixel, new Rect(0, 0, view.camera.pixelWidth, view.camera.pixelHeight));
            }
        }

        internal static Vector2? CursorNdc(Vector2 pixel, Rect viewport)
        {
            if (viewport.width <= 0 || viewport.height <= 0 || !viewport.Contains(pixel)) return null;
            return new Vector2((pixel.x - viewport.x) * 2 / viewport.width - 1,
                (pixel.y - viewport.y) * 2 / viewport.height - 1);
        }

        internal static float MarkerRadius(Camera camera, Vector3 point, float pixelsPerPoint)
        {
            var depth = Vector3.Dot(point - camera.transform.position, camera.transform.forward);
            var height = camera.orthographic ? 2 * camera.orthographicSize
                : 2 * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2);
            return 4 * pixelsPerPoint * height / Math.Max(1, camera.pixelHeight);
        }

        internal void Draw(SceneView view)
        {
            if (view != pivotView || !pivotPoint.HasValue || Event.current == null || Event.current.type != EventType.Repaint) return;
            var point = pivotPoint.Value;
            var projected = view.camera.WorldToViewportPoint(point);
            if (projected.x < 0 || projected.x > 1 || projected.y < 0 || projected.y > 1
                || projected.z < view.camera.nearClipPlane || projected.z > view.camera.farClipPlane) return;
            var radius = MarkerRadius(view.camera, point, EditorGUIUtility.pixelsPerPoint);
            var depthTest = Handles.zTest;
            using (new Handles.DrawingScope(Color.white, Matrix4x4.identity))
            try
            {
                // Complementary per-fragment passes let geometry intersect the
                // marker naturally. Handles' unlit graphics do not write depth.
                DrawMarker(view.camera, point, radius, CompareFunction.LessEqual, 1);
                DrawMarker(view.camera, point, radius, CompareFunction.Greater, 0.2f);
            }
            finally { Handles.zTest = depthTest; }
        }

        private static void DrawMarker(Camera camera, Vector3 point, float radius, CompareFunction depth, float alpha)
        {
            Handles.zTest = depth;
            Handles.color = new Color(0, 1, 0, alpha);
            Handles.DrawSolidDisc(point, camera.transform.forward, radius);

            // A real annulus, not a black disc underneath the translucent green
            // fill. Both layers retain the same opacity without double blending.
            Handles.color = new Color(0, 0, 0, alpha);
            var right = camera.transform.right * radius;
            var up = camera.transform.up * radius;
            const int segments = 64;
            const float outerScale = 5.5f / 4;
            var quad = new Vector3[4];
            for (var i = 0; i < segments; i++)
            {
                var a = i * (2 * Mathf.PI / segments);
                var b = (i + 1) * (2 * Mathf.PI / segments);
                var first = right * Mathf.Cos(a) + up * Mathf.Sin(a);
                var second = right * Mathf.Cos(b) + up * Mathf.Sin(b);
                quad[0] = point + first;
                quad[1] = point + first * outerScale;
                quad[2] = point + second * outerScale;
                quad[3] = point + second;
                Handles.DrawAAConvexPolygon(quad);
            }
        }

        internal sealed class Capture : INavigationCapture
        {
            private readonly Context context;
            private readonly SceneCameraState camera;
            private readonly double translationScale;
            private readonly Vector2? cursor;
            private SceneGeometry? geometry;
            private SceneGeometry Geometry => geometry ??= new SceneGeometry(context.View, context.Stage, camera);
            private CameraPoseValue? initial;
            internal Capture(Context context, Vector2? cursor)
            {
                this.context = context;
                this.cursor = cursor.HasValue && Math.Abs(cursor.Value.x) <= 1 && Math.Abs(cursor.Value.y) <= 1 ? cursor : null;
                camera = ReadState(context.View);
                // Use the native base speed, not its normalized slider or the
                // temporary Shift multiplier. Rotatrix owns input gain/rate policy.
                translationScale = context.View.cameraSettings.speed * NativeFlySpeed * TravelSecondsPerQuarterTurn;
            }
            public CameraPoseValue? InitialCameraObservation() => initial;

            private object Pick(Vector2 ndc, bool selected)
            {
                // GUI coordinates match Handles.BeginGUI, independently of the
                // protocol's normalized cursor convention and display scaling.
                var rect = context.View.camera.pixelRect;
                var scale = EditorGUIUtility.pixelsPerPoint;
                var marker = new[] { (ndc.x + 1.0) * rect.width / (2 * scale),
                    (1.0 - ndc.y) * rect.height / (2 * scale) };
                return Geometry.Pick(SceneGeometry.Ray(camera, ndc), selected, marker);
            }

            public object? Resolve(string name)
            {
                switch (name)
                {
                    case "document.id": return context.Document;
                    case "world.orientation": return new Dictionary<string, object> {
                        ["forward"] = new[] { 0.0, 0.0, 1.0 }, ["up"] = new[] { 0.0, 1.0, 0.0 }, ["handedness"] = "left" };
                    case "camera.pose":
                        var pose = camera.ToPose(); initial = pose;
                        var value = new Dictionary<string, object> { ["t"] = Values(pose.Position), ["r"] = Values(pose.RotationVector) };
                        if (pose.Fov.HasValue) value["fov"] = pose.Fov.Value;
                        else value["ortho_extent"] = pose.OrthoExtent!.Value;
                        return value;
                    case "camera.view_target": return Values(camera.Pivot);
                    case "viewport.aspect": return camera.Aspect;
                    case "viewport.cursor": return cursor.HasValue ? new[] { (double)cursor.Value.x, cursor.Value.y } : NavigationQuery.Unavailable;
                    case "selection.bounds": return Geometry.Bounds(true);
                    case "model.bounds": return Geometry.Bounds(false);
                    case "navigation.translation_scale":
                        return translationScale > 0 && !double.IsInfinity(translationScale)
                            ? (object)translationScale : NavigationQuery.Unavailable;
                    case "navigation.preferences": return new Dictionary<string, object> {
                        ["lock_roll"] = EditorPrefs.GetBool("Rotatrix.Unity.LockRoll", true),
                        ["lock_translation_plane"] = EditorPrefs.GetBool("Rotatrix.Unity.LockPlane", true) };
                    case "pick.cursor":
                    case "pick.cursor.selection":
                        return cursor.HasValue ? Pick(cursor.Value, name.EndsWith(".selection")) : NavigationQuery.Unavailable;
                    case "pick.viewport_center":
                    case "pick.viewport_center.selection":
                        return Pick(Vector2.zero, name.EndsWith(".selection"));
                    default: return NavigationQuery.Unavailable;
                }
            }
        }

    }
}
