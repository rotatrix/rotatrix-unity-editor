using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Rotatrix.Unity.Editor
{
    // The editor's native mesh intersection supports non-readable imported meshes.
    // Isolated here because Unity exposes it internally rather than as a public API.
    internal sealed class SceneGeometry
    {
        private delegate bool IntersectMesh(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit);
        private static readonly IntersectMesh intersect = (IntersectMesh)Delegate.CreateDelegate(typeof(IntersectMesh),
            typeof(HandleUtility).GetMethod("IntersectRayMesh", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4), typeof(RaycastHit).MakeByRefType() }, null)
            ?? throw new MissingMethodException("Unity editor mesh picking API is unavailable"));
        private readonly Renderer[] renderers;
        private readonly Terrain[] terrains;
        private readonly Transform[] selection;
        private readonly float near, far;
        private readonly Vector3 eye, forward;
        private readonly Func<GameObject, bool> visible;

        internal SceneGeometry(SceneView view, StageHandle stage, SceneCameraState camera)
        {
            bool Visible(GameObject go) => go.scene.IsValid() && go.activeInHierarchy && stage.Contains(go)
                && (view.camera.cullingMask & (1 << go.layer)) != 0
                && (Tools.visibleLayers & (1 << go.layer)) != 0
                && !SceneVisibilityManager.instance.IsHidden(go);
            visible = Visible;
            renderers = stage.FindComponentsOfType<Renderer>()
                .Where(r => r.enabled && !r.forceRenderingOff && Visible(r.gameObject)).ToArray();
            terrains = stage.FindComponentsOfType<Terrain>()
                .Where(t => t.enabled && t.drawHeightmap && t.terrainData != null && Visible(t.gameObject)).ToArray();
            selection = Selection.transforms.Where(t => t.gameObject.scene.IsValid() && stage.Contains(t.gameObject)).ToArray();
            near = view.camera.nearClipPlane; far = view.camera.farClipPlane;
            eye = SceneViewAdapter.V(camera.ToPose().Position);
            forward = SceneViewAdapter.V(camera.Rotation.Rotate(new OpenAxis.Geometry.Vec3(0, 0, 1)));
        }

        private bool Selected(Transform t) => selection.Any(root => t == root || t.IsChildOf(root));
        internal object Bounds(bool selected)
        {
            Bounds? result = null;
            foreach (var r in renderers) if (!selected || Selected(r.transform)) Include(ref result, r.bounds);
            foreach (var t in terrains) if (!selected || Selected(t.transform)) Include(ref result, TerrainBounds(t));
            // Transform-only selections (lights, empty hierarchy roots) still provide a pivot.
            if (selected)
                foreach (var t in selection)
                    if (visible(t.gameObject) && t.GetComponentsInChildren<Renderer>(true).Length == 0
                        && t.GetComponentsInChildren<Terrain>(true).Length == 0)
                        Include(ref result, new Bounds(t.position, Vector3.zero));
            return result.HasValue ? Box(result.Value) : OpenAxis.Client.NavigationQuery.Unavailable;
        }

        internal object Pick(Ray ray, bool selected, double[]? markerPosition = null)
        {
            if (selected && selection.Length == 0) return OpenAxis.Client.NavigationQuery.Unavailable;
            var slope = Vector3.Dot(ray.direction, forward);
            if (slope <= 0) return OpenAxis.Client.NavigationQuery.Unavailable;
            var startDepth = Vector3.Dot(ray.origin - eye, forward);
            if (startDepth < near) ray.origin += ray.direction * ((near - startDepth) / slope);
            var distance = float.PositiveInfinity;
            Vector3 point = default;
            Bounds bounds = default;
            Transform? target = null;
            void Consider(RaycastHit hit, Bounds box, Transform owner)
            {
                var depth = Vector3.Dot(hit.point - eye, forward);
                var along = Vector3.Dot(hit.point - ray.origin, ray.direction);
                if (depth < near || depth > far || along < 0 || along >= distance) return;
                distance = along; point = hit.point; bounds = box; target = owner;
            }
            foreach (var r in renderers)
            {
                if (selected && !Selected(r.transform)) continue;
                if (SceneVisibilityManager.instance.IsPickingDisabled(r.gameObject) || !r.bounds.IntersectRay(ray)) continue;
                Mesh? baked = null;
                try
                {
                    Mesh? mesh;
                    if (r is SkinnedMeshRenderer skin)
                    {
                        if (skin.sharedMesh == null) continue;
                        baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                        skin.BakeMesh(baked); mesh = baked;
                    }
                    else mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh != null && intersect(ray, mesh, r.localToWorldMatrix, out var hit))
                        Consider(hit, r.bounds, r.transform);
                }
                finally { if (baked != null) UnityEngine.Object.DestroyImmediate(baked); }
            }
            foreach (var t in terrains)
            {
                if (selected && !Selected(t.transform)) continue;
                if (SceneVisibilityManager.instance.IsPickingDisabled(t.gameObject)) continue;
                var collider = t.GetComponent<TerrainCollider>();
                if (collider != null && collider.enabled && collider.terrainData == t.terrainData
                    && collider.Raycast(ray, out var hit, float.MaxValue))
                    Consider(hit, TerrainBounds(t), t.transform);
                else if (collider == null || !collider.enabled || collider.terrainData != t.terrainData)
                    PickTerrainMesh(t, ray, hit => Consider(hit, TerrainBounds(t), t.transform));
            }
            // Selection-only queries exclude unselected surfaces before hit ordering.
            var result = new Dictionary<string, object>();
            if (markerPosition != null) result["markerPosition"] = markerPosition;
            if (target == null)
                return markerPosition != null ? result : OpenAxis.Client.NavigationQuery.Unavailable;
            result["point"] = SceneViewAdapter.Values(SceneViewAdapter.V(point));
            result["bounds"] = Box(bounds);
            return result;
        }

        private static void PickTerrainMesh(Terrain terrain, Ray ray, Action<RaycastHit> hit)
        {
            // Collider-free terrains: intersect only heightfield tiles crossed by
            // the ray. Respect terrain holes; never add components to the scene.
            var data = terrain.terrainData;
            var cells = data.heightmapResolution - 1;
            var step = data.heightmapScale;
            const int tile = 32;
            for (var z = 0; z < cells; z += tile)
            for (var x = 0; x < cells; x += tile)
            {
                var width = Math.Min(tile, cells - x);
                var depth = Math.Min(tile, cells - z);
                var size = new Vector3(width * step.x, data.size.y, depth * step.z);
                var start = terrain.transform.position + new Vector3(x * step.x, 0, z * step.z);
                if (!new Bounds(start + size / 2, size).IntersectRay(ray)) continue;
                var heights = data.GetHeights(x, z, width + 1, depth + 1);
                var vertices = new Vector3[(width + 1) * (depth + 1)];
                var triangles = new List<int>();
                for (var iz = 0; iz <= depth; iz++)
                for (var ix = 0; ix <= width; ix++)
                    vertices[iz * (width + 1) + ix] = new Vector3((x + ix) * step.x, heights[iz, ix] * data.size.y, (z + iz) * step.z);
                for (var iz = 0; iz < depth; iz++)
                for (var ix = 0; ix < width; ix++)
                {
                    if (data.IsHole(x + ix, z + iz)) continue;
                    var a = iz * (width + 1) + ix; var b = a + width + 1;
                    triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
                var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    mesh.vertices = vertices; mesh.triangles = triangles.ToArray();
                    if (intersect(ray, mesh, Matrix4x4.Translate(terrain.transform.position), out var contact)) hit(contact);
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }

        internal static Ray Ray(SceneCameraState camera, Vector2 ndc)
        {
            var eye = camera.ToPose().Position;
            var height = camera.Orthographic ? camera.Size / Math.Min(camera.Aspect, 1) : Math.Tan(camera.VerticalFov / 2);
            var offset = camera.Rotation.Rotate(new OpenAxis.Geometry.Vec3(ndc.x * height * camera.Aspect, ndc.y * height, 0));
            var forward = camera.Rotation.Rotate(new OpenAxis.Geometry.Vec3(0, 0, 1));
            return camera.Orthographic
                ? new Ray(SceneViewAdapter.V(eye + offset), SceneViewAdapter.V(forward))
                : new Ray(SceneViewAdapter.V(eye), SceneViewAdapter.V((forward + offset).Normalized()));
        }
        private static Bounds TerrainBounds(Terrain t) => new Bounds(t.transform.position + t.terrainData.size / 2, t.terrainData.size);
        private static object Box(Bounds b) => new Dictionary<string, object> {
            ["min"] = SceneViewAdapter.Values(SceneViewAdapter.V(b.min)), ["max"] = SceneViewAdapter.Values(SceneViewAdapter.V(b.max)) };
        private static void Include(ref Bounds? all, Bounds value)
        { if (!all.HasValue) all = value; else { var b = all.Value; b.Encapsulate(value); all = b; } }
    }
}
