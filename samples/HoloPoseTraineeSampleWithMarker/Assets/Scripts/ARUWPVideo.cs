﻿/*
*  ARUWPUtils.cs
*  ARToolKitUWP-Unity
*
*  This file is a part of ARToolKitUWP-Unity.
*
*  ARToolKitUWP-Unity is free software: you can redistribute it and/or modify
*  it under the terms of the GNU Lesser General Public License as published by
*  the Free Software Foundation, either version 3 of the License, or
*  (at your option) any later version.
*
*  ARToolKitUWP-Unity is distributed in the hope that it will be useful,
*  but WITHOUT ANY WARRANTY; without even the implied warranty of
*  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
*  GNU Lesser General Public License for more details.
*
*  You should have received a copy of the GNU Lesser General Public License
*  along with ARToolKitUWP-Unity.  If not, see <http://www.gnu.org/licenses/>.
*
*  Copyright 2017 Long Qian
*
*  Author: Long Qian
*  Contact: lqian8@jhu.edu
*
*/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using UnityEngine.UI;
#if !UNITY_EDITOR && UNITY_METRO
using Windows.Storage.Streams;
using Windows.Storage;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using System.Threading.Tasks;
using System;
using Windows.Graphics.Imaging;
using System.Threading;
using System.Linq;
using Windows.Perception.Spatial;
using UnityEngine.XR.WSA;

using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics;

#endif

/// <summary>
/// The ARUWPVideo class handles video access, using Windows MediaCapture APIs.
/// Through this class, ARUWPController.cs is able to control video initialization,
/// start, stop, enable or disable preview. ARUWPVideo uses .NET asynchronous feature
/// to optimize the video pipeline, including maintaining a high video frame rate
/// even if the tracking or rendering is slow.
///
/// Author:     Long Qian
/// Email:      lqian8@jhu.edu
/// </summary>
public class ARUWPVideo : MonoBehaviour
{
    /// <summary>
    /// If true (default), then ARUWPVideo sets up a connection to the camera frames and handles receiving each frame.
    /// If false, then we are letting some other part of the application (like a WebRTC connection) get frames and hand them off to ARUWPVideo to process.
    /// (added by Dan Andersen for use in HoloPoseWebRtc)
    /// </summary>
    public bool initializeVideoHere = true;
    
    /// <summary>
    /// Class and object identifier for logging. [internal use]
    /// </summary>
    private static string TAG = "ARUWPVideo";

    /// <summary>
    /// ARUWPController instance. [internal use]
    /// </summary>
    private ARUWPController controller = null;

    /// <summary>
    /// Unity Material to hold the Unity Texture of camera preview image. [internal use]
    /// </summary>
    private Material mediaMaterial = null;

    /// <summary>
    /// Unity Texture to hold the camera preview image. [internal use]
    /// </summary>
    private Texture2D mediaTexture = null;

    /// <summary>
    /// Initial value for video preview visualization. Video preview visualization might pose
    /// additional burden to rendering, but is useful for debugging tracking behavior.
    /// At runtime, please use EnablePreview() and DisablePreview() to control it. [public use]
    /// [initialization only]
    /// </summary>
    public bool videoPreview = true;

    /// <summary>
    /// Initial value for the GameObject to hold the video preview. It is a Quad object in the
    /// samples, but can potentially be any GameObject that has a MeshRenderer. [public use]
    /// </summary>
    public GameObject previewPlane = null;

    /// <summary>
    /// Initial value for holding the text containing video frame rate information. It is useful
    /// to indicate the runtime performance of the application. [public use]
    /// </summary>
    public Text videoFPS = null;


    public enum VideoParameter
    {
        Param896x504x30,
        Param896x504x15,
        Param1280x720x30,
        Param1280x720x15,
        Param1344x756x30,
        Param1344x756x15
    }

    /// <summary>
    /// Video resolution and targeted frame rate (may not be achieved). [public use]
    /// </summary>
    public VideoParameter videoParameter = VideoParameter.Param896x504x30;

    public static Matrix4x4 latestLocatableCameraToWorld = Matrix4x4.identity;

#if !UNITY_EDITOR && UNITY_METRO
    
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess {
        /// <summary>
        /// Unsafe function to retrieve the pointer and size information of the underlying
        /// buffer object. Must be used within unsafe functions. In addition, the project needs
        /// to be configured as "Allow unsafe code". [internal use]
        /// </summary>
        /// <param name="buffer">byte pointer to the start of the buffer</param>
        /// <param name="capacity">the size of the buffer</param>
        void GetBuffer(out byte* buffer, out uint capacity);
    }
    
    /// <summary>
    /// Signal indicating that the video has updated between the last render loop, the previous
    /// Update() and the current Update(). [internal use]
    /// </summary>
    private bool signalTrackingUpdated = false;

    /// <summary>
    /// Signal indicating that the initialization of video loop is done. [internal use]
    /// </summary>
    private bool signalInitDone = false;

    /// <summary>
    /// The view matrix used for Unity UI thread for updating the parent object for tracked objects. [internal use]
    /// </summary>
    private float[] updateCameraToWorldMatrix = null;

    /// <summary>
    /// Windows MediaCapture object used for UWP video loop. [internal use]
    /// </summary>
    private MediaCapture mediaCapture = null;

    /// <summary>
    /// Windows MediaFrameReader object used for UWP video loop. [internal use]
    /// </summary>
    private MediaFrameReader frameReader = null;

    /// <summary>
    /// Average video frame period in millisecond for previous 50 frames, calculated when 
    /// necessary. [internal use]
    /// </summary>
    private float videoDeltaTime = 0.0f;

    /// <summary>
    /// Retrieve the FPS of video. [public use]
    /// </summary>
    /// <returns>Number of frames per second</returns>
    public float GetVideoFPS() {
        return 1000.0f / videoDeltaTime;
    }

    /// <summary>
    /// If we are having a different source of (grayscale) frames provide the frame and pose information, rather than setting up the camera in ARUWPVideo,
    /// the external class will call this function.
    /// </summary>
    unsafe public void OnFrameArrivedExternal(Matrix4x4 cameraPoseMatrix, byte[] grayscaleFrameBytes)
    {
        ARUWPUtils.VideoTick();

        latestLocatableCameraToWorld = cameraPoseMatrix;

        grayscaleFrameBytes.CopyTo(frameData, 0);

        controller.ProcessFrameSync(frameData, latestLocatableCameraToWorld);

        signalTrackingUpdated = true;
    }

    /// <summary>
    /// The byte buffer of current frame image. [internal use]
    /// </summary>
    private byte[] frameData = null;

    /// <summary>
    /// The Task to asynchronously initialize MediaCapture in UWP. The camera of HoloLens will
    /// be configured to preview video of 896x504 at 30 fps, pixel format is NV12. MediaFrameReader
    /// will be initialized and register the callback function OnFrameArrived to each video
    /// frame. Note that this task does not start running the video preview, but configures the
    /// running behavior. This task should be executed when ARUWPController status is 
    /// ARUWP_STATUS_CLEAN, and will change it to ARUWP_STATUS_VIDEO_INITIALIZED if no error 
    /// occurred. [internal use]
    /// </summary>
    /// <returns>Whether video pipeline is successfully initialized</returns>
    public async Task<bool> InitializeMediaCaptureAsyncTask() {
        if (controller.status != ARUWP.ARUWP_STATUS_CLEAN) {
            Debug.Log(TAG + ": InitializeMediaCaptureAsyncTask() unsupported status");
            return false;
        }

        if (mediaCapture != null) {
            Debug.Log(TAG + ": InitializeMediaCaptureAsyncTask() fails because mediaCapture is not null");
            return false;
        }

        var allGroups = await MediaFrameSourceGroup.FindAllAsync();
        foreach (var group in allGroups) {
            Debug.Log(group.DisplayName + ", " + group.Id);
        }

        if (allGroups.Count <= 0) {
            Debug.Log(TAG + ": InitializeMediaCaptureAsyncTask() fails because there is no MediaFrameSourceGroup");
            return false;
        }

        // Initialize mediacapture with the source group.
        mediaCapture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings {
            SourceGroup = allGroups[0],
            // This media capture can share streaming with other apps.
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            // Only stream video and don't initialize audio capture devices.
            StreamingCaptureMode = StreamingCaptureMode.Video,
            // Set to CPU to ensure frames always contain CPU SoftwareBitmap images
            // instead of preferring GPU D3DSurface images.
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        };

        await mediaCapture.InitializeAsync(settings);
        Debug.Log(TAG + ": MediaCapture is successfully initialized in shared mode.");

        try {
            int targetVideoWidth, targetVideoHeight;
            float targetVideoFrameRate;
            switch (videoParameter) {
                case VideoParameter.Param1280x720x15:
                    targetVideoWidth = 1280;
                    targetVideoHeight = 720;
                    targetVideoFrameRate = 15.0f;
                    break;
                case VideoParameter.Param1280x720x30:
                    targetVideoWidth = 1280;
                    targetVideoHeight = 720;
                    targetVideoFrameRate = 30.0f;
                    break;
                case VideoParameter.Param1344x756x15:
                    targetVideoWidth = 1344;
                    targetVideoHeight = 756;
                    targetVideoFrameRate = 15.0f;
                    break;
                case VideoParameter.Param1344x756x30:
                    targetVideoWidth = 1344;
                    targetVideoHeight = 756;
                    targetVideoFrameRate = 30.0f;
                    break;
                case VideoParameter.Param896x504x15:
                    targetVideoWidth = 896;
                    targetVideoHeight = 504;
                    targetVideoFrameRate = 15.0f;
                    break;
                case VideoParameter.Param896x504x30:
                    targetVideoWidth = 896;
                    targetVideoHeight = 504;
                    targetVideoFrameRate = 30.0f;
                    break;
                default:
                    targetVideoWidth = 896;
                    targetVideoHeight = 504;
                    targetVideoFrameRate = 30.0f;
                    break;
            }
            
            var mediaFrameSourceVideoPreview = mediaCapture.FrameSources.Values.Single(x => x.Info.MediaStreamType == MediaStreamType.VideoPreview);
            MediaFrameFormat targetResFormat = null;
            float framerateDiffMin = 60f;
            foreach (var f in mediaFrameSourceVideoPreview.SupportedFormats.OrderBy(x => x.VideoFormat.Width * x.VideoFormat.Height)) {
                if (f.VideoFormat.Width == targetVideoWidth && f.VideoFormat.Height == targetVideoHeight ) {
                    if (targetResFormat == null) {
                        targetResFormat = f;
                        framerateDiffMin = Mathf.Abs(f.FrameRate.Numerator / f.FrameRate.Denominator - targetVideoFrameRate);
                    }
                    else if (Mathf.Abs(f.FrameRate.Numerator / f.FrameRate.Denominator - targetVideoFrameRate) < framerateDiffMin) {
                        targetResFormat = f;
                        framerateDiffMin = Mathf.Abs(f.FrameRate.Numerator / f.FrameRate.Denominator - targetVideoFrameRate);
                    }
                }
            }
            if (targetResFormat == null) {
                targetResFormat = mediaFrameSourceVideoPreview.SupportedFormats[0];
                Debug.Log(TAG + ": Unable to choose the selected format, fall back");
                targetResFormat = mediaFrameSourceVideoPreview.SupportedFormats.OrderBy(x => x.VideoFormat.Width * x.VideoFormat.Height).FirstOrDefault();
            }

            if (initializeVideoHere)
            {
                await mediaFrameSourceVideoPreview.SetFormatAsync(targetResFormat);
                frameReader = await mediaCapture.CreateFrameReaderAsync(mediaFrameSourceVideoPreview, targetResFormat.Subtype);
                frameReader.FrameArrived += OnFrameArrived;
            }

            controller.frameWidth = Convert.ToInt32(targetResFormat.VideoFormat.Width);
            controller.frameHeight = Convert.ToInt32(targetResFormat.VideoFormat.Height);
            // Feature grayscale branch
            frameData = new byte[controller.frameWidth * controller.frameHeight];
            Debug.Log(TAG + ": FrameReader is successfully initialized, " + controller.frameWidth + "x" + controller.frameHeight +
                ", Framerate: " + targetResFormat.FrameRate.Numerator + "/" + targetResFormat.FrameRate.Denominator);
        }
        catch (Exception e) {
            Debug.Log(TAG + ": FrameReader is not initialized");
            Debug.Log(TAG + ": Exception: " + e);
            return false;
        }

        controller.status = ARUWP.ARUWP_STATUS_VIDEO_INITIALIZED;
        signalInitDone = true;
        Debug.Log(TAG + ": InitializeMediaCaptureAsyncTask() is successful");
        return true;
    }

    /// <summary>
    /// The task to asynchronously starts the video pipeline and frame reading. The task should
    /// be executed when the ARUWPController status is ARUWP_STATUS_CTRL_INITIALIZED, and will
    /// change the status to ARUWP_STATUS_RUNNING if the task is successful. The task is wrapped
    /// up in ARUWPController.Resume() function. [internal use]
    /// </summary>
    /// <returns>Whether the frame reader is successfully started</returns>
    public async Task<bool> StartFrameReaderAsyncTask() {
        if (controller.status != ARUWP.ARUWP_STATUS_CTRL_INITIALIZED) {
            Debug.Log(TAG + ": StartFrameReaderAsyncTask() fails because of incorrect status");
            return false;
        }

        if (initializeVideoHere)
        {
            MediaFrameReaderStartStatus mediaFrameReaderStartStatus = await frameReader.StartAsync();
            if (mediaFrameReaderStartStatus == MediaFrameReaderStartStatus.Success)
            {
                Debug.Log(TAG + ": StartFrameReaderAsyncTask() is successful");
                controller.status = ARUWP.ARUWP_STATUS_RUNNING;
                return true;
            }
            else
            {
                Debug.Log(TAG + ": StartFrameReaderAsyncTask() is not successful, status = " + mediaFrameReaderStartStatus);
                return false;
            }
        } else
        {
            Debug.Log(TAG + ": StartFrameReaderAsyncTask(): initializeVideoHere = false, mock-setting the controller status to ARUWP_STATUS_RUNNING despite not starting frameReader");
            controller.status = ARUWP.ARUWP_STATUS_RUNNING;
            return true;
        }
    }

    internal SpatialCoordinateSystem worldOrigin { get; private set; }
    public IntPtr WorldOriginPtr
    {
        set
        {
            //worldOrigin = Marshal.PtrToStructure<SpatialCoordinateSystem>(value);

            if (value == null)
            {
                throw new ArgumentException("World origin pointer is null");
            }

            var obj = Marshal.GetObjectForIUnknown(value);
            var scs = obj as SpatialCoordinateSystem;
            worldOrigin = scs ?? throw new InvalidCastException("Failed to set SpatialCoordinateSystem from IntPtr");
        }
    }

    /// <summary>
    /// The task to asynchronously stops the video pipeline and frame reading. The task should
    /// be executed when the ARUWPController status is ARUWP_STATUS_RUNNING, and will change it
    /// to ARUWP_STATUS_CTRL_INITIALIZED when the task is successful. Note that the frame reader
    /// can be restarted after stop. It is more pause-resume instead of stop-start, because the 
    /// video pipeline does not need to be configured again. The task is wrapped up in the 
    /// ARUWPController.Pause() function. [internal use]
    /// </summary>
    /// <returns>Whether the video frame reader is successfully stopped</returns>
    public async Task<bool> StopFrameReaderAsyncTask() {
        if (controller.status != ARUWP.ARUWP_STATUS_RUNNING) {
            Debug.Log(TAG + ": StopFrameReaderAsyncTask() fails because of incorrect status");
            return false;
        }
        if (initializeVideoHere)
        {
            await frameReader.StopAsync();
        }
        controller.status = ARUWP.ARUWP_STATUS_CTRL_INITIALIZED;
        Debug.Log(TAG + ": StopFrameReaderAsyncTask() is successful");
        return true;
    }

    /// <summary>
    /// The guid for getting the view transform from the frame sample.
    /// See https://developer.microsoft.com/en-us/windows/mixed-reality/locatable_camera#locating_the_device_camera_in_the_world
    /// </summary>
    static Guid viewTransformGuid = new Guid("4E251FA4-830F-4770-859A-4B8D99AA809B");

    /// <summary>
    /// The guid for getting the projection transform from the frame sample.
    /// See https://developer.microsoft.com/en-us/windows/mixed-reality/locatable_camera#locating_the_device_camera_in_the_world
    /// </summary>
    static Guid projectionTransformGuid = new Guid("47F9FCB5-2A02-4F26-A477-792FDF95886A");

    /// <summary>
    /// The guid for getting the camera coordinate system for the frame sample.
    /// See https://developer.microsoft.com/en-us/windows/mixed-reality/locatable_camera#locating_the_device_camera_in_the_world
    /// </summary>
    static Guid cameraCoordinateSystemGuid = new Guid("9D13C82F-2199-4E67-91CD-D1A4181F2534");

    /// <summary>
    /// From https://github.com/VulcanTechnologies/HoloLensCameraStream/blob/master/HoloLensCameraStream/Plugin%20Project/VideoCaptureSample.cs
    /// </summary>
    /// <returns>The identity matrix as a size-16 float array.</returns>
    static float[] GetIdentityMatrixFloatArray()
    {
        return new float[] { 1f, 0, 0, 0, 0, 1f, 0, 0, 0, 0, 1f, 0, 0, 0, 0, 1f };
    }

    private System.Numerics.Matrix4x4 ConvertByteArrayToMatrix4x4(byte[] matrixAsBytes)
    {
        if (matrixAsBytes == null)
        {
            throw new ArgumentNullException("matrixAsBytes");
        }

        if (matrixAsBytes.Length != 64)
        {
            throw new Exception("Cannot convert byte[] to Matrix4x4. Size of array should be 64, but it is " + matrixAsBytes.Length);
        }

        var m = matrixAsBytes;
        return new System.Numerics.Matrix4x4(
            BitConverter.ToSingle(m, 0),
            BitConverter.ToSingle(m, 4),
            BitConverter.ToSingle(m, 8),
            BitConverter.ToSingle(m, 12),
            BitConverter.ToSingle(m, 16),
            BitConverter.ToSingle(m, 20),
            BitConverter.ToSingle(m, 24),
            BitConverter.ToSingle(m, 28),
            BitConverter.ToSingle(m, 32),
            BitConverter.ToSingle(m, 36),
            BitConverter.ToSingle(m, 40),
            BitConverter.ToSingle(m, 44),
            BitConverter.ToSingle(m, 48),
            BitConverter.ToSingle(m, 52),
            BitConverter.ToSingle(m, 56),
            BitConverter.ToSingle(m, 60));
    }

    private float[] ConvertMatrixToFloatArray(System.Numerics.Matrix4x4 matrix)
    {
        return new float[16] {
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44 };
    }

    /// <summary>
    /// Modified from https://github.com/VulcanTechnologies/HoloLensCameraStream/blob/master/HoloLensCameraStream/Plugin%20Project/VideoCaptureSample.cs
    /// This returns the transform matrix at the time the photo was captured, if location data if available.
    /// If it's not, that is probably an indication that the HoloLens is not tracking and its location is not known.
    /// It could also mean the VideoCapture stream is not running.
    /// If location data is unavailable then the camera to world matrix will be set to the identity matrix.
    /// </summary>
    /// <param name="matrix">The transform matrix used to convert between coordinate spaces.
    /// The matrix will have to be converted to a Unity matrix before it can be used by methods in the UnityEngine namespace.
    /// See https://forum.unity3d.com/threads/locatable-camera-in-unity.398803/ for details.</param>
    public bool TryGetCameraToWorldMatrix(MediaFrameReference frameReference, out float[] outMatrix)
    {
        if (frameReference.Properties.ContainsKey(viewTransformGuid) == false)
        {
            outMatrix = GetIdentityMatrixFloatArray();
            return false;
        }

        if (worldOrigin == null)
        {
            outMatrix = GetIdentityMatrixFloatArray();
            return false;
        }

        System.Numerics.Matrix4x4 cameraViewTransform = ConvertByteArrayToMatrix4x4(frameReference.Properties[viewTransformGuid] as byte[]);
        if (cameraViewTransform == null)
        {
            outMatrix = GetIdentityMatrixFloatArray();
            return false;
        }

        SpatialCoordinateSystem cameraCoordinateSystem = frameReference.Properties[cameraCoordinateSystemGuid] as SpatialCoordinateSystem;
        if (cameraCoordinateSystem == null)
        {
            outMatrix = GetIdentityMatrixFloatArray();
            return false;
        }

        System.Numerics.Matrix4x4? cameraCoordsToUnityCoordsMatrix = cameraCoordinateSystem.TryGetTransformTo(worldOrigin);
        if (cameraCoordsToUnityCoordsMatrix == null)
        {
            outMatrix = GetIdentityMatrixFloatArray();
            return false;
        }

        // Transpose the matrices to obtain a proper transform matrix
        cameraViewTransform = System.Numerics.Matrix4x4.Transpose(cameraViewTransform);

        System.Numerics.Matrix4x4 cameraCoordsToUnityCoords = System.Numerics.Matrix4x4.Transpose(cameraCoordsToUnityCoordsMatrix.Value);

        System.Numerics.Matrix4x4 viewToWorldInCameraCoordsMatrix;
        System.Numerics.Matrix4x4.Invert(cameraViewTransform, out viewToWorldInCameraCoordsMatrix);
        System.Numerics.Matrix4x4 viewToWorldInUnityCoordsMatrix = System.Numerics.Matrix4x4.Multiply(cameraCoordsToUnityCoords, viewToWorldInCameraCoordsMatrix);

        // Change from right handed coordinate system to left handed UnityEngine
        viewToWorldInUnityCoordsMatrix.M31 *= -1f;
        viewToWorldInUnityCoordsMatrix.M32 *= -1f;
        viewToWorldInUnityCoordsMatrix.M33 *= -1f;
        viewToWorldInUnityCoordsMatrix.M34 *= -1f;

        outMatrix = ConvertMatrixToFloatArray(viewToWorldInUnityCoordsMatrix);

        return true;
    }


    /// <summary>
    /// Helper method for converting into UnityEngine.Matrix4x4
    /// </summary>
    /// <param name="matrixAsArray"></param>
    /// <returns></returns>
    public static UnityEngine.Matrix4x4 ConvertFloatArrayToMatrix4x4(float[] matrixAsArray)
    {
        //There is probably a better way to be doing this but System.Numerics.Matrix4x4 is not available 
        //in Unity and we do not include UnityEngine in the plugin.
        UnityEngine.Matrix4x4 m = new UnityEngine.Matrix4x4();
        m.m00 = matrixAsArray[0];
        m.m01 = matrixAsArray[1];
        m.m02 = -matrixAsArray[2];
        m.m03 = matrixAsArray[3];
        m.m10 = matrixAsArray[4];
        m.m11 = matrixAsArray[5];
        m.m12 = -matrixAsArray[6];
        m.m13 = matrixAsArray[7];
        m.m20 = matrixAsArray[8];
        m.m21 = matrixAsArray[9];
        m.m22 = -matrixAsArray[10];
        m.m23 = matrixAsArray[11];
        m.m30 = matrixAsArray[12];
        m.m31 = matrixAsArray[13];
        m.m32 = matrixAsArray[14];
        m.m33 = matrixAsArray[15];

        return m;
    }
    

    private async void SaveRequestedPicture(SoftwareBitmap softwareBitmap)
    {
        Debug.Log("start SaveRequestedPicture");

        StorageFile outputFile = await _requestedStorageFolder.CreateFileAsync(_requestedFilename, CreationCollisionOption.ReplaceExisting);

        using (IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
        {
            // Create an encoder with the desired format
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);

            // Set the software bitmap
            encoder.SetSoftwareBitmap(softwareBitmap);

            // Set additional encoding parameters, if needed
            //encoder.BitmapTransform.ScaledWidth = 320;
            //encoder.BitmapTransform.ScaledHeight = 240;
            //encoder.BitmapTransform.Rotation = Windows.Graphics.Imaging.BitmapRotation.Clockwise90Degrees;
            //encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            encoder.IsThumbnailGenerated = true;

            try
            {
                await encoder.FlushAsync();
            }
            catch (Exception err)
            {
                const int WINCODEC_ERR_UNSUPPORTEDOPERATION = unchecked((int)0x88982F81);
                switch (err.HResult)
                {
                    case WINCODEC_ERR_UNSUPPORTEDOPERATION:
                        // If the encoder does not support writing a thumbnail, then try again
                        // but disable thumbnail generation.
                        encoder.IsThumbnailGenerated = false;
                        break;
                    default:
                        throw;
                }
            }

            if (encoder.IsThumbnailGenerated == false)
            {
                await encoder.FlushAsync();
            }


        }

        Debug.Log("end SaveRequestedPicture");
    }

    /// <summary>
    /// The callback that is triggered when new video preview frame arrives. In this function,
    /// video frame is saved for Unity UI if videoPreview is enabled, tracking task is triggered
    /// in this function call, and video FPS is recorded. [internal use]
    /// </summary>
    /// <param name="sender">MediaFrameReader object</param>
    /// <param name="args">arguments not used here</param>
    unsafe private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args) {
        ARUWPUtils.VideoTick();
        using (var frame = sender.TryAcquireLatestFrame()) {
            if (frame != null) {

                float[] cameraToWorldMatrixAsFloat;
                if (TryGetCameraToWorldMatrix(frame, out cameraToWorldMatrixAsFloat) == false) {
                    return;
                }

                latestLocatableCameraToWorld = ConvertFloatArrayToMatrix4x4(cameraToWorldMatrixAsFloat);
                
                var originalSoftwareBitmap = frame.VideoMediaFrame.SoftwareBitmap;
                using (var input = originalSoftwareBitmap.LockBuffer(BitmapBufferAccessMode.Read))
                using (var inputReference = input.CreateReference()) {
                    byte* inputBytes;
                    uint inputCapacity;
                    ((IMemoryBufferByteAccess)inputReference).GetBuffer(out inputBytes, out inputCapacity);
                    Marshal.Copy((IntPtr)inputBytes, frameData, 0, frameData.Length);
                }
                // Process the frame in this thread (still different from Unity thread)
                controller.ProcessFrameSync(frameData, latestLocatableCameraToWorld);

                if (_requestingSavePicture)
                {
                    Debug.Log("Saving picture...");
                    var softwareBitmap = SoftwareBitmap.Convert(originalSoftwareBitmap, BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore);
                    SaveRequestedPicture(softwareBitmap);
                    _requestingSavePicture = false;
                }

                originalSoftwareBitmap?.Dispose();
                signalTrackingUpdated = true;
            }
        }
    }

    

    
    /// <summary>
    /// Unity Monobehavior function. ARUWPController is set here. Video preview is
    /// initialized depending on the initial value. [internal use]
    /// </summary>
    private void Start() {

        // Fetch a pointer to Unity's spatial coordinate system
        WorldOriginPtr = WorldManager.GetNativeISpatialCoordinateSystemPtr();

        controller = GetComponent<ARUWPController>();
        if (controller == null) {
            Debug.Log(TAG + ": not able to find ARUWPController");
            Application.Quit();
        }
        if (videoPreview) {
            if (previewPlane != null) {
                mediaMaterial = previewPlane.GetComponent<MeshRenderer>().material;
                EnablePreview();
            }
            else {
                videoPreview = false;
            }
        }
    }
    
    /// <summary>
    /// Unity Monobehavior function: update texture depending on whether new frame arrives,
    /// trigger visualization of video frame rate information, initialize media texture when
    /// initialization of video pipeline is done. [internal use]
    /// </summary>
    private void LateUpdate() {

        if (signalInitDone) {
            if (mediaMaterial != null) {
                // Feature grayscale branch
                mediaTexture = new Texture2D(controller.frameWidth, controller.frameHeight, TextureFormat.Alpha8, false);
                mediaMaterial.mainTexture = mediaTexture;
            }
            signalInitDone = false;
        }

        if (signalTrackingUpdated) {

            if (videoPreview && previewPlane != null && mediaMaterial != null) {
                // Feature grayscale branch
                if (frameData != null) {
                    mediaTexture.LoadRawTextureData(frameData);
                    mediaTexture.Apply();
                }
            }
        }

        videoDeltaTime = ARUWPUtils.GetVideoDeltaTime();
        if (videoFPS != null) {
            videoFPS.text = string.Format("Video:   {0:0.0} ms ({1:0.} fps)", videoDeltaTime, 1000.0f / videoDeltaTime);
        }

        signalTrackingUpdated = false;
    }




    /// <summary>
    /// Determine whether the access to the native code, ARToolKitUWP.dll is given to video thread.
    /// Only when the ARUWPController status is ARUWP_STATUS_CLEAN or ARUWP_STATUS_RUNNING, the
    /// video thread is able to call native functions. When a call to native function is used, it 
    /// is recommended to check the access handle first, in order to avoid shared memory issue.
    /// [internal use]
    /// </summary>
    /// <returns></returns>
    private bool HasNativeHandlePreviewThread() {
        var t_status = controller.status;
        return (t_status == ARUWP.ARUWP_STATUS_CLEAN || t_status == ARUWP.ARUWP_STATUS_RUNNING);
    }



    /// <summary>
    /// TODO: Clean the video thread when the application quits.
    /// </summary>
    //private void OnApplicationQuit() {
    //    if (Interlocked.Equals(videoStatus, ARUWP_VIDEO_RUNNING)) {
    //        StopFrameReaderAsyncWrapper();
    //    }
    //}



#else

#endif

    /// <summary>
    /// Enable video preview. Video preview allows the user to see the current video frame.
    /// [public use]
    /// </summary>
    public void EnablePreview()
    {
        Debug.Log(TAG + ": EnablePreview() called");
        if (previewPlane != null)
        {
            previewPlane.SetActive(true);
            videoPreview = true;
        }
    }

    /// <summary>
    /// Disable video preview. Rendering frame rate can be higher because per-frame bitmap 
    /// manipulation is expensive. [public use]
    /// </summary>
    public void DisablePreview()
    {
        Debug.Log(TAG + ": DisablePreview() called");
        if (previewPlane != null)
        {
            previewPlane.SetActive(false);
            videoPreview = false;
        }
    }

    /// <summary>
    /// Toggle the video previewing mode.
    /// </summary>
    public void TogglePreview()
    {
        Debug.Log(TAG + ": TogglePreview() called");
        if (videoPreview)
        {
            DisablePreview();
        }
        else
        {
            EnablePreview();
        }
    }

#if !UNITY_EDITOR && UNITY_METRO
    private Windows.Storage.StorageFolder _requestedStorageFolder;
    private string _requestedFilename;
    private bool _requestingSavePicture = false;

    internal void RequestSavePicture(Windows.Storage.StorageFolder storageFolder, string filename)
    {
        _requestingSavePicture = true;
        _requestedStorageFolder = storageFolder;
        _requestedFilename = filename;
    }
#endif
}
