using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.XR.WSA;
using Newtonsoft.Json.Linq;

#if !UNITY_EDITOR
using Windows.UI.Core;
using Windows.Foundation;
using Windows.Media.Core;
using System.Linq;
using System.Threading.Tasks;
using HoloPoseClient.Signalling;
//using PeerConnectionClient.Signalling;
using Windows.ApplicationModel.Core;

using Newtonsoft.Json.Linq;
#endif

public class ControlScript : MonoBehaviour
{
    private const string StarTraineeName = "star-trainee";
    private const string StarMentorName = "star-mentor";


    public string ServerAddress = "https://purduestarproj-webrtc-signal.herokuapp.com";
    public string ServerPort = "443";
    public string ClientName = "star-trainee"; // star-trainee, star-mentor, etc

    // if this is true:
    // - this client will be able to initiate a call to a peer
    // - this client will offer its own media
    // if this is false:
    // - this client will not be able to initiate a call to a peer (it will accept incoming calls)
    // - this client will not offer its own media
    // i.e.: the trainee should have LocalStreamEnabled = true, and the mentor should have LocalStreamEnabled = false
    public bool LocalStreamEnabled = true;

    public string PreferredVideoCodec = "VP8"; // options are "VP8" and "H264". Currently (as of 5/28/2018) we only support HoloLens pose on VP8.

    public uint LocalTextureWidth = 160;
    public uint LocalTextureHeight = 120;

    public uint RemoteTextureWidth = 640;
    public uint RemoteTextureHeight = 480;

    public RawImage LocalVideoImage;
    public RawImage RemoteVideoImage;


    

    private string LocalName = StarTraineeName;

    public Dictionary<string, uint> SourceIDs = new Dictionary<string, uint> { { StarMentorName, 0 }, { StarTraineeName, 1 } };



    public InputField ServerAddressInputField;
    public InputField ServerPortInputField;
    public InputField ClientNameInputField;

    public Button ConnectButton;
    public Button CallButton;

    public RectTransform PeerContent;
    public RectTransform SelfConnectedAsContent;

    public Text PreferredCodecLabel;

    public Text LastReceivedMessageLabel;

    public Text LastPeerPoseLabel;
    public Text LastSelfPoseLabel;

    public GameObject TextItemPrefab;

    private enum Status
    {
        NotConnected,
        Connecting,
        Disconnecting,
        Connected,
        Calling,
        EndingCall,
        InCall
    }

    private enum CommandType
    {
        Empty,
        SetNotConnected,
        SetConnected,
        SetInCall,
        AddRemotePeer,
        RemoveRemotePeer
    }

    private struct Command
    {
        public CommandType type;
#if !UNITY_EDITOR
        public Conductor.Peer remotePeer;
#endif
    }

    private Status status = Status.NotConnected;
    private List<Command> commandQueue = new List<Command>();
    private int selectedPeerIndex = -1;

    public ControlScript()
    {
    }

    void Awake()
    {
    }

    void Start()
    {
        /*
        Debug.Log("NOTE: creating some mock remote peers to test UI");
        for (int i = 0; i < 5; i++)
        {
            string mockName = "mock-peer-" + UnityEngine.Random.value;
            if (i == 0)
            {
                mockName = ClientName; // testing to make sure we can't accidentally call ourselves
            }
            AddRemotePeer(mockName);
        }
        */






#if !UNITY_EDITOR
        if (LocalStreamEnabled) {
            Debug.Log("because this is the TRAINEE app, we enable the local stream so we can send video to the mentor.");
        } else {
            Debug.Log("because this is the MENTOR app, we disable the local stream so we are not sending any video back to the trainee.");
        }

        Conductor.Instance.LocalStreamEnabled = LocalStreamEnabled;
#endif

#if !UNITY_EDITOR
        // Set up spatial coordinate system for sending pose metadata
        Debug.Log("setting up spatial coordinate system");
        IntPtr spatialCoordinateSystemPtr = WorldManager.GetNativeISpatialCoordinateSystemPtr();
        if (spatialCoordinateSystemPtr.ToInt32() != 0)
        {
            Debug.Log("spatialCoordinateSystemPtr: " + spatialCoordinateSystemPtr.ToString());
            Conductor.Instance.InitializeSpatialCoordinateSystem(spatialCoordinateSystemPtr);
            Debug.Log("SetSpatialCoordinateSystem done");
        } else
        {
            Debug.Log("spatialCoordinateSystemPtr was null. Probably not running on a Mixed Reality headset. Skipping initing video pose data.");
        }
        

        Debug.Log("setting up the rest of the conductor...");

        Conductor.Instance.IncomingRawMessage += Conductor_IncomingRawMessage;
        Conductor.Instance.OnSelfRawFrame += Conductor_OnSelfRawFrame;
        Conductor.Instance.OnPeerRawFrame += Conductor_OnPeerRawFrame;

        Conductor.Instance.Initialized += Conductor_Initialized;
        Conductor.Instance.Initialize(CoreApplication.MainView.CoreWindow.Dispatcher);
        Conductor.Instance.EnableLogging(Conductor.LogLevel.Verbose);
        Debug.Log("done setting up the rest of the conductor");
#endif
        ServerAddressInputField.text = ServerAddress;
        ServerPortInputField.text = ServerPort;
        ClientNameInputField.text = ClientName;
    }

    private void InitMediaTexture(uint id, RawImage videoImage, uint width, uint height)
    {
        Plugin.CreateMediaPlayback(id);
        IntPtr nativeTex = IntPtr.Zero;
        Plugin.GetPrimaryTexture(id, width, height, out nativeTex);
        var primaryPlaybackTexture = Texture2D.CreateExternalTexture((int)width, (int)height, TextureFormat.BGRA32, false, false, nativeTex);
        if (videoImage != null)
        {
            videoImage.texture = primaryPlaybackTexture;
        }
    }

    private void OnEnable()
    {
        if (LocalStreamEnabled)
        {
            InitMediaTexture(SourceIDs[StarTraineeName], LocalVideoImage, LocalTextureWidth, LocalTextureHeight);
        }

        if (RemoteVideoImage != null)
        {
            InitMediaTexture(SourceIDs[StarMentorName], RemoteVideoImage, RemoteTextureWidth, RemoteTextureHeight);
        }
    }

    private void TeardownMediaTexture(uint id, RawImage videoImage)
    {
        if (videoImage != null)
        {
            videoImage.texture = null;
        }
        Plugin.ReleaseMediaPlayback(id);
    }

    private void OnDisable()
    {
        if (LocalStreamEnabled)
        {
            TeardownMediaTexture(SourceIDs[StarTraineeName], LocalVideoImage);
        }

        if (RemoteVideoImage != null)
        {
            TeardownMediaTexture(SourceIDs[StarMentorName], RemoteVideoImage);
        }
    }

    private void AddRemotePeer(string peerName)
    {
        bool isSelf = (peerName == ClientName); // when we connect, our own user appears as a peer. we don't want to accidentally try to call ourselves.

        Debug.Log("AddRemotePeer: " + peerName);
        GameObject textItem = (GameObject)Instantiate(TextItemPrefab);

        textItem.GetComponent<Text>().text = peerName;

        if (isSelf)
        {
            textItem.transform.SetParent(SelfConnectedAsContent, false);
        }
        else
        {
            textItem.transform.SetParent(PeerContent, false);

            EventTrigger trigger = textItem.GetComponentInChildren<EventTrigger>();
            EventTrigger.Entry entry = new EventTrigger.Entry();
            entry.eventID = EventTriggerType.PointerDown;
            entry.callback.AddListener((data) => { OnRemotePeerItemClick((PointerEventData)data); });
            trigger.triggers.Add(entry);

            if (selectedPeerIndex == -1)
            {
                textItem.GetComponent<Text>().fontStyle = FontStyle.Bold;
                selectedPeerIndex = PeerContent.transform.childCount - 1;
            }
        }
    }

    private void RemoveRemotePeer(string peerName)
    {
        bool isSelf = (peerName == ClientName); // when we connect, our own user appears as a peer. we don't want to accidentally try to call ourselves.

        Debug.Log("RemoveRemotePeer: " + peerName);

        if (isSelf)
        {
            for (int i = 0; i < SelfConnectedAsContent.transform.childCount; i++)
            {
                if (SelfConnectedAsContent.GetChild(i).GetComponent<Text>().text == peerName)
                {
                    SelfConnectedAsContent.GetChild(i).SetParent(null);
                    break;
                }
            }
        }
        else
        {
            for (int i = 0; i < PeerContent.transform.childCount; i++)
            {
                if (PeerContent.GetChild(i).GetComponent<Text>().text == peerName)
                {
                    PeerContent.GetChild(i).SetParent(null);
                    if (selectedPeerIndex == i)
                    {
                        if (PeerContent.transform.childCount > 0)
                        {
                            PeerContent.GetChild(0).GetComponent<Text>().fontStyle = FontStyle.Bold;
                            selectedPeerIndex = 0;
                        }
                        else
                        {
                            selectedPeerIndex = -1;
                        }
                    }
                    break;
                }
            }
        }
    }


    private void Update()
    {
        lock (this)
        {
            switch (status)
            {
                case Status.NotConnected:
                    if (!ServerAddressInputField.enabled)
                        ServerAddressInputField.enabled = true;
                    if (!ConnectButton.enabled)
                        ConnectButton.enabled = true;
                    if (CallButton.enabled)
                        CallButton.enabled = false;
                    break;
                case Status.Connecting:
                    if (ServerAddressInputField.enabled)
                        ServerAddressInputField.enabled = false;
                    if (ConnectButton.enabled)
                        ConnectButton.enabled = false;
                    if (CallButton.enabled)
                        CallButton.enabled = false;
                    break;
                case Status.Disconnecting:
                    if (ServerAddressInputField.enabled)
                        ServerAddressInputField.enabled = false;
                    if (ConnectButton.enabled)
                        ConnectButton.enabled = false;
                    if (CallButton.enabled)
                        CallButton.enabled = false;
                    break;
                case Status.Connected:
                    if (ServerAddressInputField.enabled)
                        ServerAddressInputField.enabled = false;
                    if (!ConnectButton.enabled)
                        ConnectButton.enabled = true;
                    if (CallButton.enabled != LocalStreamEnabled)
                        CallButton.enabled = LocalStreamEnabled; // only allow pressing the Call button (when not in a call) if our client is set up to initiate a call
                    break;
                case Status.Calling:
                    if (ServerAddressInputField.enabled)
                        ServerAddressInputField.enabled = false;
                    if (ConnectButton.enabled)
                        ConnectButton.enabled = false;
                    if (CallButton.enabled)
                        CallButton.enabled = false;
                    break;
                case Status.EndingCall:
                    if (ServerAddressInputField.enabled)
                        ServerAddressInputField.enabled = false;
                    if (ConnectButton.enabled)
                        ConnectButton.enabled = false;
                    if (CallButton.enabled)
                        CallButton.enabled = false;
                    break;
                case Status.InCall:
                    if (ServerAddressInputField.enabled)
                        ServerAddressInputField.enabled = false;
                    if (ConnectButton.enabled)
                        ConnectButton.enabled = false;
                    if (!CallButton.enabled)
                        CallButton.enabled = true;
                    break;
                default:
                    break;
            }

#if !UNITY_EDITOR
            while (commandQueue.Count != 0)
            {
                Command command = commandQueue.First();
                commandQueue.RemoveAt(0);
                switch (status)
                {
                    case Status.NotConnected:
                        if (command.type == CommandType.SetNotConnected)
                        {
                            ConnectButton.GetComponentInChildren<Text>().text = "Connect";
                            CallButton.GetComponentInChildren<Text>().text = "Call";
                        }
                        break;
                    case Status.Connected:
                        if (command.type == CommandType.SetConnected)
                        {
                            ConnectButton.GetComponentInChildren<Text>().text = "Disconnect";
                            CallButton.GetComponentInChildren<Text>().text = "Call";
                        }
                        break;
                    case Status.InCall:
                        if (command.type == CommandType.SetInCall)
                        {
                            ConnectButton.GetComponentInChildren<Text>().text = "Disconnect";
                            CallButton.GetComponentInChildren<Text>().text = "Hang Up";
                        }
                        break;
                    default:
                        break;
                }
                if (command.type == CommandType.AddRemotePeer)
                {
                    string remotePeerName = command.remotePeer.Name;
                    AddRemotePeer(remotePeerName);
                }
                else if (command.type == CommandType.RemoveRemotePeer)
                {
                    string remotePeerName = command.remotePeer.Name;
                    RemoveRemotePeer(remotePeerName);
                }
            }
#endif
        }
    }

    // fired whenever we get a video frame from the remote peer.
    // if there is pose data, posXYZ and rotXYZW will have non-zero values.
    private void Conductor_OnPeerRawFrame(string peerName, uint width, uint height,
            byte[] yPlane, uint yPitch, byte[] vPlane, uint vPitch, byte[] uPlane, uint uPitch,
            float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW)
    {
        // TODO: use the peerName to determine which video source this is coming from

        UnityEngine.WSA.Application.InvokeOnAppThread(() =>
        {
            //Set property on UI thread
            //Debug.Log("ControlScript: OnPeerRawFrame " + width + " " + height + " " + posX + " " + posY + " " + posZ + " " + rotX + " " + rotY + " " + rotZ + " " + rotW);

            if (LastPeerPoseLabel != null)
            {
                LastPeerPoseLabel.text = posX + " " + posY + " " + posZ + "\n" + rotX + " " + rotY + " " + rotZ + " " + rotW;
            }
        }, false);
    }

    // fired whenever we encode one of our own video frames before sending it to the remote peer.
    // if there is pose data, posXYZ and rotXYZW will have non-zero values.
    private void Conductor_OnSelfRawFrame(uint width, uint height,
            byte[] yPlane, uint yPitch, byte[] vPlane, uint vPitch, byte[] uPlane, uint uPitch,
            float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW)
    {
        UnityEngine.WSA.Application.InvokeOnAppThread(() =>
        {
            //Set property on UI thread
            //Debug.Log("ControlScript: OnSelfRawFrame " + width + " " + height + " " + posX + " " + posY + " " + posZ + " " + rotX + " " + rotY + " " + rotZ + " " + rotW);

            if (LastSelfPoseLabel != null)
            {
                LastSelfPoseLabel.text = posX + " " + posY + " " + posZ + "\n" + rotX + " " + rotY + " " + rotZ + " " + rotW;
            }
        }, false);
    }

    private void Conductor_IncomingRawMessage(string peerName, string rawMessageString)
    {
        UnityEngine.WSA.Application.InvokeOnAppThread(() =>
        {
            Debug.Log("incoming raw message from peer " + peerName + ": " + rawMessageString);

            if (LastReceivedMessageLabel != null)
            {
                LastReceivedMessageLabel.text = rawMessageString;
            }
        }, false);
    }

    private void Conductor_Initialized(bool succeeded)
    {
        if (succeeded)
        {
            Initialize();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("Conductor initialization failed");
        }
    }

    public void OnConnectClick()
    {
#if !UNITY_EDITOR
        lock (this)
        {
            if (status == Status.NotConnected)
            {
                new Task(() =>
                {
                    Conductor.Instance.StartLogin(ServerAddressInputField.text, ServerPortInputField.text, ClientNameInputField.text);
                }).Start();
                status = Status.Connecting;
            }
            else if (status == Status.Connected)
            {
                new Task(() =>
                {
                    var task = Conductor.Instance.DisconnectFromServer();
                }).Start();

                status = Status.Disconnecting;
                selectedPeerIndex = -1;
                PeerContent.DetachChildren();
                SelfConnectedAsContent.DetachChildren();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("OnConnectClick() - wrong status - " + status);
            }
        }
#endif
    }

    // Sends a test JSON message to the peer with which we are connected. Requires that we be both connected to the server and "in a call" with another peer before we can send.
    public void OnSendTestMessageClick()
    {
        lock (this)
        {
            if (status == Status.InCall)
            { 
                // NOTE: this is the raw message to be sent
                //
                JObject messageToSend = new JObject();
                messageToSend["hello"] = "world";
                messageToSend["timestamp"] = Time.time;
                //


                // To handle the message properly, it should be wrapped in an outer JSON object where the "message" key points to your actual message.
                JObject messageContainer = new JObject();
                messageContainer["message"] = messageToSend;

                string jsonString = messageContainer.ToString();

                Debug.Log("sending test message " + jsonString);

                string dummyMessageRecipient = "star-mentor";

                Debug.Log("TODO: test message being sent just to '" + dummyMessageRecipient + "' (hardcoded)");
#if !UNITY_EDITOR
                Conductor.Instance.SendMessage(dummyMessageRecipient, Windows.Data.Json.JsonObject.Parse(jsonString));
#endif
            }
            else
            {
                Debug.LogError("attempted to send test message while not in call");
            }
        }
    }

    public void OnCallClick()
    {
#if !UNITY_EDITOR
        lock (this)
        {
            if (status == Status.Connected)
            {
                if (selectedPeerIndex == -1)
                    return;

                Debug.Log("selectedPeerIndex: " + selectedPeerIndex);
                string selectedRemotePeerName = PeerContent.GetChild(selectedPeerIndex).GetComponent<Text>().text;
                Debug.Log("selectedRemotePeerName: " + selectedRemotePeerName);

                new Task(() =>
                {
                    // given the selectedPeerIndex, find which remote peer that matches. 
                    // Note: it's not just that index in Conductor.Instance.GetPeers() because that list contains both remote peers and ourselves.
                    Conductor.Peer selectedConductorPeer = null;

                    var conductorPeers = Conductor.Instance.GetPeers();
                    foreach (var conductorPeer in conductorPeers)
                    {
                        if (conductorPeer.Name == selectedRemotePeerName)
                        {
                            selectedConductorPeer = conductorPeer;
                            break;
                        }
                    }

                    Debug.Log("selectedConductorPeer: " + selectedConductorPeer.Name);
                    Debug.Log("going to try to connect to peer");

                    if (selectedConductorPeer != null)
                    {
                        Conductor.Instance.ConnectToPeer(selectedConductorPeer);
                    }
                }).Start();
                status = Status.Calling;
            }
            else if (status == Status.InCall)
            {
                if (selectedPeerIndex == -1)
                    return;

                Debug.Log("selectedPeerIndex: " + selectedPeerIndex);
                string selectedRemotePeerName = PeerContent.GetChild(selectedPeerIndex).GetComponent<Text>().text;
                Debug.Log("selectedRemotePeerName: " + selectedRemotePeerName);

                new Task(() =>
                {
                    var task = Conductor.Instance.DisconnectFromPeer(selectedRemotePeerName);
                }).Start();
                status = Status.EndingCall;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("OnCallClick() - wrong status - " + status);
            }
        }
#endif
    }

    public void OnRemotePeerItemClick(PointerEventData data)
    {

        for (int i = 0; i < PeerContent.transform.childCount; i++)
        {
            if (PeerContent.GetChild(i) == data.selectedObject.transform)
            {
                data.selectedObject.GetComponent<Text>().fontStyle = FontStyle.Bold;
                selectedPeerIndex = i;
            }
            else
            {
                PeerContent.GetChild(i).GetComponent<Text>().fontStyle = FontStyle.Normal;
            }
        }
    }

#if !UNITY_EDITOR
    public async Task OnAppSuspending()
    {
        Conductor.Instance.CancelConnectingToPeer();

        var conductorPeers = Conductor.Instance.GetPeers();
        foreach (var conductorPeer in conductorPeers) 
        {
            if (conductorPeer.Name != LocalName) 
            {
                await Conductor.Instance.DisconnectFromPeer(conductorPeer.Name);
            }
        }

        
        await Conductor.Instance.DisconnectFromServer();

        Conductor.Instance.OnAppSuspending();
    }

    private IAsyncAction RunOnUiThread(Action fn)
    {
        return CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, new DispatchedHandler(fn));
    }
#endif

    public void Initialize()
    {
#if !UNITY_EDITOR
        // A Peer is connected to the server event handler
        Conductor.Instance.Signaller.OnPeerConnected += (peerId, peerName) =>
        {
            var task = RunOnUiThread(() =>
            {
                lock (this)
                {
                    Conductor.Peer peer = new Conductor.Peer { Id = peerId, Name = peerName };
                    Conductor.Instance.AddPeer(peer);
                    commandQueue.Add(new Command { type = CommandType.AddRemotePeer, remotePeer = peer });
                }
            });
        };

        // A Peer is disconnected from the server event handler
        Conductor.Instance.Signaller.OnPeerDisconnected += peerId =>
        {
            var task = RunOnUiThread(() =>
            {
                lock (this)
                {
                    var peerToRemove = Conductor.Instance.GetPeers().FirstOrDefault(p => p.Id == peerId);
                    if (peerToRemove != null)
                    {
                        Conductor.Peer peer = new Conductor.Peer { Id = peerToRemove.Id, Name = peerToRemove.Name };
                        Conductor.Instance.RemovePeer(peer);
                        commandQueue.Add(new Command { type = CommandType.RemoveRemotePeer, remotePeer = peer });
                    }
                }
            });
        };

        // The user is Signed in to the server event handler
        Conductor.Instance.Signaller.OnSignedIn += () =>
        {
            var task = RunOnUiThread(() =>
            {
                lock (this)
                {
                    if (status == Status.Connecting)
                    {
                        status = Status.Connected;
                        commandQueue.Add(new Command { type = CommandType.SetConnected });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Signaller.OnSignedIn() - wrong status - " + status);
                    }
                }
            });
        };

        // Failed to connect to the server event handler
        Conductor.Instance.Signaller.OnServerConnectionFailure += () =>
        {
            var task = RunOnUiThread(() =>
            {
                lock (this)
                {
                    if (status == Status.Connecting)
                    {
                        status = Status.NotConnected;
                        commandQueue.Add(new Command { type = CommandType.SetNotConnected });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Signaller.OnServerConnectionFailure() - wrong status - " + status);
                    }
                }
            });
        };

        // The current user is disconnected from the server event handler
        Conductor.Instance.Signaller.OnDisconnected += () =>
        {
            var task = RunOnUiThread(() =>
            {
                lock (this)
                {
                    if (status == Status.Disconnecting)
                    {
                        status = Status.NotConnected;
                        commandQueue.Add(new Command { type = CommandType.SetNotConnected });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Signaller.OnDisconnected() - wrong status - " + status);
                    }
                }
            });
        };

        Conductor.Instance.OnAddRemoteStream += Conductor_OnAddRemoteStream;
        Conductor.Instance.OnRemoveRemoteStream += Conductor_OnRemoveRemoteStream;
        Conductor.Instance.OnAddLocalStream += Conductor_OnAddLocalStream;

        // Connected to a peer event handler
        Conductor.Instance.OnPeerConnectionCreated += (peerName) =>
        {
            var task = RunOnUiThread(() =>
            {
                lock (this)
                {
                    if (status == Status.Calling)
                    {
                        status = Status.InCall;
                        commandQueue.Add(new Command { type = CommandType.SetInCall });
                    }
                    else if (status == Status.Connected)
                    {
                        status = Status.InCall;
                        commandQueue.Add(new Command { type = CommandType.SetInCall });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Conductor.OnPeerConnectionCreated() - wrong status - " + status);
                    }
                }
            });
        };

        // Connection between the current user and a peer is closed event handler
        Conductor.Instance.OnPeerConnectionClosed += (remotePeerName) =>
        {
            var localId = SourceIDs[LocalName];
            var remoteId = SourceIDs[remotePeerName];

            var task = RunOnUiThread(() =>
            {
                lock (this)
                {
                    if (status == Status.EndingCall)
                    {
                        Plugin.UnloadMediaStreamSource(localId);
                        Plugin.UnloadMediaStreamSource(remoteId);
                        status = Status.Connected;
                        commandQueue.Add(new Command { type = CommandType.SetConnected });
                    }
                    else if (status == Status.InCall)
                    {
                        Plugin.UnloadMediaStreamSource(localId);
                        Plugin.UnloadMediaStreamSource(remoteId);
                        status = Status.Connected;
                        commandQueue.Add(new Command { type = CommandType.SetConnected });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Conductor.OnPeerConnectionClosed() - wrong status - " + status);
                    }
                }
            });
        };

        // Ready to connect to the server event handler
        Conductor.Instance.OnReadyToConnect += () => { var task = RunOnUiThread(() => { }); };

        List<Conductor.IceServer> iceServers = new List<Conductor.IceServer>();
        iceServers.Add(new Conductor.IceServer { Host = "stun.l.google.com:19302", Type = Conductor.IceServer.ServerType.STUN });
        iceServers.Add(new Conductor.IceServer { Host = "stun1.l.google.com:19302", Type = Conductor.IceServer.ServerType.STUN });
        iceServers.Add(new Conductor.IceServer { Host = "stun2.l.google.com:19302", Type = Conductor.IceServer.ServerType.STUN });
        iceServers.Add(new Conductor.IceServer { Host = "stun3.l.google.com:19302", Type = Conductor.IceServer.ServerType.STUN });
        iceServers.Add(new Conductor.IceServer { Host = "stun4.l.google.com:19302", Type = Conductor.IceServer.ServerType.STUN });
        Conductor.IceServer turnServer = new Conductor.IceServer { Host = "turnserver3dstreaming.centralus.cloudapp.azure.com:5349", Type = Conductor.IceServer.ServerType.TURN };
        turnServer.Credential = "3Dtoolkit072017";
        turnServer.Username = "user";
        iceServers.Add(turnServer);
        Conductor.Instance.ConfigureIceServers(iceServers);

        var audioCodecList = Conductor.Instance.GetAudioCodecs();
        Conductor.Instance.AudioCodec = audioCodecList.FirstOrDefault(c => c.Name == "opus");
        System.Diagnostics.Debug.WriteLine("Selected audio codec - " + Conductor.Instance.AudioCodec.Name);

        var videoCodecList = Conductor.Instance.GetVideoCodecs();
        Conductor.Instance.VideoCodec = videoCodecList.FirstOrDefault(c => c.Name == PreferredVideoCodec);

        UnityEngine.WSA.Application.InvokeOnAppThread(() =>
        {
            //Set property on UI thread
            PreferredCodecLabel.text = Conductor.Instance.VideoCodec.Name;
        }, false);

        System.Diagnostics.Debug.WriteLine("Selected video codec - " + Conductor.Instance.VideoCodec.Name);

        uint preferredWidth = 896;
        uint preferredHeght = 504;
        uint preferredFrameRate = 15;
        uint minSizeDiff = uint.MaxValue;
        Conductor.CaptureCapability selectedCapability = null;
        var videoDeviceList = Conductor.Instance.GetVideoCaptureDevices();
        foreach (Conductor.MediaDevice device in videoDeviceList)
        {
            Conductor.Instance.GetVideoCaptureCapabilities(device.Id).AsTask().ContinueWith(capabilities =>
            {
                foreach (Conductor.CaptureCapability capability in capabilities.Result)
                {
                    uint sizeDiff = (uint)Math.Abs(preferredWidth - capability.Width) + (uint)Math.Abs(preferredHeght - capability.Height);
                    if (sizeDiff < minSizeDiff)
                    {
                        selectedCapability = capability;
                        minSizeDiff = sizeDiff;
                    }
                    System.Diagnostics.Debug.WriteLine("Video device capability - " + device.Name + " - " + capability.Width + "x" + capability.Height + "@" + capability.FrameRate);
                }
            }).Wait();
        }

        if (selectedCapability != null)
        {
            selectedCapability.FrameRate = preferredFrameRate;
            Conductor.Instance.VideoCaptureProfile = selectedCapability;
            Conductor.Instance.UpdatePreferredFrameFormat();
            System.Diagnostics.Debug.WriteLine("Selected video device capability - " + selectedCapability.Width + "x" + selectedCapability.Height + "@" + selectedCapability.FrameRate);
        }

#endif
    }

    private void Conductor_OnAddRemoteStream(string remotePeerName)
    {
        var remoteId = SourceIDs[remotePeerName];

#if !UNITY_EDITOR
        var task = RunOnUiThread(() =>
        {
            lock (this)
            {
                if (status == Status.InCall)
                {
                    IMediaSource source;
                    if (Conductor.Instance.VideoCodec.Name == "H264")
                        source = Conductor.Instance.CreateRemoteMediaStreamSource(remotePeerName, "H264");
                    else
                        source = Conductor.Instance.CreateRemoteMediaStreamSource(remotePeerName, "I420");
                    Plugin.LoadMediaStreamSource(remoteId, (MediaStreamSource)source);
                }
                else if (status == Status.Connected)
                {
                    IMediaSource source;
                    if (Conductor.Instance.VideoCodec.Name == "H264")
                        source = Conductor.Instance.CreateRemoteMediaStreamSource(remotePeerName, "H264");
                    else
                        source = Conductor.Instance.CreateRemoteMediaStreamSource(remotePeerName, "I420");
                    Plugin.LoadMediaStreamSource(remoteId, (MediaStreamSource)source);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Conductor.OnAddRemoteStream() - wrong status - " + status);
                }
            }
        });
#endif
    }

    private void Conductor_OnRemoveRemoteStream(string peerName)
    {
#if !UNITY_EDITOR
        var task = RunOnUiThread(() =>
        {
            lock (this)
            {
                if (status == Status.InCall)
                {
                }
                else if (status == Status.Connected)
                {
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Conductor.OnRemoveRemoteStream() - wrong status - " + status);
                }
            }
        });
#endif
    }

    private void Conductor_OnAddLocalStream()
    {
        var localId = SourceIDs[LocalName];

#if !UNITY_EDITOR
        var task = RunOnUiThread(() =>
        {
            lock (this)
            {
                if (status == Status.InCall)
                {
                    var source = Conductor.Instance.CreateLocalMediaStreamSource("I420");
                    Plugin.LoadMediaStreamSource(localId, (MediaStreamSource)source);

                    Conductor.Instance.EnableLocalVideoStream();
                    Conductor.Instance.UnmuteMicrophone();
                }
                else if (status == Status.Connected)
                {
                    var source = Conductor.Instance.CreateLocalMediaStreamSource("I420");
                    Plugin.LoadMediaStreamSource(localId, (MediaStreamSource)source);

                    Conductor.Instance.EnableLocalVideoStream();
                    Conductor.Instance.UnmuteMicrophone();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Conductor.OnAddLocalStream() - wrong status - " + status);
                }
            }
        });
#endif
    }

    private static class Plugin
    {
        [DllImport("MediaEngineUWP", CallingConvention = CallingConvention.StdCall, EntryPoint = "CreateMediaPlayback")]
        internal static extern void CreateMediaPlayback(UInt32 id);

        [DllImport("MediaEngineUWP", CallingConvention = CallingConvention.StdCall, EntryPoint = "ReleaseMediaPlayback")]
        internal static extern void ReleaseMediaPlayback(UInt32 id);

        [DllImport("MediaEngineUWP", CallingConvention = CallingConvention.StdCall, EntryPoint = "GetPrimaryTexture")]
        internal static extern void GetPrimaryTexture(UInt32 id, UInt32 width, UInt32 height, out System.IntPtr playbackTexture);

#if !UNITY_EDITOR
        [DllImport("MediaEngineUWP", CallingConvention = CallingConvention.StdCall, EntryPoint = "LoadMediaStreamSource")]
        internal static extern void LoadMediaStreamSource(UInt32 id, MediaStreamSource IMediaSourceHandler);

        [DllImport("MediaEngineUWP", CallingConvention = CallingConvention.StdCall, EntryPoint = "UnloadMediaStreamSource")]
        internal static extern void UnloadMediaStreamSource(UInt32 id);
#endif

        [DllImport("MediaEngineUWP", CallingConvention = CallingConvention.StdCall, EntryPoint = "Play")]
        internal static extern void Play(UInt32 id);

        [DllImport("MediaEngineUWP", CallingConvention = CallingConvention.StdCall, EntryPoint = "Pause")]
        internal static extern void Pause(UInt32 id);
    }
}
