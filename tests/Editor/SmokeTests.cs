using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenAxis.Client;
using OpenAxis.Geometry;
using OpenAxis.Navigation;
using Rotatrix.Unity.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Rotatrix.Unity.Tests
{
    public static class SmokeTests
    {
        private static SceneView view;
        private static GameObject cube;
        private static SceneViewAdapter adapter;
        private static EditorScheduler scheduler;
        private static NavigationSession session;
        private static UnityDiagnostics diagnostics;
        private static OpenAxisClient client;
        private static Task connecting;
        private static double deadline;
        private static int stage;
        private static volatile bool finished;
        private static volatile bool cancelled;
        private static volatile string failure;

        public static void Run()
        {
            try
            {
                // The production connector is disabled in batchmode. The test
                // uses the public SDK against a protocol fixture, never changes
                // licensing in the package, and never connects to the real host.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.position = new Vector3(2, 3, 4);
                Selection.activeGameObject = cube;
                view = ScriptableObject.CreateInstance<SceneView>();
                view.Show();
                view.in2DMode = false;
                view.isRotationLocked = false;
                view.camera.aspect = 1.5f;
                view.LookAt(Vector3.zero, Quaternion.identity, 5, false, true);
                adapter = new SceneViewAdapter { ActiveView = view, Focused = true };
                ValidateNativeCamera();
                ValidatePanelFocus();
                ValidateNativeSpeed();
                foreach (var freeCamera in new[] { false, true })
                {
                    var metadata = RotatrixEditor.NavigationMetadata(true, freeCamera);
                    Check(metadata.Tags.Contains(freeCamera ? "navigation.hint.free_camera" : "navigation.hint.orbit")
                        && !metadata.Tags.Contains(freeCamera ? "navigation.hint.orbit" : "navigation.hint.free_camera"),
                        "camera preference publishes exactly the matching shared-profile hint");
                    Check(metadata.Capabilities.Contains("navigation") && metadata.Focused == true, "mode preserves navigation routing");
                }
                ValidateScreenCoordinates();
                ValidateGeometry();
                ValidateTerrainAndSkin();
                ValidatePrefabStage();
                RotatrixEditor.SetEnabled(false);
                Check(!RotatrixEditor.Enabled, "disconnect is retained in editor session");
                RotatrixEditor.SetEnabled(true);
                Check(RotatrixEditor.Enabled, "connect resumes editor session");
                if (Environment.GetEnvironmentVariable("ROTATRIX_UNITY_NATIVE_ONLY") == "1")
                {
                    Finish(null);
                    return;
                }
                scheduler = new EditorScheduler();
                diagnostics = new UnityDiagnostics(Environment.GetEnvironmentVariable("ROTATRIX_UNITY_TEST_RESULT") + ".log");
                diagnostics.SetEnabled(true);
                client = new OpenAxisClient("Unity integration test", new Listener(),
                    Environment.GetEnvironmentVariable("ROTATRIX_UNITY_TEST_URL"));
                session = new NavigationSession(client, adapter, scheduler,
                    observation: adapter.Read, comparison: SceneCameraState.Compare,
                    diagnostics: diagnostics.Model);
                connecting = Task.Run(async () => {
                    await client.ConnectAsync();
                    await client.SendTagsAsync(new[] { "app.unity", "navigation.hint.orbit" });
                    await client.SendCapabilitiesAsync(new[] { "navigation" });
                    await client.SendFocusAsync(true);
                });
                deadline = EditorApplication.timeSinceStartup + 40;
                EditorApplication.update += Tick;
            }
            catch (Exception error) { Finish(error.ToString()); }
        }

        private static void Check(bool condition, string description)
        { if (!condition) throw new Exception(description); }

        private static void ValidateNativeCamera()
        {
            foreach (var ortho in new[] { false, true })
            foreach (var aspect in new[] { 0.5f, 1.5f })
            {
                view.camera.aspect = aspect;
                view.LookAt(new Vector3(2, -1, 3), Quaternion.Euler(15, 37, 22), 5, ortho, true);
                var context = adapter.CaptureContext();
                Check(context != null, "capture Scene view context");
                var pose = adapter.Read(context).Value;
                view.camera.orthographic = ortho;
                Check(Math.Abs(view.cameraDistance - SceneViewAdapter.ReadState(view).Distance) < 1e-4,
                    "eye distance matches native Unity in perspective and ortho");
                var look = PoseHelpers.LookAtFromPose(pose);
                var expectedForward = view.rotation * Vector3.forward;
                var actualForward = SceneViewAdapter.V((look.target - look.eye).Normalized());
                Check(Vector3.Distance(expectedForward, actualForward) < 1e-5, "native quaternion direction");
                var moved = new CameraPoseValue(pose.Position + new Vec3(1, -2, 3),
                    new Vec3(0.2, -0.3, 0.6), pose.Fov, pose.OrthoExtent);
                var write = adapter.ApplyCamera(context, moved, null, new Vec3(2, 3, 4));
                Check(write.Success && write.Realized.HasValue, "native camera write");
                Check(!SceneCameraState.Compare(moved, write.Realized.Value).Changed, "native pose round trip");
                Check(SceneViewAdapter.ReadState(view).Orthographic == ortho, "native projection retained");
            }
            view.LookAt(Vector3.zero, Quaternion.identity, 5, false, true);
            view.camera.aspect = 1.5f;
            var capture = adapter.BeginQuery(adapter.CaptureContext());
            var bounds = (Dictionary<string, object>)capture.Resolve("selection.bounds");
            var min = (double[])bounds["min"];
            Check(Math.Abs(min[0] - 1.5) < 1e-5 && Math.Abs(min[1] - 2.5) < 1e-5, "selection world bounds");
            Check(ReferenceEquals(capture.Resolve("pick.cursor"), NavigationQuery.Unavailable), "unknown cursor is unavailable");
            var old = adapter.CaptureContext();
            adapter.Focused = false;
            Check(!adapter.IsCurrent(old) && adapter.CaptureContext() == null, "focus loss invalidates context");
            adapter.Focused = true;
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Packages/com.rotatrix.unity/") || !path.EndsWith(".dll")) continue;
                var importer = (PluginImporter)AssetImporter.GetAtPath(path);
                Check(importer.GetCompatibleWithEditor() && !importer.GetCompatibleWithAnyPlatform()
                    && !importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)
                    && !importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX)
                    && !importer.GetCompatibleWithPlatform(BuildTarget.StandaloneLinux64), "editor-only DLL import: " + path);
            }
        }

        private static void ValidatePanelFocus()
        {
            var panels = new[] { "UnityEditor.InspectorWindow", "UnityEditor.SceneHierarchyWindow", "UnityEditor.ProjectBrowser" }
                .Select(name => (EditorWindow)ScriptableObject.CreateInstance(typeof(UnityEditor.Editor).Assembly.GetType(name, true)))
                .Append(ScriptableObject.CreateInstance<RotatrixWindow>()).ToArray();
            var game = (EditorWindow)ScriptableObject.CreateInstance(typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView", true));
            var secondView = ScriptableObject.CreateInstance<SceneView>();
            secondView.Show();
            try
            {
                foreach (var panel in panels)
                {
                    Check(RotatrixEditor.NavigationView(panel, view, true) == view, panel.GetType().Name + " preserves last Scene view");
                    Check(RotatrixEditor.NavigationView(panel, view, false) == null, "background Unity cannot navigate");
                    Check(RotatrixEditor.NavigationView(panel, null, true) == null, "panel needs an existing Scene view");
                    view.in2DMode = true;
                    Check(RotatrixEditor.NavigationView(panel, view, true) == null, "panel cannot activate unsupported Scene view");
                    view.in2DMode = false;
                }
                Check(RotatrixEditor.NavigationView(game, view, true) == null, "Game view pauses navigation");
                Check(RotatrixEditor.NavigationView(null, view, true) == null, "missing focused window pauses navigation");
                Check(RotatrixEditor.NavigationView(secondView, view, true) == secondView, "focused Scene view takes precedence");
                Check(RotatrixEditor.NavigationView(view, view, true, secondView) == secondView,
                    "hover selects another Scene view without clicking");
                Check(RotatrixEditor.NavigationView(view, secondView, true, panels[0]) == secondView,
                    "panel hover retains hovered target rather than old keyboard focus");
                Check(RotatrixEditor.NavigationView(game, view, true, secondView) == secondView,
                    "hovered Scene view takes precedence over focused Game view");
                Check(RotatrixEditor.NavigationView(view, view, true, game) == null,
                    "hovered Game view pauses navigation");
                Check(RotatrixEditor.NavigationView(view, view, false, secondView) == null,
                    "hover cannot activate background Unity");
                secondView.in2DMode = true;
                Check(RotatrixEditor.NavigationView(secondView, view, true) == null, "unsupported focused view does not fall back to another view");
                Check(RotatrixEditor.NavigationView(view, view, true, secondView) == null,
                    "unsupported hovered Scene view pauses instead of falling back");
                secondView.in2DMode = false;
                secondView.isRotationLocked = true;
                Check(RotatrixEditor.NavigationView(view, view, true, secondView) == null,
                    "rotation-locked hovered Scene view pauses navigation");
            }
            finally
            {
                view.in2DMode = false;
                foreach (var panel in panels) UnityEngine.Object.DestroyImmediate(panel);
                UnityEngine.Object.DestroyImmediate(game);
                secondView.Close();
            }
        }

        private static void ValidateNativeSpeed()
        {
            var nativeMotion = typeof(SceneView).Assembly.GetType("UnityEditor.SceneViewMotion", true);
            var nativeSpeed = Convert.ToDouble(nativeMotion.GetField("k_FlySpeed",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetRawConstantValue());
            Check(nativeSpeed == SceneViewAdapter.NativeFlySpeed, "conversion agrees with installed Unity native fly speed");
            var normalized = view.cameraSettings.speedNormalized;
            var size = view.size;
            try
            {
                var settings = view.cameraSettings;
                settings.speedNormalized = 0.2f;
                view.cameraSettings = settings;
                var capture = adapter.BeginQuery(adapter.CaptureContext());
                var scale = (double)capture.Resolve("navigation.translation_scale");
                Check(scale == (double)view.cameraSettings.speed * nativeSpeed * 2 && scale > 0,
                    "quarter-turn scale equals two seconds of native camera travel");
                view.size *= 3;
                Check((double)adapter.BeginQuery(adapter.CaptureContext()).Resolve("navigation.translation_scale") == scale,
                    "camera zoom does not change movement scale");
                settings = view.cameraSettings;
                settings.speedNormalized = 0.8f;
                view.cameraSettings = settings;
                var updated = (double)adapter.BeginQuery(adapter.CaptureContext()).Resolve("navigation.translation_scale");
                Check(updated == (double)view.cameraSettings.speed * nativeSpeed * 2 && updated != scale, "new query follows native speed change");
                Check((double)capture.Resolve("navigation.translation_scale") == scale, "query retains consistent speed snapshot");
            }
            finally
            {
                var settings = view.cameraSettings;
                settings.speedNormalized = normalized;
                view.cameraSettings = settings;
                view.size = size;
            }
        }

        private static void ValidateScreenCoordinates()
        {
            var center = SceneViewAdapter.CursorNdc(new Vector2(500, 350), new Rect(100, 50, 800, 600));
            Check(center.HasValue && center.Value.sqrMagnitude < 1e-8, "cursor uses viewport offset");
            var highDpi = SceneViewAdapter.CursorNdc(new Vector2(1000, 700), new Rect(200, 100, 1600, 1200));
            Check(highDpi == center, "cursor NDC independent of DPI");
            Check(!SceneViewAdapter.CursorNdc(new Vector2(50, 350), new Rect(100, 50, 800, 600)).HasValue,
                "cursor outside render viewport unavailable");
            var obj = new GameObject("Marker projection test");
            try
            {
                var camera = obj.AddComponent<Camera>();
                camera.pixelRect = new Rect(0, 0, 800, 600);
                camera.fieldOfView = 60;
                camera.orthographicSize = 5;
                foreach (var ortho in new[] { false, true })
                foreach (var dpi in new[] { 1f, 1.5f, 2f })
                foreach (var depth in new[] { 2f, 20f })
                {
                    camera.orthographic = ortho;
                    var point = new Vector3(0, 0, depth);
                    var radius = SceneViewAdapter.MarkerRadius(camera, point, dpi);
                    var a = camera.WorldToScreenPoint(point);
                    var b = camera.WorldToScreenPoint(point + Vector3.right * radius);
                    Check(Math.Abs((b.x - a.x) * 2 / dpi - 8) < 1e-3, "pivot stays 8 logical pixels");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        private static void ValidateGeometry()
        {
            // Picking must work on editor geometry without physics components.
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
            view.LookAt(cube.transform.position, Quaternion.identity, 5, false, true);
            var capture = adapter.BeginQuery(adapter.CaptureContext());
            var pick = (Dictionary<string, object>)capture.Resolve("pick.viewport_center");
            Check(pick.ContainsKey("markerPosition"), "actual test returns local marker position");
            Check(Math.Abs(((double[])pick["point"])[2] - 3.5) < 1e-4, "collider-free mesh surface pick");
            Check(capture.Resolve("pick.viewport_center.selection") is Dictionary<string, object>, "selected surface pick");
            Check(capture.Resolve("model.bounds") is Dictionary<string, object>, "model bounds");
            Check((double)capture.Resolve("navigation.translation_scale") > 0, "positive translation scale");
            Check(capture.Resolve("navigation.preferences") is Dictionary<string, object>, "camera preferences");
            var cursorCapture = new SceneViewAdapter.Capture((SceneViewAdapter.Context)adapter.CaptureContext(), Vector2.zero);
            Check(cursorCapture.Resolve("viewport.cursor") is double[], "normalized cursor fact");
            Check(cursorCapture.Resolve("pick.cursor") is Dictionary<string, object>, "cursor surface pick");
            Check(cursorCapture.Resolve("pick.cursor.selection") is Dictionary<string, object>, "selected cursor surface pick");
            var outside = new SceneViewAdapter.Capture((SceneViewAdapter.Context)adapter.CaptureContext(), new Vector2(2, 0));
            Check(ReferenceEquals(outside.Resolve("viewport.cursor"), NavigationQuery.Unavailable)
                && ReferenceEquals(outside.Resolve("pick.cursor"), NavigationQuery.Unavailable), "outside cursor not clamped");
            var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                blocker.transform.position = cube.transform.position - Vector3.forward * 2;
                UnityEngine.Object.DestroyImmediate(blocker.GetComponent<Collider>());
                capture = adapter.BeginQuery(adapter.CaptureContext());
                var selectedHit = (Dictionary<string, object>)capture.Resolve("pick.viewport_center.selection");
                Check(selectedHit.ContainsKey("markerPosition") && Math.Abs(((double[])selectedHit["point"])[2] - 3.5) < 1e-4,
                    "unselected foreground does not occlude selected surface");
                var selectedCursor = new SceneViewAdapter.Capture((SceneViewAdapter.Context)adapter.CaptureContext(), Vector2.zero);
                selectedHit = (Dictionary<string, object>)selectedCursor.Resolve("pick.cursor.selection");
                Check(Math.Abs(((double[])selectedHit["point"])[2] - 3.5) < 1e-4, "selected cursor excludes foreground");
                pick = (Dictionary<string, object>)capture.Resolve("pick.viewport_center");
                Check(Math.Abs(((double[])pick["point"])[2] - 1.5) < 1e-4, "nearest surface wins");
                SceneVisibilityManager.instance.Hide(blocker, true);
                capture = adapter.BeginQuery(adapter.CaptureContext());
                Check(capture.Resolve("pick.viewport_center.selection") is Dictionary<string, object>, "hidden geometry ignored");
                SceneVisibilityManager.instance.Show(blocker, true);
                blocker.SetActive(false);
                view.LookAt(cube.transform.position, Quaternion.identity, 5, true, true);
                capture = adapter.BeginQuery(adapter.CaptureContext());
                Check(capture.Resolve("pick.viewport_center") is Dictionary<string, object>, "orthographic pick");
                var camera = SceneViewAdapter.ReadState(view);
                var offscreen = SceneGeometry.Ray(camera, new Vector2(1, 1));
                var geometry = new SceneGeometry(view, UnityEditor.SceneManagement.StageUtility.GetCurrentStageHandle(), camera);
                Check(ReferenceEquals(geometry.Pick(offscreen, false), NavigationQuery.Unavailable), "empty-space miss");
            }
            finally { UnityEngine.Object.DestroyImmediate(blocker); }
            view.LookAt(cube.transform.position, Quaternion.identity, 5, false, true);
        }

        private static void ValidateTerrainAndSkin()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(10, 2, 10) };
            data.SetHeights(0, 0, new float[33, 33]);
            var terrain = Terrain.CreateTerrainGameObject(data);
            terrain.transform.position = new Vector3(100, 0, 100);
            Physics.SyncTransforms();
            var skinned = new GameObject("Skin test");
            var mesh = UnityEngine.Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            try
            {
                view.LookAt(new Vector3(105, 0, 105), Quaternion.Euler(90, 0, 0), 5, false, true);
                Selection.activeGameObject = terrain;
                var capture = adapter.BeginQuery(adapter.CaptureContext());
                Check(capture.Resolve("pick.viewport_center") is Dictionary<string, object>, "terrain collider pick");
                UnityEngine.Object.DestroyImmediate(terrain.GetComponent<TerrainCollider>());
                capture = adapter.BeginQuery(adapter.CaptureContext());
                var pick = (Dictionary<string, object>)capture.Resolve("pick.viewport_center.selection");
                Check(Math.Abs(((double[])pick["point"])[1]) < 1e-4, "collider-free terrain surface");
                data.SetHoles(0, 0, new bool[32, 32]);
                capture = adapter.BeginQuery(adapter.CaptureContext());
                var miss = (Dictionary<string, object>)capture.Resolve("pick.viewport_center");
                Check(miss.ContainsKey("markerPosition") && !miss.ContainsKey("point"), "terrain hole is a tested miss");
                terrain.SetActive(false);
                skinned.transform.position = new Vector3(200, 0, 0);
                var skin = skinned.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh;
                skin.sharedMaterial = cube.GetComponent<Renderer>().sharedMaterial;
                skin.localBounds = mesh.bounds;
                view.LookAt(skinned.transform.position, Quaternion.identity, 5, false, true);
                Selection.activeGameObject = skinned;
                capture = adapter.BeginQuery(adapter.CaptureContext());
                Check(capture.Resolve("pick.viewport_center.selection") is Dictionary<string, object>, "baked skinned mesh pick");
                skinned.SetActive(false);
                var filter = cube.GetComponent<MeshFilter>();
                var original = filter.sharedMesh;
                try
                {
                    filter.sharedMesh = mesh;
                    mesh.UploadMeshData(true);
                    view.LookAt(cube.transform.position, Quaternion.identity, 5, false, true);
                    capture = adapter.BeginQuery(adapter.CaptureContext());
                    Check(capture.Resolve("pick.viewport_center") is Dictionary<string, object>, "non-readable mesh pick");
                }
                finally { filter.sharedMesh = original; }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrain);
                UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(skinned);
                UnityEngine.Object.DestroyImmediate(mesh);
                Selection.activeGameObject = cube;
                view.LookAt(cube.transform.position, Quaternion.identity, 5, false, true);
            }
        }

        private static void ValidatePrefabStage()
        {
            const string path = "Assets/RotatrixPickingTest.prefab";
            PrefabUtility.SaveAsPrefabAsset(cube, path);
            var previous = adapter.CaptureContext();
            try
            {
                var prefab = PrefabStageUtility.OpenPrefab(path);
                var root = prefab.prefabContentsRoot;
                Selection.activeGameObject = root;
                view.LookAt(root.transform.position, Quaternion.identity, 5, false, true);
                Check(!adapter.IsCurrent(previous), "prefab stage invalidates gesture context");
                var capture = adapter.BeginQuery(adapter.CaptureContext());
                Check(capture.Resolve("pick.viewport_center.selection") is Dictionary<string, object>, "prefab-stage geometry pick");
                var box = (Dictionary<string, object>)capture.Resolve("model.bounds");
                Check(Math.Abs(((double[])box["min"])[0] - (root.transform.position.x - 0.5)) < 1e-4, "prefab-stage model bounds");
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(path);
                Selection.activeGameObject = cube;
                view.LookAt(cube.transform.position, Quaternion.identity, 5, false, true);
            }
        }

        private static void Tick()
        {
            try
            {
                if (failure != null) throw new Exception(failure);
                if (connecting.IsFaulted) throw connecting.Exception;
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException(
                    $"Unity protocol smoke test timed out: stage={stage}, finished={finished}, cancelled={cancelled}, connection={client.State}, active={session.IsActive}");
                session.CheckContext();
                session.NativeCameraChanged();
                scheduler.Drain();
                diagnostics.Update();
                var pose = SceneViewAdapter.ReadState(view).ToPose();
                if (stage == 0 && Math.Abs(pose.Position.X - 11) < 1e-4)
                {
                    // An independent mouse-like pan must generate camera.delta.
                    view.pivot += Vector3.right;
                    stage = 1;
                }
                if (stage == 1 && Math.Abs(pose.Position.X - 12) < 1e-4 && finished)
                {
                    var frame = diagnostics.Model.Presentation();
                    Check(frame.Lines.Any(row => row.Text.Contains("selection.bounds")), "SDK records query facts");
                    Check(frame.Lines.Any(row => row.Text.Contains("camera write: equivalent")), "SDK uses adapter float comparison for write verdicts");
                    Check(frame.Segments.Count > 0, "SDK produces scene diagnostic geometry");
                    Check(diagnostics.LogText.Contains("camera correction"), "SDK correction logs reach Unity sink");
                    Check(diagnostics.LogText.Contains("query"), "SDK query logs reach Unity sink");
                    adapter.Focused = false;
                    session.CheckContext();
                    stage = 2;
                }
                if (stage == 2 && !session.IsActive && cancelled)
                {
                    diagnostics.SetEnabled(false);
                    Check(diagnostics.Model.Presentation().Lines.Count == 0, "diagnostics disable clears presentation");
                    var log = new UnityDiagnostics(Environment.GetEnvironmentVariable("ROTATRIX_UNITY_TEST_RESULT") + ".worker.log");
                    Task.Run(() => { for (var i = 0; i < 250; i++) log.Log("info", "entry-" + i); }).GetAwaiter().GetResult();
                    Check(log.LogText.Split('\n').Length == 200 && log.LogText.Contains("entry-249"), "worker log sink is bounded");
                    Finish(null);
                }
            }
            catch (Exception error) { Finish(error.ToString()); }
        }

        private static void Finish(string error)
        {
            EditorApplication.update -= Tick;
            try
            {
                client?.DisconnectAsync().GetAwaiter().GetResult();
                session?.Dispose();
                session?.Drain();
                scheduler?.Dispose();
                diagnostics?.Clear();
                if (view != null) view.Close();
                if (cube != null) UnityEngine.Object.DestroyImmediate(cube);
            }
            catch (Exception cleanup) { error = error ?? cleanup.ToString(); }
            var report = Environment.GetEnvironmentVariable("ROTATRIX_UNITY_TEST_RESULT");
            var passed = Environment.GetEnvironmentVariable("ROTATRIX_UNITY_NATIVE_ONLY") == "1"
                ? "PASS: native camera, cursor/center/selection picking, mesh/skin/terrain geometry, scene bounds, Prefab Mode, focus, editor-only imports and session connection controls. No network/device test."
                : "PASS: native camera, cursor/center/selection picking, mesh/skin/terrain geometry, scene bounds, Prefab Mode, focus, editor-only imports, MessagePack/WebSocket navigation, native correction and SDK diagnostics.";
            File.WriteAllText(report, error ?? passed);
            if (error != null) Debug.LogError(error);
            EditorApplication.Exit(error == null ? 0 : 1);
        }

        private sealed class Listener : OpenAxisListenerBase
        {
            public override void OnExtension(string type, Dictionary<string, object> message)
            {
                if (type == "test.finished") finished = true;
                if (type == "test.cancelled") cancelled = true;
            }
            public override void OnError(string code, string message) => failure = code + ": " + message;
        }
    }
}
