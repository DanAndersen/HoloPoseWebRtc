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
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace HoloPoseClient.Signalling
{
    public delegate void SignedInDelegate();
    public delegate void DisconnectedDelegate();
    public delegate void PeerConnectedDelegate(int id, string name);
    public delegate void PeerDisconnectedDelegate(int peer_id);
    public delegate void PeerHangupDelegate(int peer_id);
    public delegate void MessageFromPeerDelegate(int peer_id, string message);
    public delegate void MessageSentDelegate(int err);
    public delegate void ServerConnectionFailureDelegate();

    /// <summary>
    /// Class providing helper functions for parsing responses and messages.
    /// </summary>
    public static class Extensions
    {
        public static async void WriteStringAsync(this StreamSocket socket, string str)
        {
            try
            {
                var writer = new DataWriter(socket.OutputStream);
                writer.WriteString(str);
                await writer.StoreAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Error] Singnaling: Couldn't write to socket : " + ex.Message);
            }
        }

        public static int ParseLeadingInt(this string str)
        {
            return int.Parse(Regex.Match(str, "\\d+").Value);
        }
    }
}
