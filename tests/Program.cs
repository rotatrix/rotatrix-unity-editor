using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenAxis.Geometry;
using Rotatrix.Unity.Editor;

static class Program
{
    private static int checks;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; }
    private static void Near(double actual, double expected, string name, double tolerance = 1e-9) =>
        Check(Math.Abs(actual - expected) < tolerance, name + $": {actual} != {expected}");

    static void Main()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rotatrix-log-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "unity-1.log");
            using var log = new DiagnosticLog(path, 1024, "0.1.0-test");
            path = log.FilePath;
            Parallel.For(0, 250, i => log.Write("warning", "test-entry-" + i));
            Check(log.Text.Split('\n').Length == 200, "bounded concurrent memory log");
            Check(System.IO.File.ReadAllText(path).Contains("WARNING test-entry-"), "SDK formatted disk log");
            Check(System.IO.File.ReadAllText(path).Contains("client_version=0.1.0-test"), "versioned SDK session header");
            Check(System.IO.File.Exists(path + ".1"), "disk rotation");
            log.Write("info", "flushed sentinel");
            Check(System.IO.File.ReadAllText(path).Contains("flushed sentinel"), "immediately available to app reader");
            using var unavailable = new DiagnosticLog(System.IO.Path.Combine(path, "impossible.log"));
            unavailable.Write("error", "retained on disk failure");
            Check(unavailable.Error != null && unavailable.Text.Contains("retained on disk failure"), "nonfatal disk failure");
        }
        finally { System.IO.Directory.Delete(directory, true); }
        foreach (var aspect in new[] { 0.5, 1.0, 16.0 / 9 })
        foreach (var ortho in new[] { false, true })
        foreach (var rotation in new[] { Quat.Identity, Quat.FromRotvec(0.3, -0.8, 1.2) })
        {
            var original = new SceneCameraState(new Vec3(2, -3, 7), rotation, 5, 60, aspect, ortho);
            var pose = original.ToPose();
            var result = original.Apply(pose, original.Pivot).ToPose();
            Check(!SceneCameraState.Compare(pose, result).Changed, "pose round trip");
            Check(!SceneCameraState.Compare(pose, result).Discontinuity, "projection round trip");
            Near((result.Position - pose.Position).Length(), 0, "position");
            if (ortho) Near(result.OrthoExtent!.Value, 10 / Math.Min(aspect, 1), "vertical extent");
            else Near(result.Fov!.Value, 2 * Math.Atan(Math.Tan(Math.PI / 6) / Math.Min(aspect, 1)), "vertical fov");

            var offAxis = original.Pivot + new Vec3(3, 5, -2);
            var moved = new CameraPoseValue(pose.Position + new Vec3(1, 2, 3),
                new Vec3(0.4, 0.8, -0.3), pose.Fov, pose.OrthoExtent);
            var realized = original.Apply(moved, offAxis).ToPose();
            Check(!SceneCameraState.Compare(moved, realized).Changed, "off-axis pivot preserves requested pose");
            if (!ortho)
            {
                var forward = rotation.Rotate(new Vec3(0, 0, 1));
                foreach (var depth in new[] { -2.0, 0, 0.00001 })
                {
                    var next = original.Apply(pose, pose.Position + forward * depth);
                    Near(next.Distance, original.Distance, "unusable pivot retains native orbit distance");
                    Check(!SceneCameraState.Compare(pose, next.ToPose()).Changed, "fallback preserves camera pose");
                }
            }
        }

        var identity = new SceneCameraState(new Vec3(0, 0, 0), Quat.Identity, 5, 60, 1, false).ToPose();
        Near(identity.Position.Z, -10, "Unity forward +Z");
        var look = PoseHelpers.LookAtFromPose(identity);
        Check((look.target - look.eye).Z > 0, "OpenAxis camera points into Unity +Z");
        Near(look.up.Y, 1, "up preserved");
        var jitter = new CameraPoseValue(identity.Position + new Vec3(1e-7, 0, 0), identity.RotationVector, identity.Fov);
        Check(!SceneCameraState.Compare(identity, jitter).Changed, "float roundoff does not feed back");
        var mouse = new CameraPoseValue(identity.Position + new Vec3(0.01, 0, 0), identity.RotationVector, identity.Fov);
        Check(SceneCameraState.Compare(identity, mouse).Changed, "native mouse motion detected");

        using var scheduler = new EditorScheduler();
        var events = new List<int>();
        scheduler.Post(() => { events.Add(1); scheduler.Post(() => events.Add(3)); });
        Task.Run(() => scheduler.Post(() => events.Add(2))).GetAwaiter().GetResult();
        scheduler.PostAt(EditorScheduler.Now + 100, () => events.Add(9));
        Check(events.Count == 0, "scheduler never inline");
        scheduler.Drain();
        Check(string.Join(",", events) == "1,2", "ordered drain defers reentrant posts and deadlines");
        scheduler.Drain();
        Check(string.Join(",", events) == "1,2,3", "next tick receives nested post");
        scheduler.Dispose(); scheduler.Post(() => events.Add(4)); scheduler.Drain();
        Check(events.Count == 3, "disposal rejects delayed work");
        Console.WriteLine($"Passed {checks} camera, scheduler, and logging assertions.");
    }
}
