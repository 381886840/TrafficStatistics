﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace TrafficStatistics.Relay
{
    public class UDPPipe
    {
        private IRelay _relay;
        private EndPoint _localEP;
        private EndPoint _remoteEP;

        Hashtable _handlers = Hashtable.Synchronized(new Hashtable());

        System.Timers.Timer _timer = new System.Timers.Timer(10000);

        public UDPPipe(IRelay relay, EndPoint localEP, EndPoint remoteEP)
        {
            _relay = relay;
            _localEP = localEP;
            _remoteEP = remoteEP;
            _timer.AutoReset = true;
            _timer.Enabled = true;
            _timer.Elapsed += _timer_Elapsed;
            _timer.Start();
        }

        ~UDPPipe()
        {
            _timer.Stop();
            _handlers.Clear();
        }

        public bool CreatePipe(byte[] firstPacket, int length, Socket fromSocket, EndPoint fromEP)
        {
            Handler handler = getHandler(fromEP, fromSocket);
            handler.Handle(firstPacket, length);
            return true;
        }

        Handler getHandler(EndPoint fromEP, Socket fromSocket)
        {
            string key = fromEP.ToString();
            lock(this)
            {
                if (_handlers.ContainsKey(key))
                {
                    return (Handler)_handlers[key];
                }
                Handler handler = new Handler(_relay);
                _handlers.Add(key, handler);
                handler._local = fromSocket;
                handler._localEP = fromEP;
                handler._remoteEP = _remoteEP;
                handler.OnClose += handler_OnClose;
                handler.Start();
                return handler;
            }
        }

        private void handler_OnClose(object sender, EventArgs e)
        {
            Handler handler = (Handler)sender;
            string key = handler._localEP.ToString();
            lock (this)
            {
                if (_handlers.ContainsKey(key))
                {
                    _handlers.Remove(key);
                }
            }
        }

        private void _timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                _timer.Stop();
                List<Handler> keys = new List<Handler>(_handlers.Count);
                lock (this)
                {
                    foreach (string key in _handlers.Keys)
                    {
                        Handler handler = (Handler)_handlers[key];
                        if (handler.IsExpire())
                        {
                            keys.Add(handler);
                        }
                    }
                    foreach (Handler handler in keys)
                    {
                        string key = handler._localEP.ToString();
                        handler.Close(false);
                        _handlers.Remove(key);
                    }
                }
            }
            catch (Exception ex)
            {
                _relay.onError(new RelayErrorEventArgs(ex));
            }
            finally
            {
                _timer.Start();
            }
        }

        class Handler
        {
            public event EventHandler OnClose;

            private DateTime _expires;

            public Socket _local;
            private IRelay _relay;
            public EndPoint _localEP;
            public EndPoint _remoteEP;
            public Socket _remote;

            private bool _closed = false;
            public const int RecvSize = 16384;
            // remote receive buffer
            private byte[] remoteRecvBuffer = new byte[RecvSize];

            private LinkedList<byte[]> _packages = new LinkedList<byte[]>();
            private bool _connected = false;
            private bool _sending = false;

            public Handler(IRelay relay)
            {
                _relay = relay;
            }

            public bool IsExpire()
            {
                lock(this)
                {
                    return _expires <= DateTime.Now;
                }
            }

            public void Start()
            {
                try
                {
                    _remote = new Socket(_remoteEP.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    _remote.SetSocketOption(SocketOptionLevel.Udp, SocketOptionName.NoDelay, true);
                    _remote.BeginConnect(_remoteEP, new AsyncCallback(remoteConnectCallback), null);
                    Delay();
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            public void Handle(byte[] buffer, int length)
            {
                if (_closed) return;
                try
                {
                    if (length > 0)
                    {
                        lock (_packages)
                        {
                            byte[] bytes = new byte[length];
                            Buffer.BlockCopy(buffer, 0, bytes, 0, length);
                            _packages.AddLast(bytes);
                        }
                        StartSend();
                    }
                    else
                    {
                        this.Close();
                    }
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            private void remoteConnectCallback(IAsyncResult ar)
            {
                if (_closed) return;
                try
                {
                    _remote.EndConnect(ar);
                    lock (_packages)
                        _connected = true;
                    StartPipe();
                    StartSend();
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            private void StartPipe()
            {
                if (_closed) return;
                try
                {
                    _remote.BeginReceive(this.remoteRecvBuffer, 0, RecvSize, 0,
                        new AsyncCallback(remoteReceiveCallback), null);
                    Delay();
                    StartSend();
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            private void StartSend()
            {
                if (_closed) return;
                try
                {
                    lock (_packages)
                    {
                        if (_sending || !_connected)
                            return;
                        if (_packages.Count > 0)
                        {
                            _sending = true;
                            byte[] bytes = _packages.First.Value;
                            _packages.RemoveFirst();
                            var e = new RelayEventArgs(bytes, 0, bytes.Length);
                            _relay.onInbound(e);
                            _remote.BeginSend(e.Buffer, e.Offset, e.Length, 0, new AsyncCallback(remoteSendCallback), null);
                            Delay();
                        }
                    }
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            private void remoteSendCallback(IAsyncResult ar)
            {
                if (_closed) return;
                try
                {
                    _remote.EndSend(ar);
                    lock (_packages)
                        _sending = false;
                    StartSend();
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            private void remoteReceiveCallback(IAsyncResult ar)
            {
                if (_closed) return;
                try
                {
                    int bytesRead = _remote.EndReceive(ar);
                    if (bytesRead > 0)
                    {
                        var e = new RelayEventArgs(remoteRecvBuffer, 0, bytesRead);
                        _relay.onOutbound(e);
                        _local.BeginSendTo(e.Buffer, 0, e.Length, 0, _localEP, new AsyncCallback(localSendCallback), null);
                        Delay();
                    }
                    else
                    {
                        this.Close();
                    }
                    Delay();
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            private void localSendCallback(IAsyncResult ar)
            {
                if (_closed) return;
                try
                {
                    _local.EndSendTo(ar);
                    _remote.BeginReceive(this.remoteRecvBuffer, 0, RecvSize, 0,
                        new AsyncCallback(remoteReceiveCallback), null);
                    Delay();
                }
                catch (Exception e)
                {
                    _relay.onError(new RelayErrorEventArgs(e));
                    this.Close();
                }
            }

            private void Delay()
            {
                lock (this)
                {
                    _expires = DateTime.Now.AddMilliseconds(30000);
                }
            }

            public void Close(bool reportClose = true)
            {
                if (_closed) return;
                _closed = true;
                if (_remote != null)
                {
                    try
                    {
                        _remote.Shutdown(SocketShutdown.Both);
                        _remote.Close();
                        _remote = null;
                        remoteRecvBuffer = null;
                        _packages.Clear();
                        _packages = null;
                    }
                    catch (SocketException e)
                    {
                        _relay.onError(new RelayErrorEventArgs(e));
                    }
                }
                if (reportClose && OnClose != null)
                {
                    OnClose(this, new EventArgs());
                    OnClose = null;
                }
            }
        }
    }
}
