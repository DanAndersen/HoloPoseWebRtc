using HoloPoseClient.Signalling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking;

namespace HoloPoseClientCore.Signalling
{
    public abstract class Signaller
    {
        // Connection events
        public event SignedInDelegate OnSignedIn;
        public event DisconnectedDelegate OnDisconnected;
        public event PeerConnectedDelegate OnPeerConnected;
        public event PeerDisconnectedDelegate OnPeerDisconnected;
        public event PeerHangupDelegate OnPeerHangup;
        public event MessageFromPeerDelegate OnMessageFromPeer;
        public event ServerConnectionFailureDelegate OnServerConnectionFailure;

        /// <summary>
        /// Creates an instance of a Signaller.
        /// </summary>
        public Signaller()
        {
            _state = State.NOT_CONNECTED;
            _myId = -1;

            // Annoying but register empty handlers
            // so we don't have to check for null everywhere
            OnSignedIn += () => { };
            OnDisconnected += () => { };
            OnPeerConnected += (a, b) => { };
            OnPeerDisconnected += (a) => { };
            OnMessageFromPeer += (a, b) => { };
            OnServerConnectionFailure += () => { };
        }

        /// <summary>
        /// The connection state.
        /// </summary>
        public enum State
        {
            NOT_CONNECTED,
            RESOLVING, // Note: State not used
            SIGNING_IN,
            CONNECTED,
            SIGNING_OUT_WAITING, // Note: State not used
            SIGNING_OUT,
        };
        protected State _state;

        protected HostName _server;
        protected string _port;
        protected string _clientName;
        protected int _myId;
        protected Dictionary<int, string> _peers = new Dictionary<int, string>();

        /// <summary>
        /// Checks if connected to the server.
        /// </summary>
        /// <returns>True if connected to the server.</returns>
        public bool IsConnected()
        {
            return _myId != -1;
        }

        /// <summary>
        /// Connects to the server.
        /// </summary>
        /// <param name="server">Host name/IP.</param>
        /// <param name="port">Port to connect.</param>
        /// <param name="client_name">Client name.</param>
        public abstract void Connect(string server, string port, string client_name);
        
        /// <summary>
        /// Disconnects the user from the server.
        /// </summary>
        /// <returns>True if the user is disconnected from the server.</returns>
        public abstract Task<bool> SignOut();

        /// <summary>
        /// Sends a message to a peer.
        /// </summary>
        /// <param name="peerId">ID of the peer to send a message to.</param>
        /// <param name="message">Message to send.</param>
        /// <returns>True if the message was sent.</returns>
        public abstract Task<bool> SendToPeer(int peerId, string message);

        /// <summary>
        /// Sends a message to a peer.
        /// </summary>
        /// <param name="peerId">ID of the peer to send a message to.</param>
        /// <param name="json">The json message.</param>
        /// <returns>True if the message is sent.</returns>
        public abstract Task<bool> SendToPeer(int peerId, IJsonValue json);



        // The event-invoking methods that derived classes can override.
        // Note: we don't have to check for null here because we initialized all events in the constructor
        protected void RaiseOnSignedIn()
        {
            OnSignedIn();
        }
        protected void RaiseOnDisconnected()
        {
            OnDisconnected();
        }
        protected void RaiseOnPeerConnected(int id, string name)
        {
            OnPeerConnected(id, name);
        }
        protected void RaiseOnPeerDisconnected(int peer_id)
        {
            OnPeerDisconnected(peer_id);
        }
        protected void RaiseOnPeerHangup(int peer_id)
        {
            OnPeerHangup(peer_id);
        }
        protected void RaiseOnMessageFromPeer(int peer_id, string message)
        {
            OnMessageFromPeer(peer_id, message);
        }
        protected void RaiseOnServerConnectionFailure()
        {
            OnServerConnectionFailure();
        }


    }
}
