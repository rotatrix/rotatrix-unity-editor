using System;
using System.Diagnostics;
using System.Threading.Tasks;
using OpenAxis.Client;
using OpenAxis.Navigation;

using UnityEditor;
using UnityEngine;

namespace Rotatrix.Unity.Editor
{
    [InitializeOnLoad]
    internal static class RotatrixEditor
    {
        private const string DisconnectedKey = "Rotatrix.Unity.Disconnected";
        private const string DiagnosticsKey = "Rotatrix.Unity.Diagnostics";
        private const string FreeCameraKey = "Rotatrix.Unity.FreeCamera";
        private static readonly SceneViewAdapter adapter = new SceneViewAdapter();
        private static readonly UnityDiagnostics diagnostics = new UnityDiagnostics();
        private static EditorScheduler? scheduler;
        private static NavigationSession? session;
        private static OpenAxisConnectionManager? connection;
        private static Task? run;
        private static volatile ConnectionMetadata metadata = new ConnectionMetadata();
        private static volatile string detail = "";
        private static bool lastFocused;
        private static SceneView? lastView;
        private static SceneView? lastTarget;
        private static bool lastOrtho;
        private static float lastCameraSpeed;
        private static bool lastFreeCamera;
        private static bool suspended;

        // SessionState survives script reload, but resets when the editor exits.
        internal static bool Enabled => !SessionState.GetBool(DisconnectedKey, false);
        internal static bool FreeCamera => EditorPrefs.GetBool(FreeCameraKey, false);
        internal static string Status => connection?.State.ToString() ?? (Enabled ? "Inactive" : "Disabled");
        internal static string Detail => detail;
        internal static bool DiagnosticsEnabled => diagnostics.Model.Enabled;
        internal static string DiagnosticLog => diagnostics.LogText;
        internal static string DiagnosticPath => diagnostics.LogSink.FilePath;
        internal static string? DiagnosticError => diagnostics.LogSink.Error;

        static RotatrixEditor()
        {
            // Import/build workers must not connect to a user's device.
            if (Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess()) return;
            diagnostics.SetEnabled(EditorPrefs.GetBool(DiagnosticsKey, false));
            EditorApplication.update += Update;
            SceneView.beforeSceneGui += adapter.TrackCursor;
            SceneView.duringSceneGui += adapter.Draw;
            SceneView.duringSceneGui += diagnostics.Draw;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            Selection.selectionChanged += ContextChanged;
        }

        internal static void SetEnabled(bool enabled)
        {
            SessionState.SetBool(DisconnectedKey, !enabled);
            if (!enabled) Stop();
        }

        internal static void SetDiagnosticsEnabled(bool enabled)
        {
            EditorPrefs.SetBool(DiagnosticsKey, enabled);
            diagnostics.SetEnabled(enabled);
        }

        internal static void SetFreeCamera(bool enabled)
        {
            if (enabled == FreeCamera) return;
            EditorPrefs.SetBool(FreeCameraKey, enabled);
            ContextChanged();
            lastFreeCamera = enabled;
            metadata = NavigationMetadata(adapter.Focused, enabled);
            if (connection != null) Observe(connection.RefreshMetadataAsync());
        }

        internal static ConnectionMetadata NavigationMetadata(bool focused, bool freeCamera) => new ConnectionMetadata {
            Tags = new[] { "app.unity", "workspace.scene", freeCamera ? "navigation.hint.free_camera" : "navigation.hint.orbit" },
            Capabilities = new[] { "navigation" }, Focused = focused };

        private static void Start()
        {
            scheduler = new EditorScheduler();
            var client = new OpenAxisClient("Unity Editor", new Listener(),
                clientVersion: UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RotatrixEditor).Assembly)?.version,
                target: new Target { Pid = OpenAxis.ProcessIdentity.Current(), App = "Unity", AppVersion = UnityEngine.Application.unityVersion });
            session = new NavigationSession(client, adapter, scheduler,
                observation: adapter.Read, comparison: SceneCameraState.Compare,
                diagnostics: diagnostics.Model);
            connection = new OpenAxisConnectionManager(client, () => metadata,
                connect: client.ConnectAsync, startupTimeout: TimeSpan.FromSeconds(25),
                log: diagnostics.Log);
            connection.StateChanged += (_, error, __) => detail = error?.Message ?? "";
            run = connection.StartAsync();
        }

        internal static SceneView? NavigationView(EditorWindow? focused, SceneView? lastSceneView, bool appFocused,
            EditorWindow? hovered = null)
        {
            if (!appFocused) return null;
            var window = hovered != null ? hovered : focused;
            if (window == null) return null;
            // Hover selects a viewport without stealing keyboard focus. Panels
            // retain that target even when another Scene view still has focus.
            for (var type = window.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "UnityEditor.GameView") return null;
            var target = window as SceneView ?? lastSceneView;
            return SceneViewAdapter.Supported(target) ? target : null;
        }

        private static void Update()
        {
            if (!Enabled || suspended || EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                adapter.ActiveView = NavigationView(EditorWindow.focusedWindow,
                    lastTarget != null ? lastTarget : SceneView.lastActiveSceneView,
                    EditorApplication.isFocused, EditorWindow.mouseOverWindow);
                if (adapter.ActiveView != null) lastTarget = adapter.ActiveView;
                if (adapter.ActiveView != null) adapter.ActiveView.wantsMouseMove = true;
                adapter.Focused = EditorApplication.isFocused && SceneViewAdapter.Supported(adapter.ActiveView);
                var ortho = adapter.ActiveView != null && adapter.ActiveView.orthographic;
                var cameraSpeed = adapter.ActiveView != null ? adapter.ActiveView.cameraSettings.speed : 0;
                var freeCamera = FreeCamera;
                if (lastFocused != adapter.Focused || lastView != adapter.ActiveView || lastOrtho != ortho
                    || lastCameraSpeed != cameraSpeed || lastFreeCamera != freeCamera)
                {
                    ContextChanged();
                    lastFocused = adapter.Focused; lastView = adapter.ActiveView; lastOrtho = ortho;
                    lastCameraSpeed = cameraSpeed;
                    lastFreeCamera = freeCamera;
                    metadata = NavigationMetadata(adapter.Focused, freeCamera);
                    if (connection != null) Observe(connection.RefreshMetadataAsync());
                }
                if (connection == null)
                {
                    metadata = NavigationMetadata(adapter.Focused, freeCamera);
                    Start();
                }
                session?.CheckContext();
                session?.NativeCameraChanged();
                scheduler?.Drain();
                diagnostics.Update();
                if (run?.IsFaulted == true) { detail = run.Exception!.GetBaseException().Message; Stop(); }
            }
            catch (Exception error)
            {
                detail = error.Message;
                diagnostics.Log("warning", "Unity navigation update failed: " + error);
                ContextChanged();
            }
        }

        private static async void Observe(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error) { detail = error.Message; diagnostics.Log("warning", "Metadata update failed: " + error); }
        }

        internal static void ContextChanged() { session?.ContextChanged(); adapter.ClearPivot(); diagnostics.Clear(); }
        private static void PlayModeChanged(PlayModeStateChange change)
        {
            // Also works when Enter Play Mode has domain reload disabled.
            suspended = change != PlayModeStateChange.EnteredEditMode;
            if (suspended) Stop();
        }

        private static void Stop()
        {
            adapter.Focused = false;
            var manager = connection; connection = null;
            if (manager != null)
            {
                // SDK shutdown cancels networking and never waits on the Unity
                // dispatcher. Await before domain unload so no worker survives it.
                try { manager.StopAsync().GetAwaiter().GetResult(); }
                catch (Exception error) { detail = error.Message; diagnostics.Log("warning", "Connection shutdown failed: " + error); }
            }
            session?.Dispose();
            // Dispose queues native cleanup and the final performance summary.
            // Drain directly on the editor thread before releasing the scheduler.
            session?.Drain(); session = null;
            adapter.ClearPivot();
            diagnostics.Clear();
            scheduler?.Dispose(); scheduler = null; run = null;
        }

        private static void Shutdown()
        {
            EditorApplication.update -= Update;
            SceneView.duringSceneGui -= adapter.Draw;
            SceneView.duringSceneGui -= diagnostics.Draw;
            SceneView.beforeSceneGui -= adapter.TrackCursor;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            Selection.selectionChanged -= ContextChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            Stop();
            diagnostics.LogSink.Dispose();
        }

        private sealed class Listener : OpenAxisListenerBase
        {
            public override void OnError(string code, string message)
            {
                detail = code + ": " + message;
                diagnostics.Log("warning", detail);
            }
        }
    }

    public sealed class RotatrixWindow : EditorWindow
    {
        [MenuItem("Window/Rotatrix")]
        public static void Open() => GetWindow<RotatrixWindow>("Rotatrix");
        private void OnInspectorUpdate() => Repaint();
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Rotatrix Scene Navigation", EditorStyles.boldLabel);
            if (GUILayout.Button(RotatrixEditor.Enabled ? "Disconnect" : "Connect"))
                RotatrixEditor.SetEnabled(!RotatrixEditor.Enabled);
            EditorGUILayout.LabelField("Connection", RotatrixEditor.Status);
            var freeCamera = RotatrixEditor.FreeCamera;
            var mode = EditorGUILayout.Popup("Camera preference", freeCamera ? 1 : 0, new[] { "Orbit", "Free Camera" });
            if ((mode == 1) != freeCamera) RotatrixEditor.SetFreeCamera(mode == 1);
            var lockRoll = EditorPrefs.GetBool("Rotatrix.Unity.LockRoll", true);
            var lockPlane = EditorPrefs.GetBool("Rotatrix.Unity.LockPlane", true);
            var newRoll = EditorGUILayout.Toggle("Free camera: keep upright", lockRoll);
            var newPlane = EditorGUILayout.Toggle("Free camera: ground plane", lockPlane);
            if (newRoll != lockRoll) EditorPrefs.SetBool("Rotatrix.Unity.LockRoll", newRoll);
            if (newPlane != lockPlane) EditorPrefs.SetBool("Rotatrix.Unity.LockPlane", newPlane);
            if (newRoll != lockRoll || newPlane != lockPlane) RotatrixEditor.ContextChanged();
            var showDiagnostics = EditorGUILayout.Toggle("Navigation diagnostics", RotatrixEditor.DiagnosticsEnabled);
            if (showDiagnostics != RotatrixEditor.DiagnosticsEnabled) RotatrixEditor.SetDiagnosticsEnabled(showDiagnostics);
            if (GUILayout.Button("Copy diagnostic log")) EditorGUIUtility.systemCopyBuffer = RotatrixEditor.DiagnosticLog;
            EditorGUILayout.LabelField("Log file", RotatrixEditor.DiagnosticPath);
            if (RotatrixEditor.DiagnosticError is string logError)
                EditorGUILayout.HelpBox("Could not write diagnostics: " + logError, MessageType.Warning);
            if (!string.IsNullOrEmpty(RotatrixEditor.Detail))
                EditorGUILayout.HelpBox(RotatrixEditor.Detail, MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Start the Rotatrix app and focus a 3D Scene view. Select an object to orbit around it. " +
                "Bindings and navigation settings come from Rotatrix. Navigation pauses in Play Mode and in 2D or rotation-locked views.", MessageType.None);
        }
    }
}
