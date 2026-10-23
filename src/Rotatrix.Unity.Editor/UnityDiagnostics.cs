using System;
using System.Collections.Generic;
using OpenAxis.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Rotatrix.Unity.Editor
{
    // Unity only presents SDK evidence. Query collection, pose equivalence,
    // correction status, retention, and diagnostic colors belong to the SDK.
    internal sealed class UnityDiagnostics
    {
        internal NavigationDiagnostics Model { get; }
        internal DiagnosticLog LogSink { get; }
        private SceneView? view;
        private string? document;
        private DiagnosticPresentation? presentation;
        private bool dirty;

        internal UnityDiagnostics(string? logPath = null)
        {
            LogSink = new DiagnosticLog(logPath, clientVersion:
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityDiagnostics).Assembly)?.version);
            Model = new NavigationDiagnostics(log: Log, contextKey: ContextKey);
            Model.Changed = () => dirty = true;
        }

        private object ContextKey(object context)
        {
            var bound = (SceneViewAdapter.Context)context;
            view = bound.View;
            document = bound.Document;
            return Tuple.Create(document, view);
        }

        // The connection manager calls this from its worker. No Unity API here.
        internal void Log(string level, string message)
        {
            LogSink.Write(level, message);
        }
        internal string LogText => LogSink.Text;

        internal void SetEnabled(bool enabled)
        {
            Model.SetEnabled(enabled);
            Update();
        }

        internal void Clear()
        {
            Model.Clear();
            presentation = null;
            if (view != null) view.Repaint();
            view = null; document = null;
        }

        internal void Update()
        {
            if (!dirty && !(presentation?.ExpiresAt is double deadline && EditorScheduler.Now >= deadline)) return;
            dirty = false;
            presentation = Model.Enabled ? Model.Presentation() : null;
            if (view != null) view.Repaint();
        }

        internal void Draw(SceneView sceneView)
        {
            var frame = presentation;
            if (!Model.Enabled || frame == null || sceneView != view || document != SceneViewAdapter.DocumentId()
                || !Equals(frame.Context, Tuple.Create(document, view)) || Event.current.type != EventType.Repaint) return;
            // DrawBezier accepts the actual color; DrawAAPolyLine adds a hidden
            // 0.75 alpha multiplier. Coincident tangents make a straight line.
            using (new Handles.DrawingScope(Color.white, Matrix4x4.identity))
                foreach (var segment in frame.Segments)
                {
                    var start = SceneViewAdapter.V(segment.Start);
                    var end = SceneViewAdapter.V(segment.End);
                    Handles.DrawBezier(start, end, start, end,
                        ColorFor(segment.Tone, segment.Opacity), null, (float)segment.Width);
                }
            Handles.BeginGUI();
            var previousColor = GUI.color;
            var previousContentColor = GUI.contentColor;
            try
            {
                GUI.contentColor = Color.white;
                var labelStyle = new GUIStyle(GUI.skin.label);
                var y = 40f;
                var width = Mathf.Min(680, sceneView.position.width - 24);
                GUI.color = new Color(0, 0, 0, 0.75f);
                GUI.DrawTexture(new Rect(8, y - 4, width, frame.Lines.Count * 18 + 8), Texture2D.whiteTexture);
                foreach (var row in frame.Lines)
                {
                    GUI.color = Color.white;
                    labelStyle.normal.textColor = ColorFor(row.Tone);
                    GUI.Label(new Rect(12, y, width - 8, 18), row.Text, labelStyle);
                    y += 18;
                }
                foreach (var marker in frame.Markers)
                {
                    GUI.color = ColorFor(marker.Tone, .65);
                    var x = (float)marker.X; var my = (float)marker.Y;
                    GUI.DrawTexture(new Rect(x - 8, my - 1, 16, 2), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x - 1, my - 8, 2, 16), Texture2D.whiteTexture);
                    var labels = marker.Name.Split('\n');
                    GUI.color = Color.white;
                    labelStyle.normal.textColor = ColorFor(marker.Tone, .65);
                    for (var i = 0; i < labels.Length; i++)
                        GUI.Label(new Rect(x + 12, my - 8 + 15*i, 400, 20), labels[i], labelStyle);
                }
            }
            finally { GUI.color = previousColor; GUI.contentColor = previousContentColor; Handles.EndGUI(); }
        }

        private static Color ColorFor(string tone, double opacity = 1)
        {
            var rgb = DiagnosticPalette.Color(tone);
            return new Color(rgb[0] / 255f, rgb[1] / 255f, rgb[2] / 255f, (float)opacity);
        }
    }
}
