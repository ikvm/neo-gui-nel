﻿using Neo.IO;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.Network
{
    internal class TcpRemoteNode : RemoteNode
    {
        private Socket socket;
        private NetworkStream stream;
        private bool connected = false;
        private int disposed = 0;

        public TcpRemoteNode(LocalNode localNode, IPEndPoint remoteEndpoint)
            : base(localNode)
        {
            this.socket = new Socket(remoteEndpoint.Address.IsIPv4MappedToIPv6 ? AddressFamily.InterNetwork : remoteEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            this.ListenerEndpoint = remoteEndpoint;
        }

        public TcpRemoteNode(LocalNode localNode, Socket socket)
            : base(localNode)
        {
            this.socket = socket;
            OnConnectedListener();//別人連我
        }

        public async Task<bool> ConnectAsync()
        {
            IPAddress address = ListenerEndpoint.Address;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            try
            {
                await socket.ConnectAsync(address, ListenerEndpoint.Port);
                OnConnected();//我連別人
            }
            catch (SocketException e)
            {
                Disconnect(false);
                return false;
            }
            return true;
        }

        public override void Disconnect(bool error)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                if (stream != null) stream.Dispose();
                socket.Dispose();
                base.Disconnect(error);
            }
        }

        private void OnConnected()
        {
            //发现在我的电脑上 socket.RemoteEndPoint 会抛出异常
            //IPEndPoint remoteEndpoint = (IPEndPoint)socket.RemoteEndPoint;
            //RemoteEndpoint = new IPEndPoint(remoteEndpoint.Address.MapToIPv6(), remoteEndpoint.Port);
            this.RemoteEndpoint = new IPEndPoint(ListenerEndpoint.Address.MapToIPv6(), ListenerEndpoint.Port);
            stream = new NetworkStream(socket);
            connected = true;
        }
        private void OnConnectedListener()
        {
            IPEndPoint remoteEndpoint = (IPEndPoint)socket.RemoteEndPoint;
            RemoteEndpoint = new IPEndPoint(remoteEndpoint.Address.MapToIPv6(), remoteEndpoint.Port);
            this.RemoteEndpoint = new IPEndPoint(RemoteEndpoint.Address.MapToIPv6(), RemoteEndpoint.Port);
            stream = new NetworkStream(socket);
            connected = true;
        }

        protected override async Task<Message> ReceiveMessageAsync(TimeSpan timeout)
        {
            CancellationTokenSource source = new CancellationTokenSource(timeout);
            //Stream.ReadAsync doesn't support CancellationToken
            //see: https://stackoverflow.com/questions/20131434/cancel-networkstream-readasync-using-tcplistener
            source.Token.Register(() => Disconnect(false));
            try
            {
                return await Message.DeserializeFromAsync(stream, source.Token);
            }
            catch (ArgumentException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is OperationCanceledException)
            {
                Disconnect(false);
            }
            finally
            {
                source.Dispose();
            }
            return null;
        }

        protected override async Task<bool> SendMessageAsync(Message message)
        {
            if (!connected) throw new InvalidOperationException();
            if (disposed > 0) return false;
            byte[] buffer = message.ToArray();
            CancellationTokenSource source = new CancellationTokenSource(10000);
            //Stream.WriteAsync doesn't support CancellationToken
            //see: https://stackoverflow.com/questions/20131434/cancel-networkstream-readasync-using-tcplistener
            source.Token.Register(() => Disconnect(false));
            try
            {
                await stream.WriteAsync(buffer, 0, buffer.Length, source.Token);
                return true;
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException)
            {
                Disconnect(false);
            }
            finally
            {
                source.Dispose();
            }
            return false;
        }
    }
}