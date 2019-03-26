//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using System;
using System.Linq;
using System.Collections.Generic;
using Windows.Networking.Connectivity;
using Windows.Networking;
using Windows.Data.Json;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Threading;
using System.Text.RegularExpressions;
using static System.String;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Media.Core;
using HoloPoseClientCore.Signalling;
using Windows.Perception.Spatial;
using System.Runtime.InteropServices;
#if ORTCLIB
using Org.Ortc;
using Org.Ortc.Adapter;
using PeerConnectionClient.Ortc;
using PeerConnectionClient.Ortc.Utilities;
using CodecInfo = Org.Ortc.RTCRtpCodecCapability;
using MediaVideoTrack = Org.Ortc.MediaStreamTrack;
using MediaAudioTrack = Org.Ortc.MediaStreamTrack;
using RTCIceCandidate = Org.Ortc.Adapter.RTCIceCandidate;
#else
using Org.WebRtc;
using HoloPoseClient.Utilities;
#endif

namespace HoloPoseClient.Signalling
{
    /// <summary>
    /// A singleton conductor for WebRTC session.
    /// </summary>
    public class Conductor
    {
        public class Peer
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class IceServer
        {
            public enum ServerType { STUN, TURN };

            public ServerType Type { get; set; }
            public string Host { get; set; }
            public string Credential { get; set; }
            public string Username { get; set; }
        }

        public enum MediaDeviceType
        {
            AudioCapture,
            AudioPlayout,
            VideoCapture
        };

        public delegate void MediaDevicesChanged(MediaDeviceType type);

        public class MediaDevice
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        public class CaptureCapability
        {
            public uint Width { get; set; }
            public uint Height { get; set; }
            public uint FrameRate { get; set; }
            public bool MrcEnabled { get; set; }
            public string ResolutionDescription { get; set; }
            public string FrameRateDescription { get; set; }
        }

        public class CodecInfo
        {
            public string Name { get; set; }
            public int ClockRate { get; set; }
        }

        public enum LogLevel
        {
            Sensitive,
            Verbose,
            Info,
            Warning,
            Error
        };

        public class PeerConnectionHealthStats
        {
            public long ReceivedBytes { get; set; }
            public long ReceivedKpbs { get; set; }
            public long SentBytes { get; set; }
            public long SentKbps { get; set; }
            public long RTT { get; set; }
            public string LocalCandidateType { get; set; }
            public string RemoteCandidateType { get; set; }
        };

        private static readonly object InstanceLock = new object();
        private static Conductor _instance;
#if ORTCLIB
        private RTCPeerConnectionSignalingMode _signalingMode;
#endif
        /// <summary>
        ///  The single instance of the Conductor class.
        /// </summary>
        public static Conductor Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (InstanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new Conductor();
                        }
                    }
                }
                return _instance;
            }
        }

        private readonly Signaller _signaller;

        /// <summary>
        /// The signaller property.
        /// Helps to pass WebRTC session signals between client and server.
        /// </summary>
        public Signaller Signaller => _signaller;

        private Dictionary<string, MediaVideoTrack> _peerVideoTracks = new Dictionary<string, MediaVideoTrack>();
        
        private MediaVideoTrack _selfVideoTrack;

        public Dictionary<string, MediaElement> PeerVideos = new Dictionary<string, MediaElement>();

        public MediaElement SelfVideo { get; set; }

        private Dictionary<string, RawVideoSource> _peerRawVideoSources = new Dictionary<string, RawVideoSource>();
        
        private RawVideoSource _selfRawVideoSource;

        private Dictionary<string, RTCPeerConnection> _peerConnections = new Dictionary<string, RTCPeerConnection>();

        /// <summary>
        /// If true, offer one's own video when receiving an offer from a remote peer. If false, we accept their video but don't try to send any of our video.
        /// </summary>
        public bool LocalStreamEnabled = true;

        /// <summary>
        /// Video codec used in WebRTC session.
        /// </summary>
        public CodecInfo VideoCodec { get; set; }

        /// <summary>
        /// Audio codec used in WebRTC session.
        /// </summary>
        public CodecInfo AudioCodec { get; set; }

        /// <summary>
        /// Video capture details (frame rate, resolution)
        /// </summary>
        public CaptureCapability VideoCaptureProfile;

        // SDP negotiation attributes
        private static readonly string kCandidateSdpMidName = "sdpMid";
        private static readonly string kCandidateSdpMlineIndexName = "sdpMLineIndex";
        private static readonly string kCandidateSdpName = "candidate";
        private static readonly string kSessionDescriptionTypeName = "type";
        private static readonly string kSessionDescriptionSdpName = "sdp";
#if ORTCLIB
        private static readonly string kSessionDescriptionJsonName = "session";
#endif
        
        readonly Media _media;

        /// <summary>
        /// Media details.
        /// </summary>
        public Media Media => _media;

        private Dictionary<string, Peer> _onlinePeers = new Dictionary<string, Peer>();
        private Dictionary<string, Peer> _connectedPeers = new Dictionary<string, Peer>();


        private Dictionary<string, int> _connectedPeerIds = new Dictionary<string, int>();
        
        private MediaStream _mediaStream;
        readonly List<RTCIceServer> _iceServers;

        private CoreDispatcher _uiDispatcher;


        
        protected bool VideoEnabled = true;
        protected bool AudioEnabled = true;

        public bool ConstraintAudioEnabled = true;
        public bool ConstraintVideoEnabled = true;
        
        //protected string SessionId;

        bool _etwStatsEnabled;

        /// <summary>
        /// Enable/Disable ETW stats used by WebRTCDiagHubTool Visual Studio plugin.
        /// If the ETW Stats are disabled, no data will be sent to the plugin.
        /// </summary>
        public bool EtwStatsEnabled
        {
            get
            {
                return _etwStatsEnabled;
            }
            set
            {
                _etwStatsEnabled = value;
#if !ORTCLIB
                foreach (var peerConnection in _peerConnections.Values)
                {
                    if (peerConnection != null)
                    {
                        peerConnection.EtwStatsEnabled = value;
                    }
                }

                
#endif
            }
        }

        bool _videoLoopbackEnabled = true;
        public bool VideoLoopbackEnabled
        {
            get
            {
                return _videoLoopbackEnabled;
            }
            set
            {
                if (_videoLoopbackEnabled == value)
                    return;

                _videoLoopbackEnabled = value;
                if (_videoLoopbackEnabled)
                {
                    if (_selfVideoTrack != null)
                    {
                        Debug.WriteLine("Enabling video loopback");
#if UNITY_XAML
                        if (UnityPlayer.AppCallbacks.Instance.IsInitialized())
                        {
                            UnityPlayer.AppCallbacks.Instance.InvokeOnAppThread(new UnityPlayer.AppCallbackItem(() =>
                            {
                                UnityEngine.GameObject go = UnityEngine.GameObject.Find("Control");
                                go.GetComponent<ControlScript>().CreateRemoteMediaStreamSource(_selfVideoTrack, "I420", "SELF");
                            }
                            ), false);
                        }
#elif !UNITY
                        _media.AddVideoTrackMediaElementPair(_selfVideoTrack, SelfVideo, "SELF");
#endif
                        SetupSelfVideoFrameCallbacks(_selfVideoTrack);
                        Debug.WriteLine("Video loopback enabled");
                    }
                }
                else
                {
                    // This is a hack/workaround for destroying the internal stream source (RTMediaStreamSource)
                    // instance inside webrtc winuwp api when loopback is disabled.
                    // For some reason, the RTMediaStreamSource instance is not destroyed when only SelfVideo.Source
                    // is set to null.
                    // For unknown reasons, when executing the above sequence (set to null, stop, set to null), the
                    // internal stream source is destroyed.
                    // Apparently, with webrtc package version < 1.1.175, the internal stream source was destroyed
                    // corectly, only by setting SelfVideo.Source to null.
#if UNITY_XAML
                    if (UnityPlayer.AppCallbacks.Instance.IsInitialized())
                    {
                        UnityPlayer.AppCallbacks.Instance.InvokeOnAppThread(new UnityPlayer.AppCallbackItem(() =>
                        {
                            UnityEngine.GameObject go = UnityEngine.GameObject.Find("Control");
                            go.GetComponent<ControlScript>().DestroyRemoteMediaStreamSource();
                        }
                        ), false);
                    }
#elif !UNITY
                    _media.RemoveVideoTrackMediaElementPair(_selfVideoTrack);
#endif
                    DestroySelfVideoSources();
                    GC.Collect(); // Ensure all references are truly dropped.
                }
            }
        }

        bool _tracingEnabled;
        public bool TracingEnabled
        {
            get
            {
                return _tracingEnabled;
            }
            set
            {
                _tracingEnabled = value;
#if ORTCLIB
                if (_tracingEnabled)
                {
                    Org.Ortc.Ortc.StartMediaTracing();
                }
                else
                {
                    Org.Ortc.Ortc.StopMediaTracing();
                    Org.Ortc.Ortc.SaveMediaTrace(_traceServerIp, Int32.Parse(_traceServerPort));
                }
#else
                if (_tracingEnabled)
                {
                    WebRTC.StartTracing("webrtc-trace.txt");
                }
                else
                {
                    WebRTC.StopTracing();
                }
#endif
            }
        }

        bool _peerConnectionStatsEnabled;

        public void Initialize(CoreDispatcher uiDispatcher)
        {
            _uiDispatcher = uiDispatcher;

            // Display a permission dialog to request access to the microphone and camera
            WebRTC.RequestAccessForMediaCapture().AsTask().ContinueWith(antecedent =>
            {
                if (antecedent.Result)
                {
                    WebRTC.Initialize(uiDispatcher);
                    Initialized?.Invoke(true);
                }
                else
                {
                    Initialized?.Invoke(false);
                }
            });

            Media.OnMediaDevicesChanged += (type) =>
            {
                MediaDeviceType deviceType;
                switch (type)
                {
                    case Org.WebRtc.MediaDeviceType.MediaDeviceType_AudioCapture:
                        deviceType = MediaDeviceType.AudioCapture;
                        break;
                    case Org.WebRtc.MediaDeviceType.MediaDeviceType_AudioPlayout:
                        deviceType = MediaDeviceType.AudioPlayout;
                        break;
                    case Org.WebRtc.MediaDeviceType.MediaDeviceType_VideoCapture:
                        deviceType = MediaDeviceType.VideoCapture;
                        break;
                    default:
                        deviceType = MediaDeviceType.VideoCapture;
                        break;
                }
                OnMediaDevicesChanged?.Invoke(deviceType);
            };

            // Handler for Peer/Self video frame rate changed event
            FrameCounterHelper.FramesPerSecondChanged += (id, frameRate) =>
            {
                FramesPerSecondChanged?.Invoke(id, frameRate);
            };

            // Handler for Peer/Self video resolution changed event 
            ResolutionHelper.ResolutionChanged += (id, width, height) =>
            {
                ResolutionChanged?.Invoke(id, width, height);
            };
        }

        public event Action<bool> Initialized;

        public List<MediaDevice> GetVideoCaptureDevices()
        {
            List<MediaDevice> devices = new List<MediaDevice>();
            foreach (Org.WebRtc.MediaDevice device in _media.GetVideoCaptureDevices())
            {
                devices.Add(new MediaDevice() { Id = device.Id, Name = device.Name });
            }
            return devices;
        }

        public IAsyncOperation<List<CaptureCapability>> GetVideoCaptureCapabilities(String deviceId)
        {
            Org.WebRtc.MediaDevice device = null;
            foreach (Org.WebRtc.MediaDevice currentDevice in _media.GetVideoCaptureDevices())
            {
                if (currentDevice.Id == deviceId)
                {
                    device = currentDevice;
                    break;
                }
            }
            if (device == null)
                return null;
            var asyncOperation = device.GetVideoCaptureCapabilities();
            return asyncOperation.AsTask().ContinueWith(operationCapabilities =>
            {
                List<CaptureCapability> capabilities = new List<CaptureCapability>();
                foreach (Org.WebRtc.CaptureCapability capability in operationCapabilities.Result)
                {
                    capabilities.Add(new CaptureCapability()
                    {
                        Width = capability.Width,
                        Height = capability.Height,
                        FrameRate = capability.FrameRate,
                        MrcEnabled = capability.MrcEnabled,
                        ResolutionDescription = capability.ResolutionDescription,
                        FrameRateDescription = capability.FrameRateDescription
                    });
                }
                return capabilities;
            }).AsAsyncOperation();
        }

        public void SelectVideoDevice(MediaDevice device)
        {
            Org.WebRtc.MediaDevice mediaDevice = new Org.WebRtc.MediaDevice(device.Id, device.Name);
            _media.SelectVideoDevice(mediaDevice);
        }

        public event Action<MediaDeviceType> OnMediaDevicesChanged;

        public event Action<string, string> FramesPerSecondChanged;

        public event Action<string, uint, uint> ResolutionChanged;

        public List<CodecInfo> GetAudioCodecs()
        {
            List<CodecInfo> codecs = new List<CodecInfo>();
            foreach (Org.WebRtc.CodecInfo codec in WebRTC.GetAudioCodecs())
            {
                codecs.Add(new CodecInfo() { Name = codec.Name, ClockRate = codec.ClockRate });
            }
            return codecs;
        }

        public List<CodecInfo> GetVideoCodecs()
        {
            List<CodecInfo> codecs = new List<CodecInfo>();
            foreach (Org.WebRtc.CodecInfo codec in WebRTC.GetVideoCodecs())
            {
                codecs.Add(new CodecInfo() { Name = codec.Name });
            }
            return codecs;
        }

        public void EnableLogging(LogLevel level)
        {
            Org.WebRtc.LogLevel logLevel = Org.WebRtc.LogLevel.LOGLVL_ERROR;
            switch (level)
            {
                case LogLevel.Sensitive:
                    logLevel = Org.WebRtc.LogLevel.LOGLVL_SENSITIVE;
                    break;
                case LogLevel.Verbose:
                    logLevel = Org.WebRtc.LogLevel.LOGLVL_VERBOSE;
                    break;
                case LogLevel.Info:
                    logLevel = Org.WebRtc.LogLevel.LOGLVL_INFO;
                    break;
                case LogLevel.Warning:
                    logLevel = Org.WebRtc.LogLevel.LOGLVL_WARNING;
                    break;
                case LogLevel.Error:
                    logLevel = Org.WebRtc.LogLevel.LOGLVL_ERROR;
                    break;
            }

            WebRTC.EnableLogging(logLevel);
        }

        public void DisableLogging()
        {
            WebRTC.DisableLogging();
        }

        public Windows.Storage.StorageFolder LogFolder
        {
            get
            {
                return WebRTC.LogFolder;
            }
        }

        public String LogFileName
        {
            get
            {
                return WebRTC.LogFileName;
            }
        }

        public void SynNTPTime(long ntpTime)
        {
            WebRTC.SynNTPTime(ntpTime);
        }

        public double CpuUsage
        {
            get
            {
                return WebRTC.CpuUsage;
            }
            set
            {
                WebRTC.CpuUsage = value;
            }
        }

        public long MemoryUsage
        {
            get
            {
                return WebRTC.MemoryUsage;
            }
            set
            {
                WebRTC.MemoryUsage = value;
            }
        }

        public void OnAppSuspending()
        {
            Media.OnAppSuspending();
        }

        /// <summary>
        /// Enable/Disable connection health stats.
        /// Connection health stats are delivered by the OnConnectionHealthStats event. 
        /// </summary>
        public bool PeerConnectionStatsEnabled
        {
            get
            {
                return _peerConnectionStatsEnabled;
            }
            set
            {
                _peerConnectionStatsEnabled = value;
#if !ORTCLIB
                foreach (var peerConnection in _peerConnections.Values)
                {
                    if (peerConnection != null)
                    {
                        peerConnection.ConnectionHealthStatsEnabled = value;
                    }
                }

#endif
            }
        }

        public object MediaLock { get; set; } = new object();

        CancellationTokenSource _connectToPeerCancelationTokenSource;
        Task<bool> _connectToPeerTask;

        // Public events for adding and removing the local stream
        public event Action OnAddLocalStream;

        // Public events to notify about connection status
        public event Action<string> OnPeerConnectionCreated;
        public event Action<string> OnPeerConnectionClosed;
        public event Action OnReadyToConnect;

        /// <summary>
        /// Updates the preferred video frame rate and resolution.
        /// </summary>
        public void UpdatePreferredFrameFormat()
        {
            if (VideoCaptureProfile != null)
            {
#if ORTCLIB
            _media.SetPreferredVideoCaptureFormat(
              (int)VideoCaptureProfile.Width, (int)VideoCaptureProfile.Height, (int)VideoCaptureProfile.FrameRate);
#else
                Org.WebRtc.WebRTC.SetPreferredVideoCaptureFormat(
                              (int)VideoCaptureProfile.Width, (int)VideoCaptureProfile.Height, (int)VideoCaptureProfile.FrameRate, VideoCaptureProfile.MrcEnabled);
#endif
            }
        }
        
        public event Action<string, uint, uint,
            byte[], uint, byte[], uint, byte[], uint,
            float, float, float, float, float, float, float> OnPeerRawFrame;

        private void _peerRawVideoSource_OnRawVideoFrame(string peerName,
            uint width, uint height, 
            byte[] yPlane, uint yPitch, byte[] vPlane, uint vPitch, byte[] uPlane, uint uPitch, 
            float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW)
        {
            //Debug.WriteLine("Conductor: _peerRawVideoSource_OnRawVideoFrame " + width + " " + height + " " + posX + " " + posY + " " + posZ + " " + rotX + " " + rotY + " " + rotZ + " " + rotW);
            OnPeerRawFrame?.Invoke(peerName, width, height, yPlane, yPitch, vPlane, vPitch, uPlane, uPitch, posX, posY, posZ, rotX, rotY, rotZ, rotW);
        }

        private void DestroyPeerVideoSources(string peerName)
        {
            Debug.WriteLine("Conductor: DestroyPeerVideoSources");

            if (_peerRawVideoSources.ContainsKey(peerName))
            {
                _peerRawVideoSources[peerName].Dispose();
                _peerRawVideoSources.Remove(peerName);
            }
        }

        private void SetupSelfVideoFrameCallbacks(MediaVideoTrack selfVideoTrack)
        {
            Debug.WriteLine("Conductor: SetupSelfVideoFrameCallbacks");
            _selfRawVideoSource = _media.CreateRawVideoSource(selfVideoTrack);
            _selfRawVideoSource.OnRawVideoFrame += _selfRawVideoSource_OnRawVideoFrame;
        }

        public event Action<uint, uint,
            byte[], uint, byte[], uint, byte[], uint,
            float, float, float, float, float, float, float> OnSelfRawFrame;

        private void _selfRawVideoSource_OnRawVideoFrame(
            uint width, uint height,
            byte[] yPlane, uint yPitch, byte[] vPlane, uint vPitch, byte[] uPlane, uint uPitch,
            float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW)
        {
            //Debug.WriteLine("Conductor: _selfRawVideoSource_OnRawVideoFrame " + width + " " + height + " " + posX + " " + posY + " " + posZ + " " + rotX + " " + rotY + " " + rotZ + " " + rotW);
            OnSelfRawFrame?.Invoke(width, height, yPlane, yPitch, vPlane, vPitch, uPlane, uPitch, posX, posY, posZ, rotX, rotY, rotZ, rotW);
        }

        private void DestroySelfVideoSources()
        {
            Debug.WriteLine("Conductor: DestroySelfVideoSources");
            if (_selfRawVideoSource != null)
            {
                _selfRawVideoSource.Dispose();
                _selfRawVideoSource = null;
            }
        }

        /// <summary>
        /// Creates a peer connection.
        /// </summary>
        /// <returns>True if connection to a peer is successfully created.</returns>
        private async Task<bool> CreatePeerConnection(string peerName, CancellationToken cancelationToken)
        {
            Debug.Assert(!_peerConnections.ContainsKey(peerName));
            if (cancelationToken.IsCancellationRequested)
            {
                return false;
            }

            var config = new RTCConfiguration()
            {
                BundlePolicy = RTCBundlePolicy.Balanced,
#if ORTCLIB
                SignalingMode = _signalingMode,
                GatherOptions = new RTCIceGatherOptions()
                {
                    IceServers = new List<RTCIceServer>(_iceServers),
                }
#else
                IceTransportPolicy = RTCIceTransportPolicy.All,
                IceServers = _iceServers
#endif
            };

            Debug.WriteLine("Conductor: Creating peer connection.");
            _peerConnections[peerName] = new RTCPeerConnection(config);

            if (_peerConnections[peerName] == null)
                throw new NullReferenceException("Peer connection is not created.");

#if !ORTCLIB
            _peerConnections[peerName].EtwStatsEnabled = _etwStatsEnabled;
            _peerConnections[peerName].ConnectionHealthStatsEnabled = _peerConnectionStatsEnabled;
#endif
            if (cancelationToken.IsCancellationRequested)
            {
                return false;
            }
#if ORTCLIB
            OrtcStatsManager.Instance.Initialize(_peerConnection);
#endif
            OnPeerConnectionCreated?.Invoke(peerName);

            _peerConnections[peerName].OnIceCandidate += (RTCPeerConnectionIceEvent evt) =>
            {
                PeerConnection_OnIceCandidate(peerName, evt);
            };
#if ORTCLIB
            _peerConnections[peerName].OnTrack += PeerConnection_OnAddTrack;
            _peerConnections[peerName].OnTrackGone += PeerConnection_OnRemoveTrack;
            _peerConnections[peerName].OnIceConnectionStateChange += () => { Debug.WriteLine("Conductor: Ice connection state change, state=" + (null != _peerConnection ? _peerConnection.IceConnectionState.ToString() : "closed")); };
#else
            _peerConnections[peerName].OnAddStream += (MediaStreamEvent evt) =>
            {
                PeerConnection_OnAddStream(peerName, evt);
            };
            _peerConnections[peerName].OnRemoveStream += (MediaStreamEvent evt) =>
            {
                PeerConnection_OnRemoveStream(peerName, evt);
            };
            _peerConnections[peerName].OnConnectionHealthStats += PeerConnection_OnConnectionHealthStats;
#endif

            
            Debug.WriteLine("Conductor: Getting user media.");
            RTCMediaStreamConstraints mediaStreamConstraints = new RTCMediaStreamConstraints
            {
                // Old:
                // Always include audio/video enabled in the media stream,
                // so it will be possible to enable/disable audio/video if 
                // the call was initiated without microphone/camera
                // ------------
                // New: 
                // Let the constraint be configurable so we can accept video-only streams
                audioEnabled = ConstraintAudioEnabled,
                videoEnabled = ConstraintVideoEnabled
            };

            if (cancelationToken.IsCancellationRequested)
            {
                return false;
            }

            if (LocalStreamEnabled)
            {
#if ORTCLIB
            var tracks = await _media.GetUserMedia(mediaStreamConstraints);
            if (tracks != null)
            {
                RTCRtpCapabilities audioCapabilities = RTCRtpSender.GetCapabilities("audio");
                RTCRtpCapabilities videoCapabilities = RTCRtpSender.GetCapabilities("video");

                _mediaStream = new MediaStream(tracks);
                Debug.WriteLine("Conductor: Adding local media stream.");
                IList<MediaStream> mediaStreamList = new List<MediaStream>();
                mediaStreamList.Add(_mediaStream);
                foreach (var mediaStreamTrack in tracks)
                {
                    //Create stream track configuration based on capabilities
                    RTCMediaStreamTrackConfiguration configuration = null;
                    if (mediaStreamTrack.Kind == MediaStreamTrackKind.Audio && audioCapabilities != null)
                    {
                        configuration =
                            await Helper.GetTrackConfigurationForCapabilities(audioCapabilities, AudioCodec);
                    }
                    else if (mediaStreamTrack.Kind == MediaStreamTrackKind.Video && videoCapabilities != null)
                    {
                        configuration =
                            await Helper.GetTrackConfigurationForCapabilities(videoCapabilities, VideoCodec);
                    }
                    if (configuration != null)
                        _peerConnection.AddTrack(mediaStreamTrack, mediaStreamList, configuration);
                }
            }
#else
                _mediaStream = await _media.GetUserMedia(mediaStreamConstraints);
#endif

                if (cancelationToken.IsCancellationRequested)
                {
                    return false;
                }


#if !ORTCLIB
                Debug.WriteLine("Conductor: Adding local media stream.");
                _peerConnections[peerName].AddStream(_mediaStream);
#endif
                _selfVideoTrack = _mediaStream.GetVideoTracks().FirstOrDefault();
                if (_selfVideoTrack != null)
                {
                    if (VideoLoopbackEnabled)
                    {
#if UNITY_XAML
            if (UnityPlayer.AppCallbacks.Instance.IsInitialized())
            {
                UnityPlayer.AppCallbacks.Instance.InvokeOnAppThread(new UnityPlayer.AppCallbackItem(() =>
                {
                    UnityEngine.GameObject go = UnityEngine.GameObject.Find("Control");
                    go.GetComponent<ControlScript>().CreateLocalMediaStreamSource(_selfVideoTrack, "I420", "SELF");
                }
                ), false);
            }
#elif !UNITY
                        Conductor.Instance.Media.AddVideoTrackMediaElementPair(_selfVideoTrack, SelfVideo, "SELF");
#endif
                        SetupSelfVideoFrameCallbacks(_selfVideoTrack);
                    }
                }

                OnAddLocalStream?.Invoke();
            }

            
            
            if (cancelationToken.IsCancellationRequested)
            {
                return false;
            }
            

            
            return true;
        }

        /// <summary>
        /// Closes a peer connection.
        /// </summary>
        private void ClosePeerConnection(string peerName)
        {
            lock (MediaLock)
            {
                RTCPeerConnection peerConnection = null;
                _peerConnections.TryGetValue(peerName, out peerConnection);

                if (peerConnection != null)
                {
                    _connectedPeerIds.Remove(peerName);
                    if (_mediaStream != null)
                    {
                        foreach (var track in _mediaStream.GetTracks())
                        {
                            _mediaStream.RemoveTrack(track);
                            track.Stop();
                        }
                    }
                    _mediaStream = null;

#if UNITY_XAML
                    if (UnityPlayer.AppCallbacks.Instance.IsInitialized())
                    {
                        UnityPlayer.AppCallbacks.Instance.InvokeOnAppThread(new UnityPlayer.AppCallbackItem(() =>
                        {
                            UnityEngine.GameObject go = UnityEngine.GameObject.Find("Control");
                            go.GetComponent<ControlScript>().DestroyLocalMediaStreamSource();
                            go.GetComponent<ControlScript>().DestroyRemoteMediaStreamSource();
                        }
                        ), false);
                    }
#elif !UNITY
                    _uiDispatcher.RunAsync(CoreDispatcherPriority.Normal, new DispatchedHandler(() =>
                    {
                        Conductor.Instance.Media.RemoveVideoTrackMediaElementPair(_peerVideoTracks[peerName]);
                        Conductor.Instance.Media.RemoveVideoTrackMediaElementPair(_selfVideoTrack);
                    })).AsTask().Wait();
#endif
                    DestroyPeerVideoSources(peerName);
                    DestroySelfVideoSources();

                    _peerVideoTracks.Remove(peerName);
                    _selfVideoTrack = null;

                    OnPeerConnectionClosed?.Invoke(peerName);

                    peerConnection.Close(); // Slow, so do this after UI updated and camera turned off

#if ORTCLIB
                    SessionId = null;
                    OrtcStatsManager.Instance.CallEnded();
#endif
                    _peerConnections.Remove(peerName);

                    OnReadyToConnect?.Invoke();

                    GC.Collect(); // Ensure all references are truly dropped.
                }
            }
        }

        public IMediaSource CreateLocalMediaStreamSource(String type)
        {
            return Media.CreateMedia().CreateMediaStreamSource(_selfVideoTrack, type, "SELF");
        }

        public IMediaSource CreateRemoteMediaStreamSource(string peerName, String type)
        {
            return Media.CreateMedia().CreateMediaStreamSource(_peerVideoTracks[peerName], type, GetPeerLabel(peerName));
        }

        public void AddPeer(Peer peer)
        {
            _onlinePeers[peer.Name] = peer;
        }

        public void RemovePeer(Peer peer)
        {
            _onlinePeers.Remove(peer.Name);
        }

        public List<Peer> GetPeers()
        {
            return _onlinePeers.Values.ToList<Peer>();
        }

        /// <summary>
        /// Called when WebRTC detects another ICE candidate. 
        /// This candidate needs to be sent to the other peer.
        /// </summary>
        /// <param name="evt">Details about RTC Peer Connection Ice event.</param>
        private void PeerConnection_OnIceCandidate(string peerName, RTCPeerConnectionIceEvent evt)
        {
            if (evt.Candidate == null) // relevant: GlobalObserver::OnIceComplete in Org.WebRtc
            {
                return;
            }

            double index = (double)evt.Candidate.SdpMLineIndex;

            JsonObject json;
#if ORTCLIB
            if (RTCPeerConnectionSignalingMode.Json == _signalingMode)
            {
                json = JsonObject.Parse(evt.Candidate.ToJsonString());
            }
            else
#endif
            {
                json = new JsonObject
                {
                    {kCandidateSdpMidName, JsonValue.CreateStringValue(evt.Candidate.SdpMid)},
                    {kCandidateSdpMlineIndexName, JsonValue.CreateNumberValue(index)},
                    {kCandidateSdpName, JsonValue.CreateStringValue(evt.Candidate.Candidate)}
                };
            }
            Debug.WriteLine("Conductor: Sending ice candidate.\n" + json.Stringify());
            SendMessage(peerName, json);
        }

        private string GetPeerLabel(string peerName)
        {
            return "PEER_" + peerName;
        }

#if ORTCLIB
        /// <summary>
        /// Invoked when the remote peer added a media track to the peer connection.
        /// </summary>
        public event Action<RTCTrackEvent> OnAddRemoteTrack;
        private void PeerConnection_OnAddTrack(RTCTrackEvent evt)
        {
            OnAddRemoteTrack?.Invoke(evt);
        }

        /// <summary>
        /// Invoked when the remote peer removed a media track from the peer connection.
        /// </summary>
        public event Action<RTCTrackEvent> OnRemoveTrack;
        private void PeerConnection_OnRemoveTrack(RTCTrackEvent evt)
        {
            OnRemoveTrack?.Invoke(evt);
        }
#else
        /// <summary>
        /// Invoked when the remote peer added a media stream to the peer connection.
        /// </summary>
        public event Action<string> OnAddRemoteStream;
        private void PeerConnection_OnAddStream(string peerName, MediaStreamEvent evt)
        {
            Debug.WriteLine("Conductor: PeerConnection_OnAddStream");

            _peerVideoTracks[peerName] = evt.Stream.GetVideoTracks().FirstOrDefault();
            if (_peerVideoTracks[peerName] != null)
            {
#if UNITY_XAML
                if (UnityPlayer.AppCallbacks.Instance.IsInitialized())
                {
                    UnityPlayer.AppCallbacks.Instance.InvokeOnAppThread(new UnityPlayer.AppCallbackItem(() =>
                    {
                        UnityEngine.GameObject go = UnityEngine.GameObject.Find("Control");
                        if (VideoCodec.Name == "H264")
                        {
                            go.GetComponent<ControlScript>().CreateRemoteMediaStreamSource(_peerVideoTrack, "H264", GetPeerLabel(peerName));
                        }
                        else
                        {
                            go.GetComponent<ControlScript>().CreateRemoteMediaStreamSource(_peerVideoTrack, "I420", GetPeerLabel(peerName));
                        }
                    }
                    ), false);
                }
#elif !UNITY
                _media.AddVideoTrackMediaElementPair(_peerVideoTracks[peerName], PeerVideos[peerName], GetPeerLabel(peerName));
#endif

                Debug.WriteLine("Conductor: SetupPeerVideoFrameCallbacks");
                _peerRawVideoSources[peerName] = _media.CreateRawVideoSource(_peerVideoTracks[peerName]);
                _peerRawVideoSources[peerName].OnRawVideoFrame += (uint width, uint height,
                    byte[] yPlane, uint yPitch, byte[] vPlane, uint vPitch, byte[] uPlane, uint uPitch,
                    float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW) =>
                {
                    _peerRawVideoSource_OnRawVideoFrame(peerName, width, height,
                    yPlane, yPitch, vPlane, vPitch, uPlane, uPitch,
                    posX, posY, posZ, rotX, rotY, rotZ, rotW);
                };
            }

            OnAddRemoteStream?.Invoke(peerName);
        }

        /// <summary>
        /// Invoked when the remote peer removed a media stream from the peer connection.
        /// </summary>
        public event Action<string> OnRemoveRemoteStream;
        private void PeerConnection_OnRemoveStream(string peerName, MediaStreamEvent evt)
        {
#if UNITY_XAML
            if (UnityPlayer.AppCallbacks.Instance.IsInitialized())
            {
                UnityPlayer.AppCallbacks.Instance.InvokeOnAppThread(new UnityPlayer.AppCallbackItem(() =>
                {
                    UnityEngine.GameObject go = UnityEngine.GameObject.Find("Control");
                    go.GetComponent<ControlScript>().DestroyRemoteMediaStreamSource();
                }
                ), false);
            }
#elif !UNITY
            _media.RemoveVideoTrackMediaElementPair(_peerVideoTracks[peerName]);
#endif
            DestroyPeerVideoSources(peerName);
            OnRemoveRemoteStream?.Invoke(peerName);
        }

        /// <summary>
        /// Invoked when new connection health stats are available.
        /// Use ToggleConnectionHealthStats to turn on/of the connection health stats.
        /// </summary>
        public event Action<PeerConnectionHealthStats> OnConnectionHealthStats;
        public void PeerConnection_OnConnectionHealthStats(RTCPeerConnectionHealthStats stats)
        {
            OnConnectionHealthStats?.Invoke(new PeerConnectionHealthStats
            {
                ReceivedBytes = stats.ReceivedBytes,
                ReceivedKpbs = stats.ReceivedKpbs,
                SentBytes = stats.SentBytes,
                SentKbps = stats.SentKbps,
                RTT = stats.RTT,
                LocalCandidateType = stats.LocalCandidateType,
                RemoteCandidateType = stats.RemoteCandidateType
            });
        }
#endif
        /// <summary>
        /// Private constructor for singleton class.
        /// </summary>
        private Conductor()
        {
#if ORTCLIB
            _signalingMode = RTCPeerConnectionSignalingMode.Json;
//#else
            //_signalingMode = RTCPeerConnectionSignalingMode.Sdp;
#endif
            //_signaller = new Signaller();
            //_signaller = new PeerConnectionServerSignaller();
            _signaller = new SympleSignaller();
            _media = Media.CreateMedia();

            Signaller.OnDisconnected += Signaller_OnDisconnected;
            Signaller.OnMessageFromPeer += Signaller_OnMessageFromPeer;
            Signaller.OnPeerConnected += Signaller_OnPeerConnected;
            Signaller.OnPeerHangup += Signaller_OnPeerHangup;
            Signaller.OnPeerDisconnected += Signaller_OnPeerDisconnected;
            Signaller.OnServerConnectionFailure += Signaller_OnServerConnectionFailure;
            Signaller.OnSignedIn += Signaller_OnSignedIn;

            _iceServers = new List<RTCIceServer>();
        }

        /// <summary>
        /// Handler for Signaller's OnPeerHangup event.
        /// </summary>
        /// <param name="peerId">ID of the peer to hung up the call with.</param>
        void Signaller_OnPeerHangup(int peerId)
        {
            string peerName = null;
            foreach (var kv in _connectedPeerIds)
            {
                if (kv.Value == peerId)
                {
                    peerName = kv.Key;
                    break;
                }
            }

            if (peerName == null) return;

            Debug.WriteLine("Conductor: Our peer hung up.");
            ClosePeerConnection(peerName);
        }

        /// <summary>
        /// Handler for Signaller's OnSignedIn event.
        /// </summary>
        private void Signaller_OnSignedIn()
        {
        }

        /// <summary>
        /// Handler for Signaller's OnServerConnectionFailure event.
        /// </summary>
        private void Signaller_OnServerConnectionFailure()
        {
            Debug.WriteLine("[Error]: Connection to server failed!");
        }

        /// <summary>
        /// Handler for Signaller's OnPeerDisconnected event.
        /// </summary>
        /// <param name="peerId">ID of disconnected peer.</param>
        private void Signaller_OnPeerDisconnected(int peerId)
        {
            string peerName = null;
            foreach (var kv in _connectedPeerIds)
            {
                if (kv.Value == peerId)
                {
                    peerName = kv.Key;
                    break;
                }
            }
            
            // is the same peer or peer_id does not exist (0) in case of 500 Error
            if (peerName == null && peerId != 0) return;

            Debug.WriteLine("Conductor: Our peer disconnected.");
            ClosePeerConnection(peerName);
        }

        /// <summary>
        /// Handler for Signaller's OnPeerConnected event.
        /// </summary>
        /// <param name="id">ID of the connected peer.</param>
        /// <param name="name">Name of the connected peer.</param>
        private void Signaller_OnPeerConnected(int id, string name)
        {
        }

        public event Action<string, string> IncomingRawMessage;

        /// <summary>
        /// Handler for Signaller's OnMessageFromPeer event.
        /// </summary>
        /// <param name="peerId">ID of the peer.</param>
        /// <param name="message">Message from the peer.</param>
        private void Signaller_OnMessageFromPeer(int peerId, string message)
        {
            Task.Run(async () =>
            {
                Debug.WriteLine("Signaller_OnMessageFromPeer, peerId = " + peerId + ", message = " + message);

                string peerName = null;
                foreach (var kv in _onlinePeers)
                {
                    if (kv.Value.Id == peerId)
                    {
                        peerName = kv.Key;
                        break;
                    }
                }

                Debug.Assert(peerName != null);
                
                Debug.Assert(message.Length > 0);
                
                if (!JsonObject.TryParse(message, out JsonObject jMessage))
                {
                    Debug.WriteLine("[Error] Conductor: Received unknown message." + message);
                    return;
                }

                string type = jMessage.ContainsKey(kSessionDescriptionTypeName) ? jMessage.GetNamedString(kSessionDescriptionTypeName) : null;

                // handle user-generated messages here
                if (!IsNullOrEmpty(type) && type == "message")
                {
                    JsonObject rawMessage = jMessage.GetNamedObject("message");
                    IncomingRawMessage?.Invoke(peerName, rawMessage.ToString());
                    return;
                }
                
#if ORTCLIB
                bool created = false;
#endif
                if (!_peerConnections.ContainsKey(peerName))
                {
                    if (!IsNullOrEmpty(type))
                    {
                        // Create the peer connection only when call is
                        // about to get initiated. Otherwise ignore the
                        // messages from peers which could be a result
                        // of old (but not yet fully closed) connections.
                        if (type == "offer" || type == "answer" || type == "json")
                        {
                            Debug.Assert(!_connectedPeerIds.ContainsKey(peerName));

                            _connectedPeerIds[peerName] = peerId;

                            _connectedPeers[peerName] = _onlinePeers[peerName];
#if ORTCLIB
                            created = true;
                            _signalingMode = Helper.SignalingModeForClientName(Peer.Name);
#endif
                            _connectToPeerCancelationTokenSource = new CancellationTokenSource();
                            _connectToPeerTask = CreatePeerConnection(peerName, _connectToPeerCancelationTokenSource.Token);
                            bool connectResult = await _connectToPeerTask;
                            _connectToPeerTask = null;
                            _connectToPeerCancelationTokenSource.Dispose();
                            if (!connectResult)
                            {
                                Debug.WriteLine("[Error] Conductor: Failed to initialize our PeerConnection instance");
                                await Signaller.SignOut();
                                return;
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[Warn] Conductor: Received an untyped message after closing peer connection.");
                        return;
                    }
                }
                
                if (_peerConnections.ContainsKey(peerName) && !IsNullOrEmpty(type))
                {
                    if (type == "offer-loopback")
                    {
                        // Loopback not supported
                        Debug.Assert(false);
                    }
                    string sdp = null;
#if ORTCLIB
                    if (jMessage.ContainsKey(kSessionDescriptionJsonName))
                    {
                        var containerObject = new JsonObject { { kSessionDescriptionJsonName, jMessage.GetNamedObject(kSessionDescriptionJsonName) } };
                        sdp = containerObject.Stringify();
                    }
                    else if (jMessage.ContainsKey(kSessionDescriptionSdpName))
                    {
                        sdp = jMessage.GetNamedString(kSessionDescriptionSdpName);
                    }
#else
                    sdp = jMessage.ContainsKey(kSessionDescriptionSdpName) ? jMessage.GetNamedString(kSessionDescriptionSdpName) : null;
#endif
                    if (IsNullOrEmpty(sdp))
                    {
                        Debug.WriteLine("[Error] Conductor: Can't parse received session description message.");
                        return;
                    }

#if ORTCLIB
                    RTCSessionDescriptionSignalingType messageType = RTCSessionDescriptionSignalingType.SdpOffer;
                    switch (type)
                    {
                        case "json": messageType = RTCSessionDescriptionSignalingType.Json; break;
                        case "offer": messageType = RTCSessionDescriptionSignalingType.SdpOffer; break;
                        case "answer": messageType = RTCSessionDescriptionSignalingType.SdpAnswer; break;
                        case "pranswer": messageType = RTCSessionDescriptionSignalingType.SdpPranswer; break;
                        default: Debug.Assert(false, type); break;
                    }
#else
                    RTCSdpType messageType = RTCSdpType.Offer;
                    switch (type)
                    {
                        case "offer": messageType = RTCSdpType.Offer; break;
                        case "answer": messageType = RTCSdpType.Answer; break;
                        case "pranswer": messageType = RTCSdpType.Pranswer; break;
                        default: Debug.Assert(false, type); break;
                    }
#endif
                    Debug.WriteLine("Conductor: Received session description: " + message);
                    await _peerConnections[peerName].SetRemoteDescription(new RTCSessionDescription(messageType, sdp));

#if ORTCLIB
                    if ((messageType == RTCSessionDescriptionSignalingType.SdpOffer) ||
                        ((created) && (messageType == RTCSessionDescriptionSignalingType.Json)))
#else
                    if (messageType == RTCSdpType.Offer)
#endif
                    {
                        var answer = await _peerConnections[peerName].CreateAnswer();
                        await _peerConnections[peerName].SetLocalDescription(answer);
                        // Send answer
                        SendSdp(peerName, answer);
#if ORTCLIB
                        OrtcStatsManager.Instance.StartCallWatch(SessionId, false);
#endif
                    }
                }
                else
                {
                    RTCIceCandidate candidate = null;
#if ORTCLIB
                    if (RTCPeerConnectionSignalingMode.Json != _signalingMode)
#endif
                    {
                        var sdpMid = jMessage.ContainsKey(kCandidateSdpMidName)
                            ? jMessage.GetNamedString(kCandidateSdpMidName)
                            : null;
                        var sdpMlineIndex = jMessage.ContainsKey(kCandidateSdpMlineIndexName)
                            ? jMessage.GetNamedNumber(kCandidateSdpMlineIndexName)
                            : -1;
                        var sdp = jMessage.ContainsKey(kCandidateSdpName)
                            ? jMessage.GetNamedString(kCandidateSdpName)
                            : null;
                        //TODO: Check is this proper condition ((String.IsNullOrEmpty(sdpMid) && (sdpMlineIndex == -1)) || String.IsNullOrEmpty(sdp))
                        if (IsNullOrEmpty(sdpMid) || sdpMlineIndex == -1 || IsNullOrEmpty(sdp))
                        {
                            Debug.WriteLine("[Error] Conductor: Can't parse received message.\n" + message);
                            return;
                        }
#if ORTCLIB
                        candidate = IsNullOrEmpty(sdpMid) ? RTCIceCandidate.FromSdpStringWithMLineIndex(sdp, (ushort)sdpMlineIndex) : RTCIceCandidate.FromSdpStringWithMid(sdp, sdpMid);
#else
                        candidate = new RTCIceCandidate(sdp, sdpMid, (ushort)sdpMlineIndex);
#endif
                    }
#if ORTCLIB
                    else
                    {
                        candidate = RTCIceCandidate.FromJsonString(message);
                    }
                    _peerConnection?.AddIceCandidate(candidate);
#else
                    await _peerConnections[peerName].AddIceCandidate(candidate);
#endif


                    Debug.WriteLine("Conductor: Received candidate : " + message);
                }
            }).Wait();
        }

        public void CloseAllPeerConnections()
        {
            foreach (var kv in _peerConnections)
            {
                ClosePeerConnection(kv.Key);
            }
        }

        /// <summary>
        /// Handler for Signaller's OnDisconnected event handler.
        /// </summary>
        private void Signaller_OnDisconnected()
        {
            CloseAllPeerConnections();
        }

        /// <summary>
        /// Starts the login to server process.
        /// </summary>
        /// <param name="server">The host server.</param>
        /// <param name="port">The port to connect to.</param>
        /// <param name="clientName">The client name to connect as.</param>
        public void StartLogin(string server, string port, string clientName)
        {
            if (_signaller.IsConnected())
            {
                return;
            }
            _signaller.Connect(server, port, clientName);
        }

        /// <summary>
        /// Calls to disconnect the user from the server.
        /// </summary>
        public async Task DisconnectFromServer()
        {
            if (_signaller.IsConnected())
            {
                await _signaller.SignOut();
                _onlinePeers.Clear();
            }
        }

        /// <summary>
        /// Calls to connect to the selected peer.
        /// </summary>
        /// <param name="peer">Peer to connect to.</param>
        public async void ConnectToPeer(Peer peer)
        {
            Debug.Assert(peer != null);

            string peerName = peer.Name;

            Debug.Assert(!_connectedPeerIds.ContainsKey(peerName));
            
#if ORTCLIB
            _signalingMode = Helper.SignalingModeForClientName(peer.Name);
#endif
            _connectToPeerCancelationTokenSource = new System.Threading.CancellationTokenSource();
            _connectToPeerTask = CreatePeerConnection(peerName, _connectToPeerCancelationTokenSource.Token);
            bool connectResult = await _connectToPeerTask;
            _connectToPeerTask = null;
            _connectToPeerCancelationTokenSource.Dispose();
            if (connectResult)
            {
                _connectedPeerIds[peerName] = peer.Id;
                var offer = await _peerConnections[peerName].CreateOffer();
#if !ORTCLIB
                // Alter sdp to force usage of selected codecs
                string newSdp = offer.Sdp;
                SdpUtils.SelectCodecs(ref newSdp,
                    new Org.WebRtc.CodecInfo(AudioCodec.ClockRate, AudioCodec.Name),
                    new Org.WebRtc.CodecInfo(VideoCodec.ClockRate, VideoCodec.Name));
                offer.Sdp = newSdp;
#endif
                await _peerConnections[peerName].SetLocalDescription(offer);
                Debug.WriteLine("Conductor: Sending offer.");
                SendSdp(peerName, offer);
#if ORTCLIB
                OrtcStatsManager.Instance.StartCallWatch(SessionId, true);
#endif
            }
        }

        /// <summary>
        /// Calls to disconnect from peer.
        /// </summary>
        public async Task DisconnectFromPeer(string peerName)
        {
            await SendHangupMessage(peerName);
            ClosePeerConnection(peerName);
        }

        /// <summary>
        /// Constructs and returns the local peer name.
        /// </summary>
        /// <returns>The local peer name.</returns>
        public string GetLocalPeerName()
        {
            var hostname = NetworkInformation.GetHostNames().FirstOrDefault(h => h.Type == HostNameType.DomainName);
            string ret = hostname?.CanonicalName ?? "<unknown host>";
#if ORTCLIB
            ret = ret + "-dual";
#endif
            return ret;
        }

        /// <summary>
        /// Sends SDP message.
        /// </summary>
        /// <param name="description">RTC session description.</param>
        private void SendSdp(string peerName, RTCSessionDescription description)
        {
            JsonObject json = null;
#if ORTCLIB
            var type = description.Type.ToString().ToLower();
            string formattedDescription = description.FormattedDescription;

            if (description.Type == RTCSessionDescriptionSignalingType.Json)
            {
                if (IsNullOrEmpty(SessionId))
                {
                    var match = Regex.Match(formattedDescription, "{\"username\":\"-*[a-zA-Z0-9]*\",\"id\":\"([0-9]+)\"");
                    if (match.Success)
                    {
                        SessionId = match.Groups[1].Value;
                    }
                }
                var jsonDescription = JsonObject.Parse(formattedDescription);
                var sessionValue = jsonDescription.GetNamedObject(kSessionDescriptionJsonName);
                json = new JsonObject
                {
                    {kSessionDescriptionTypeName, JsonValue.CreateStringValue(type)},
                    {kSessionDescriptionJsonName,  sessionValue}
                };
            }
            else
            {
                var match = Regex.Match(formattedDescription, "o=[^ ]+ ([0-9]+) [0-9]+ [a-zA-Z]+ [a-zA-Z0-9]+ [0-9\\.]+");
                if (match.Success)
                {
                    SessionId = match.Groups[1].Value;
                }

                var prefix = type.Substring(0, "sdp".Length);
                if (prefix == "sdp")
                {
                    type = type.Substring("sdp".Length);
                }

                json = new JsonObject
                {
                    {kSessionDescriptionTypeName, JsonValue.CreateStringValue(type)},
                    {kSessionDescriptionSdpName, JsonValue.CreateStringValue(formattedDescription)}
                };
            }
#else
            json = new JsonObject
            {
                { kSessionDescriptionTypeName, JsonValue.CreateStringValue(description.Type.GetValueOrDefault().ToString().ToLower()) },
                { kSessionDescriptionSdpName, JsonValue.CreateStringValue(description.Sdp) }
            };
#endif
            SendMessage(peerName, json);
        }

        /// <summary>
        /// Helper method to send a message to a peer.
        /// </summary>
        /// <param name="json">Message body.</param>
        public void SendMessage(string peerName, IJsonValue json)
        {
            int peerId = _connectedPeerIds[peerName];

            // Don't await, send it async.
            var task = _signaller.SendToPeer(peerId, json);
        }

        /// <summary>
        /// Helper method to send a hangup message to a peer.
        /// </summary>
        private async Task SendHangupMessage(string peerName)
        {
            int peerId = _connectedPeerIds[peerName];

            await _signaller.SendToPeer(peerId, "BYE");
        }

        /// <summary>
        /// Enables the local video stream.
        /// </summary>
        public void EnableLocalVideoStream()
        {
            lock (MediaLock)
            {
                if (_mediaStream != null)
                {
                    foreach (MediaVideoTrack videoTrack in _mediaStream.GetVideoTracks())
                    {
                        videoTrack.Enabled = true;
                    }
                }
                VideoEnabled = true;
            }
        }

        /// <summary>
        /// Disables the local video stream.
        /// </summary>
        public void DisableLocalVideoStream()
        {
            lock (MediaLock)
            {
                if (_mediaStream != null)
                {
                    foreach (MediaVideoTrack videoTrack in _mediaStream.GetVideoTracks())
                    {
                        videoTrack.Enabled = false;
                    }
                }
                VideoEnabled = false;
            }
        }

        /// <summary>
        /// Mutes the microphone.
        /// </summary>
        public void MuteMicrophone()
        {
            lock (MediaLock)
            {
                if (_mediaStream != null)
                {
                    foreach (MediaAudioTrack audioTrack in _mediaStream.GetAudioTracks())
                    {
                        audioTrack.Enabled = false;
                    }
                }
                AudioEnabled = false;
            }
        }

        /// <summary>
        /// Unmutes the microphone.
        /// </summary>
        public void UnmuteMicrophone()
        {
            lock (MediaLock)
            {
                if (_mediaStream != null)
                {
                    foreach (MediaAudioTrack audioTrack in _mediaStream.GetAudioTracks())
                    {
                        audioTrack.Enabled = true;
                    }
                }
                AudioEnabled = true;
            }
        }

        /// <summary>
        /// Receives a new list of Ice servers and updates the local list of servers.
        /// </summary>
        /// <param name="iceServers">List of Ice servers to configure.</param>
        public void ConfigureIceServers(List<IceServer> iceServers)
        {
            _iceServers.Clear();
            foreach (IceServer iceServer in iceServers)
            {
                //Url format: stun:stun.l.google.com:19302
                string url = "stun:";
                if (iceServer.Type == IceServer.ServerType.TURN)
                {
                    url = "turn:";
                }
                RTCIceServer server = null;
                url += iceServer.Host;
#if ORTCLIB
                //url += iceServer.Host.Value;
                server = new RTCIceServer()
                {
                    Urls = new List<string>(),
                };
                server.Urls.Add(url);
#else
                //url += iceServer.Host.Value + ":" + iceServer.Port.Value;
                server = new RTCIceServer { Url = url };
#endif
                if (iceServer.Credential != null)
                {
                    server.Credential = iceServer.Credential;
                }
                if (iceServer.Username != null)
                {
                    server.Username = iceServer.Username;
                }
                _iceServers.Add(server);
            }
        }

        /// <summary>
        /// If a connection to a peer is establishing, requests it's
        /// cancelation and wait the operation to cancel (blocks curren thread).
        /// </summary>
        public void CancelConnectingToPeer()
        {
            if (_connectToPeerTask != null)
            {
                Debug.WriteLine("Conductor: Connecting to peer in progress, canceling");
                _connectToPeerCancelationTokenSource.Cancel();
                _connectToPeerTask.Wait();
                Debug.WriteLine("Conductor: Connecting to peer flow canceled");
            }
        }

        
        // Set up spatial coordinate system for sending pose metadata
        public void InitializeSpatialCoordinateSystem(IntPtr spatialCoordinateSystemPtr)
        {
            SpatialCoordinateSystem spatialCoordinateSystem = (SpatialCoordinateSystem)Marshal.GetObjectForIUnknown(spatialCoordinateSystemPtr);
            Media.SetSpatialCoordinateSystem(spatialCoordinateSystem);
        }
        
    }
}
