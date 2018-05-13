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
    public class SympleSignaller : Signaller
    {
        
        private class PeerData
        {
            public string serverId; // "id" given by the socket server
            public string user; // username
            public string name; // display name
            public int localIntId; // an int version of the id, used only for interaction with Conductor (never sent to other peers).
            public string type;
            public string token;
            public bool online;

            public PeerData()
            {
                localIntId = -1;
                type = "";
                token = "";
                online = false;
            }
        }

        private PeerData _myPeerData;

        private Dictionary<string, PeerData> _roster; // maps serverIds to PeerData
        private Dictionary<int, string> _localIntIdsToServerIds; // maps local int ids to server ids


        private void SignallerOnMessage(string mType, JObject m)
        {
            Debug.WriteLine("SignallerOnMessage " + mType + " " + m);

            // here we process what the webrtccontext would normally do when a new message comes in.
            // so we adjust the message and pass it on to the Conductor.

            if (m["offer"] != null)
            {
                Debug.WriteLine("TODO: incoming offer message");
                throw new NotImplementedException();
            } else if (m["answer"] != null)
            {
                Debug.WriteLine("TODO: incoming answer message");
                throw new NotImplementedException();
            }
            else if (m["candidate"] != null)
            {
                Debug.WriteLine("TODO: incoming candidate message");
                throw new NotImplementedException();
            } else
            {
                // the content of the message is unrecognized by the signaller and should be passed along as an incoming message
                Debug.WriteLine("TODO: incoming misc message of type " + m["type"]);

                // this can include things like "presence" messages which were already handled earlier

            }
        }


        private Socket _socket;

        public SympleSignaller() : base()
        {
            _myPeerData = new PeerData();

            _roster = new Dictionary<string, PeerData>();
            _localIntIdsToServerIds = new Dictionary<int, string>();

            // comment these out if not needed
            Messenger.AddListener<string>(SympleLog.LogTrace, OnLog);
            Messenger.AddListener<string>(SympleLog.LogDebug, OnLog);
            Messenger.AddListener<string>(SympleLog.LogInfo, OnLog);
            Messenger.AddListener<string>(SympleLog.LogError, OnLog);
        }

        public override async void Connect(string server, string port, string client_name)
        {
            Debug.WriteLine("SympleSignaller:Connect: " + server + " " + port + " " + client_name);

            if (_socket != null)
            {
                string err = "the client socket is not null";
                Messenger.Broadcast(SympleLog.LogError, err);
                throw new Exception(err);
            }

            _myPeerData.user = client_name;
            _myPeerData.name = client_name;

            string fullUrl = server + ":" + port;
            bool options_secure = (fullUrl != null && (fullUrl.IndexOf("https") == 0 || fullUrl.IndexOf("wss") == 0));
            Uri socketUri = new Uri(fullUrl);

            var ioOptions = new IO.Options();
            ioOptions.Secure = options_secure;
            ioOptions.Port = socketUri.Port;
            ioOptions.Hostname = socketUri.Host;
            ioOptions.IgnoreServerCertificateValidation = true;
            
            _socket = IO.Socket(fullUrl, ioOptions);
            _socket.On(Socket.EVENT_CONNECT, SocketOnConnect);
            _socket.On(Socket.EVENT_ERROR, SocketOnError);
            _socket.On("connecting", SocketOnConnecting);
            _socket.On(Socket.EVENT_RECONNECTING, SocketOnReconnecting);
            _socket.On("connect_failed", SocketOnConnectFailed);
            _socket.On(Socket.EVENT_DISCONNECT, SocketOnDisconnect);

            Debug.WriteLine("SympleSignaller:Connect: finished setting up socket");
        }

        





        #region Socket Callbacks
        private void SocketOnConnect()
        {
            Debug.WriteLine("SocketOnConnect");
            // connected to socket, but need to now announce self onto server

            JObject announceData = new JObject();
            announceData["user"] = _myPeerData.user;
            announceData["name"] = _myPeerData.name;
            announceData["type"] = _myPeerData.type;
            announceData["token"] = _myPeerData.token;

            Debug.WriteLine("announceData: " + announceData.ToString());

            _socket.Emit("announce", SocketOnAnnounced, announceData);
        }

        private void SocketOnAnnounced(object resObj)
        {
            Debug.WriteLine("TODO: SocketOnAnnounced");

            JObject res = (JObject)resObj;

            if ((int)res["status"] != 200)
            {
                // authentication error
                RaiseOnServerConnectionFailure();
                return;
            }

            JObject myPeerData = (JObject)res["data"];
            
            Debug.WriteLine("myPeerData: " + myPeerData.ToString());

            string serverId = (string)myPeerData["id"];
            addOrUpdatePeerToRoster(myPeerData);
            _myPeerData = _roster[serverId];
            


            JObject sendPresenceParams = new JObject();
            sendPresenceParams["probe"] = true;
            this.sendPresence(sendPresenceParams);

            _socket.On(Socket.EVENT_MESSAGE, SocketOnMessage);

            RaiseOnSignedIn();
        }

        private void SocketOnMessage(object msg)
        {
            Messenger.Broadcast(SympleLog.LogTrace, "SocketOnMessage: " + msg);

            JObject m = (JObject)msg;

            string mType = (string)m["type"];

            switch (mType)
            {
                case "message":
                    break;
                case "command":
                    break;
                case "event":
                    break;
                case "presence":

                    JObject remotePeerData = (JObject)m["data"]; // data about another peer that has been updated or removed

                    bool remotePeerOnline = (bool)remotePeerData["online"];

                    if (remotePeerOnline)
                    {
                        addOrUpdatePeerToRoster(remotePeerData);
                    } else
                    {
                        string remotePeerServerId = (string)remotePeerData["id"];
                        removePeerFromRoster(remotePeerServerId);
                    }

                    if (m["probe"] != null && (bool)m["probe"] == true)
                    {
                        JObject presenceTo = new JObject();
                        presenceTo["to"] = Symple.parseAddress(m["from"].ToString())["id"];

                        this.sendPresence(presenceTo);
                    }
                    break;
                default:
                    m["type"] = m["type"] ?? "message";
                    break;
            }

            if (m["from"].Type != JTokenType.String)
            {
                Messenger.Broadcast(SympleLog.LogError, "symple:client: invalid sender address: " + m);
                return;
            }

            // replace the from attribute with the full peer object.
            // this will only work for peer messages, not server messages.

            string mFrom = (string)m["from"];
            Messenger.Broadcast(SympleLog.LogTrace, "looking up rpeer in roster, mFrom = " + mFrom + "...");

            var addr = Symple.parseAddress(mFrom);
            Debug.WriteLine("addr " + addr);
            mFrom = (string)addr["id"] ?? mFrom;
            
            if (_roster.ContainsKey(mFrom))
            {
                PeerData rpeerData = _roster[mFrom];

                Messenger.Broadcast(SympleLog.LogTrace, "found rpeerData: " + rpeerData);
                m["from"] = peerDataToJObject(rpeerData);
            }
            else
            {
                Messenger.Broadcast(SympleLog.LogDebug, "symple:client: got message from unknown peer: " + m);
            }

            // Dispatch to the application
            SignallerOnMessage(mType, m);
        }

        private void removePeerFromRoster(string peerServerId)
        {
            Debug.WriteLine("removePeerFromRoster " + peerServerId);

            var addr = Symple.parseAddress(peerServerId);

            Debug.WriteLine("addr " + addr);

            peerServerId = (string)addr["id"] ?? peerServerId;

            if (_roster.ContainsKey(peerServerId))
            {
                var peer = _roster[peerServerId];
                int peerLocalIntId = peer.localIntId;

                _roster.Remove(peerServerId);
                _localIntIdsToServerIds.Remove(peerLocalIntId);

                RaiseOnPeerDisconnected(peerLocalIntId);
            } else
            {
                Debug.WriteLine("could not find peerServerId " + peerServerId + " in roster");
            }
        }

        private void addOrUpdatePeerToRoster(JObject peerData)
        {
            Debug.WriteLine("addOrUpdatePeerToRoster " + peerData);

            string serverId = (string)peerData["id"];

            PeerData peer = null;

            if (_roster.ContainsKey(serverId))
            {
                peer = _roster[serverId];
            } else
            {
                peer = new PeerData();
            }
            
            peer.name = (string)peerData["name"];
            peer.localIntId = serverId.GetHashCode();
            peer.online = (bool)peerData["online"];
            peer.serverId = (string)peerData["id"];
            peer.token = (string)peerData["token"];
            peer.type = (string)peerData["type"];
            peer.user = (string)peerData["user"];

            if (!_roster.ContainsKey(serverId))
            {
                _roster.Add(peer.serverId, peer);
                _localIntIdsToServerIds.Add(peer.localIntId, peer.serverId);

                RaiseOnPeerConnected(peer.localIntId, peer.user);
            }
        }

        private void sendPresence(JObject p)
        {
            p = p ?? new JObject();
            if (p["data"] != null)
            {
                JObject pDataObj = (JObject)p["data"];
                p["data"] = Symple.merge(peerDataToJObject(_myPeerData), pDataObj);
            }
            else
            {
                p["data"] = peerDataToJObject(_myPeerData);
            }

            p["type"] = "presence";

            this.send(p);
        }

        private JObject peerDataToJObject(PeerData pd)
        {
            JObject p = new JObject();
            p["user"] = pd.user;
            p["name"] = pd.name;
            p["type"] = pd.type;
            p["token"] = pd.token;
            p["id"] = pd.serverId;
            p["online"] = pd.online;

            return p;
        }






        private void SocketOnError()
        {
            Debug.WriteLine("TODO: SocketOnError");
            throw new NotImplementedException();
        }

        private void SocketOnConnecting()
        {
            Debug.WriteLine("TODO: SocketOnConnecting");
            throw new NotImplementedException();
        }

        private void SocketOnDisconnect(object data)
        {
            Debug.WriteLine("SocketOnDisconnect");

            string msg = (string)data;
            Debug.WriteLine("Disconnect msg: " + msg);

            _myPeerData.online = false;
            RaiseOnDisconnected();
        }

        private void SocketOnConnectFailed()
        {
            Debug.WriteLine("TODO: SocketOnConnectFailed");
            throw new NotImplementedException();
        }

        private void SocketOnReconnecting()
        {
            Debug.WriteLine("TODO: SocketOnReconnecting");
            throw new NotImplementedException();
        }

        #endregion







        // send a message to the given peer
        // m = JSON object
        // to = either a string or a JSON object to build an address from
        public void send(JObject m, JToken to = null)
        {
            Debug.WriteLine("SympleSignaller:send: " + m + " " + to);

            if (!IsConnected())
            {
                throw new Exception("cannot send messages while offline"); // TODO: add to pending queue?
            }

            if (m.Type != JTokenType.Object)
            {
                throw new Exception("message must be an object");
            }

            if (m["type"].Type != JTokenType.String)
            {
                m["type"] = "message";
            }

            if (m["id"] == null)
            {
                m["id"] = Symple.randomString(8);
            }

            if (to != null)
            {
                m["to"] = to;
            }

            if (m["to"] != null && m["to"].Type == JTokenType.Object)
            {
                JObject mToObj = (JObject)m["to"];
                m["to"] = Symple.buildAddress(mToObj);
            }

            if (m["to"] != null && m["to"].Type != JTokenType.String)
            {
                throw new Exception("message 'to' attribute must be an address string");
            }

            m["from"] = buildAddress(_myPeerData);

            if (m["from"] == m["to"])
            {
                throw new Exception("message sender cannot match the recipient");
            }

            Messenger.Broadcast(SympleLog.LogTrace, "symple:client: sending" + m);

            _socket.Send(m);
        }

        private string buildAddress(PeerData pd)
        {
            return Symple.buildAddress(peerDataToJObject(pd));
        }













        public override bool IsConnected()
        {
            Debug.WriteLine("SympleSignaller:IsConnected");
            
            return _myPeerData.online;
        }

        public override async Task<bool> SendToPeer(int peerId, string message)
        {
            Debug.WriteLine("SympleSignaller:SendToPeer string: " + peerId + " " + message);
            
            throw new NotImplementedException();
        }

        public override async Task<bool> SendToPeer(int peerId, IJsonValue json)
        {
            Debug.WriteLine("SympleSignaller:SendToPeer json: " + peerId + " " + json);

            Debug.Assert(IsConnected());

            if (!IsConnected())
            {
                return false;
            }


            if (_localIntIdsToServerIds.ContainsKey(peerId))
            {
                string peerServerId = _localIntIdsToServerIds[peerId];

                PeerData recipientPeerData = _roster[peerServerId];

                Debug.WriteLine("original IJsonValue: " + json);

                JObject jsonMessage = JObject.Parse(json.Stringify());

                Debug.WriteLine("after conversion to JObject: " + jsonMessage);

                if (jsonMessage["sdp"] != null)
                {
                    JObject sessionDesc = jsonMessage;

                    JObject parameters = new JObject();
                    parameters["to"] = peerDataToJObject(recipientPeerData);
                    parameters["type"] = "message";
                    parameters["offer"] = sessionDesc;

                    send(parameters);
                    return true;
                }
                
                if (jsonMessage["candidate"] != null)
                {
                    JObject candidateObj = jsonMessage;

                    JObject parameters = new JObject();
                    parameters["to"] = peerDataToJObject(recipientPeerData);
                    parameters["type"] = "message";
                    parameters["candidate"] = candidateObj;

                    send(parameters);
                    return true;
                }




                Debug.WriteLine("unknown type of message " + jsonMessage);
                throw new NotImplementedException();
            }
            else
            {
                Debug.WriteLine("attempted SendToPeer on unknown peer id " + peerId);
                throw new NotImplementedException();
            }
        }

        public override async Task<bool> SignOut()
        {
            Debug.WriteLine("SympleSignaller:SignOut");

            if (_socket != null)
            {
                _socket.Disconnect();
                _socket.Close();
                _socket = null;
            }

            _roster.Clear();
            _localIntIdsToServerIds.Clear();
            _myPeerData = new PeerData();

            return true;
        }





        private void OnLog(string msg)
        {
            Debug.WriteLine(msg);
        }
    }
}