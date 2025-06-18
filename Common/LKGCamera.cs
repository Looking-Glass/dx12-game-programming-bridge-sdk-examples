using System;
using SharpDX;

namespace NullEngine
{
    public class LKGCamera
    {
        public float Size;
        public Vector3 Center;
        public Vector3 Up;
        public float Fov;
        public float Viewcone;
        public float AspectRatio;
        public float NearPlane;
        public float FarPlane;

        public LKGCamera()
        {
            Size = 10.0f;
            Center = Vector3.Zero;
            Up = Vector3.UnitY;
            Fov = 45.0f;
            Viewcone = 40.0f;
            AspectRatio = 1.0f;
            NearPlane = 0.1f;
            FarPlane = 100.0f;
        }

        public LKGCamera(float size, Vector3 center, Vector3 upVec, float fieldOfView, float viewcone, float aspect, float nearP, float farP)
        {
            Size = size;
            Center = center;
            Up = upVec;
            Fov = fieldOfView;
            Viewcone = viewcone;
            AspectRatio = aspect;
            NearPlane = nearP;
            FarPlane = farP;
        }

        public float GetCameraDistance()
        {
            return Size / (float)Math.Tan(Fov * (float)Math.PI / 180.0f);
        }

        public float GetCameraOffset()
        {
            return GetCameraDistance() * (float)Math.Tan(Viewcone * (float)Math.PI / 180.0f);
        }

        public void ComputeViewProjectionMatrices(float normalizedView, bool invert, float depthiness, float focus, out Matrix viewMatrix, out Matrix projectionMatrix)
        {
            float offset = -(normalizedView - 0.5f) * depthiness * GetCameraOffset();
            Vector3 eye = new Vector3(offset, 0.0f, -GetCameraDistance());
            Vector3 adjustedUp = invert ? new Vector3(Up.X, -Up.Y, Up.Z) : Up;

            viewMatrix = Matrix.LookAtLH(eye, Center, Vector3.Normalize(adjustedUp));

            if (!invert)
            {
                Matrix flipX = Matrix.Scaling(-1.0f, 1.0f, 1.0f);
                viewMatrix = flipX * viewMatrix;
            }

            float fovRad = MathUtil.DegreesToRadians(Fov);
            projectionMatrix = Matrix.PerspectiveFovLH(fovRad, AspectRatio, NearPlane, FarPlane);

            float distanceFromCenter = normalizedView - 0.5f;
            float frustumShift = distanceFromCenter * focus;
            projectionMatrix.M31 += (offset * 2.0f / (Size * AspectRatio)) + frustumShift;
        }

        public Matrix ComputeModelMatrix(float angleX, float angleY)
        {
            float cosX = (float)Math.Cos(angleX);
            float sinX = (float)Math.Sin(angleX);
            float cosY = (float)Math.Cos(angleY);
            float sinY = (float)Math.Sin(angleY);

            Matrix rotationX = Matrix.Identity;
            rotationX.M22 = cosX;
            rotationX.M23 = -sinX;
            rotationX.M32 = sinX;
            rotationX.M33 = cosX;

            Matrix rotationY = Matrix.Identity;
            rotationY.M11 = cosY;
            rotationY.M13 = sinY;
            rotationY.M31 = -sinY;
            rotationY.M33 = cosY;

            Matrix rotation = rotationY * rotationX;

            Matrix translation = Matrix.Identity;
            translation.M43 = -3.0f;

            return rotation * translation;
        }
    }
}
