﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Features.Network
{
    public abstract class PacketSession : Session
    {
        public static readonly int HeaderSize = 4;

        public sealed override int OnRecv(ArraySegment<byte> buffer)
        {
            int processLen = 0;
            int packetCount = 0;

            while(true)
            {
                if (buffer.Count < HeaderSize)
                    break;

                ushort dataSize = BitConverter.ToUInt16(buffer.Array!, buffer.Offset + 2);
                if (buffer.Count < dataSize)
                    break;

                OnRecvPacket(new ArraySegment<byte>(buffer.Array!, buffer.Offset, dataSize));
                packetCount++;

                processLen += dataSize;
                buffer = new ArraySegment<byte>(buffer.Array!, buffer.Offset + dataSize, buffer.Count - dataSize);
            }

            if(packetCount > 1)
                Console.WriteLine($"패킷 모아보내기 : {packetCount}");

            return processLen;
        }

        public abstract void OnRecvPacket(ArraySegment<byte> buffer);
    }

    public abstract class Session
    {
        Socket _socket = null!;
        int _disconnected = 0;

        RecvBuffer _recvBuffer = new RecvBuffer(65535);

        object _lock = new object();
        Queue<ArraySegment<byte>> _sendQueue = new Queue<ArraySegment<byte>>();
        List<ArraySegment<byte>> _pendingList = new List<ArraySegment<byte>>();
        SocketAsyncEventArgs _sendArgs = new SocketAsyncEventArgs();
        SocketAsyncEventArgs _recvArgs = new SocketAsyncEventArgs();

        public abstract void OnConnected(EndPoint endPoint);
        public abstract int OnRecv(ArraySegment<byte> buffer);
        public abstract void OnSend(int numOfBytes);
        public abstract void OnDisconnected(EndPoint endPoint);
        
        void Clear()
        {
            lock(_lock)
            {
                _socket = null!;
                _disconnected = 0;
                _recvBuffer = new RecvBuffer(65535);
                _sendQueue.Clear();
                _pendingList.Clear();
                _sendArgs = new SocketAsyncEventArgs();
                _recvArgs = new SocketAsyncEventArgs();
            }
        }

        public void Start(Socket socket)
        {
            _socket = socket;

            _recvArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnRecvCompleted!);
            _recvArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnSendCompleted!);

            RegisterRecv();
        }

        public void Send(List<ArraySegment<byte>> sendBuffList)
        {
            if (sendBuffList.Count == 0)
                return;

            lock(_lock)
            {
                foreach(ArraySegment<byte> sendBuff in sendBuffList)
                    _sendQueue.Enqueue(sendBuff);

                if (_pendingList.Count == 0)
                    RegisterSend();
            }
        }

        public void Send(ArraySegment<byte> sendBuff)
        {
            if(_socket != null)
            {
                lock(_lock)
                {
                    _sendQueue.Enqueue(sendBuff);
                    if (_pendingList.Count == 0)
                        RegisterSend();
                }
            }
        }

        public void Disconnect()
        {
            if (Interlocked.Exchange(ref _disconnected, 1) == 1)
                return;

            _socket.Shutdown(SocketShutdown.Both);
            _socket.Close();
            Clear();
            OnDisconnected(null!);
        }

        #region 네트워크 통신

        void RegisterSend()
        {
            if (_disconnected == 1)
                return;

            while(_sendQueue.Count > 0)
            {
                ArraySegment<byte> buff = _sendQueue.Dequeue();
                _pendingList.Add(buff);
            }
            _sendArgs.BufferList = _pendingList;

            try
            {
                if(_socket == null)
                {
                    Console.WriteLine("_socket is null");
                }
                else
                {
                    bool pending = _socket.SendAsync(_sendArgs);
                    if(pending == false)
                    {
                        OnSendCompleted(null, _sendArgs);
                    }
                }
            }
            catch(Exception e)
            {
                Console.WriteLine($"RegisterSend Failed {e}");
            }
        }

        void OnSendCompleted(object sender, SocketAsyncEventArgs args)
        {
            lock(_lock)
            {
                if(args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
                {
                    try
                    {
                        _sendArgs.BufferList = null;
                        _pendingList.Clear();

                        OnSend(_sendArgs.BytesTransferred);

                        if (_sendQueue.Count > 0)
                            RegisterSend();
                    }
                    catch(Exception e)
                    {
                        Console.WriteLine($"OnSendCompleted Failed {e}");
                    }
                }
                else
                {
                    Disconnect();
                }
            }
        }

        void RegisterRecv()
        {
            if (_disconnected == 1)
                return;

            _recvBuffer.Clean();
            ArraySegment<byte> segment = _recvBuffer.WriteSegment;
            _recvArgs.SetBuffer(segment.Array, segment.Offset, segment.Count);

            try
            {
                bool pending = _socket.ReceiveAsync(_recvArgs);
                if (pending == false)
                    OnRecvCompleted(null!, _recvArgs);
            }
            catch(Exception e)
            {
                Console.WriteLine($"RegisterRecv Failed {e}");
            }
        }

        void OnRecvCompleted(object sender, SocketAsyncEventArgs args)
        {
            if(args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
            {
                try
                {
                    if(_recvBuffer.OnWrite(args.BytesTransferred) == false)
                    {
                        Disconnect();
                        return;
                    }

                    int processLen = OnRecv(_recvBuffer.ReadSegment);
                    if(processLen < 0 || _recvBuffer.DataSize < processLen)
                    {
                        Disconnect();
                        return;
                    }

                    if(_recvBuffer.OnRead(processLen) == false)
                    {
                        Disconnect();
                        return;
                    }

                    RegisterRecv();
                }
                catch(Exception e)
                {
                    Console.WriteLine($"OnRecvCompleted Failed {e}");
                }
            }
            else
            {
                Disconnect();
            }
        }

        #endregion
    }
}