using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;

using Quobject.SocketIoClientDotNet.Client;
using Windows.Networking;
using SympleRtcCore;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace HoloPoseClientCore.Signalling
{
    public delegate void DispatchConnectDelegate();
    public delegate void DispatchConnectingDelegate();
    public delegate void DispatchReconnectingDelegate();
    public delegate void DispatchConnectFailedDelegate();
    public delegate void DispatchDisconnectDelegate();
    public delegate void DispatchAnnounceDelegate(JObject res);
    public delegate void DispatchErrorDelegate(string error, string message);

    public class SympleSignaller : Signaller
    {
        public event DispatchConnectDelegate DispatchConnect;
        public event DispatchConnectingDelegate DispatchConnecting;
        public event DispatchReconnectingDelegate DispatchReconnecting;
        public event DispatchConnectFailedDelegate DispatchConnectFailed;
        public event DispatchDisconnectDelegate DispatchDisconnect;
        public event DispatchAnnounceDelegate DispatchAnnounce;
        public event DispatchErrorDelegate DispatchError;



        private string _url;

        private Socket _socket;

        public SympleSignaller() : base()
        {
            // Annoying but register empty handlers
            // so we don't have to check for null everywhere
            DispatchConnect += () => { };
            DispatchConnecting += () => { };
            DispatchReconnecting += () => { };
            DispatchConnectFailed += () => { };
            DispatchDisconnect += () => { };
            DispatchAnnounce += (a) => { };
            DispatchError += (a, b) => { };


            // comment these out if not needed
            Messenger.AddListener<string>(SympleLog.LogTrace, OnLog);
            Messenger.AddListener<string>(SympleLog.LogDebug, OnLog);
            Messenger.AddListener<string>(SympleLog.LogInfo, OnLog);
            Messenger.AddListener<string>(SympleLog.LogError, OnLog);
        }

        private void OnLog(string msg)
        {
            Debug.WriteLine(msg);
        }

        public override void Connect(string server, string port, string client_name)
        {
            Debug.WriteLine("SympleSignaller: Connect " + server + " " + port + " " + client_name);
            try
            {
                if (_state != State.NOT_CONNECTED)
                {
                    RaiseOnServerConnectionFailure();
                    return;
                }

                _url = server;

                Debug.WriteLine("TODO: ignoring/hardcoding the port");
                
                _clientName = client_name;

                _state = State.SIGNING_IN;

                LoginToSignallingServer();
                
                /*
                if (_state == State.CONNECTED)
                {
                    // Start the long polling loop without await
                    //StartAsyncLongPollingLoop();
                    Debug.WriteLine("TODO: this is where we would do the equivalent of the long polling loop");
                }
                else
                {
                    _state = State.NOT_CONNECTED;
                    RaiseOnServerConnectionFailure();
                }
                */
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Error] Signaling: Failed to connect to server: " + ex.Message);
            }
        }

        private void LoginToSignallingServer()
        {
            Messenger.Broadcast(SympleLog.LogInfo, "symple:client: connecting");

            if (_socket != null)
            {
                string err = "the client socket is not null";
                Messenger.Broadcast(SympleLog.LogError, err);
                throw new Exception(err);
            }

            Uri socketUri = new Uri(_url);
            
            string options_url_string = _url;
            bool options_secure = (options_url_string != null && (options_url_string.IndexOf("https") == 0 || options_url_string.IndexOf("wss") == 0));

            IO.Options ioOptions = new IO.Options();
            ioOptions.Secure = options_secure;
            ioOptions.Port = socketUri.Port;
            ioOptions.Hostname = socketUri.Host;
            ioOptions.IgnoreServerCertificateValidation = true;

            _socket = IO.Socket(options_url_string, ioOptions);
            _socket.On(Socket.EVENT_CONNECT, () =>
            {
                Messenger.Broadcast(SympleLog.LogInfo, "symple:client: connected");
                _state = State.CONNECTED;
                
                JObject announceData = new JObject();
                announceData["user"] = _clientName;
                announceData["name"] = _clientName;
                announceData["type"] = "";
                announceData["token"] = "";

                string announceDataJsonString = JsonConvert.SerializeObject(announceData, Formatting.None);
                Messenger.Broadcast(SympleLog.LogTrace, "announceDataJsonString: " + announceDataJsonString);
                
                _socket.Emit("announce", (resObj) => {
                    JObject res = (JObject)resObj;
                    OnAnnounced(res);
                    
                }, announceData);
            });

            _socket.On(Socket.EVENT_ERROR, () =>
            {
                // this is triggered when any transport fails, so not necessarily fatal
                DispatchConnect();
            });

            _socket.On("connecting", () =>
            {
                Messenger.Broadcast(SympleLog.LogDebug, "symple:client: connecting");
                DispatchConnecting();
            });

            _socket.On(Socket.EVENT_RECONNECTING, () =>
            {
                Messenger.Broadcast(SympleLog.LogDebug, "symple:client: connecting");
                Messenger.Broadcast(SympleLog.Reconnecting);
                DispatchReconnecting();
            });

            _socket.On("connect_failed", () =>
            {
                // called when all transports fail
                Messenger.Broadcast(SympleLog.LogError, "symple:client: connect failed");
                Messenger.Broadcast(SympleLog.ConnectFailed);
                DispatchConnectFailed();
                this.setError("connect");
            });

            _socket.On(Socket.EVENT_DISCONNECT, (data) =>
            {
                try
                {
                    Messenger.Broadcast(SympleLog.LogInfo, "symple:client: disconnect");

                    string msg = (string)data;
                    Messenger.Broadcast(SympleLog.LogInfo, "symple:client: disconnect msg: " + msg);

                    Messenger.Broadcast(SympleLog.Disconnect);


                    Debug.WriteLine("TODO: mark peer[online] to false");

                    DispatchDisconnect();
                }
                catch (Exception e)
                {
                    Messenger.Broadcast(SympleLog.LogInfo, "caught exception: " + e.Message);
                }
            });



            var foo = _socket.Connect();
        }

        private void OnAnnounced(JObject res)
        {
            if ((int)res["status"] != 200)
            {
                this.setError("auth", res.ToString());
                return;
            }

            JObject resData = (JObject)res["data"];

            Debug.WriteLine("do peer roster stuff, etc after getting the res result");
        }


        // sets the client to an error state and disconnects
        private async void setError(string error, string message = null)
        {
            Messenger.Broadcast(SympleLog.LogError, "symple:client: fatal error " + error + " " + message);

            DispatchError(error, message);
            await SignOut();
        }






        public override Task<bool> SendToPeer(int peerId, string message)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> SendToPeer(int peerId, IJsonValue json)
        {
            throw new NotImplementedException();
        }

        public override async Task<bool> SignOut()
        {
            if (_state == State.NOT_CONNECTED || _state == State.SIGNING_OUT)
                return true;

            // In original: we teardown the hanging get socket. In our version: do nothing.

            _state = State.SIGNING_OUT;

            if (_myId != -1)
            {
                // In original, send a GET sign_out. In ours...
                if (_socket != null)
                {
                    _socket.Disconnect();
                    _socket.Close();
                    _socket = null;
                }

                Close();
                RaiseOnDisconnected();
            }
            else
            {
                // Can occur if the app is closed before we finish connecting
                return true;
            }

            _myId = -1;
            _state = State.NOT_CONNECTED;
            return true;
        }

        /// <summary>
        /// Resets the states after connection is closed.
        /// </summary>
        private void Close()
        {
            _peers.Clear();
            _state = State.NOT_CONNECTED;
        }
    }
}
