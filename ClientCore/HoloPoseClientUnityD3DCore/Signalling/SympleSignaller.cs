using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace HoloPoseClientCore.Signalling
{
    public class SympleSignaller : Signaller
    {
        public override void Connect(string server, string port, string client_name)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> SendToPeer(int peerId, string message)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> SendToPeer(int peerId, IJsonValue json)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> SignOut()
        {
            throw new NotImplementedException();
        }
    }
}
