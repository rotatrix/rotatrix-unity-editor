using System;
using OpenAxis.Geometry;
using OpenAxis.Navigation;

namespace Rotatrix.Unity.Editor
{
    // SceneView stores an orbit pivot and a sphere radius, not a camera position.
    // This conversion is independent of Unity so it can be tested outside the editor.
    internal readonly struct SceneCameraState
    {
        internal readonly Vec3 Pivot;
        internal readonly Quat Rotation;
        internal readonly double Size, FieldOfView, Aspect;
        internal readonly bool Orthographic;

        internal SceneCameraState(Vec3 pivot, Quat rotation, double size,
            double fieldOfView, double aspect, bool orthographic)
        {
            if (!Finite(size) || size <= 0 || !Finite(aspect) || aspect <= 0
                || !Finite(fieldOfView) || fieldOfView <= 0 || fieldOfView >= 180)
                throw new ArgumentOutOfRangeException(nameof(size), "Invalid Scene view projection");
            Pivot = pivot; Rotation = rotation.Normalize(); Size = size;
            FieldOfView = fieldOfView; Aspect = aspect; Orthographic = orthographic;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal double Distance => Orthographic ? Size * 2 : Size / Math.Sin(FieldOfView * Math.PI / 360);
        internal double VerticalFov => 2 * Math.Atan(Math.Tan(FieldOfView * Math.PI / 360) / Math.Min(Aspect, 1));

        internal CameraPoseValue ToPose()
        {
            var eye = Pivot - Rotation.Rotate(new Vec3(0, 0, 1)) * Distance;
            return PoseHelpers.PoseFromLookAt(eye, Pivot, Rotation.Rotate(new Vec3(0, 1, 0)),
                Orthographic ? null : (double?)VerticalFov,
                Orthographic ? (double?)(2 * Size / Math.Min(Aspect, 1)) : null);
        }

        internal SceneCameraState Apply(CameraPoseValue pose, Vec3? orbitPivot)
        {
            var rv = pose.RotationVector;
            var protocolRotation = Quat.FromRotvec(rv.X, rv.Y, rv.Z);
            // OpenAxis's proper camera factor has +Z backward. Unity has +Z forward.
            // world.orientation declares the left-handed world separately.
            var rotation = (protocolRotation * new Quat(0, 0, 1, 0)).Normalize();
            var forward = rotation.Rotate(new Vec3(0, 0, 1));
            var ortho = pose.OrthoExtent.HasValue;
            var fov = pose.Fov.HasValue
                ? 2 * Math.Atan(Math.Tan(pose.Fov.Value / 2) * Math.Min(Aspect, 1)) * 180 / Math.PI
                : FieldOfView;
            double size, distance;
            if (ortho)
            {
                size = pose.OrthoExtent!.Value * Math.Min(Aspect, 1) / 2;
                distance = 2 * size;
            }
            else
            {
                var depth = orbitPivot.HasValue ? (orbitPivot.Value - pose.Position).Dot(forward) : double.NaN;
                distance = Finite(depth) && depth > 0.0001 ? depth : Distance;
                size = distance * Math.Sin(fov * Math.PI / 360);
            }
            // An off-axis orbit pivot must not force the camera to look at it.
            return new SceneCameraState(pose.Position + forward * distance,
                rotation, size, fov, Aspect, ortho);
        }

        internal static PoseDifference Compare(CameraPoseValue first, CameraPoseValue second) =>
            // Unity stores transforms as floats; protocol defaults are for doubles.
            PoseDifference.Compare(first, second, 2e-5, 5e-7, 2e-6, 2e-6);
    }
}
